using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Options.MediaInfoExtractOptions;

namespace StrmAssistant.ScheduledTask
{
    public class ExtractMediaInfoTask : IScheduledTask
    {
        private readonly ILogger _logger = Plugin.Instance.Logger;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("MediaInfoExtract - Scheduled Task Execute");

            var maxConcurrentCount = Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.MaxConcurrentCount;
            _logger.Info("Master Max Concurrent Count: " + maxConcurrentCount);
            var cooldownSeconds = maxConcurrentCount == 1
                ? Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.CooldownDurationSeconds
                : (int?)null;
            if (cooldownSeconds.HasValue) _logger.Info("Cooldown Duration Seconds: " + cooldownSeconds.Value);

            var persistMediaInfoMode = Plugin.Instance.MediaInfoExtractStore.GetOptions().PersistMediaInfoMode;
            _logger.Info("Persist MediaInfo Mode: " + persistMediaInfoMode);
            var mediaInfoRestoreMode = persistMediaInfoMode == PersistMediaInfoOption.Restore.ToString();

            var enableImageCapture = Plugin.Instance.MediaInfoExtractStore.GetOptions().EnableImageCapture;
            _logger.Info("Enable Image Capture: " + enableImageCapture);
            var enableIntroSkip = Plugin.Instance.IntroSkipStore.GetOptions().EnableIntroSkip;
            _logger.Info("Intro Skip Enabled: " + enableIntroSkip);

            var items = Plugin.LibraryApi.FetchPreExtractTaskItems();

            if (items.Count > 0) IsRunning = true;

            double total = items.Count;
            var index = 0;
            var current = 0;
            var skip = 0;

            var tasks = new List<Task>();

            foreach (var item in items)
            {
                try
                {
                    await QueueManager.MasterSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    QueueManager.MasterSemaphore.Release();
                    _logger.Info("MediaInfoExtract - Scheduled Task Cancelled");
                    return;
                }

                var taskIndex = ++index;
                var taskItem = item;
                var task = Task.Run(async () =>
                {
                    bool? result = null;

                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.Info("MediaInfoExtract - Scheduled Task Cancelled");
                            return;
                        }

                        result = await Plugin.LibraryApi
                            .OrchestrateMediaInfoProcessAsync(taskItem, "MediaInfoExtract Task", cancellationToken)
                            .ConfigureAwait(false);

                        if (result is null)
                        {
                            if (!mediaInfoRestoreMode)
                            {
                                _logger.Info(
                                    $"MediaInfoExtract - Item skipped or non-existent: {taskItem.Name} - {taskItem.Path}");
                            }

                            Interlocked.Increment(ref skip);
                            return;
                        }

                        if (enableIntroSkip && taskItem is Episode episode &&
                            Plugin.PlaySessionMonitor.IsLibraryInScope(episode) &&
                            Plugin.ChapterApi.SeasonHasIntroCredits(episode))
                        {
                            QueueManager.IntroSkipItemQueue.Enqueue(episode);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Info($"MediaInfoExtract - Item cancelled: {taskItem.Name} - {taskItem.Path}");
                    }
                    catch (Exception e)
                    {
                        _logger.Error($"MediaInfoExtract - Item failed: {taskItem.Name} - {taskItem.Path}");
                        _logger.Error(e.Message);
                        _logger.Debug(e.StackTrace);
                    }
                    finally
                    {
                        if (result is true && cooldownSeconds.HasValue)
                        {
                            try
                            {
                                await Task.Delay(cooldownSeconds.Value * 1000, cancellationToken).ConfigureAwait(false);
                            }
                            catch
                            {
                                // ignored
                            }
                        }

                        QueueManager.MasterSemaphore.Release();

                        var currentCount = Interlocked.Increment(ref current);
                        progress.Report(currentCount / total * 100);

                        if (!mediaInfoRestoreMode)
                        {
                            _logger.Info(
                                $"MediaInfoExtract - Progress {currentCount}/{total} - Task {taskIndex}: {taskItem.Path}");
                        }
                    }
                }, cancellationToken);
                tasks.Add(task);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);

            if (items.Count > 0) IsRunning = false;

            progress.Report(100.0);
            _logger.Info($"MediaInfoExtract - Number of items skipped: {skip}");
            _logger.Info("MediaInfoExtract - Scheduled Task Complete");
        }

        public string Category =>
            Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
                Plugin.Instance.DefaultUICulture);

        public string Key => "MediaInfoExtractTask";

        public string Description => Resources.ResourceManager.GetString(
            "ExtractMediaInfoTask_Description_Extracts_media_info_from_videos_and_audios",
            Plugin.Instance.DefaultUICulture);

        public string Name => "Extract MediaInfo";
        //public string Name => Resources.ResourceManager.GetString("ExtractMediaInfoTask_Name_Extract_MediaInfo",
        //    Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public static bool IsRunning { get; private set; }
    }
}
