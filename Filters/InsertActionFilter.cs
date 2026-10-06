using Gelato.Config;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public class InsertActionFilter(
    GelatoManager manager,
    IUserManager userManager,
    ILibraryManager libraryManager,
    IDtoService dtoService,
    ILogger<InsertActionFilter> log
) : IAsyncActionFilter, IOrderedFilter
{
    private readonly KeyLock _lock = new();
    public int Order => 1;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        // A client that opened a search result keeps the result's id in its page URL: the
        // reload, images, seasons, episodes, similar items and playback all name it. Every such
        // id goes to the item the result became, whatever the action.
        if (ctx.RedirectGuids(manager.GetInsertedId))
        {
            await next();
            return;
        }

        var insertable = ctx.IsInsertableAction();
        var actionName = ctx.GetActionName();
        var hasGuid = ctx.TryGetRouteGuid(out var guid);
        if (
            !hasGuid
            && actionName is ("GetSeasons" or "GetEpisodes")
            && (
                ctx.TryGetActionArgument<Guid>("seriesId", out guid)
                || ctx.TryGetActionArgument<Guid>("seasonId", out guid)
            )
        )
        {
            hasGuid = true;
        }

        if (!hasGuid || !ctx.TryGetUserId(out var userId) || userManager.GetUserById(userId) is not { } user)
        {
            await next();
            return;
        }

        var userDataRequest = ctx.IsUserDataAction();
        if (!insertable && !ctx.IsCanonicalIdRead() && !userDataRequest)
        {
            await next();
            return;
        }

        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var deferredImport = cfg.ImportItemsOnlyWhenPlayed;
        if (
            !insertable
            && !ctx.IsCanonicalIdRead()
            && !(deferredImport && userDataRequest)
        )
        {
            await next();
            return;
        }

        // A transient child's deterministic ID is also its eventual library ID. Once present,
        // Jellyfin owns all further reads and mutations for it.
        if (libraryManager.GetItemById(guid) is Season or Episode)
        {
            await next();
            return;
        }

        // Handle local (non-gelato) series: sync or clean tree on demand
        if (
            insertable
            && libraryManager.GetItemById(guid) is Series localSeries
            && !localSeries.IsGelato()
        )
        {
            await HandleLocalSeriesAsync(userId, localSeries, ctx.HttpContext.RequestAborted);
            await next();
            return;
        }

        var directMeta = manager.GetStremioMeta(guid);
        var parentSeriesMeta = manager.GetParentSeriesMeta(guid);
        var isTransientSeason = manager.IsTransientSeason(guid);
        var isTransientEpisode = manager.IsTransientEpisode(guid);
        var stremioMeta = directMeta ?? (isTransientSeason ? parentSeriesMeta : null);
        if (stremioMeta is null)
        {
            await next();
            return;
        }

        var playbackRequest = actionName is
            "GetPlaybackInfo"
                or "GetPostedPlaybackInfo"
                or "GetVideoStream"
                or "GetVideoStreamByContainer"
                or "GetDownload";
        if (isTransientEpisode || stremioMeta.Type is StremioMediaType.Episode)
            parentSeriesMeta = manager.GetParentSeriesMeta(guid);

        // Existing Jellyfin items are already canonical. Resolve them before asking the remote
        // addon for streams so a local copy remains playable when the addon is unavailable.
        if (!isTransientSeason && manager.IntoBaseItem(stremioMeta) is { } item)
        {
            var existing = manager.FindExistingItem(item, user);
            if (existing is not null)
            {
                log.LogInformation(
                    "Media already exists; redirecting to canonical id {Id}",
                    existing.Id
                );
                manager.RememberInsertedId(guid, existing.Id);
                ctx.ReplaceGuid(existing.Id);
                if (
                    actionName is "GetSeasons" or "GetEpisodes"
                    && ctx.ActionArguments.ContainsKey("seriesId")
                )
                {
                    ctx.ActionArguments["seriesId"] = existing.Id;
                    if (ctx.RouteData.Values.ContainsKey("seriesId"))
                        ctx.RouteData.Values["seriesId"] = existing.Id.ToString("N");
                }
                await next();
                return;
            }
        }

        // PlaybackInfo is the point where Jellyfin asks Gelato to resolve the selected source.
        // Resolve it before creating a library item so a failed/empty stream response stays transient.
        // PlaybackInfo precedes actual playback for some clients, but remains the common source
        // resolution point. Direct stream routes are covered for clients that skip it. Native
        // clients use GET and jellyfin-web uses POST.
        List<StremioStream>? resolvedStreams = null;
        if (deferredImport && playbackRequest)
        {
            var uri = stremioMeta.Type is StremioMediaType.Episode
                ? EpisodeUri(stremioMeta, parentSeriesMeta)
                : manager.IntoBaseItem(stremioMeta) is { } playbackItem
                    ? StremioUri.FromBaseItem(playbackItem)
                    : null;
            if (
                uri is null
                || stremioMeta.Type is not (StremioMediaType.Movie or StremioMediaType.Episode)
                || cfg.Stremio is not { } stremio
            )
            {
                await next();
                return;
            }

            try
            {
                resolvedStreams = await stremio.GetStreamsAsync(uri);
                resolvedStreams = resolvedStreams
                    .Where(stream => stream.IsValid() && !stream.IsTorrent())
                    .ToList();
            }
            catch (Exception ex) when (!ctx.HttpContext.RequestAborted.IsCancellationRequested)
            {
                log.LogWarning(ex, "Could not resolve playback streams for {Id}", stremioMeta.Id);
                await next();
                return;
            }

            if (resolvedStreams.Count == 0)
            {
                await next();
                return;
            }
        }

        if (deferredImport && isTransientSeason && userDataRequest)
        {
            await ImportSeriesForSeasonAsync(ctx, guid, parentSeriesMeta!, user, userId, cfg, next);
            return;
        }

        if (
            deferredImport
            && stremioMeta.Type is StremioMediaType.Episode
            && (playbackRequest || userDataRequest)
        )
        {
            await ImportSeriesForEpisodeAsync(
                ctx,
                guid,
                stremioMeta,
                parentSeriesMeta,
                resolvedStreams,
                user,
                userId,
                cfg,
                next
            );
            return;
        }

        if (
            deferredImport
            && actionName is ("GetSeasons" or "GetEpisodes")
            && stremioMeta.Type is StremioMediaType.Series
        )
        {
            var tree = await BuildTransientSeriesTreeAsync(stremioMeta, cfg);
            if (tree is not null)
            {
                ctx.Result = CreateTransientChildrenResult(ctx, actionName, tree, user);
                return;
            }
        }

        // Browsing endpoints still use Jellyfin's item lookup. Do not import from a non-playback
        // request when the option is enabled; the transient search result remains available in
        // the meta cache for a later playback request. Existing library matches above still
        // redirect to their canonical Jellyfin ids.
        if (deferredImport && !playbackRequest && !userDataRequest && insertable)
        {
            if (actionName == "GetAncestors")
            {
                if (
                    (isTransientSeason || isTransientEpisode)
                    && parentSeriesMeta is not null
                    && await BuildTransientSeriesTreeAsync(parentSeriesMeta, cfg) is { } tree
                )
                {
                    var ancestors = new List<BaseItemDto>
                    {
                        dtoService.GetBaseItemDto(tree.Series, new DtoOptions(false), user),
                    };
                    if (
                        isTransientEpisode
                        && tree.Episodes.FirstOrDefault(ep => ep.Id == guid) is { } transientEpisode
                        && tree.Seasons.FirstOrDefault(s => s.Id == transientEpisode.SeasonId)
                            is { } transientSeason
                    )
                    {
                        ancestors.Add(
                            dtoService.GetBaseItemDto(transientSeason, new DtoOptions(false), user)
                        );
                    }

                    ctx.Result = new OkObjectResult(ancestors);
                    return;
                }

                var transientRoot = stremioMeta.Type == StremioMediaType.Series
                    ? manager.TryGetSeriesFolder(userId)
                    : manager.TryGetMovieFolder(userId);
                ctx.Result = new OkObjectResult(
                    transientRoot is null
                        ? Array.Empty<BaseItemDto>()
                        : new[]
                        {
                            dtoService.GetBaseItemDto(transientRoot, new DtoOptions(false), user),
                        }
                );
                return;
            }

            if (
                actionName
                    is "GetItem"
                        or "GetItemLegacy"
                        or "GetItems"
                        or "GetItemsByUserIdLegacy"
            )
            {
                BaseItem? transientItem = manager.IntoBaseItem(stremioMeta);
                if (
                    (isTransientSeason || isTransientEpisode)
                    && parentSeriesMeta is not null
                    && await BuildTransientSeriesTreeAsync(parentSeriesMeta, cfg) is { } tree
                )
                {
                    transientItem = isTransientSeason
                        ? tree.Seasons.FirstOrDefault(season => season.Id == guid)
                        : tree.Episodes.FirstOrDefault(ep => ep.Id == guid);
                }

                if (transientItem is null)
                {
                    await next();
                    return;
                }

                transientItem.Id = guid;
                ctx.TryGetActionArgument<ItemFields[]>("fields", out var fields, []);
                var dto = dtoService.GetBaseItemDto(
                    transientItem,
                    new DtoOptions(false)
                    {
                        Fields = fields,
                        EnableImages = true,
                        EnableUserData = false,
                    },
                    user
                );
                ctx.Result = actionName is "GetItems" or "GetItemsByUserIdLegacy"
                    ? new OkObjectResult(
                        new QueryResult<BaseItemDto> { Items = [dto], TotalRecordCount = 1 }
                    )
                    : new OkObjectResult(dto);
                return;
            }

            await next();
            return;
        }

        // A read answers for what the library has; it never puts a title in. The result stays a
        // search result until something opens it.
        if (!insertable)
        {
            await next();
            return;
        }

        // Get root folder
        var isSeries = stremioMeta.Type == StremioMediaType.Series;
        var root = isSeries
            ? manager.TryGetSeriesFolder(userId)
            : manager.TryGetMovieFolder(userId);
        if (root is null)
        {
            log.LogWarning("No {Type} folder configured", isSeries ? "Series" : "Movie");
            await next();
            return;
        }

        // Fetch full metadata
        var meta = await cfg.Stremio!.GetMetaAsync(stremioMeta);
        if (meta is null)
        {
            log.LogError(
                "aio meta not found for {Id} {Type}, maybe try aiometadata as meta addon.",
                stremioMeta.Id,
                stremioMeta.Type
            );
            await next();
            return;
        }

        // A played write reaches a series' episodes, and the tree it just created is still growing:
        // the metadata refresh brings the episodes the first pass did not have. The answer goes out
        // without waiting for it (that took up to a minute on a long series), and the state is
        // applied again behind it, by the same call that runs the refresh the insert would queue.
        var wantsPlayed = isSeries ? ctx.WantsPlayedState() : null;
        var baseItem = await InsertMetaAsync(
            guid,
            root,
            meta,
            user,
            refreshItem: wantsPlayed is null
        );
        if (baseItem is not null)
        {
            if (resolvedStreams is not null)
                manager.SaveResolvedPlaybackStreams(baseItem.Id, userId, resolvedStreams);
            manager.RememberInsertedId(guid, baseItem.Id);
            ctx.ReplaceGuid(baseItem.Id);
            manager.RemoveStremioMeta(guid);
        }

        await next();

        if (baseItem is not null && wantsPlayed is bool played)
        {
            manager.RefreshAndReapplyPlayedState(baseItem, user, played);
        }
    }

    private sealed record TransientSeriesTree(
        Series Series,
        List<Season> Seasons,
        List<Episode> Episodes
    );

    private async Task<TransientSeriesTree?> BuildTransientSeriesTreeAsync(
        StremioMeta searchMeta,
        PluginConfiguration cfg
    )
    {
        if (cfg.Stremio is not { } stremio)
            return null;

        var meta = await stremio.GetMetaAsync(searchMeta);
        if (meta is null || manager.IntoBaseItem(meta) is not Series series)
            return null;

        // The series DTO keeps the search ID, but child DTOs expose this stable future library ID.
        // Keep both addresses resolvable while the tree is transient.
        manager.SaveStremioMeta(series.Id, searchMeta);

        var seasons = new List<Season>();
        var episodes = new List<Episode>();
        var episodesBySeason = (meta.Videos ?? [])
            .Where(ep => ep.Season.HasValue && (ep.Episode.HasValue || ep.Number.HasValue))
            .OrderBy(ep => ep.Season)
            .ThenBy(ep => ep.Episode ?? ep.Number)
            .GroupBy(ep => ep.Season!.Value)
            .ToList();
        var seriesKey = series.GetPresentationUniqueKey();

        foreach (var group in episodesBySeason)
        {
            var seasonNumber = group.Key;
            var seasonPath = GelatoManager.VirtualSeasonPath(series, seasonNumber);
            var season = new Season
            {
                Id = libraryManager.GetNewItemId(seasonPath, typeof(Season)),
                Name = $"Season {seasonNumber:D2}",
                IndexNumber = seasonNumber,
                SeriesId = series.Id,
                SeriesName = series.Name,
                SeriesPresentationUniqueKey = seriesKey,
                ParentId = series.Id,
                Path = seasonPath,
            };
            season.SetProviderId("Stremio", $"{series.GetProviderId("Stremio")}:{seasonNumber}");
            season.PresentationUniqueKey = season.CreatePresentationUniqueKey();
            season.SetParent(series);
            manager.SaveTransientSeason(season.Id, searchMeta);
            seasons.Add(season);

            foreach (var episodeMeta in group)
            {
                episodeMeta.Type = StremioMediaType.Episode;
                if (manager.IntoBaseItem(episodeMeta) is not Episode episode)
                    continue;

                episode.IndexNumber = episodeMeta.Episode ?? episodeMeta.Number;
                episode.ParentIndexNumber = seasonNumber;
                episode.SeriesId = series.Id;
                episode.SeriesName = series.Name;
                episode.SeasonId = season.Id;
                episode.SeasonName = season.Name;
                episode.ParentId = season.Id;
                episode.SeriesPresentationUniqueKey = seriesKey;
                episode.PresentationUniqueKey = episode.GetPresentationUniqueKey();
                episode.SetParent(season);
                manager.SaveTransientEpisode(episode.Id, episodeMeta, searchMeta);
                episodes.Add(episode);
            }
        }

        return new TransientSeriesTree(series, seasons, episodes);
    }

    private IActionResult CreateTransientChildrenResult(
        ActionExecutingContext ctx,
        string? actionName,
        TransientSeriesTree tree,
        User user
    )
    {
        IEnumerable<BaseItem> children = actionName == "GetSeasons"
            ? tree.Seasons
            : tree.Episodes;
        if (
            actionName == "GetEpisodes"
            && ctx.TryGetActionArgument<Guid>("seasonId", out var seasonId)
            && seasonId != Guid.Empty
        )
        {
            children = tree.Episodes.Where(ep => ep.SeasonId == seasonId);
        }

        ctx.TryGetActionArgument<ItemFields[]>("fields", out var fields, []);
        var dtos = children
            .Select(child =>
                dtoService.GetBaseItemDto(
                    child,
                    new DtoOptions(false)
                    {
                        Fields = fields,
                        EnableImages = true,
                        EnableUserData = false,
                    },
                    user
                )
            )
            .ToArray();
        ctx.TryGetActionArgument("startIndex", out var start, 0);
        ctx.TryGetActionArgument("limit", out var limit, 0);
        var page = dtos.Skip(start).Take(limit > 0 ? limit : dtos.Length).ToArray();
        return new OkObjectResult(new QueryResult<BaseItemDto> { Items = page, TotalRecordCount = dtos.Length });
    }

    private async Task ImportSeriesForEpisodeAsync(
        ActionExecutingContext ctx,
        Guid episodeGuid,
        StremioMeta episodeMeta,
        StremioMeta? seriesMeta,
        List<StremioStream>? resolvedStreams,
        User user,
        Guid userId,
        PluginConfiguration cfg,
        ActionExecutionDelegate next
    )
    {
        if (
            seriesMeta is null
            || cfg.Stremio is not { } stremio
            || manager.IntoBaseItem(seriesMeta) is not Series seriesCandidate
            || StremioUri.FromBaseItem(seriesCandidate) is not { } seriesUri
            || manager.TryGetSeriesFolder(userId) is not { } root
        )
        {
            await next();
            return;
        }

        var fullSeriesMeta = await stremio.GetMetaAsync(seriesMeta);
        if (fullSeriesMeta is null || !ContainsEpisode(fullSeriesMeta, episodeMeta))
        {
            await next();
            return;
        }

        var episodeCandidate = manager.IntoBaseItem(episodeMeta) as Episode;
        if (episodeCandidate is null)
        {
            await next();
            return;
        }

        var series = await InsertMetaAsync(seriesUri.ToGuid(), root, fullSeriesMeta, user);
        if (series is null)
        {
            await next();
            return;
        }

        manager.RememberInsertedId(seriesUri.ToGuid(), series.Id);
        var episode = libraryManager.GetItemById(episodeCandidate.Id) as Episode;
        episode ??= FindSeriesEpisode(series.Id, episodeMeta);

        if (episode is null)
        {
            if (
                series is Series seriesItem
                && (seriesItem.IsGelato() || cfg.ExtendLocalSeriesTrees)
            )
            {
                await _lock.RunQueuedAsync(
                    seriesUri.ToGuid(),
                    async ct =>
                    {
                        episode = FindSeriesEpisode(series.Id, episodeMeta);
                        if (episode is null)
                        {
                            await manager
                                .SyncSeriesTreesAsync(cfg, fullSeriesMeta, ct, seriesItem)
                                .ConfigureAwait(false);
                        }
                    },
                    ctx.HttpContext.RequestAborted
                );
                episode = libraryManager.GetItemById(episodeCandidate?.Id ?? Guid.Empty) as Episode;
                episode ??= FindSeriesEpisode(series.Id, episodeMeta);
            }
        }

        if (episode is null)
        {
            await next();
            return;
        }

        manager.RememberInsertedId(episodeGuid, episode.Id);
        if (resolvedStreams is not null)
            manager.SaveResolvedPlaybackStreams(episode.Id, userId, resolvedStreams);
        manager.RemoveStremioMeta(episodeGuid);
        ctx.ReplaceGuid(episode.Id);
        await next();
    }

    private async Task ImportSeriesForSeasonAsync(
        ActionExecutingContext ctx,
        Guid seasonGuid,
        StremioMeta seriesMeta,
        User user,
        Guid userId,
        PluginConfiguration cfg,
        ActionExecutionDelegate next
    )
    {
        if (
            cfg.Stremio is not { } stremio
            || manager.IntoBaseItem(seriesMeta) is not Series seriesCandidate
            || StremioUri.FromBaseItem(seriesCandidate) is not { } seriesUri
            || manager.TryGetSeriesFolder(userId) is not { } root
            || await BuildTransientSeriesTreeAsync(seriesMeta, cfg) is not { } transientTree
            || transientTree.Seasons.FirstOrDefault(s => s.Id == seasonGuid) is not { } transientSeason
        )
        {
            await next();
            return;
        }

        var fullSeriesMeta = await stremio.GetMetaAsync(seriesMeta);
        if (fullSeriesMeta is null || !ContainsSeason(fullSeriesMeta, transientSeason.IndexNumber))
        {
            await next();
            return;
        }

        var series = await InsertMetaAsync(seriesUri.ToGuid(), root, fullSeriesMeta, user);
        if (series is null)
        {
            await next();
            return;
        }

        manager.RememberInsertedId(seriesUri.ToGuid(), series.Id);
        var season = libraryManager.GetItemById(seasonGuid) as Season
            ?? FindSeriesSeason(series.Id, transientSeason.IndexNumber);

        if (
            season is null
            && series is Series seriesItem
            && (seriesItem.IsGelato() || cfg.ExtendLocalSeriesTrees)
        )
        {
            await _lock.RunQueuedAsync(
                seriesUri.ToGuid(),
                async ct =>
                {
                    season = FindSeriesSeason(series.Id, transientSeason.IndexNumber);
                    if (season is null)
                    {
                        await manager
                            .SyncSeriesTreesAsync(cfg, fullSeriesMeta, ct, seriesItem)
                            .ConfigureAwait(false);
                    }
                },
                ctx.HttpContext.RequestAborted
            );
            season = libraryManager.GetItemById(seasonGuid) as Season
                ?? FindSeriesSeason(series.Id, transientSeason.IndexNumber);
        }

        if (season is null)
        {
            await next();
            return;
        }

        manager.RememberInsertedId(seasonGuid, season.Id);
        ctx.ReplaceGuid(season.Id);
        await next();
    }

    private static StremioUri? EpisodeUri(StremioMeta episode, StremioMeta? series)
    {
        var seriesId = string.IsNullOrWhiteSpace(series?.ImdbId) ? series?.Id : series.ImdbId;
        var episodeNumber = episode.Episode ?? episode.Number;
        if (
            string.IsNullOrWhiteSpace(seriesId)
            || episode.Season is not { } season
            || episodeNumber is not { } number
        )
        {
            return null;
        }

        return new StremioUri(StremioMediaType.Series, $"{seriesId}:{season}:{number}");
    }

    private static bool ContainsEpisode(StremioMeta series, StremioMeta episode)
    {
        var episodeNumber = episode.Episode ?? episode.Number;
        return episode.Season.HasValue
            && episodeNumber.HasValue
            && (series.Videos ?? []).Any(candidate =>
                candidate.Season == episode.Season
                && (candidate.Episode ?? candidate.Number) == episodeNumber
            );
    }

    private static bool ContainsSeason(StremioMeta series, int? seasonNumber) =>
        seasonNumber.HasValue
        && (series.Videos ?? []).Any(ep =>
            ep.Season == seasonNumber && (ep.Episode.HasValue || ep.Number.HasValue)
        );

    private Episode? FindSeriesEpisode(Guid seriesId, StremioMeta episodeMeta) =>
        libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    AncestorIds = [seriesId],
                    IncludeItemTypes = [BaseItemKind.Episode],
                    Recursive = true,
                    IsDeadPerson = true,
                }
            )
            .OfType<Episode>()
            .FirstOrDefault(ep =>
                ep.ParentIndexNumber == (episodeMeta.Season ?? 0)
                && ep.IndexNumber == (episodeMeta.Episode ?? episodeMeta.Number)
            );

    private Season? FindSeriesSeason(Guid seriesId, int? seasonNumber) =>
        libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    ParentId = seriesId,
                    IncludeItemTypes = [BaseItemKind.Season],
                    IsDeadPerson = true,
                }
            )
            .OfType<Season>()
            .FirstOrDefault(season => season.IndexNumber == seasonNumber);

    private async Task HandleLocalSeriesAsync(Guid userId, Series series, CancellationToken ct)
    {
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);

        if (cfg.ExtendLocalSeriesTrees)
        {
            if (manager.HasExtendedTree(series))
                return;

            if (cfg.Stremio is not { } stremio)
                return;

            log.LogInformation(
                "InsertActionFilter: syncing local series tree for {Name} ({Id})",
                series.Name,
                series.Id
            );

            var meta = await stremio.GetMetaAsync(series).ConfigureAwait(false);
            if (meta is null)
                return;

            await manager
                .SyncSeriesTreesAsync(cfg, meta, ct, existingSeries: series)
                .ConfigureAwait(false);
        }
        else
        {
            // Setting disabled — clean any virtual items that may exist for this series
            manager.CleanVirtualTreeItem(series, ct);
        }
    }

    public async Task<BaseItem?> InsertMetaAsync(
        Guid guid,
        Folder root,
        StremioMeta meta,
        User user,
        bool refreshItem = true
    )
    {
        BaseItem? baseItem = null;
        var created = false;

        await _lock.RunQueuedAsync(
            guid,
            async ct =>
            {
                meta.Guid = guid;
                (baseItem, created) = await manager.InsertMeta(
                    root,
                    meta,
                    user,
                    false,
                    refreshItem,
                    meta.Type is StremioMediaType.Series,
                    ct
                );
            }
        );

        if (baseItem is not null && created)
            log.LogInformation("inserted new media: {Name}", baseItem.Name);

        return baseItem;
    }
}
