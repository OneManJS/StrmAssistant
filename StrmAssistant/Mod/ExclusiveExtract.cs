using Emby.Media.Model.ProbeModel;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Services;
using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Mod.PatchManager;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Mod
{
    public class ExclusiveExtract : PatchBase<ExclusiveExtract>
    {
        internal class RefreshContext
        {
            public long InternalId { get; set; }
            public MetadataRefreshOptions MetadataRefreshOptions { get; set; }
            public bool IsNewItem { get; set; }
            public bool IsScanning { get; set; }
            public bool IsPlayback { get; set; }
            public bool IsFileChanged { get; set; }
            public bool IsExternalSubtitleChanged { get; set; }
            public bool IsPersistInScope { get; set; }
            public bool MediaInfoUpdated { get; set; }
            public bool HasMetadataFetchers { get; set; }
        }

        private static MethodInfo _canRefreshMetadata;
        private static MethodInfo _canRefreshImage;
        private static MethodInfo _clearImages;
        private static MethodInfo _isSaverEnabledForItem;
        private static MethodInfo _afterMetadataRefresh;
        private static MethodInfo _isProbingAllowed;
        private static MethodInfo _runFfProcess;
        private static MethodInfo _getInputArgument;
        private static MethodInfo _getMediaInfo;

        private static MethodInfo _addVirtualFolder;
        private static MethodInfo _removeVirtualFolder;
        private static MethodInfo _addMediaPath;
        private static MethodInfo _removeMediaPath;

        private static MethodInfo _saveChapters;
        private static MethodInfo _deleteChapters;

        private static MethodInfo _getRefreshOptions;

        private static readonly AsyncLocal<bool> ShouldCleanEmbeddedMetadata = new AsyncLocal<bool>();
        private static readonly AsyncLocal<long> ExclusiveItem = new AsyncLocal<long>();
        private static readonly AsyncLocal<long> ProtectIntroItem = new AsyncLocal<long>();
        private static readonly AsyncLocal<RefreshContext> CurrentRefreshContext = new AsyncLocal<RefreshContext>();

        public ExclusiveExtract()
        {
            Initialize();

            PatchFfProbeProcess();

            if (Plugin.Instance.MediaInfoExtractStore.GetOptions().ExclusiveExtract)
            {
                UpdateExclusiveControlFeatures(Plugin.Instance.MediaInfoExtractStore.GetOptions());
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var embyProviders = Assembly.Load("Emby.Providers");
            var providerManager = embyProviders.GetType("Emby.Providers.Manager.ProviderManager");
            _canRefreshMetadata = providerManager.GetMethod("CanRefresh", BindingFlags.Static | BindingFlags.NonPublic);
            _canRefreshImage = providerManager.GetMethod("CanRefresh", BindingFlags.Instance | BindingFlags.NonPublic);
            var itemImageProvider = embyProviders.GetType("Emby.Providers.Manager.ItemImageProvider");
            _clearImages = itemImageProvider.GetMethod("ClearImages", BindingFlags.Instance | BindingFlags.NonPublic);
            _isSaverEnabledForItem =
                providerManager.GetMethod("IsSaverEnabledForItem", BindingFlags.Instance | BindingFlags.NonPublic);
            _afterMetadataRefresh =
                typeof(BaseItem).GetMethod("AfterMetadataRefresh", BindingFlags.Instance | BindingFlags.Public);
            var fFProbeProvider = embyProviders.GetType("Emby.Providers.MediaInfo.FFProbeProvider");
            _isProbingAllowed =
                fFProbeProvider.GetMethod("IsProbingAllowed", BindingFlags.Static | BindingFlags.NonPublic);

            var mediaEncodingAssembly = Assembly.Load("Emby.Server.MediaEncoding");
            var mediaProbeManager =
                mediaEncodingAssembly.GetType("Emby.Server.MediaEncoding.Probing.MediaProbeManager");
            _runFfProcess =
                mediaProbeManager.GetMethod("RunFfProcess", BindingFlags.Instance | BindingFlags.NonPublic);
            var encodingHelpers = mediaEncodingAssembly.GetType("Emby.Server.MediaEncoding.Encoder.EncodingHelpers");
            _getInputArgument =
                encodingHelpers.GetMethod("GetInputArgument", BindingFlags.Static | BindingFlags.Public);
            var probeResultNormalizer =
                mediaEncodingAssembly.GetType("Emby.Server.MediaEncoding.Probing.ProbeResultNormalizer");
            _getMediaInfo =
                probeResultNormalizer.GetMethod("GetMediaInfo", BindingFlags.Instance | BindingFlags.Public);

            var embyApi = Assembly.Load("Emby.Api");
            var libraryStructureService = embyApi.GetType("Emby.Api.Library.LibraryStructureService");
            _addVirtualFolder = libraryStructureService.GetMethod("Post",
                new[] { embyApi.GetType("Emby.Api.Library.AddVirtualFolder") });
            _removeVirtualFolder = libraryStructureService.GetMethod("Any",
                new[] { embyApi.GetType("Emby.Api.Library.RemoveVirtualFolder") });
            _addMediaPath = libraryStructureService.GetMethod("Post",
                new[] { embyApi.GetType("Emby.Api.Library.AddMediaPath") });
            _removeMediaPath = libraryStructureService.GetMethod("Any",
                new[] { embyApi.GetType("Emby.Api.Library.RemoveMediaPath") });
            
            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
            var sqliteItemRepository =
                embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
            _saveChapters = sqliteItemRepository.GetMethod("SaveChapters",
                BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(long), typeof(bool), typeof(List<ChapterInfo>) }, null);
            _deleteChapters =
                sqliteItemRepository.GetMethod("DeleteChapters", BindingFlags.Instance | BindingFlags.Public);

            var itemRefreshService = embyApi.GetType("Emby.Api.ItemRefreshService");
            _getRefreshOptions =
                itemRefreshService.GetMethod("GetRefreshOptions", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _canRefreshImage, prefix: nameof(CanRefreshImagePrefix));
            PatchUnpatch(PatchTracker, apply, _canRefreshMetadata, prefix: nameof(CanRefreshMetadataPrefix),
                postfix: nameof(CanRefreshMetadataPostfix));
            PatchUnpatch(PatchTracker, apply, _clearImages, prefix: nameof(ClearImagesPrefix));
            PatchUnpatch(PatchTracker, apply, _isSaverEnabledForItem, prefix: nameof(IsSaverEnabledForItemPrefix));
            PatchUnpatch(PatchTracker, apply, _afterMetadataRefresh, prefix: nameof(AfterMetadataRefreshPrefix));
            PatchUnpatch(PatchTracker, apply, _addVirtualFolder, prefix: nameof(RefreshLibraryPrefix));
            PatchUnpatch(PatchTracker, apply, _removeVirtualFolder, prefix: nameof(RefreshLibraryPrefix));
            PatchUnpatch(PatchTracker, apply, _addMediaPath, prefix: nameof(RefreshLibraryPrefix));
            PatchUnpatch(PatchTracker, apply, _removeMediaPath, prefix: nameof(RefreshLibraryPrefix));
            PatchUnpatch(PatchTracker, apply, _saveChapters, prefix: nameof(SaveChaptersPrefix));
            PatchUnpatch(PatchTracker, apply, _deleteChapters, prefix: nameof(DeleteChaptersPrefix));
            PatchUnpatch(PatchTracker, apply, _getRefreshOptions, postfix: nameof(GetRefreshOptionsPostfix));
        }

        private void PatchFfProbeProcess()
        {
            PatchUnpatch(PatchTracker, true, _runFfProcess, prefix: nameof(RunFfProcessPrefix),
                finalizer: nameof(RunFfProcessFinalizer));
            PatchUnpatch(PatchTracker, true, _getInputArgument, prefix: nameof(GetInputArgumentPrefix));
            PatchUnpatch(PatchTracker, true, _isProbingAllowed, prefix: nameof(IsProbingAllowedPrefix));
            PatchUnpatch(PatchTracker, true, _getMediaInfo, postfix: nameof(GetMediaInfoPostfix));
        }

        public static void AllowExtractInstance(BaseItem item)
        {
            if (!IsExclusiveFeatureSelected(ExclusiveControl.NoIntroProtect) &&
                item.DateLastRefreshed != DateTimeOffset.MinValue && item is Episode &&
                Plugin.ChapterApi.HasIntro(item))
            {
                ProtectIntroItem.Value = item.InternalId;
            }

            ExclusiveItem.Value = item.InternalId;
        }

        [HarmonyPrefix]
        private static void RunFfProcessPrefix(ref int timeoutMs)
        {
            if (ExclusiveItem.Value != 0)
            {
                timeoutMs = 60000 * Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.MaxConcurrentCount;
            }
        }

        [HarmonyPrefix]
        private static bool GetInputArgumentPrefix(ref string input, MediaProtocol protocol, ref string __result)
        {
            if (protocol == MediaProtocol.Http)
            {
                __result = string.Format(CultureInfo.InvariantCulture, "\"{0}\"", input);
                return false;
            }

            if (LibraryApi.IsFileShortcut(input))
            {
                var inputPath = input;
                var mountPath = Task.Run(async () => await Plugin.LibraryApi.GetStrmMountPath(inputPath)).Result;
                if (!string.IsNullOrEmpty(mountPath))
                {
                    input = mountPath;
                }
            }

            return true;
        }

        [HarmonyPrefix]
        private static void IsProbingAllowedPrefix(BaseItem item, MetadataRefreshOptions options)
        {
            if (item is Movie || item is Episode)
            {
                ShouldCleanEmbeddedMetadata.Value = true;
            }
        }

        [HarmonyPostfix]
        private static void GetMediaInfoPostfix(ProbeResult data, bool isAudio, string path, MediaProtocol protocol,
            MediaInfo __result)
        {
            if (isAudio) return;

            if (!ShouldCleanEmbeddedMetadata.Value) return;

            __result.Name = null;
            __result.Overview = null;
            __result.PremiereDate = null;
            __result.ProductionYear = null;
            __result.Studios = Array.Empty<string>();
            __result.Tags = Array.Empty<string>();
            __result.Album = null;
            __result.AlbumArtists = Array.Empty<string>();
            __result.AlbumTags = Array.Empty<string>();
            __result.Artists = Array.Empty<string>();
            __result.Genres = Array.Empty<string>();
            __result.MediaStreams = __result.MediaStreams.Where(ms => ms.Type != MediaStreamType.EmbeddedImage).ToList();
        }

        [HarmonyFinalizer]
        private static void RunFfProcessFinalizer(Task __result, Exception __exception)
        {
            if (__exception is OperationCanceledException) return;

            var result = Traverse.Create(__result).Property("Result").GetValue();

            if (result != null)
            {
                var traverseResult = Traverse.Create(result);
                var standardOutput = traverseResult.Property("StandardOutput").GetValue().ToString();
                var standardError = traverseResult.Property("StandardError").GetValue().ToString();

                if (standardOutput != null && standardError != null)
                {
                    var partialOutput = standardOutput.Length > 20
                        ? standardOutput.Substring(0, 20)
                        : standardOutput;

                    if (Regex.Replace(partialOutput, @"\s+", "") == "{}")
                    {
                        var lines = standardError.Split(new[] { '\r', '\n' },
                            StringSplitOptions.RemoveEmptyEntries);

                        if (lines.Length > 0)
                        {
                            var errorMessage = lines[lines.Length - 1].Trim();

                            Plugin.Instance.Logger.Error("MediaInfoExtract - FfProbe Error: " + errorMessage);
                        }
                    }
                }
            }
        }

        [HarmonyPrefix]
        private static bool CanRefreshImagePrefix(IImageProvider provider, BaseItem item, LibraryOptions libraryOptions,
            ImageRefreshOptions refreshOptions, bool ignoreMetadataLock, bool ignoreLibraryOptions, ref bool __result)
        {
            if (ExclusiveItem.Value != 0 && ExclusiveItem.Value == item.InternalId)
            {
                return true;
            }

            if ((item.Parent is null && item.ExtraType is null) || !(item is Video || item is Audio))
            {
                return true;
            }

            if (refreshOptions is MetadataRefreshOptions options)
            {
                if (CurrentRefreshContext.Value is null)
                {
                    CurrentRefreshContext.Value = new RefreshContext
                    {
                        InternalId = item.InternalId,
                        MetadataRefreshOptions = options,
                        IsNewItem = item.DateLastRefreshed == DateTimeOffset.MinValue,
                        IsScanning = options.MetadataRefreshMode <= MetadataRefreshMode.Default &&
                                     options.ImageRefreshMode <= MetadataRefreshMode.Default,
                        HasMetadataFetchers = libraryOptions.TypeOptions.Any(t =>
                            t.Type == item.GetType().Name && t.MetadataFetchers.Any())
                    };

                    if (!CurrentRefreshContext.Value.IsNewItem)
                    {
                        if (options.MetadataRefreshMode == MetadataRefreshMode.FullRefresh &&
                            options.ImageRefreshMode == MetadataRefreshMode.Default &&
                            !options.ReplaceAllMetadata && !options.ReplaceAllImages)
                        {
                            CurrentRefreshContext.Value.IsPlayback = true;
                        }

                        if (Plugin.LibraryApi.HasFileChanged(item, options.DirectoryService))
                        {
                            CurrentRefreshContext.Value.IsFileChanged = true;
                        }

                        if (!IsExclusiveFeatureSelected(item.InternalId, ExclusiveControl.IgnoreExtSubChange) &&
                            item is Video &&
                            Plugin.SubtitleApi.HasExternalSubtitleChanged(item, options.DirectoryService, false))
                        {
                            CurrentRefreshContext.Value.IsExternalSubtitleChanged = true;
                            options.EnableRemoteContentProbe = true;
                        }

                        if (!IsExclusiveFeatureSelected(ExclusiveControl.IgnoreFileChange) &&
                            IsExclusiveFeatureSelected(ExclusiveControl.ExtractOnFileChange) &&
                            CurrentRefreshContext.Value.IsFileChanged && Plugin.LibraryApi.HasMediaInfo(item) ||
                            IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow))
                        {
                            options.EnableRemoteContentProbe = true;
                            EnableImageCapture.AllowImageCaptureInstance(item);
                        }
                    }
                }

                if (CurrentRefreshContext.Value.IsNewItem)
                {
                    return true;
                }

                if (provider is IDynamicImageProviderWithLibraryOptions && item.HasImage(ImageType.Primary) &&
                    (IsExclusiveFeatureSelected(item.InternalId, ExclusiveControl.CatchAllBlock) ||
                     !IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) && !options.ReplaceAllImages))
                {
                    __result = false;
                    return false;
                }
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool CanRefreshMetadataPrefix(IMetadataProvider provider, BaseItem item,
            LibraryOptions libraryOptions, bool includeDisabled, bool forceEnableInternetMetadata,
            bool ignoreMetadataLock, ref bool __result, out bool __state)
        {
            __state = false;
            
            if (ExclusiveItem.Value != 0 && ExclusiveItem.Value == item.InternalId)
            {
                return true;
            }

            if ((item.Parent is null && item.ExtraType is null) ||
                !(provider is IPreRefreshProvider && provider.Name == "ffprobe"))
            {
                return true;
            }

            if (CurrentRefreshContext.Value != null && CurrentRefreshContext.Value.InternalId == item.InternalId)
            {
                __state = true;

                if (CurrentRefreshContext.Value.IsNewItem)
                {
                    __result = false;
                    return false;
                }

                var refreshOptions = CurrentRefreshContext.Value.MetadataRefreshOptions;

                if (!IsExclusiveFeatureSelected(ExclusiveControl.IgnoreFileChange) &&
                    CurrentRefreshContext.Value.IsFileChanged ||
                    IsExclusiveFeatureSelected(ExclusiveControl.ExtractAlternative))
                {
                    return true;
                }

                if (CurrentRefreshContext.Value.IsScanning ||
                    !IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) && refreshOptions.SearchResult != null)
                {
                    __result = false;
                    return false;
                }

                if (!IsExclusiveFeatureSelected(item.InternalId, ExclusiveControl.CatchAllBlock) && !item.IsShortcut &&
                    refreshOptions.ReplaceAllImages)
                {
                    return true;
                }

                if (!IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) && Plugin.LibraryApi.HasMediaInfo(item))
                {
                    __result = false;
                    return false;
                }

                if (IsExclusiveFeatureSelected(ExclusiveControl.CatchAllAllow) ||
                    !IsExclusiveFeatureSelected(item.InternalId, ExclusiveControl.CatchAllBlock) && !item.IsShortcut)
                {
                    return true;
                }

                if (IsExclusiveFeatureSelected(item.InternalId, ExclusiveControl.CatchAllBlock) &&
                    !CurrentRefreshContext.Value.IsPlayback)
                {
                    return false;
                }

                return true;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void CanRefreshMetadataPostfix(IMetadataProvider provider, BaseItem item,
            LibraryOptions libraryOptions, bool includeDisabled, bool forceEnableInternetMetadata,
            bool ignoreMetadataLock, ref bool __result, bool __state)
        {
            if (!__state) return;

            var isPersistInScope = !IsExclusiveFeatureSelected(ExclusiveControl.NoPersistIntegration) && item is Video;
            CurrentRefreshContext.Value.IsPersistInScope = isPersistInScope;

            if (__result)
            {
                if (!IsExclusiveFeatureSelected(ExclusiveControl.NoIntroProtect) &&
                    (IsExclusiveFeatureSelected(ExclusiveControl.IgnoreFileChange) ||
                     !CurrentRefreshContext.Value.IsFileChanged) && item is Episode &&
                    Plugin.ChapterApi.HasIntro(item))
                {
                    ProtectIntroItem.Value = item.InternalId;
                }

                if (isPersistInScope)
                {
                    ChapterChangeTracker.BypassInstance(item);
                    CurrentRefreshContext.Value.MediaInfoUpdated = true;
                }
            }
            else if (CurrentRefreshContext.Value.IsExternalSubtitleChanged)
            {
                var refreshOptions = CurrentRefreshContext.Value.MetadataRefreshOptions;
                _ = Plugin.SubtitleApi.UpdateExternalSubtitles(item, refreshOptions, false, isPersistInScope)
                    .ConfigureAwait(false);
            }
        }

        [HarmonyPrefix]
        private static void ClearImagesPrefix(BaseItem item, ref ImageType[] imageTypesToClear, int numBackdropToKeep)
        {
            if (item.HasImage(ImageType.Primary) && imageTypesToClear.Contains(ImageType.Primary))
            {
                imageTypesToClear = imageTypesToClear.Where(i => i != ImageType.Primary).ToArray();
            }
        }

        [HarmonyPrefix]
        private static void IsSaverEnabledForItemPrefix(IMetadataSaver saver, BaseItem item,
            LibraryOptions libraryOptions, ref ItemUpdateType updateType, bool includeDisabled, ref bool __result)
        {
            if ((updateType & ItemUpdateType.MetadataDownload) == 0) return;

            if (ExclusiveItem.Value != 0 && ExclusiveItem.Value == item.InternalId)
            {
                updateType &= ~ItemUpdateType.MetadataDownload;
            }

            if (CurrentRefreshContext.Value != null && CurrentRefreshContext.Value.InternalId == item.InternalId)
            {
                if (!CurrentRefreshContext.Value.IsNewItem && CurrentRefreshContext.Value.IsScanning)
                {
                    updateType &= ~ItemUpdateType.MetadataDownload;
                }
                else if (!CurrentRefreshContext.Value.HasMetadataFetchers)
                {
                    updateType &= ~ItemUpdateType.MetadataDownload;
                }
            }
        }

        [HarmonyPrefix]
        private static void AfterMetadataRefreshPrefix(BaseItem __instance)
        {
            if (CurrentRefreshContext.Value != null && CurrentRefreshContext.Value.InternalId == __instance.InternalId)
            {
                var refreshOptions = CurrentRefreshContext.Value.MetadataRefreshOptions;
                var directoryService = refreshOptions.DirectoryService;

                if (CurrentRefreshContext.Value.IsFileChanged)
                {
                    Plugin.LibraryApi.UpdateDateModifiedLastSaved(__instance, directoryService);
                }

                if (CurrentRefreshContext.Value.IsPersistInScope)
                {
                    var ignoreFileChange = IsExclusiveFeatureSelected(ExclusiveControl.IgnoreFileChange);

                    if (CurrentRefreshContext.Value.MediaInfoUpdated)
                    {
                        if (__instance.IsShortcut && !refreshOptions.EnableRemoteContentProbe)
                        {
                            if (!CurrentRefreshContext.Value.IsFileChanged)
                            {
                                _ = Plugin.MediaInfoApi.DeserializeMediaInfo(__instance, directoryService,
                                    "Exclusive Restore", true).ConfigureAwait(false);
                            }
                            else if (!ignoreFileChange)
                            {
                                Plugin.MediaInfoApi.DeleteMediaInfoJson(__instance, directoryService,
                                    "Exclusive Delete on Change");
                            }
                        }
                        else
                        {
                            _ = Plugin.MediaInfoApi.SerializeMediaInfo(__instance.InternalId, directoryService, true,
                                "Exclusive Overwrite").ConfigureAwait(false);
                        }
                    }
                    else if (!CurrentRefreshContext.Value.IsNewItem && CurrentRefreshContext.Value.IsScanning)
                    {
                        if (!Plugin.LibraryApi.HasMediaInfo(__instance))
                        {
                            _ = Plugin.MediaInfoApi
                                .DeserializeMediaInfo(__instance, directoryService, "Exclusive Restore",
                                    ignoreFileChange).ConfigureAwait(false);
                        }
                        else
                        {
                            _ = Plugin.MediaInfoApi.SerializeMediaInfo(__instance.InternalId, directoryService, false,
                                "Exclusive Non-existent").ConfigureAwait(false);
                        }
                    }
                }
            }

            CurrentRefreshContext.Value = null;
        }

        [HarmonyPrefix]
        private static void RefreshLibraryPrefix(IReturnVoid request)
        {
            Traverse.Create(request).Property("RefreshLibrary").SetValue(false);
        }

        [HarmonyPrefix]
        private static bool SaveChaptersPrefix(long itemId, bool clearExtractionFailureResult,
            List<ChapterInfo> chapters)
        {
            if (ProtectIntroItem.Value != 0 && ProtectIntroItem.Value == itemId) return false;

            return true;
        }

        [HarmonyPrefix]
        private static bool DeleteChaptersPrefix(long itemId, MarkerType[] markerTypes)
        {
            if (ProtectIntroItem.Value != 0 && ProtectIntroItem.Value == itemId) return false;

            return true;
        }

        [HarmonyPostfix]
        private static void GetRefreshOptionsPostfix(IReturnVoid request, MetadataRefreshOptions __result)
        {
            var id = Traverse.Create(request).Property("Id").GetValue<string>();
            var item = BaseItem.LibraryManager.GetItemById(id);

            Plugin.MediaInfoApi.QueueRefreshAlternateVersions(item, __result, true);
        }
    }
}
