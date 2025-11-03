using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;
using StrmAssistant.Common;
using StrmAssistant.Provider;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using static StrmAssistant.Mod.PatchManager;
using static StrmAssistant.Options.ExperienceEnhanceOptions;

namespace StrmAssistant.Mod
{
    public class MergeMultiVersion : PatchBase<MergeMultiVersion>
    {
        private static MethodInfo _isEligibleForMultiVersion;
        private static MethodInfo _canRefreshImage;
        private static MethodInfo _addLibrariesToPresentationUniqueKey;
        private static MethodInfo _getRefreshOptions;

        public static readonly AsyncLocal<BaseItem[]> CurrentAllCollectionFolders = new AsyncLocal<BaseItem[]>();

        public MergeMultiVersion()
        {
            Initialize();

            if (Plugin.Instance.ExperienceEnhanceStore.GetOptions().MergeMultiVersion)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            try
            {
                // 加载 Emby.Naming
                var namingAssembly = EmbyVersionCompatibility.TryLoadAssembly("Emby.Naming");
                if (namingAssembly != null)
                {
                    var videoListResolverType = EmbyVersionCompatibility.TryGetType(namingAssembly, "Emby.Naming.Video.VideoListResolver");
                    if (videoListResolverType != null)
                    {
                        _isEligibleForMultiVersion = videoListResolverType.GetMethod("IsEligibleForMultiVersion",
                            BindingFlags.Static | BindingFlags.NonPublic);
                        
                        if (_isEligibleForMultiVersion != null && Plugin.Instance.DebugMode)
                        {
                            Plugin.Instance.Logger.Debug("MergeMultiVersion: IsEligibleForMultiVersion method found");
                        }
                    }
                }

                // 加载 Emby.Providers
                var embyProviders = EmbyVersionCompatibility.TryLoadAssembly("Emby.Providers");
                if (embyProviders != null)
                {
                    var providerManager = EmbyVersionCompatibility.TryGetType(embyProviders, "Emby.Providers.Manager.ProviderManager");
                    if (providerManager != null)
                    {
                        _canRefreshImage = providerManager.GetMethod("CanRefresh", BindingFlags.Instance | BindingFlags.NonPublic);
                        
                        if (_canRefreshImage != null && Plugin.Instance.DebugMode)
                        {
                            Plugin.Instance.Logger.Debug("MergeMultiVersion: CanRefresh method found");
                        }
                    }
                }

                // 获取 Series 的方法
                _addLibrariesToPresentationUniqueKey = typeof(Series).GetMethod("AddLibrariesToPresentationUniqueKey",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                
                if (_addLibrariesToPresentationUniqueKey != null && Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug("MergeMultiVersion: AddLibrariesToPresentationUniqueKey method found");
                }

                // 加载 Emby.Api
                var embyApi = EmbyVersionCompatibility.TryLoadAssembly("Emby.Api");
                if (embyApi != null)
                {
                    var itemRefreshService = EmbyVersionCompatibility.TryGetType(embyApi, "Emby.Api.ItemRefreshService");
                    if (itemRefreshService != null)
                    {
                        _getRefreshOptions = itemRefreshService.GetMethod("GetRefreshOptions", 
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        
                        if (_getRefreshOptions != null && Plugin.Instance.DebugMode)
                        {
                            Plugin.Instance.Logger.Debug("MergeMultiVersion: GetRefreshOptions method found");
                        }
                    }
                }

                // 验证所有必需的组件
                var missingComponents = new List<string>();
                if (_isEligibleForMultiVersion == null) missingComponents.Add("IsEligibleForMultiVersion");
                if (_canRefreshImage == null) missingComponents.Add("CanRefresh");
                if (_addLibrariesToPresentationUniqueKey == null) missingComponents.Add("AddLibrariesToPresentationUniqueKey");
                if (_getRefreshOptions == null) missingComponents.Add("GetRefreshOptions");

                if (missingComponents.Any())
                {
                    Plugin.Instance.Logger.Warn($"MergeMultiVersion: Missing components - {string.Join(", ", missingComponents)}");
                    Plugin.Instance.Logger.Warn("Multi-version merge may not work fully on this Emby version");
                    
                    EmbyVersionCompatibility.LogCompatibilityInfo(
                        nameof(MergeMultiVersion),
                        false,
                        $"{missingComponents.Count} required methods not found");
                }
                else
                {
                    Plugin.Instance.Logger.Info("MergeMultiVersion: All components loaded successfully");
                    
                    EmbyVersionCompatibility.LogCompatibilityInfo(
                        nameof(MergeMultiVersion),
                        true,
                        "All required components available");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Error($"MergeMultiVersion initialization failed: {ex.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {ex.GetType().Name}");
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
                
                EmbyVersionCompatibility.LogCompatibilityInfo(
                    nameof(MergeMultiVersion),
                    false,
                    "Initialization error - feature may be limited");
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _isEligibleForMultiVersion,
                prefix: nameof(IsEligibleForMultiVersionPrefix));
            PatchUnpatch(PatchTracker, apply, _canRefreshImage, prefix: nameof(CanRefreshImagePrefix));
            PatchUnpatch(PatchTracker, apply, _addLibrariesToPresentationUniqueKey,
                prefix: nameof(AddLibrariesToPresentationUniqueKeyPrefix));
            PatchUnpatch(PatchTracker, apply, _getRefreshOptions, postfix: nameof(GetRefreshOptionsPostfix));
        }

        [HarmonyPrefix]
        private static bool IsEligibleForMultiVersionPrefix(string folderName, string testFilename, ref bool __result)
        {
            __result = string.Equals(folderName, Path.GetFileName(Path.GetDirectoryName(testFilename)),
                StringComparison.OrdinalIgnoreCase);

            return false;
        }

        private static BaseItem[] GetAllCollectionFolders(Series series)
        {
            if (!(series.HasProviderId(MetadataProviders.Tmdb) || series.HasProviderId(MetadataProviders.Imdb) ||
                  series.HasProviderId(MetadataProviders.Tvdb)))
            {
                return Array.Empty<BaseItem>();
            }

            var allSeries = BaseItem.LibraryManager.GetItemList(new InternalItemsQuery
            {
                EnableTotalRecordCount = false,
                Recursive = false,
                ExcludeItemIds = new[] { series.InternalId },
                IncludeItemTypes = new[] { nameof(Series) },
                AnyProviderIdEquals = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>(MetadataProviders.Tmdb.ToString(),
                        series.GetProviderId(MetadataProviders.Tmdb)),
                    new KeyValuePair<string, string>(MetadataProviders.Imdb.ToString(),
                        series.GetProviderId(MetadataProviders.Imdb)),
                    new KeyValuePair<string, string>(MetadataProviders.Tvdb.ToString(),
                        series.GetProviderId(MetadataProviders.Tvdb))
                }
            }).Concat(new[] { series }).ToList();

            var collectionFolders = new HashSet<BaseItem>();

            foreach (var item in allSeries)
            {
                var options = BaseItem.LibraryManager.GetLibraryOptions(item);

                if (options.EnableAutomaticSeriesGrouping)
                {
                    foreach (var library in BaseItem.LibraryManager.GetCollectionFolders(item))
                    {
                        collectionFolders.Add(library);
                    }
                }
            }

            return collectionFolders.OrderBy(c => c.InternalId).ToArray();
        }

        [HarmonyPrefix]
        private static void CanRefreshImagePrefix(IImageProvider provider, BaseItem item, LibraryOptions libraryOptions,
            ImageRefreshOptions refreshOptions, bool ignoreMetadataLock, bool ignoreLibraryOptions)
        {
            if (CurrentAllCollectionFolders.Value != null) return;

            if (item.Parent is null && item.ExtraType is null) return;

            if (item is Series series && Plugin.Instance.ExperienceEnhanceStore.GetOptions().MergeSeriesPreference ==
                MergeSeriesScopeOption.GlobalScope)
            {
                CurrentAllCollectionFolders.Value = GetAllCollectionFolders(series);
            }
        }

        [HarmonyPrefix]
        private static bool AddLibrariesToPresentationUniqueKeyPrefix(Series __instance, string key,
            ref BaseItem[] collectionFolders, LibraryOptions libraryOptions, ref string __result)
        {
            if (CurrentAllCollectionFolders.Value != null)
            {
                if (CurrentAllCollectionFolders.Value.Length > 1)
                {
                    collectionFolders = CurrentAllCollectionFolders.Value;
                }

                CurrentAllCollectionFolders.Value = null;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void GetRefreshOptionsPostfix(IReturnVoid request, MetadataRefreshOptions __result)
        {
            var id = Traverse.Create(request).Property("Id").GetValue<string>();
            var item = BaseItem.LibraryManager.GetItemById(id);

            if (item is Series || item is Season)
            {
                var series = item as Series ?? (item as Season).Series;
                var seriesTmdbId = series?.GetProviderId(MetadataProviders.Tmdb);
                var episodeGroupId = series?.GetProviderId(MovieDbEpisodeGroupExternalId.StaticName)?.Trim();

                var itemsToRefresh = BaseItem.LibraryManager.GetItemList(new InternalItemsQuery
                {
                    PresentationUniqueKey = item.PresentationUniqueKey,
                    ExcludeItemIds = new[] { item.InternalId }
                });

                foreach (var alt in itemsToRefresh)
                {
                    if (!string.IsNullOrEmpty(episodeGroupId))
                    {
                        var altSeries = alt as Series ?? (alt as Season)?.Series;

                        if (altSeries != null)
                        {
                            var altSeriesTmdbId = altSeries.GetProviderId(MetadataProviders.Tmdb);
                            var altEpisodeGroupId = altSeries.GetProviderId(MovieDbEpisodeGroupExternalId.StaticName);
                            if (string.IsNullOrEmpty(altEpisodeGroupId) && !string.IsNullOrEmpty(seriesTmdbId) &&
                                !string.IsNullOrEmpty(altSeriesTmdbId) && string.Equals(seriesTmdbId, altSeriesTmdbId,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                alt.SetProviderId(MovieDbEpisodeGroupExternalId.StaticName, episodeGroupId);
                                alt.UpdateToRepository(ItemUpdateType.MetadataEdit);
                            }
                        }
                    }

                    BaseItem.ProviderManager.QueueRefresh(alt.InternalId, __result, RefreshPriority.Normal, true);
                }
            }
        }
    }
}
