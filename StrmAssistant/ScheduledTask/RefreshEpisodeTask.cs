using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.ScheduledTask
{
    public class RefreshEpisodeTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILogger _logger = Plugin.Instance.Logger;

        private static readonly Random Random = new Random();

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("EpisodeRefresh - Scheduled Task Execute");
            var tier2MaxConcurrentCount = Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions
                .Tier2MaxConcurrentCount;
            _logger.Info("Tier2 Max Concurrent Count: " + tier2MaxConcurrentCount);

            await Task.Yield();
            progress.Report(0);

            var itemsToRefresh = Plugin.LibraryApi.FetchEpisodeRefreshTaskItems();

            IsRunning = true;

            double total = itemsToRefresh.Count;
            var index = 0;
            var current = 0;

            var tasks = new List<Task>();

            foreach (var item in itemsToRefresh)
            {
                try
                {
                    await QueueManager.Tier2Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }
                
                if (cancellationToken.IsCancellationRequested)
                {
                    QueueManager.Tier2Semaphore.Release();
                    _logger.Info("EpisodeRefresh - Scheduled Task Cancelled");
                    return;
                }

                var taskIndex = ++index;
                var taskItem = item;
                var task = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(
                                Random.Next(0,
                                    Math.Max(0, tier2MaxConcurrentCount - QueueManager.Tier2Semaphore.CurrentCount) *
                                    MetadataApi.RequestIntervalMs), cancellationToken)
                            .ConfigureAwait(false);

                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.Info("EpisodeRefresh - Scheduled Task Cancelled");
                            return;
                        }

                        await Plugin.LibraryApi.OrchestrateEpisodeRefreshAsync(taskItem, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Info("EpisodeRefresh - Item cancelled: " + taskItem.Name + " - " + taskItem.Path);
                    }
                    catch (Exception e)
                    {
                        _logger.Info("EpisodeRefresh - Item failed: " + taskItem.Name + " - " + taskItem.Path);
                        _logger.Debug(e.Message);
                        _logger.Debug(e.StackTrace);
                    }
                    finally
                    {
                        QueueManager.Tier2Semaphore.Release();

                        ClearItemExclusiveFeatures(taskItem.InternalId);

                        var currentCount = Interlocked.Increment(ref current);
                        progress.Report(currentCount / total * 100);
                        _logger.Info("EpisodeRefresh - Progress " + currentCount + "/" + total + " - " +
                                     "Task " + taskIndex + ": " + taskItem.Path);
                    }
                }, cancellationToken);
                tasks.Add(task);
                Task.Delay(10).Wait();
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);

            IsRunning = false;

            progress.Report(100.0);
            _logger.Info("EpisodeRefresh - Scheduled Task Complete");
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "EpisodeRefreshTask";

        public string Description => Resources.ResourceManager.GetString(
            "EpisodeRefreshTask_Description_Refresh_metadata_for_episodes_missing_overview",
            Plugin.Instance.DefaultUICulture);

        public string Name => "Refresh Episode";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        public static bool IsRunning { get; private set; }
    }
}
