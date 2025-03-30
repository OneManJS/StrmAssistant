using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.CommonUtility;
using static StrmAssistant.Options.ExperienceEnhanceOptions;

namespace StrmAssistant.ScheduledTask
{
    public class TriggerMergeMovieTask : ILibraryPostScanTask
    {
        private readonly MergeMultiVersionTask _task;
        
        public TriggerMergeMovieTask(MergeMultiVersionTask task) => _task = task;

        public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            return Plugin.Instance.ExperienceEnhanceStore.GetOptions().MergeMultiVersion
                ? _task.Execute(cancellationToken, progress)
                : Task.CompletedTask;
        }
    }

    public class MergeMultiVersionTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;

        private static readonly HashSet<string> CheckKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "tmdb", "imdb", "tvdb" };

        public static readonly AsyncLocal<CollectionFolder> CurrentScanLibrary = new AsyncLocal<CollectionFolder>();

        public MergeMultiVersionTask(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("MergeMultiVersion - Scheduled Task Execute");

            var currentScanLibrary = CurrentScanLibrary.Value;
            CurrentScanLibrary.Value = null;

            double cumulativeProgress = 0;

            var seriesLibraryGroups = PrepareMergeSeries();
            var movieLibraryGroups = PrepareMergeMovies(currentScanLibrary);

            var processSeries = seriesLibraryGroups.Any() && (currentScanLibrary is null ||
                                                              currentScanLibrary.CollectionType == CollectionType.TvShows.ToString() ||
                                                              currentScanLibrary.CollectionType is null);
            var processMovies = movieLibraryGroups.Any() && (currentScanLibrary is null ||
                                                             currentScanLibrary.CollectionType == CollectionType.Movies.ToString() ||
                                                             currentScanLibrary.CollectionType is null);

            if (processSeries)
            {
                var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
                {
                    EnableRemoteContentProbe = false,
                    ReplaceAllMetadata = false,
                    EnableThumbnailImageExtraction = false,
                    EnableSubtitleDownloading = false,
                    ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                    MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ReplaceAllImages = false
                };

                Traverse.Create(refreshOptions).Property("Recursive").SetValue(true);

                var multiply = processMovies ? 1 : 2;

                var alternativeSeries = FindDuplicateSeries(seriesLibraryGroups);
                progress.Report(cumulativeProgress += 5.0 * multiply);

                if (alternativeSeries.Any())
                {
                    var seriesProgressWeight = 35.0 * multiply / alternativeSeries.Count;

                    foreach (var series in alternativeSeries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (currentScanLibrary is null || _libraryManager.GetCollectionFolders(series)
                                .Any(c => c.InternalId != currentScanLibrary.InternalId))
                        {
                            await _providerManager.RefreshFullItem(series, refreshOptions, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        cumulativeProgress += seriesProgressWeight;
                        progress.Report(cumulativeProgress);

                        _logger.Info($"MergeMultiVersion - Series merged: {series.Name} - {series.Path}");
                    }
                }
                else
                {
                    cumulativeProgress += 35.0 * multiply;
                    progress.Report(cumulativeProgress);
                }

                var inconsistentSeries = FindInconsistentSeries(seriesLibraryGroups);
                progress.Report(cumulativeProgress += 5.0 * multiply);

                if (inconsistentSeries.Any())
                {
                    var seriesProgressWeight = 5.0 * multiply / inconsistentSeries.Count;

                    foreach (var series in inconsistentSeries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        await _providerManager.RefreshFullItem(series, refreshOptions, cancellationToken)
                            .ConfigureAwait(false);

                        cumulativeProgress += seriesProgressWeight;
                        progress.Report(cumulativeProgress);

                        _logger.Info($"MergeMultiVersion - Series merged: {series.Name} - {series.Path}");
                    }
                }
                else
                {
                    cumulativeProgress += 5.0 * multiply;
                    progress.Report(cumulativeProgress);
                }
            }

            if (processMovies)
            {
                var totalGroups = movieLibraryGroups.Length;
                var groupProgressWeight = processSeries ? 50.0 / totalGroups : 100.0 / totalGroups;

                foreach (var group in movieLibraryGroups)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var groupProgress = new Progress<double>(p =>
                    {
                        cumulativeProgress += p * groupProgressWeight / 100;
                        progress.Report(cumulativeProgress);
                    });

                    ExecuteMergeMovies(group, groupProgress);
                }
            }

            progress.Report(100);
            _logger.Info("MergeMultiVersion - Scheduled Task Complete");
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "MergeMultiVersionTask";

        public string Description => Resources.ResourceManager.GetString(
            "MergeMovieTask_Description_Merge_movies_per_library_or_across_libraries_per_preference",
            Plugin.Instance.DefaultUICulture);

        public string Name => "Merge Multi Versions";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public bool IsHidden => false;

        public bool IsEnabled => true;
        
        public bool IsLogged => true;

        private long[] PrepareMergeSeries()
        {
            var mergeSeriesPreference = Plugin.Instance.ExperienceEnhanceStore.GetOptions().MergeSeriesPreference;
            _logger.Info("MergeMultiVersion - Merge Series Preference: " + mergeSeriesPreference);

            if (!Plugin.Instance.IsModSupported || mergeSeriesPreference != MergeSeriesScopeOption.GlobalScope)
                return Array.Empty<long>();

            var libraries = Plugin.LibraryApi.GetSeriesLibraries()
                .Where(l => l.GetLibraryOptions().EnableAutomaticSeriesGrouping)
                .ToList();

            if (!libraries.Any()) return Array.Empty<long>();

            _logger.Info("MergeMultiVersion - Series Libraries: " + string.Join(", ", libraries.Select(l => l.Name)));

            var libraryGroups = libraries.Select(l => l.InternalId).ToArray();

            return libraryGroups;
        }

        private List<BaseItem> FindDuplicateSeries(long[] parents)
        {
            var allSeries = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    ParentIds = parents,
                    IncludeItemTypes = new[] { nameof(Series) },
                    HasAnyProviderId = new[]
                    {
                        MetadataProviders.Tmdb.ToString(), MetadataProviders.Imdb.ToString(),
                        MetadataProviders.Tvdb.ToString()
                    }
                })
                .ToList();

            var dupSeries = allSeries
                .SelectMany(item =>
                    item.ProviderIds.Where(kvp => CheckKeys.Contains(kvp.Key))
                        .Select(kvp => new { kvp.Key, kvp.Value, item }))
                .GroupBy(x => new { x.Key, x.Value })
                .Where(g =>
                {
                    var uniqueKeys = new HashSet<string>();
                    foreach (var x in g)
                    {
                        uniqueKeys.Add(x.item.PresentationUniqueKey);
                        if (uniqueKeys.Count > 1) return true;
                    }

                    return false;
                })
                .SelectMany(g => g.Select(x => x.item))
                .GroupBy(item => item.InternalId)
                .Select(g => g.First())
                .ToList();

            return dupSeries;
        }

        private List<Series> FindInconsistentSeries(long[] parents)
        {
            var allSeries = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    ParentIds = parents,
                    IncludeItemTypes = new[] { nameof(Season), nameof(Episode) },
                    GroupBySeriesPresentationUniqueKey = true,
                    HasAnyProviderId = new[]
                    {
                        MetadataProviders.Tmdb.ToString(), MetadataProviders.Imdb.ToString(),
                        MetadataProviders.Tvdb.ToString()
                    }
                })
                .ToList();

            var inconsistentSeries = allSeries
                .Select(item => new
                {
                    Series = (item as Season)?.Series ?? (item as Episode)?.Series, item.SeriesPresentationUniqueKey
                })
                .Where(x => x.Series != null && x.SeriesPresentationUniqueKey != x.Series.PresentationUniqueKey)
                .GroupBy(x => x.Series.InternalId)
                .Select(g => g.First().Series)
                .ToList();

            allSeries.Clear();
            allSeries.TrimExcess();

            return inconsistentSeries;
        }

        private long[][] PrepareMergeMovies(CollectionFolder currentScanLibrary)
        {
            var mergeMoviesPreference = Plugin.Instance.ExperienceEnhanceStore.GetOptions().MergeMoviesPreference;
            _logger.Info("MergeMultiVersion - Merge Movies Preference: " + mergeMoviesPreference);

            var libraryGroups = Array.Empty<long[]>();

            if (mergeMoviesPreference == MergeMoviesScopeOption.FolderScope) return libraryGroups;

            if (mergeMoviesPreference == MergeMoviesScopeOption.LibraryScope && currentScanLibrary != null)
            {
                libraryGroups = new[] { new[] { currentScanLibrary.InternalId } };
                _logger.Info("MergeMultiVersion - Movies Libraries: " + currentScanLibrary.Name);
            }
            else
            {
                var libraries = Plugin.LibraryApi.GetMovieLibraries();

                if (!libraries.Any()) return libraryGroups;

                _logger.Info(
                    "MergeMultiVersion - Movies Libraries: " + string.Join(", ", libraries.Select(l => l.Name)));

                var libraryIds = libraries.Select(l => l.InternalId).ToArray();
                libraryGroups = mergeMoviesPreference == MergeMoviesScopeOption.GlobalScope
                    ? new[] { libraryIds }
                    : libraryIds.Select(library => new[] { library }).ToArray();
            }

            return libraryGroups;
        }

        private void ExecuteMergeMovies(long[] parents, IProgress<double> groupProgress = null)
        {
            var allMovies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                ParentIds = parents,
                IncludeItemTypes = new[] { nameof(Movie) },
                HasAnyProviderId = new[]
                {
                    MetadataProviders.Tmdb.ToString(),
                    MetadataProviders.Imdb.ToString(),
                    MetadataProviders.Tvdb.ToString()
                }
            }).Cast<Movie>().ToList();

            var dupMovies = allMovies
                .SelectMany(item =>
                    item.ProviderIds.Where(kvp => CheckKeys.Contains(kvp.Key))
                        .Select(kvp => new { kvp.Key, kvp.Value, item }))
                .GroupBy(kvp => new { kvp.Key, kvp.Value })
                .Where(g =>
                {
                    var groupItems = g.Select(kvp => kvp.item).ToList();

                    var altVersionCount = g.Sum(kvp =>
                        kvp.item.GetAlternateVersionIds().Count(id => groupItems.Any(i => i.InternalId == id)));

                    return g.Count() != 1 + altVersionCount / g.Count();
                })
                .ToList();
            allMovies.Clear();
            allMovies.TrimExcess();

            if (dupMovies.Count > 0)
            {
                var parentMap = new Dictionary<long, long>(dupMovies.Count);

                foreach (var group in dupMovies)
                {
                    long rootId = -1;

                    foreach (var kvp in group)
                    {
                        var movie = kvp.item;

                        if (!parentMap.ContainsKey(movie.InternalId))
                        {
                            parentMap[movie.InternalId] = movie.InternalId;
                        }

                        if (rootId == -1)
                            rootId = movie.InternalId;
                        else
                            Union(rootId, movie.InternalId, parentMap);
                    }
                }

                var rootIdGroups = parentMap.Values.GroupBy(id => Find(id, parentMap)).ToList();

                var movieLookup = dupMovies.SelectMany(g => g)
                    .GroupBy(kvp => Find(kvp.item.InternalId, parentMap))
                    .ToDictionary(d => d.Key,
                        d => d.GroupBy(kvp => kvp.item.InternalId).Select(g => g.First().item).ToList());

                var total = rootIdGroups.Count;
                var current = 0;

                foreach (var group in rootIdGroups)
                {
                    var movies = group
                        .SelectMany(
                            rootId => movieLookup.TryGetValue(rootId, out var m) ? m : Enumerable.Empty<Movie>())
                        .GroupBy(m => m.InternalId)
                        .Select(g => g.First())
                        .OfType<BaseItem>()
                        .ToArray();

                    _libraryManager.MergeItems(movies);

                    foreach (var item in movies)
                    {
                        _logger.Info($"MergeMultiVersion - Movie merged: {item.Name} - {item.Path}");
                    }

                    current++;
                    _logger.Info($"MergeMultiVersion - Merged group {current} of {total} with {movies.Length} items");

                    var progress = (double)current / total * 100;
                    groupProgress?.Report(progress);
                }

                groupProgress?.Report(100);
            }
        }
    }
}
