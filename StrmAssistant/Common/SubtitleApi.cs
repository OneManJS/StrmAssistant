using Emby.Naming.Common;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
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
    public class SubtitleApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;

        private static readonly PatchTracker PatchTracker =
            new PatchTracker(typeof(SubtitleApi),
                Plugin.Instance.IsModSupported ? PatchApproach.Harmony : PatchApproach.Reflection);
        private readonly object _subtitleResolver;
        private readonly MethodInfo _getExternalSubtitleFiles;
        private readonly MethodInfo _getExternalSubtitleStreams;
        private readonly object _ffProbeSubtitleInfo;
        private readonly MethodInfo _updateExternalSubtitleStream;

        private static readonly HashSet<string> ProbeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".sub", ".smi", ".sami", ".mpl" };

        public SubtitleApi(ILibraryManager libraryManager, IFileSystem fileSystem, IMediaProbeManager mediaProbeManager,
            ILocalizationManager localizationManager, IItemRepository itemRepository)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var subtitleResolverType = embyProviders.GetType("Emby.Providers.MediaInfo.SubtitleResolver");
                var subtitleResolverConstructor = subtitleResolverType.GetConstructor(new[]
                {
                    typeof(ILocalizationManager), typeof(IFileSystem), typeof(ILibraryManager)
                });
                _subtitleResolver = subtitleResolverConstructor?.Invoke(new object[]
                {
                    localizationManager, fileSystem, libraryManager
                });
                _getExternalSubtitleFiles = subtitleResolverType.GetMethod("GetExternalSubtitleFiles");
                _getExternalSubtitleStreams = subtitleResolverType.GetMethod("GetExternalSubtitleStreams");

                var ffProbeSubtitleInfoType = embyProviders.GetType("Emby.Providers.MediaInfo.FFProbeSubtitleInfo");
                var ffProbeSubtitleInfoConstructor = ffProbeSubtitleInfoType.GetConstructor(new[]
                {
                    typeof(IMediaProbeManager)
                });
                _ffProbeSubtitleInfo = ffProbeSubtitleInfoConstructor?.Invoke(new object[] { mediaProbeManager });
                _updateExternalSubtitleStream = ffProbeSubtitleInfoType.GetMethod("UpdateExternalSubtitleStream");
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    _logger.Debug(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            if (_subtitleResolver is null || _getExternalSubtitleFiles is null || _getExternalSubtitleStreams is null ||
                _ffProbeSubtitleInfo is null || _updateExternalSubtitleStream is null)
            {
                _logger.Warn($"{PatchTracker.PatchType.Name} Init Failed");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
            }
            else if (Plugin.Instance.IsModSupported)
            {
                PatchManager.ReversePatch(PatchTracker, _getExternalSubtitleFiles,
                    nameof(GetExternalSubtitleFilesStub));
                PatchManager.ReversePatch(PatchTracker, _getExternalSubtitleStreams,
                    nameof(GetExternalSubtitleStreamsStub));
                PatchManager.ReversePatch(PatchTracker, _updateExternalSubtitleStream,
                    nameof(UpdateExternalSubtitleStreamStub));
            }
        }

        [HarmonyReversePatch]
        private static List<string> GetExternalSubtitleFilesStub(object instance, BaseItem item,
            IDirectoryService directoryService, NamingOptions namingOptions, bool clearCache) =>
            throw new NotImplementedException();

        private List<string> GetExternalSubtitleFiles(BaseItem item, IDirectoryService directoryService,
            bool clearCache)
        {
            var namingOptions = _libraryManager.GetNamingOptions();

            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    return GetExternalSubtitleFilesStub(_subtitleResolver, item, directoryService, namingOptions,
                        clearCache);
                case PatchApproach.Reflection:
                    return (List<string>)_getExternalSubtitleFiles.Invoke(_subtitleResolver,
                        new object[] { item, directoryService, namingOptions, clearCache });
                default:
                    throw new NotImplementedException();
            }
        }

        [HarmonyReversePatch]
        private static List<MediaStream> GetExternalSubtitleStreamsStub(object instance, BaseItem item, int startIndex,
            IDirectoryService directoryService, NamingOptions namingOptions, bool clearCache) =>
            throw new NotImplementedException();

        private List<MediaStream> GetExternalSubtitleStreams(BaseItem item, int startIndex,
            IDirectoryService directoryService, bool clearCache)
        {
            var namingOptions = _libraryManager.GetNamingOptions();

            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    return GetExternalSubtitleStreamsStub(_subtitleResolver, item, startIndex, directoryService,
                        namingOptions, clearCache);
                case PatchApproach.Reflection:
                    return (List<MediaStream>)_getExternalSubtitleStreams.Invoke(_subtitleResolver,
                        new object[] { item, startIndex, directoryService, namingOptions, clearCache });
                default:
                    throw new NotImplementedException();
            }
        }

#pragma warning disable CS1998
        [HarmonyReversePatch]
        private static async Task<bool> UpdateExternalSubtitleStreamStub(object instance, BaseItem item,
            MediaStream subtitleStream, MetadataRefreshOptions options, LibraryOptions libraryOptions,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
#pragma warning restore CS1998

        private Task<bool> UpdateExternalSubtitleStream(BaseItem item, MediaStream subtitleStream,
            MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    return UpdateExternalSubtitleStreamStub(_ffProbeSubtitleInfo, item, subtitleStream, options,
                        libraryOptions, cancellationToken);
                case PatchApproach.Reflection:
                    return (Task<bool>)_updateExternalSubtitleStream.Invoke(_ffProbeSubtitleInfo,
                        new object[] { item, subtitleStream, options, libraryOptions, cancellationToken });
                default:
                    throw new NotImplementedException();
            }
        }

        public bool HasExternalSubtitleChanged(BaseItem item, IDirectoryService directoryService, bool clearCache)
        {
            var currentExternalSubtitleFiles = _libraryManager.GetExternalSubtitleFiles(item.InternalId);

            try
            {
                return GetExternalSubtitleFiles(item, directoryService, clearCache) is
                           { } newExternalSubtitleFiles &&
                       !currentExternalSubtitleFiles.SequenceEqual(newExternalSubtitleFiles, StringComparer.Ordinal);
            }
            catch
            {
                // ignored
            }

            return false;
        }

        public async Task UpdateExternalSubtitles(BaseItem item, MetadataRefreshOptions refreshOptions, bool clearCache,
            bool persistMediaInfo)
        {
            var directoryService = refreshOptions.DirectoryService;
            var currentStreams = item.GetMediaStreams()
                .FindAll(i =>
                    !(i.IsExternal && i.Type == MediaStreamType.Subtitle && i.Protocol == MediaProtocol.File));
            var startIndex = currentStreams.Count == 0 ? 0 : currentStreams.Max(i => i.Index) + 1;

            if (GetExternalSubtitleStreams(item, startIndex, directoryService, clearCache) is
                { } externalSubtitleStreams)
            {
                foreach (var subtitleStream in externalSubtitleStreams)
                {
                    var extension = Path.GetExtension(subtitleStream.Path);
                    if (!string.IsNullOrEmpty(extension) && ProbeExtensions.Contains(extension))
                    {
                        var result =
                            await UpdateExternalSubtitleStream(item, subtitleStream, refreshOptions,
                                CancellationToken.None).ConfigureAwait(false);

                        if (!result)
                            _logger.Warn("No result when probing external subtitle file: {0}", subtitleStream.Path);
                    }

                    _logger.Info("ExternalSubtitle - Subtitle Processed: " + subtitleStream.Path);
                }

                currentStreams.AddRange(externalSubtitleStreams);
                _itemRepository.SaveMediaStreams(item.InternalId, currentStreams, CancellationToken.None);

                if (persistMediaInfo && Plugin.LibraryApi.IsLibraryInScope(item))
                {
                    _ = Plugin.MediaInfoApi.SerializeMediaInfo(item.InternalId, directoryService, true,
                        "External Subtitle Update").ConfigureAwait(false);
                }
            }
        }
    }
}
