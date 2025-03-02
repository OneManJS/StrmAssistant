using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using StrmAssistant.Provider;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.LanguageUtility;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class MovieDbEpisodeGroup : PatchBase<MovieDbEpisodeGroup>
    {
        internal class SeasonGroupName
        {
            public int? LookupSeasonNumber { get; set; }
            public string LookupLanguage { get; set; }
            public string GroupName { get; set; }
        }

        internal class SeasonEpisodeMapping
        {
            public string SeriesTmdbId { get; set; }
            public string EpisodeGroupId { get; set; }
            public int? LookupSeasonNumber { get; set; }
            public int? LookupEpisodeNumber { get; set; }
            public int? MappedSeasonNumber { get; set; }
            public int? MappedEpisodeNumber { get; set; }
        }

        private static Assembly _movieDbAssembly;
        private static MethodInfo _seriesGetMetadata;
        private static MethodInfo _seasonGetMetadata;
        private static MethodInfo _episodeGetMetadata;
        private static MethodInfo _episodeGetImages;
        private static MethodInfo _canRefreshMetadata;

        private static readonly AsyncLocal<Series> CurrentSeries = new AsyncLocal<Series>();
        private static readonly AsyncLocal<bool> EnableEpisodeGroupScraping = new AsyncLocal<bool>();

        public const string LocalEpisodeGroupFileName = "episodegroup.json";

        public MovieDbEpisodeGroup()
        {
            Initialize();

            if (Plugin.Instance.MetadataEnhanceStore.GetOptions().MovieDbEpisodeGroup)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            _movieDbAssembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "MovieDb");

            if (_movieDbAssembly != null)
            {
                var movieDbSeriesProvider = _movieDbAssembly.GetType("MovieDb.MovieDbSeriesProvider");
                _seriesGetMetadata =
                    movieDbSeriesProvider.GetMethod("GetMetadata", BindingFlags.Public | BindingFlags.Instance);
                var movieDbSeasonProvider = _movieDbAssembly.GetType("MovieDb.MovieDbSeasonProvider");
                _seasonGetMetadata = movieDbSeasonProvider.GetMethod("GetMetadata",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(RemoteMetadataFetchOptions<SeasonInfo>), typeof(CancellationToken) }, null);
                var movieDbEpisodeProvider = _movieDbAssembly.GetType("MovieDb.MovieDbEpisodeProvider");
                _episodeGetMetadata = movieDbEpisodeProvider.GetMethod("GetMetadata",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(RemoteMetadataFetchOptions<EpisodeInfo>), typeof(CancellationToken) }, null);
                var movieDbEpisodeImageProvider = _movieDbAssembly.GetType("MovieDb.MovieDbEpisodeImageProvider");
                _episodeGetImages = movieDbEpisodeImageProvider.GetMethod("GetImages",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(RemoteImageFetchOptions), typeof(CancellationToken) }, null);

                var embyProviders = Assembly.Load("Emby.Providers");
                var providerManager = embyProviders.GetType("Emby.Providers.Manager.ProviderManager");
                _canRefreshMetadata =
                    providerManager.GetMethod("CanRefresh", BindingFlags.Static | BindingFlags.NonPublic);
            }
            else
            {
                Plugin.Instance.Logger.Warn("MovieDbEpisodeGroup - MovieDb plugin is not installed");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                PatchTracker.IsSupported = false;
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _seriesGetMetadata, prefix: nameof(SeriesGetMetadataPrefix),
                postfix: nameof(SeriesGetMetadataPostfix));
            PatchUnpatch(PatchTracker, apply, _seasonGetMetadata, prefix: nameof(SeasonGetMetadataPrefix),
                postfix: nameof(SeasonGetMetadataPostfix));
            PatchUnpatch(PatchTracker, apply, _episodeGetMetadata, prefix: nameof(EpisodeGetMetadataPrefix),
                postfix: nameof(EpisodeGetMetadataPostfix));
            PatchUnpatch(PatchTracker, apply, _episodeGetImages, prefix: nameof(EpisodeGetImagesPrefix),
                postfix: nameof(EpisodeGetImagesPostfix));
            PatchUnpatch(PatchTracker, apply, _canRefreshMetadata, prefix: nameof(CanRefreshMetadataPrefix));
        }

        [HarmonyPrefix]
        private static void CanRefreshMetadataPrefix(IMetadataProvider provider, BaseItem item,
            LibraryOptions libraryOptions, bool includeDisabled, bool forceEnableInternetMetadata,
            bool ignoreMetadataLock)
        {
            if (CurrentSeries.Value != null) return;

            if (item.Parent is null && item.ExtraType is null) return;

            if (provider is IRemoteMetadataProvider && provider.Name == "TheMovieDb")
            {
                var providerName = provider.GetType().FullName;
                Series series = null;

                if (item is Episode episode && providerName == "MovieDb.MovieDbEpisodeProvider")
                {
                    series = episode.Series;
                }
                else if (item is Season season && providerName == "MovieDb.MovieDbSeasonProvider")
                {
                    series = season.Series;
                }
                else if (item is Series self && providerName == "MovieDb.MovieDbSeriesProvider")
                {
                    series = self;
                }

                if (series != null)
                {
                    CurrentSeries.Value = series;
                    EnableEpisodeGroupScraping.Value =
                        series.ProviderIds.TryGetValue(MetadataProviders.Tmdb.ToString(), out _) &&
                        series.ProviderIds.TryGetValue(MovieDbEpisodeGroupExternalId.StaticName, out _);
                }
            }
        }

        [HarmonyPrefix]
        private static void SeriesGetMetadataPrefix(SeriesInfo info, CancellationToken cancellationToken,
            Task<MetadataResult<Series>> __result, out string __state)
        {
            __state = null;
            var series = CurrentSeries.Value;
            CurrentSeries.Value = null;

            if (Plugin.Instance.MetadataEnhanceStore.GetOptions().LocalEpisodeGroup &&
                series?.ContainingFolderPath != null)
            {
                var localEpisodeGroupPath = Path.Combine(series.ContainingFolderPath, LocalEpisodeGroupFileName);
                var episodeGroupInfo = Task.Run(
                    () => Plugin.MetadataApi.FetchLocalEpisodeGroup(localEpisodeGroupPath),
                    cancellationToken).Result;

                if (episodeGroupInfo != null && !string.IsNullOrEmpty(episodeGroupInfo.id) &&
                    series.ProviderIds.TryGetValue(MetadataProviders.Tmdb.ToString(), out _))
                {
                    __state = episodeGroupInfo.id;
                    EnableEpisodeGroupScraping.Value = true;
                }
            }
        }

        [HarmonyPostfix]
        private static void SeriesGetMetadataPostfix(SeriesInfo info, CancellationToken cancellationToken,
            Task<MetadataResult<Series>> __result, string __state)
        {
            if (!EnableEpisodeGroupScraping.Value || __state is null) return;

            MetadataResult<Series> metadataResult = null;

            try
            {
                metadataResult = __result?.Result;
            }
            catch
            {
                // ignored
            }

            if (metadataResult != null && metadataResult.HasMetadata && metadataResult.Item != null)
            {
                metadataResult.Item.SetProviderId(MovieDbEpisodeGroupExternalId.StaticName, __state);
            }
        }

        [HarmonyPrefix]
        private static void SeasonGetMetadataPrefix(RemoteMetadataFetchOptions<SeasonInfo> options,
            CancellationToken cancellationToken, Task<MetadataResult<Season>> __result, out SeasonGroupName __state)
        {
            __state = null;
            var series = CurrentSeries.Value;
            CurrentSeries.Value = null;

            if (EnableEpisodeGroupScraping.Value)
            {
                var season = options.SearchInfo;
                season.SeriesProviderIds.TryGetValue(MovieDbEpisodeGroupExternalId.StaticName, out var episodeGroupId);
                episodeGroupId = episodeGroupId?.Trim();
                EpisodeGroupResponse episodeGroupInfo = null;
                string localEpisodeGroupPath = null;

                if (Plugin.Instance.MetadataEnhanceStore.GetOptions().LocalEpisodeGroup &&
                    series?.ContainingFolderPath != null)
                {
                    localEpisodeGroupPath = Path.Combine(series.ContainingFolderPath, LocalEpisodeGroupFileName);
                    episodeGroupInfo = Task.Run(() => Plugin.MetadataApi.FetchLocalEpisodeGroup(localEpisodeGroupPath),
                        cancellationToken).Result;

                    if (episodeGroupInfo != null && !string.IsNullOrEmpty(episodeGroupInfo.id) &&
                        string.IsNullOrEmpty(episodeGroupId))
                    {
                        series.SetProviderId(MovieDbEpisodeGroupExternalId.StaticName, episodeGroupInfo.id);
                    }
                }

                if (episodeGroupInfo is null && season.IndexNumber.HasValue && season.IndexNumber > 0 &&
                    season.SeriesProviderIds.TryGetValue(MetadataProviders.Tmdb.ToString(), out var seriesTmdbId) &&
                    !string.IsNullOrEmpty(episodeGroupId))
                {
                    episodeGroupInfo = Task
                        .Run(
                            () => Plugin.MetadataApi.FetchOnlineEpisodeGroup(seriesTmdbId, episodeGroupId,
                                season.MetadataLanguage, localEpisodeGroupPath, cancellationToken), cancellationToken)
                        .Result;
                }

                var matchingSeason = episodeGroupInfo?.groups.FirstOrDefault(g => g.order == season.IndexNumber);

                if (matchingSeason != null)
                {
                    __state = new SeasonGroupName
                    {
                        LookupSeasonNumber = season.IndexNumber,
                        LookupLanguage = season.MetadataLanguage,
                        GroupName = matchingSeason.name
                    };
                }
            }
        }

        [HarmonyPostfix]
        private static void SeasonGetMetadataPostfix(RemoteMetadataFetchOptions<SeasonInfo> options,
            CancellationToken cancellationToken, Task<MetadataResult<Season>> __result, SeasonGroupName __state)
        {
            if (!EnableEpisodeGroupScraping.Value || __state is null) return;

            MetadataResult<Season> metadataResult = null;

            try
            {
                metadataResult = __result?.Result;
            }
            catch
            {
                // ignored
            }
            
            if (metadataResult is null) return;

            var isZh = __state.LookupLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            var isJapaneseFallback = Plugin.Instance.MetadataEnhanceStore.GetOptions().ChineseMovieDb &&
                                     HasMovieDbJapaneseFallback();

            var shouldAssignSeasonName = !isZh ||
                                         (!isJapaneseFallback
                                             ? IsChinese(__state.GroupName)
                                             : IsChineseJapanese(__state.GroupName));

            if (metadataResult.Item is null) metadataResult.Item = new Season();

            metadataResult.Item.IndexNumber = __state.LookupSeasonNumber;

            if (shouldAssignSeasonName) metadataResult.Item.Name = __state.GroupName;

            metadataResult.Item.PremiereDate = null;
            metadataResult.Item.ProductionYear = null;

            metadataResult.HasMetadata = true;
        }

        [HarmonyPrefix]
        private static void EpisodeGetMetadataPrefix(RemoteMetadataFetchOptions<EpisodeInfo> options,
            CancellationToken cancellationToken, Task<MetadataResult<Episode>> __result,
            out SeasonEpisodeMapping __state)
        {
            __state = null;
            var series = CurrentSeries.Value;
            CurrentSeries.Value = null;

            if (EnableEpisodeGroupScraping.Value)
            {
                var episode = options.SearchInfo;
                string localEpisodeGroupPath = null;

                if (Plugin.Instance.MetadataEnhanceStore.GetOptions().LocalEpisodeGroup &&
                    series?.ContainingFolderPath != null)
                {
                    localEpisodeGroupPath = Path.Combine(series.ContainingFolderPath, LocalEpisodeGroupFileName);
                }

                if (episode.SeriesProviderIds.TryGetValue(MetadataProviders.Tmdb.ToString(), out var seriesTmdbId))
                {
                    episode.SeriesProviderIds.TryGetValue(MovieDbEpisodeGroupExternalId.StaticName,
                        out var episodeGroupId);
                    episodeGroupId = episodeGroupId?.Trim();

                    var matchingEpisode =
                        Task.Run(
                                () => MapSeasonEpisode(seriesTmdbId, episodeGroupId, episode.MetadataLanguage,
                                    episode.ParentIndexNumber, episode.IndexNumber, localEpisodeGroupPath,
                                    cancellationToken),
                                cancellationToken)
                            .Result;

                    if (matchingEpisode != null)
                    {
                        __state = matchingEpisode;
                        episode.ParentIndexNumber = matchingEpisode.MappedSeasonNumber;
                        episode.IndexNumber = matchingEpisode.MappedEpisodeNumber;

                        if (series != null && string.IsNullOrEmpty(episodeGroupId) &&
                            !string.IsNullOrEmpty(matchingEpisode.EpisodeGroupId))
                        {
                            series.SetProviderId(MovieDbEpisodeGroupExternalId.StaticName,
                                matchingEpisode.EpisodeGroupId);
                        }
                    }
                }
            }
        }

        [HarmonyPostfix]
        private static void EpisodeGetMetadataPostfix(RemoteMetadataFetchOptions<EpisodeInfo> options,
            CancellationToken cancellationToken, Task<MetadataResult<Episode>> __result, SeasonEpisodeMapping __state)
        {
            if (!EnableEpisodeGroupScraping.Value || __state is null) return;

            MetadataResult<Episode> metadataResult = null;

            try
            {
                metadataResult = __result?.Result;
            }
            catch
            {
                // ignored
            }

            if (metadataResult is null || !metadataResult.HasMetadata || metadataResult.Item is null) return;

            metadataResult.Item.ParentIndexNumber = __state.LookupSeasonNumber;
            metadataResult.Item.IndexNumber = __state.LookupEpisodeNumber;
        }

        [HarmonyPrefix]
        private static void EpisodeGetImagesPrefix(RemoteImageFetchOptions options, CancellationToken cancellationToken,
            Task<IEnumerable<RemoteImageInfo>> __result, out SeasonEpisodeMapping __state)
        {
            __state = null;

            if (!EnableEpisodeGroupScraping.Value) return;

            if (options.Item is Episode episode)
            {
                var seriesTmdbId = episode.Series.GetProviderId(MetadataProviders.Tmdb);
                var localEpisodeGroupPath = Plugin.Instance.MetadataEnhanceStore.GetOptions().LocalEpisodeGroup
                    ? Path.Combine(episode.Series.ContainingFolderPath, LocalEpisodeGroupFileName)
                    : null;

                if (!string.IsNullOrEmpty(seriesTmdbId))
                {
                    var episodeGroupId = episode.Series.GetProviderId(MovieDbEpisodeGroupExternalId.StaticName)?.Trim();
                    var matchingEpisode = Task.Run(() => MapSeasonEpisode(seriesTmdbId, episodeGroupId, null,
                            episode.ParentIndexNumber, episode.IndexNumber, localEpisodeGroupPath, cancellationToken),
                        cancellationToken).Result;

                    if (matchingEpisode != null)
                    {
                        __state = matchingEpisode;
                        episode.ParentIndexNumber = matchingEpisode.MappedSeasonNumber;
                        episode.IndexNumber = matchingEpisode.MappedEpisodeNumber;
                    }
                }
            }
        }

        [HarmonyPostfix]
        private static void EpisodeGetImagesPostfix(RemoteImageFetchOptions options,
            CancellationToken cancellationToken, Task<IEnumerable<RemoteImageInfo>> __result,
            SeasonEpisodeMapping __state)
        {
            if (!EnableEpisodeGroupScraping.Value || __state is null) return;

            if (options.Item is Episode episode)
            {
                episode.ParentIndexNumber = __state.LookupSeasonNumber;
                episode.IndexNumber = __state.LookupEpisodeNumber;
            }
        }

        private static async Task<SeasonEpisodeMapping> MapSeasonEpisode(string seriesTmdbId, string episodeGroupId,
            string language, int? lookupSeasonNumber, int? lookupEpisodeNumber, string localEpisodeGroupPath,
            CancellationToken cancellationToken)
        {
            if (!lookupSeasonNumber.HasValue || !lookupEpisodeNumber.HasValue) return null;

            EpisodeGroupResponse episodeGroupInfo = null;

            if (Plugin.Instance.MetadataEnhanceStore.GetOptions().LocalEpisodeGroup &&
                !string.IsNullOrEmpty(localEpisodeGroupPath))
            {
                episodeGroupInfo = await Plugin.MetadataApi.FetchLocalEpisodeGroup(localEpisodeGroupPath)
                    .ConfigureAwait(false);
            }

            if (episodeGroupInfo is null && !string.IsNullOrEmpty(episodeGroupId))
            {
                episodeGroupInfo =
                    await Plugin.MetadataApi.FetchOnlineEpisodeGroup(seriesTmdbId, episodeGroupId, language,
                        localEpisodeGroupPath, cancellationToken);
            }

            var matchingEpisode = episodeGroupInfo?.groups.Where(g => g.order == lookupSeasonNumber)
                .SelectMany(g => g.episodes)
                .FirstOrDefault(e => e.order + 1 == lookupEpisodeNumber.Value);

            if (matchingEpisode != null)
            {
                return new SeasonEpisodeMapping
                {
                    SeriesTmdbId = seriesTmdbId,
                    EpisodeGroupId = episodeGroupInfo.id,
                    LookupSeasonNumber = lookupSeasonNumber,
                    LookupEpisodeNumber = lookupEpisodeNumber,
                    MappedSeasonNumber = matchingEpisode.season_number,
                    MappedEpisodeNumber = matchingEpisode.episode_number
                };
            }

            return null;
        }
    }
}
