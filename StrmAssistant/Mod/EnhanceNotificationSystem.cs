using Emby.Notifications;
using HarmonyLib;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class EnhanceNotificationSystem: PatchBase<EnhanceNotificationSystem>
    {
        private static MethodInfo _convertToGroups;
        private static MethodInfo _sendNotification;
        private static MethodInfo _queueNotification;

        private static readonly AsyncLocal<Dictionary<long, List<(int? IndexNumber, int? ParentIndexNumber)>>>
            GroupDetails = new AsyncLocal<Dictionary<long, List<(int? IndexNumber, int? ParentIndexNumber)>>>();
        private static readonly AsyncLocal<string> Description = new AsyncLocal<string>();

        public EnhanceNotificationSystem()
        {
            Initialize();

            if (Plugin.Instance.ExperienceEnhanceStore.GetOptions().EnhanceNotificationSystem)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var notificationsAssembly = Assembly.Load("Emby.Notifications");
            var notificationManager = notificationsAssembly.GetType("Emby.Notifications.NotificationManager");
            _convertToGroups = notificationManager.GetMethod("ConvertToGroups",
                BindingFlags.Instance | BindingFlags.NonPublic);
            _sendNotification = notificationManager.GetMethod("SendNotification",
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[] { typeof(INotifier), typeof(NotificationInfo[]), typeof(NotificationRequest), typeof(bool) },
                null);
            var notificationQueueManager = notificationsAssembly.GetType("Emby.Notifications.NotificationQueueManager");
            _queueNotification = notificationQueueManager.GetMethod("QueueNotification",
                BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(INotifier), typeof(InternalNotificationRequest), typeof(int) }, null);
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _convertToGroups, postfix: nameof(ConvertToGroupsPostfix));
            PatchUnpatch(PatchTracker, apply, _sendNotification, prefix: nameof(SendNotificationPrefix));
            PatchUnpatch(PatchTracker, apply, _queueNotification, prefix: nameof(QueueNotificationPrefix));
        }

        [HarmonyPostfix]
        private static void ConvertToGroupsPostfix(ItemChangeEventArgs[] list,
            ref Dictionary<long, List<ItemChangeEventArgs>> __result)
        {
            var filteredItems = list.Where(i => i.Item.SeriesId != 0L).ToArray();

            if (filteredItems.Length == 0) return;

            GroupDetails.Value = filteredItems.GroupBy(i => i.Item.SeriesId)
                .ToDictionary(g => g.Key, g => g.Select(i => (i.Item.IndexNumber, i.Item.ParentIndexNumber)).ToList());
        }

        [HarmonyPrefix]
        private static void SendNotificationPrefix(INotifier notifier, NotificationInfo[] notifications,
            NotificationRequest request, bool enableUserDataInDto)
        {
            if (notifications.FirstOrDefault()?.GroupItems is true
                && request.Item is Series series && GroupDetails.Value != null
                && GroupDetails.Value.TryGetValue(series.InternalId, out var groupDetails))
            {
                var groupedBySeason = groupDetails.Where(e => e.ParentIndexNumber.HasValue)
                    .GroupBy(e => e.ParentIndexNumber)
                    .OrderBy(g => g.Key)
                    .ToList();

                var descriptions = new List<string>();

                foreach (var seasonGroup in groupedBySeason)
                {
                    var seasonIndex = seasonGroup.Key;
                    var episodesBySeason = seasonGroup
                        .Where(e => e.IndexNumber.HasValue)
                        .OrderBy(e => e.IndexNumber.Value)
                        .Select(e => e.IndexNumber.Value)
                        .Distinct()
                        .ToList();

                    if (!episodesBySeason.Any()) continue;

                    var episodeRanges = new List<string>();
                    var rangeStart = episodesBySeason[0];
                    var lastEpisodeInRange = rangeStart;

                    for (var i = 1; i < episodesBySeason.Count; i++)
                    {
                        var current = episodesBySeason[i];
                        if (current != lastEpisodeInRange + 1)
                        {
                            episodeRanges.Add(rangeStart == lastEpisodeInRange
                                ? $"E{rangeStart:D2}"
                                : $"E{rangeStart:D2}-E{lastEpisodeInRange:D2}");
                            rangeStart = current;
                        }

                        lastEpisodeInRange = current;
                    }

                    episodeRanges.Add(rangeStart == lastEpisodeInRange
                        ? $"E{rangeStart:D2}"
                        : $"E{rangeStart:D2}-E{lastEpisodeInRange:D2}");

                    descriptions.Add($"S{seasonIndex:D2} {string.Join(", ", episodeRanges)}");
                }

                var summary = string.Join(" / ", descriptions);

                var tmdbId = series.GetProviderId(MetadataProviders.Tmdb);

                if (!string.IsNullOrEmpty(tmdbId))
                {
                    summary += $"{Environment.NewLine}{Environment.NewLine}TmdbId: {tmdbId}";
                }

                Description.Value = summary;
            }
        }

        [HarmonyPrefix]
        private static void QueueNotificationPrefix(INotifier sender, InternalNotificationRequest request, int priority)
        {
            if (!string.IsNullOrEmpty(Description.Value))
            {
                request.Description = Description.Value;
                Description.Value = null;
            }
        }
    }
}
