using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class ChapterChangeTracker : PatchBase<ChapterChangeTracker>
    {
        private static MethodInfo _saveChapters;
        private static MethodInfo _deleteChapters;
        private static MethodInfo _onFailedToFindIntro;

        private static readonly AsyncLocal<long> BypassItem = new AsyncLocal<long>();

        public ChapterChangeTracker()
        {
            Initialize();

            if (Plugin.Instance.MediaInfoExtractStore.GetOptions().PersistMediaInfo)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
            var sqliteItemRepository =
                embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Data.SqliteItemRepository");
            _saveChapters = sqliteItemRepository.GetMethod("SaveChapters",
                BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(long), typeof(bool), typeof(List<ChapterInfo>) }, null);
            _deleteChapters =
                sqliteItemRepository.GetMethod("DeleteChapters", BindingFlags.Instance | BindingFlags.Public);

            var embyProviders = Assembly.Load("Emby.Providers");
            var audioFingerprintManager = embyProviders.GetType("Emby.Providers.Markers.AudioFingerprintManager");
            _onFailedToFindIntro = audioFingerprintManager.GetMethod("OnFailedToFindIntro",
                BindingFlags.NonPublic | BindingFlags.Static);
        }

        protected override void Prepare(bool apply)
        {
            if (Plugin.Instance.MediaInfoExtractStore.GetOptions().IsModSupported)
            {
                PatchUnpatch(PatchTracker, apply, _saveChapters, postfix: nameof(SaveChaptersPostfix));
                //PatchUnpatch(PatchTracker, apply, _deleteChapters, postfix: nameof(DeleteChaptersPostfix));
                PatchUnpatch(PatchTracker, apply, _onFailedToFindIntro, postfix: nameof(OnFailedToFindIntroPostfix));
            }
        }

        public static void BypassInstance(BaseItem item)
        {
            BypassItem.Value = item.InternalId;
        }

        [HarmonyPostfix]
        private static void SaveChaptersPostfix(long itemId, bool clearExtractionFailureResult,
            List<ChapterInfo> chapters)
        {
            if (chapters.Count == 0) return;

            if (BypassItem.Value != 0 && BypassItem.Value == itemId) return;

            _ = Plugin.MediaInfoApi.SerializeMediaInfo(itemId, true, "Save Chapters", CancellationToken.None);
        }

        [HarmonyPostfix]
        private static void DeleteChaptersPostfix(long itemId, MarkerType[] markerTypes)
        {
            if (BypassItem.Value != 0 && BypassItem.Value == itemId) return;

            _ = Plugin.MediaInfoApi.SerializeMediaInfo(itemId, true, "Delete Chapters", CancellationToken.None);
        }

        [HarmonyPostfix]
        private static void OnFailedToFindIntroPostfix(Episode episode, bool __runOriginal)
        {
            if (__runOriginal)
            {
                _ = Plugin.MediaInfoApi.SerializeMediaInfo(episode.InternalId, true, "Zero Fingerprint Confidence",
                    CancellationToken.None);
            }
        }
    }
}
