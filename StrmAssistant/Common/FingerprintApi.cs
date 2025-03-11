using HarmonyLib;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Mod;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Common
{
    public class FingerprintApi
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;

        private static readonly PatchTracker PatchTracker =
            new PatchTracker(typeof(FingerprintApi),
                Plugin.Instance.IsModSupported ? PatchApproach.Harmony : PatchApproach.Reflection);
        private readonly object _audioFingerprintManager;
        private readonly MethodInfo _createTitleFingerprint;
        private readonly MethodInfo _getAllFingerprintFilesForSeason;
        private readonly MethodInfo _updateSequencesForSeason;
        private readonly FieldInfo _timeoutMs;

        public static List<string> LibraryPathsInScope;

        public FingerprintApi(ILibraryManager libraryManager, IFileSystem fileSystem,
            IApplicationPaths applicationPaths, IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder,
            IMediaMountManager mediaMountManager, IJsonSerializer jsonSerializer,
            IServerApplicationHost serverApplicationHost)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;

            UpdateLibraryPathsInScope(Plugin.Instance.IntroSkipStore.GetOptions().MarkerEnabledLibraryScope);

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var audioFingerprintManager = embyProviders.GetType("Emby.Providers.Markers.AudioFingerprintManager");
                var audioFingerprintManagerConstructor = audioFingerprintManager.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[]
                    {
                        typeof(IFileSystem), typeof(ILogger), typeof(IApplicationPaths), typeof(IFfmpegManager),
                        typeof(IMediaEncoder), typeof(IMediaMountManager), typeof(IJsonSerializer),
                        typeof(IServerApplicationHost)
                    }, null);
                _audioFingerprintManager = audioFingerprintManagerConstructor?.Invoke(new object[]
                {
                    fileSystem, _logger, applicationPaths, ffmpegManager, mediaEncoder, mediaMountManager,
                    jsonSerializer, serverApplicationHost
                });
                _createTitleFingerprint = audioFingerprintManager.GetMethod("CreateTitleFingerprint",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[]
                    {
                        typeof(Episode), typeof(LibraryOptions), typeof(IDirectoryService),
                        typeof(CancellationToken)
                    }, null);
                _getAllFingerprintFilesForSeason = audioFingerprintManager.GetMethod("GetAllFingerprintFilesForSeason",
                    BindingFlags.Public | BindingFlags.Instance);
                _updateSequencesForSeason = audioFingerprintManager.GetMethod("UpdateSequencesForSeason",
                    BindingFlags.Public | BindingFlags.Instance);
                _timeoutMs = audioFingerprintManager.GetField("TimeoutMs",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                PatchTimeout(Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.MaxConcurrentCount);
            }
            catch (Exception e)
            {
                _logger.Debug(e.Message);
                _logger.Debug(e.StackTrace);
            }

            if (_audioFingerprintManager is null || _createTitleFingerprint is null ||
                _getAllFingerprintFilesForSeason is null || _updateSequencesForSeason is null || _timeoutMs is null)
            {
                _logger.Warn($"{PatchTracker.PatchType.Name} Init Failed");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
            }
            else if (Plugin.Instance.IsModSupported)
            {
                PatchManager.ReversePatch(PatchTracker, _createTitleFingerprint, nameof(CreateTitleFingerprintStub));
                PatchManager.ReversePatch(PatchTracker, _getAllFingerprintFilesForSeason,
                    nameof(GetAllFingerprintFilesForSeasonStub));
                PatchManager.ReversePatch(PatchTracker, _updateSequencesForSeason,
                    nameof(UpdateSequencesForSeasonStub));
            }
        }

#pragma warning disable CS1998
        [HarmonyReversePatch]
        private static async Task<Tuple<string, bool>> CreateTitleFingerprintStub(object instance, Episode item,
            LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
#pragma warning restore CS1998

        public Task<Tuple<string, bool>> CreateTitleFingerprint(Episode item, IDirectoryService directoryService,
            CancellationToken cancellationToken)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    return CreateTitleFingerprintStub(_audioFingerprintManager, item, libraryOptions, directoryService,
                        cancellationToken);
                case PatchApproach.Reflection:
                    return (Task<Tuple<string, bool>>)_createTitleFingerprint.Invoke(_audioFingerprintManager,
                        new object[] { item, libraryOptions, directoryService, cancellationToken });
                default:
                    throw new NotImplementedException();
            }
        }

        public Task<Tuple<string, bool>> CreateTitleFingerprint(Episode item, CancellationToken cancellationToken)
        {
            var directoryService = new DirectoryService(_logger, _fileSystem);

            return CreateTitleFingerprint(item, directoryService, cancellationToken);
        }

#pragma warning disable CS1998
        [HarmonyReversePatch]
        private static async Task<object> GetAllFingerprintFilesForSeasonStub(object instance, Season season,
            Episode[] episodes, LibraryOptions libraryOptions, IDirectoryService directoryService,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
#pragma warning restore CS1998

        private Task<object> GetAllFingerprintFilesForSeason(Season season, Episode[] episodes,
            LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    return GetAllFingerprintFilesForSeasonStub(_audioFingerprintManager, season, episodes,
                        libraryOptions, directoryService, cancellationToken);
                case PatchApproach.Reflection:
                    return (Task<object>)_getAllFingerprintFilesForSeason.Invoke(_audioFingerprintManager,
                        new object[] { season, episodes, libraryOptions, directoryService, cancellationToken });
                default:
                    throw new NotImplementedException();
            }
        }

        [HarmonyReversePatch]
        private static void UpdateSequencesForSeasonStub(object instance, Season season, object seasonFingerprintInfo,
            Episode episode, LibraryOptions libraryOptions, IDirectoryService directoryService) =>
            throw new NotImplementedException();

        private void UpdateSequencesForSeason(Season season, object seasonFingerprintInfo, Episode episode,
            LibraryOptions libraryOptions, IDirectoryService directoryService)
        {
            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    UpdateSequencesForSeasonStub(_audioFingerprintManager, season, seasonFingerprintInfo, episode,
                        libraryOptions, directoryService);
                    break;
                case PatchApproach.Reflection:
                    _updateSequencesForSeason.Invoke(_audioFingerprintManager,
                        new[] { season, seasonFingerprintInfo, episode, libraryOptions, directoryService });
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        public void PatchTimeout(int maxConcurrentCount)
        {
            var newTimeout = maxConcurrentCount * Convert.ToInt32(TimeSpan.FromMinutes(10.0).TotalMilliseconds);
            _timeoutMs.SetValue(_audioFingerprintManager, newTimeout);
        }

        public bool IsLibraryInScope(BaseItem item)
        {
            return !string.IsNullOrEmpty(item.Path) && LibraryPathsInScope.Any(l => item.Path.StartsWith(l));
        }

        public void UpdateLibraryPathsInScope(string currentScope)
        {
            var libraryIds = currentScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();

            LibraryPathsInScope = _libraryManager.GetVirtualFolders()
                .Where(f => libraryIds != null && libraryIds.Any(id => id != "-1")
                    ? libraryIds.Contains(f.Id)
                    : f.LibraryOptions.EnableMarkerDetection &&
                      (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null))
                .SelectMany(l => l.Locations)
                .Select(ls => ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? ls
                    : ls + Path.DirectorySeparatorChar)
                .ToList();
        }

        public void UpdateLibraryPathsInScope()
        {
            UpdateLibraryPathsInScope(Plugin.Instance.IntroSkipStore.GetOptions().MarkerEnabledLibraryScope);
        }

        public HashSet<long> GetAllBlacklistSeasons()
        {
            var blacklistShowIds = Plugin.Instance.IntroSkipStore.GetOptions()
                .FingerprintBlacklistShows.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => long.TryParse(part.Trim(), out var id) ? id : (long?)null)
                .Where(id => id.HasValue)
                .Select(id => id.Value)
                .ToArray();

            if (!blacklistShowIds.Any()) return new HashSet<long>();

            var items = _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = blacklistShowIds });

            var seasons = items.OfType<Season>().Select(s => s.InternalId).ToList();
            seasons.AddRange(items.OfType<Series>()
                .SelectMany(series => _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { nameof(Season) },
                        ParentWithPresentationUniqueKeyFromItemId = series.InternalId
                    })
                    .OfType<Season>()
                    .Select(s => s.InternalId)));

            return new HashSet<long>(seasons);
        }

        public long[] GetAllFavoriteSeasons()
        {
            var favorites = LibraryApi.AllUsers.Select(e => e.Key)
                .SelectMany(u => _libraryManager.GetItemList(new InternalItemsQuery
                {
                    User = u,
                    IsFavorite = true,
                    IncludeItemTypes = new[] { nameof(Series), nameof(Episode) },
                    PathStartsWithAny = LibraryPathsInScope.ToArray()
                }))
                .GroupBy(i => i.InternalId)
                .Select(g => g.First())
                .ToList();

            var expanded = Plugin.LibraryApi.ExpandFavorites(favorites, false, null, false).OfType<Episode>();

            var result = expanded.GroupBy(e => e.ParentId).Select(g => g.Key).ToArray();

            var blackListSeasons = GetAllBlacklistSeasons();
            result = result.Where(s => !blackListSeasons.Contains(s)).ToArray();

            return result;
        }

        public List<Episode> FetchFingerprintQueueItems(List<BaseItem> items)
        {
            var libraryIds = Plugin.Instance.IntroSkipStore.GetOptions().MarkerEnabledLibraryScope?
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();

            var includeFavorites = libraryIds?.Contains("-1") == true;

            var resultItems = new List<Episode>();
            var incomingItems = items.OfType<Episode>().ToList();

            if (IsCatchupTaskSelected(GeneralOptions.CatchupTask.Fingerprint) && LibraryPathsInScope.Any())
            {
                if (includeFavorites)
                {
                    resultItems = Plugin.LibraryApi.ExpandFavorites(items, true, null, false).OfType<Episode>()
                        .ToList();
                }

                if (libraryIds is null || !libraryIds.Any() || libraryIds.Any(id => id != "-1"))
                {
                    var filteredItems = incomingItems
                        .Where(i => LibraryPathsInScope.Any(p => i.ContainingFolderPath.StartsWith(p)))
                        .ToList();
                    resultItems = resultItems.Concat(filteredItems).ToList();
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            resultItems = resultItems.Where(i => isModSupported || !i.IsShortcut).GroupBy(i => i.InternalId)
                .Select(g => g.First()).ToList();

            var blackListSeasons = GetAllBlacklistSeasons();
            resultItems = resultItems.Where(e => !blackListSeasons.Contains(e.ParentId)).ToList();

            var unprocessedItems = FilterUnprocessed(resultItems);

            return unprocessedItems;
        }

        private List<Episode> FilterUnprocessed(List<Episode> items)
        {
            var enableImageCapture = Plugin.Instance.MediaInfoExtractStore.GetOptions().EnableImageCapture;

            var results = new List<Episode>();

            foreach (var item in items)
            {
                if (Plugin.LibraryApi.IsExtractNeeded(item, enableImageCapture))
                {
                    results.Add(item);
                }
                else if (IsExtractNeeded(item))
                {
                    results.Add(item);
                }
            }

            _logger.Info("IntroFingerprintExtract - Number of items: " + results.Count);

            return results;
        }

        public bool IsExtractNeeded(BaseItem item)
        {
            return !Plugin.ChapterApi.HasIntro(item) &&
                   string.IsNullOrEmpty(BaseItem.ItemRepository.GetIntroDetectionFailureResult(item.InternalId));
        }

        public List<Episode> FetchIntroPreExtractTaskItems()
        {
            var markerEnabledLibraryScope = Plugin.Instance.IntroSkipStore.GetOptions().MarkerEnabledLibraryScope;

            var itemsFingerprintQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                HasPath = true,
                HasAudioStream = false,
            };

            if (!string.IsNullOrEmpty(markerEnabledLibraryScope) && markerEnabledLibraryScope.Contains("-1"))
            {
                itemsFingerprintQuery.ParentIds = GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
            }
            else
            {
                if (LibraryPathsInScope.Any())
                {
                    itemsFingerprintQuery.PathStartsWithAny = LibraryPathsInScope.ToArray();
                }

                var blackListSeasons = GetAllBlacklistSeasons();
                if (blackListSeasons.Any())
                {
                    itemsFingerprintQuery.ExcludeParentIds = blackListSeasons.ToArray();
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            var items = _libraryManager.GetItemList(itemsFingerprintQuery).Where(i => isModSupported || !i.IsShortcut)
                .OfType<Episode>().ToList();

            return items;
        }

        public List<Episode> FetchIntroFingerprintTaskItems()
        {
            var markerEnabledLibraryScope = Plugin.Instance.IntroSkipStore.GetOptions().MarkerEnabledLibraryScope;
            var introDetectionFingerprintMinutes =
                Plugin.Instance.IntroSkipStore.GetOptions().IntroDetectionFingerprintMinutes;

            var itemsFingerprintQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                WithoutChapterMarkers = new[] { MarkerType.IntroStart },
                MinRunTimeTicks = TimeSpan.FromMinutes(introDetectionFingerprintMinutes).Ticks,
                HasIntroDetectionFailure = false,
                HasAudioStream = true
            };

            if (!string.IsNullOrEmpty(markerEnabledLibraryScope) && markerEnabledLibraryScope.Contains("-1"))
            {
                itemsFingerprintQuery.ParentIds = GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
            }
            else
            {
                if (LibraryPathsInScope.Any())
                {
                    itemsFingerprintQuery.PathStartsWithAny = LibraryPathsInScope.ToArray();
                }

                var blackListSeasons = GetAllBlacklistSeasons();
                if (blackListSeasons.Any())
                {
                    itemsFingerprintQuery.ExcludeParentIds = blackListSeasons.ToArray();
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            var items = _libraryManager.GetItemList(itemsFingerprintQuery).Where(i => isModSupported || !i.IsShortcut)
                .OfType<Episode>().ToList();

            return items;
        }

        public void UpdateLibraryIntroDetectionFingerprintLength(int currentLength)
        {
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null)
                .ToList();

            foreach (var library in libraries)
            {
                var options = library.LibraryOptions;

                if (options.IntroDetectionFingerprintLength != currentLength &&
                    long.TryParse(library.ItemId, out var itemId))
                {
                    options.IntroDetectionFingerprintLength = currentLength;
                    CollectionFolder.SaveLibraryOptions(itemId, options);
                }
            }
        }

        public void UpdateLibraryIntroDetectionFingerprintLength()
        {
            UpdateLibraryIntroDetectionFingerprintLength(Plugin.Instance.IntroSkipStore.GetOptions()
                .IntroDetectionFingerprintMinutes);
        }

        public async Task UpdateIntroMarkerForSeason(Season season, CancellationToken cancellationToken)
        {
            var introDetectionFingerprintMinutes =
                Plugin.Instance.IntroSkipStore.GetOptions().IntroDetectionFingerprintMinutes;

            var libraryOptions = _libraryManager.GetLibraryOptions(season);
            var directoryService = new DirectoryService(_logger, _fileSystem);

            var episodeQuery = new InternalItemsQuery
            {
                GroupByPresentationUniqueKey = false,
                EnableTotalRecordCount = false,
                MinRunTimeTicks = TimeSpan.FromMinutes(introDetectionFingerprintMinutes).Ticks,
                HasIntroDetectionFailure = false,
                HasAudioStream = true
            };
            var allEpisodes = season.GetEpisodes(episodeQuery).Items.OfType<Episode>().ToArray();

            episodeQuery.WithoutChapterMarkers = new[] { MarkerType.IntroStart };
            var episodesWithoutMarkers = season.GetEpisodes(episodeQuery).Items.OfType<Episode>().ToArray();

            var seasonFingerprintInfo = await GetAllFingerprintFilesForSeason(season,
                allEpisodes, libraryOptions, directoryService, cancellationToken).ConfigureAwait(false);

            foreach (var episode in episodesWithoutMarkers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateSequencesForSeason(season, seasonFingerprintInfo, episode, libraryOptions, directoryService);
            }
        }
    }
}
