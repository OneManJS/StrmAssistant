using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class PreferOriginalPoster : PatchBase<PreferOriginalPoster>
    {
        internal class ContextItem
        {
            public string TmdbId { get; set; }
            public string ImdbId { get; set; }
            public string TvdbId { get; set; }
            public string OriginalLanguage { get; set; }
        }

        private static Assembly _movieDbAssembly;
        private static MethodInfo _getMovieInfo;
        private static MethodInfo _ensureSeriesInfo;
        private static MethodInfo _getBackdrops;

        private static Assembly _tvdbAssembly;
        private static MethodInfo _ensureMovieInfoTvdb;
        private static MethodInfo _ensureSeriesInfoTvdb;

        private static MethodInfo _getAvailableRemoteImages;
        private static MethodInfo _addLocalImage;
        private static MethodInfo _getLocalFiles;
        private static MethodInfo _populateSeasonImagesFromSeasonOrSeriesFolder;

        private static readonly ConcurrentDictionary<string, ContextItem> CurrentItemsByTmdbId =
            new ConcurrentDictionary<string, ContextItem>();
        private static readonly ConcurrentDictionary<string, ContextItem> CurrentItemsByImdbId =
            new ConcurrentDictionary<string, ContextItem>();
        private static readonly ConcurrentDictionary<string, ContextItem> CurrentItemsByTvdbId =
            new ConcurrentDictionary<string, ContextItem>();
        private static readonly ConcurrentDictionary<string, string> BackdropByLanguage =
            new ConcurrentDictionary<string, string>();

        private static readonly AsyncLocal<ContextItem> CurrentLookupItem = new AsyncLocal<ContextItem>();

        private static readonly AsyncLocal<bool> WasCalledByImageProvider = new AsyncLocal<bool>();

        public PreferOriginalPoster()
        {
            Initialize();

            if (Plugin.Instance.MetadataEnhanceStore.GetOptions().PreferOriginalPoster)
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
                var movieDbImageProvider = _movieDbAssembly.GetType("MovieDb.MovieDbImageProvider");
                _getMovieInfo = movieDbImageProvider.GetMethod("GetMovieInfo",
                    BindingFlags.Instance | BindingFlags.NonPublic, null,
                    new[] { typeof(BaseItem), typeof(string), typeof(IJsonSerializer), typeof(CancellationToken) },
                    null);

                var movieDbSeriesProvider = _movieDbAssembly.GetType("MovieDb.MovieDbSeriesProvider");
                _ensureSeriesInfo = movieDbSeriesProvider.GetMethod("EnsureSeriesInfo",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                var movieDbProviderBase = _movieDbAssembly.GetType("MovieDb.MovieDbProviderBase");
                _getBackdrops = movieDbProviderBase.GetMethod("GetBackdrops",
                    BindingFlags.Instance | BindingFlags.NonPublic);
            }
            else
            {
                Plugin.Instance.Logger.Warn("OriginalPoster - MovieDb plugin is not installed");
            }

            _tvdbAssembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Tvdb");

            if (_tvdbAssembly != null)
            {
                var tvdbMovieProvider = _tvdbAssembly.GetType("Tvdb.TvdbMovieProvider");
                _ensureMovieInfoTvdb = tvdbMovieProvider.GetMethod("EnsureMovieInfo",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var tvdbSeriesProvider = _tvdbAssembly.GetType("Tvdb.TvdbSeriesProvider");
                _ensureSeriesInfoTvdb = tvdbSeriesProvider.GetMethod("EnsureSeriesInfo",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            else
            {
                Plugin.Instance.Logger.Warn("OriginalPoster - Tvdb plugin is not installed");
            }

            if (_movieDbAssembly != null || _tvdbAssembly != null)
            {
                var embyProvidersAssembly = Assembly.Load("Emby.Providers");
                var providerManager = embyProvidersAssembly.GetType("Emby.Providers.Manager.ProviderManager");
                _getAvailableRemoteImages = providerManager.GetMethod("GetAvailableRemoteImages",
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new[]
                    {
                        typeof(BaseItem), typeof(LibraryOptions), typeof(RemoteImageQuery),
                        typeof(IDirectoryService), typeof(CancellationToken)
                    }, null);

                var embyLocalMetadata = Assembly.Load("Emby.LocalMetadata");
                var localImageProvider = embyLocalMetadata.GetType("Emby.LocalMetadata.Images.LocalImageProvider");
                _addLocalImage = localImageProvider.GetMethod("AddImage",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    new[]
                    {
                        typeof(FileSystemMetadata[]), typeof(List<LocalImageInfo>), typeof(string), typeof(ImageType)
                    });
                ReversePatch(PatchTracker, _addLocalImage, nameof(AddLocalImageStub));
                _getLocalFiles = localImageProvider.GetMethod("GetFiles",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    new[] { typeof(BaseItem), typeof(LibraryOptions), typeof(bool), typeof(IDirectoryService) });
                ReversePatch(PatchTracker, _getLocalFiles, nameof(GetLocalFilesStub));
                _populateSeasonImagesFromSeasonOrSeriesFolder = localImageProvider
                    .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name.StartsWith("PopulateSeasonImagesFrom"));
            }
            else
            {
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                PatchTracker.IsSupported = false;
            }
        }

        protected override void Prepare(bool apply)
        {
            if (_movieDbAssembly != null)
            {
                PatchUnpatch(PatchTracker, apply, _getMovieInfo, postfix: nameof(GetMovieInfoTmdbPostfix));
                PatchUnpatch(PatchTracker, apply, _ensureSeriesInfo, postfix: nameof(EnsureSeriesInfoTmdbPostfix));
                PatchUnpatch(PatchTracker, apply, _getBackdrops, postfix: nameof(GetBackdropsPostfix));
            }

            if (_tvdbAssembly != null)
            {
                PatchUnpatch(PatchTracker, apply, _ensureMovieInfoTvdb, postfix: nameof(EnsureMovieInfoTvdbPostfix));
                PatchUnpatch(PatchTracker, apply, _ensureSeriesInfoTvdb, postfix: nameof(EnsureSeriesInfoTvdbPostfix));
            }

            PatchUnpatch(PatchTracker, apply, _getAvailableRemoteImages,
                prefix: nameof(GetAvailableRemoteImagesPrefix), postfix: nameof(GetAvailableRemoteImagesPostfix));
            PatchUnpatch(PatchTracker, apply, _populateSeasonImagesFromSeasonOrSeriesFolder,
                postfix: nameof(PopulateSeasonImagesFromSeasonOrSeriesFolderPostfix));
        }

        private static void AddContextItem(string tmdbId, string imdbId, string tvdbId)
        {
            if (tmdbId == null && imdbId == null && tvdbId == null) return;

            var item = new ContextItem { TmdbId = tmdbId, ImdbId = imdbId, TvdbId = tvdbId };

            if (tmdbId != null) CurrentItemsByTmdbId[tmdbId] = item;

            if (imdbId != null) CurrentItemsByImdbId[imdbId] = item;

            if (tvdbId != null) CurrentItemsByTvdbId[tvdbId] = item;

            CurrentLookupItem.Value = new ContextItem { TmdbId = tmdbId, ImdbId = imdbId, TvdbId = tvdbId };
        }

        private static void UpdateOriginalLanguage(string tmdbId, string imdbId, string tvdbId, string originalLanguage)
        {
            ContextItem itemToUpdate = null;

            if (tmdbId != null) CurrentItemsByTmdbId.TryGetValue(tmdbId, out itemToUpdate);

            if (itemToUpdate == null && imdbId != null) CurrentItemsByImdbId.TryGetValue(imdbId, out itemToUpdate);

            if (itemToUpdate == null && tvdbId != null) CurrentItemsByTvdbId.TryGetValue(tvdbId, out itemToUpdate);

            if (itemToUpdate != null) itemToUpdate.OriginalLanguage = originalLanguage;
        }

        private static ContextItem GetAndRemoveItem()
        {
            var lookupItem = CurrentLookupItem.Value;
            CurrentLookupItem.Value = null;

            if (lookupItem == null) return null;

            ContextItem foundItem = null;

            if (lookupItem.TmdbId != null)
            {
                CurrentItemsByTmdbId.TryRemove(lookupItem.TmdbId, out foundItem);
            }

            if (foundItem == null && lookupItem.ImdbId != null)
            {
                CurrentItemsByImdbId.TryRemove(lookupItem.ImdbId, out foundItem);
            }

            if (foundItem == null && lookupItem.TvdbId != null)
            {
                CurrentItemsByTvdbId.TryRemove(lookupItem.TvdbId, out foundItem);
            }

            return foundItem;
        }

        private static string GetOriginalLanguage(BaseItem item)
        {
            var itemLookup = GetAndRemoveItem();

            if (itemLookup != null && !string.IsNullOrEmpty(itemLookup.OriginalLanguage))
                return itemLookup.OriginalLanguage;

            var fallbackItem = item is Movie || item is Series ? item :
                item is Season season ? season.Series :
                item is Episode episode ? episode.Series : null;

            if (fallbackItem != null)
            {
                return LanguageUtility.GetLanguageByTitle(fallbackItem.OriginalTitle);
            }

            if (item is BoxSet collection)
            {
                return Plugin.MetadataApi.GetCollectionOriginalLanguage(collection);
            }

            return null;
        }

        [HarmonyPostfix]
        private static void GetMovieInfoTmdbPostfix(BaseItem item, string language, IJsonSerializer jsonSerializer,
            CancellationToken cancellationToken, Task __result)
        {
            object movieData = null;

            try
            {
                movieData = Traverse.Create(__result).Property("Result").GetValue();
            }
            catch
            {
                // ignored
            }

            if (movieData != null)
            {
                var tmdbId = Traverse.Create(movieData).Property("id").GetValue<int>().ToString();
                var imdbId = Traverse.Create(movieData).Property("imdb_id").GetValue<string>();
                var originalLanguage = Traverse.Create(movieData).Property("original_language").GetValue<string>();

                if ((!string.IsNullOrEmpty(tmdbId) || !string.IsNullOrEmpty(imdbId)) &&
                    !string.IsNullOrEmpty(originalLanguage))
                {
                    UpdateOriginalLanguage(tmdbId, imdbId, null, originalLanguage);
                }
            }
        }

        [HarmonyPostfix]
        private static void EnsureSeriesInfoTmdbPostfix(string tmdbId, string language,
            CancellationToken cancellationToken, Task __result)
        {
            if (WasCalledByMethod(_movieDbAssembly, "FetchImages")) WasCalledByImageProvider.Value = true;

            object seriesInfo = null;

            try
            {
                seriesInfo = Traverse.Create(__result).Property("Result").GetValue();
            }
            catch
            {
                // ignored
            }

            if (!WasCalledByImageProvider.Value) return;

            if (seriesInfo != null)
            {
                var id = Traverse.Create(seriesInfo).Property("id").GetValue<int>().ToString();
                var originalLanguage = Traverse.Create(seriesInfo)
                    .Property("languages")
                    .GetValue<List<string>>()
                    ?.FirstOrDefault();

                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(originalLanguage))
                {
                    UpdateOriginalLanguage(id, null, null, originalLanguage);
                }
            }
        }

        [HarmonyPostfix]
        private static void EnsureMovieInfoTvdbPostfix(string tvdbId, IDirectoryService directoryService,
            CancellationToken cancellationToken, Task __result)
        {
            if (WasCalledByMethod(_tvdbAssembly, "GetImages")) WasCalledByImageProvider.Value = true;

            object movieData = null;

            try
            {
                movieData = Traverse.Create(__result).Property("Result").GetValue();
            }
            catch
            {
                // ignored
            }

            if (!WasCalledByImageProvider.Value) return;

            if (movieData != null)
            {
                var id = Traverse.Create(movieData).Property("id").GetValue<int>().ToString();
                var originalLanguage = Traverse.Create(movieData).Property("originalLanguage").GetValue<string>();

                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(originalLanguage))
                {
                    var convertedLanguage = Plugin.MetadataApi.ConvertToServerLanguage(originalLanguage);
                    UpdateOriginalLanguage(null, null, id, convertedLanguage);
                }
            }
        }

        [HarmonyPostfix]
        private static void EnsureSeriesInfoTvdbPostfix(string tvdbId, IDirectoryService directoryService,
            CancellationToken cancellationToken, Task __result)
        {
            if (WasCalledByMethod(_tvdbAssembly, "GetImages")) WasCalledByImageProvider.Value = true;

            object seriesData = null;

            try
            {
                seriesData = Traverse.Create(__result).Property("Result").GetValue();
            }
            catch
            {
                // ignored
            }

            if (!WasCalledByImageProvider.Value) return;

            if (seriesData != null)
            {
                var id = Traverse.Create(seriesData).Property("id").GetValue<int>().ToString();
                var originalLanguage = Traverse.Create(seriesData).Property("originalLanguage").GetValue<string>();

                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(originalLanguage))
                {
                    var convertedLanguage = Plugin.MetadataApi.ConvertToServerLanguage(originalLanguage);
                    UpdateOriginalLanguage(null, null, id, convertedLanguage);
                }
            }
        }

        [HarmonyPostfix]
        private static void GetBackdropsPostfix(IEnumerable<object> __result)
        {
            if (__result != null)
            {
                foreach (var image in __result)
                {
                    var filePath = Traverse.Create(image).Property("file_path").GetValue<string>();
                    var languageProperty = Traverse.Create(image).Property("iso_639_1");
                    var language = languageProperty.GetValue<string>();

                    if (!string.IsNullOrEmpty(filePath) && !string.IsNullOrEmpty(language))
                    {
                        BackdropByLanguage[filePath] = language;
                        languageProperty.SetValue(null);
                    }
                }
            }
        }

        [HarmonyPrefix]
        private static bool GetAvailableRemoteImagesPrefix(IHasProviderIds item, LibraryOptions libraryOptions,
            ref RemoteImageQuery query, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            query.IncludeAllLanguages = true;

            var tmdbId = item.GetProviderId(MetadataProviders.Tmdb);
            var imdbId = item.GetProviderId(MetadataProviders.Imdb);
            var tvdbId = item.GetProviderId(MetadataProviders.Tvdb);

            AddContextItem(tmdbId, imdbId, tvdbId);

            return true;
        }

        [HarmonyPostfix]
        private static Task<IEnumerable<RemoteImageInfo>> GetAvailableRemoteImagesPostfix(
            Task<IEnumerable<RemoteImageInfo>> __result, BaseItem item, LibraryOptions libraryOptions,
            RemoteImageQuery query, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            IEnumerable<RemoteImageInfo> result = null;

            try
            {
                result = __result.Result;
            }
            catch
            {
                // ignored
            }

            if (result is null) return Task.FromResult(Enumerable.Empty<RemoteImageInfo>());

            var originalLanguage = GetOriginalLanguage(item);
            var libraryPreferredImageLanguage = libraryOptions.PreferredImageLanguage?.Split('-')[0];

            var remoteImages = result.ToList();

            foreach (var image in remoteImages.Where(image => image.Type == ImageType.Backdrop))
            {
                var match = BackdropByLanguage
                    .FirstOrDefault(kvp => image.Url.EndsWith(kvp.Key, StringComparison.Ordinal)).Key;

                if (match != null)
                {
                    image.Language = BackdropByLanguage[match];
                    BackdropByLanguage.TryRemove(match, out _);
                }
            }

            var reorderedImages = remoteImages.OrderBy(i =>
                !string.IsNullOrEmpty(libraryPreferredImageLanguage) && string.Equals(i.Language,
                    libraryPreferredImageLanguage, StringComparison.OrdinalIgnoreCase) ? 0 :
                !string.IsNullOrEmpty(originalLanguage) && string.Equals(i.Language, originalLanguage,
                    StringComparison.OrdinalIgnoreCase) ? 1 : 2);

            return Task.FromResult(reorderedImages.AsEnumerable());
        }

        [HarmonyReversePatch]
        private static FileSystemMetadata[] GetLocalFilesStub(ILocalImageFileProvider instance, BaseItem item,
            LibraryOptions libraryOptions, bool includeDirectories, IDirectoryService directoryService) =>
            throw new NotImplementedException();

        [HarmonyReversePatch]
        private static bool AddLocalImageStub(ILocalImageFileProvider instance, FileSystemMetadata[] files,
            List<LocalImageInfo> images, string name, ImageType type) =>
            throw new NotImplementedException();

        [HarmonyPostfix]
        private static void PopulateSeasonImagesFromSeasonOrSeriesFolderPostfix(ILocalImageFileProvider __instance,
            Season season, LibraryOptions libraryOptions, List<LocalImageInfo> images,
            IDirectoryService directoryService)
        {
            var indexNumber = season.IndexNumber;

            if (indexNumber.HasValue && indexNumber.Value == 0 && images.All(i => i.Type != ImageType.Primary))
            {
                var name = nameof(season) + indexNumber.Value.ToString("00", CultureInfo.InvariantCulture) + "-poster";

                var seriesFolderFiles =
                    GetLocalFilesStub(__instance, season.Series, libraryOptions, false, directoryService);
                var result = AddLocalImageStub(__instance, seriesFolderFiles, images, name, ImageType.Primary);

                if (result) return;

                var seasonFolderFiles = GetLocalFilesStub(__instance, season, libraryOptions, false, directoryService);
                AddLocalImageStub(__instance, seasonFolderFiles, images, name, ImageType.Primary);
            }
        }
    }
}
