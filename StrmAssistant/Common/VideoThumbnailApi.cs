using HarmonyLib;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using StrmAssistant.Mod;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class VideoThumbnailApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;

        private static readonly PatchTracker PatchTracker = new PatchTracker(typeof(VideoThumbnailApi),
            Plugin.Instance.IsModSupported ? PatchApproach.Harmony : PatchApproach.Reflection);

        private readonly object _thumbnailGenerator;
        private readonly MethodInfo _refreshThumbnailImages;

        private static readonly Version AppVer = Plugin.Instance.ApplicationHost.ApplicationVersion;
        private static readonly Version Ver4925 = new Version("4.9.0.25");

        public VideoThumbnailApi(ILibraryManager libraryManager, IFileSystem fileSystem,
            IImageExtractionManager imageExtractionManager, IItemRepository itemRepository,
            IMediaMountManager mediaMountManager, IServerApplicationPaths applicationPaths,
            ILibraryMonitor libraryMonitor, IFfmpegManager ffmpegManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var thumbnailGenerator = embyProviders.GetType("Emby.Providers.MediaInfo.ThumbnailGenerator");
                var thumbnailGeneratorConstructor = thumbnailGenerator.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[]
                    {
                        typeof(IFileSystem), typeof(ILogger), typeof(IImageExtractionManager),
                        typeof(IItemRepository), typeof(IMediaMountManager), typeof(IServerApplicationPaths),
                        typeof(ILibraryMonitor), typeof(IFfmpegManager)
                    }, null);
                _thumbnailGenerator = thumbnailGeneratorConstructor?.Invoke(new object[]
                {
                    fileSystem, _logger, imageExtractionManager, itemRepository, mediaMountManager,
                    applicationPaths, libraryMonitor, ffmpegManager
                });
                _refreshThumbnailImages = thumbnailGenerator.GetMethod("RefreshThumbnailImages",
                    BindingFlags.Public | BindingFlags.Instance);
            }
            catch (Exception e)
            {
                _logger.Debug(e.Message);
                _logger.Debug(e.StackTrace);
            }

            if (_thumbnailGenerator is null || _refreshThumbnailImages is null)
            {
                _logger.Warn($"{PatchTracker.PatchType.Name} Init Failed");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
            }
            else if (Plugin.Instance.IsModSupported)
            {
                PatchManager.ReversePatch(PatchTracker, _refreshThumbnailImages,
                    AppVer >= Ver4925 ? nameof(RefreshThumbnailImagesStub49) : nameof(RefreshThumbnailImagesStub48));
            }
        }

#pragma warning disable CS1998
        [HarmonyReversePatch]
        private static async Task<bool> RefreshThumbnailImagesStub49(object instance, Video item,
            MediaSourceInfo mediaSource, MediaStream videoStream, LibraryOptions libraryOptions,
            IDirectoryService directoryService, List<ChapterInfo> chapters, bool extractImages, bool saveChapters,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        [HarmonyReversePatch]
        private static async Task<bool> RefreshThumbnailImagesStub48(object instance, Video item, MediaStream videoStream,
            LibraryOptions libraryOptions, IDirectoryService directoryService, List<ChapterInfo> chapters,
            bool extractImages, bool saveChapters, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
#pragma warning restore CS1998

        public Task<bool> RefreshThumbnailImages(Video item, LibraryOptions libraryOptions,
            IDirectoryService directoryService, List<ChapterInfo> chapters, bool extractImages, bool saveChapters,
            CancellationToken cancellationToken)
        {
            var mediaSource = AppVer >= Ver4925
                ? item.GetMediaSources(false, false, libraryOptions).FirstOrDefault()
                : null;

            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    return AppVer >= Ver4925
                        ? RefreshThumbnailImagesStub49(_thumbnailGenerator, item, mediaSource, null, libraryOptions,
                            directoryService, chapters, extractImages, saveChapters, cancellationToken)
                        : RefreshThumbnailImagesStub48(_thumbnailGenerator, item, null, libraryOptions,
                            directoryService, chapters, extractImages, saveChapters, cancellationToken);
                case PatchApproach.Reflection:
                {
                    var parameters = AppVer >= Ver4925
                        ? new object[]
                        {
                            item, mediaSource, null, libraryOptions, directoryService, chapters, extractImages,
                            saveChapters, cancellationToken
                        }
                        : new object[]
                        {
                            item, null, libraryOptions, directoryService, chapters, extractImages, saveChapters,
                            cancellationToken
                        };

                    return (Task<bool>)_refreshThumbnailImages.Invoke(_thumbnailGenerator, parameters);
                }
                default:
                    throw new NotImplementedException();
            }
        }

        public List<Video> FetchExtractTaskItems()
        {
            var libraryIds = Plugin.Instance.MediaInfoExtractStore.GetOptions().LibraryScope
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => (!libraryIds.Any() || libraryIds.Contains(f.Id)) &&
                            f.LibraryOptions.EnableChapterImageExtraction).ToList();

            _logger.Info("VideoThumbnailExtract - LibraryScope: " +
                         (libraryIds.Any() ? string.Join(", ", libraries.Select(l => l.Name)) : "ALL"));

            var includeExtra = Plugin.Instance.MediaInfoExtractStore.GetOptions().IncludeExtra;
            _logger.Info("Include Extra: " + includeExtra);

            var libraryPathsInScope = libraries.SelectMany(l => l.Locations).Select(ls =>
                ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? ls
                    : ls + Path.DirectorySeparatorChar).ToArray();

            var favoritesWithExtra = Array.Empty<BaseItem>();

            if (libraryIds.Contains("-1") && libraryPathsInScope.Any())
            {
                var favorites = LibraryApi.AllUsers.Select(e => e.Key)
                    .SelectMany(user => _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        User = user,
                        IsFavorite = true,
                        PathStartsWithAny = libraryPathsInScope
                    })).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();

                var expanded = Plugin.LibraryApi.ExpandFavorites(favorites, false, false, false);

                favoritesWithExtra = expanded
                    .Concat(includeExtra
                        ? expanded.SelectMany(f => f.GetExtras(LibraryApi.IncludeExtraTypes))
                        : Enumerable.Empty<BaseItem>())
                    .Where(i => Plugin.LibraryApi.HasMediaInfo(i) && !i.HasImage(ImageType.Chapter))
                    .ToArray();
            }

            var items = Array.Empty<BaseItem>();
            var extras = Array.Empty<BaseItem>();

            if (libraryIds.Any(id => id != "-1") && libraryPathsInScope.Any())
            {
                var videoThumbnailQuery = new InternalItemsQuery
                {
                    MediaTypes = new[] { MediaType.Video },
                    HasAudioStream = true,
                    HasChapterImages = false,
                    PathStartsWithAny = libraryPathsInScope
                };

                items = _libraryManager.GetItemList(videoThumbnailQuery);

                if (includeExtra)
                {
                    videoThumbnailQuery.ExtraTypes = LibraryApi.IncludeExtraTypes;
                    extras = _libraryManager.GetItemList(videoThumbnailQuery);
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            var combined = favoritesWithExtra.Concat(items).Concat(extras).GroupBy(i => i.InternalId)
                .Select(g => g.First()).Where(i => isModSupported || !i.IsShortcut).OfType<Video>().ToList();

            return combined;
        }
    }
}
