using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
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
    public class ScanExternalSubtitleTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;

        public ScanExternalSubtitleTask(IFileSystem fileSystem)
        {
            _logger = Plugin.Instance.Logger;
            _fileSystem = fileSystem;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("ExternalSubtitle - Scheduled Task Execute");
            _logger.Info("Tier2 Max Concurrent Count: " +
                         Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.Tier2MaxConcurrentCount);

            await Task.Yield();
            progress.Report(0);

            var persistMediaInfoMode = Plugin.Instance.MediaInfoExtractStore.GetOptions().PersistMediaInfoMode;
            _logger.Info("Persist MediaInfo Mode: " + persistMediaInfoMode);
            var persistMediaInfo = persistMediaInfoMode != PersistMediaInfoOption.None.ToString();

            var items = Plugin.LibraryApi.FetchPostExtractTaskItems(false);
            _logger.Info("ExternalSubtitle - Number of items: " + items.Count);

            var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
            {
                EnableRemoteContentProbe = true,
                ReplaceAllMetadata = false,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false
            };

            double total = items.Count;
            var index = 0;
            var current = 0;

            var tasks = new List<Task>();

            foreach (var item in items)
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
                    _logger.Info("ExternalSubtitle - Scheduled Task Cancelled");
                    return;
                }

                var taskIndex = ++index;
                var taskItem = item;
                var task = Task.Run(async () =>
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.Info("ExternalSubtitle - Scheduled Task Cancelled");
                            return;
                        }

                        if (Plugin.SubtitleApi.HasExternalSubtitleChanged(taskItem, refreshOptions.DirectoryService,
                                true))
                        {
                            await Plugin.SubtitleApi
                                .UpdateExternalSubtitles(taskItem, refreshOptions, false, persistMediaInfo)
                                .ConfigureAwait(false);

                            _logger.Info("ExternalSubtitle - Item Processed: " + taskItem.Name + " - " + taskItem.Path);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Info("ExternalSubtitle - Item cancelled: " + taskItem.Name + " - " + taskItem.Path);
                    }
                    catch (Exception e)
                    {
                        _logger.Info("ExternalSubtitle - Item failed: " + taskItem.Name + " - " + taskItem.Path);
                        _logger.Debug(e.Message);
                        _logger.Debug(e.StackTrace);
                    }
                    finally
                    {
                        QueueManager.Tier2Semaphore.Release();

                        var currentCount = Interlocked.Increment(ref current);
                        progress.Report(currentCount / total * 100);
                        _logger.Info("ExternalSubtitle - Progress " + currentCount + "/" + total + " - " +
                                     "Task " + taskIndex + ": " + taskItem.Path);
                    }
                }, cancellationToken);
                tasks.Add(task);
                Task.Delay(10).Wait();
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);

            progress.Report(100.0);
            _logger.Info("ExternalSubtitle - Scheduled Task Complete");
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "ScanExternalSubtitleTask";

        public string Description => Resources.ResourceManager.GetString(
            "ScanExternalSubtitleTask_Description_Scans_external_subtitles_for_videos",
            Plugin.Instance.DefaultUICulture);

        public string Name => "Scan External Subtitles";
        //public string Name =>
        //    Resources.ResourceManager.GetString("ScanExternalSubtitleTask_Name_Scan_External_Subtitles",
        //        Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}
