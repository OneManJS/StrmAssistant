using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Options.MediaInfoExtractOptions;

namespace StrmAssistant.ScheduledTask
{
    public class ExtractIntroFingerprintTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;
        private readonly ITaskManager _taskManager;

        public ExtractIntroFingerprintTask(IFileSystem fileSystem, ITaskManager taskManager)
        {
            _logger = Plugin.Instance.Logger;
            _fileSystem = fileSystem;
            _taskManager = taskManager;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger.Info("IntroFingerprintExtract - Scheduled Task Execute");
            
            var unlockIntroSkip = Plugin.Instance.IntroSkipStore.GetOptions().UnlockIntroSkip;
            if (!unlockIntroSkip)
            {
                progress.Report(100.0);
                _ = Plugin.NotificationApi.SendMessageToAdmins(
                    $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {Resources.IntroDetectionEnhancedNotEnabled}",
                    10000);
                _logger.Warn("Built-in Intro Detection Enhanced is not enabled.");
                _logger.Warn("IntroFingerprintExtract - Scheduled Task Aborted");
                return;
            }

            var maxConcurrentCount = Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.MaxConcurrentCount;
            _logger.Info("Master Max Concurrent Count: " + maxConcurrentCount);
            var cooldownSeconds = maxConcurrentCount == 1
                ? Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.CooldownDurationSeconds
                : (int?)null;
            if (cooldownSeconds.HasValue) _logger.Info("Cooldown Duration Seconds: " + cooldownSeconds.Value);

            _logger.Info("Intro Detection Fingerprint Length (Minutes): " +
                         Plugin.Instance.IntroSkipStore.GetOptions().IntroDetectionFingerprintMinutes);

            var persistMediaInfoMode = Plugin.Instance.MediaInfoExtractStore.GetOptions().PersistMediaInfoMode;
            _logger.Info("Persist MediaInfo Mode: " + persistMediaInfoMode);
            var persistMediaInfo = persistMediaInfoMode != PersistMediaInfoOption.None.ToString();
            var mediaInfoRestoreMode = persistMediaInfoMode == PersistMediaInfoOption.Restore.ToString();

            var preExtractEpisodes = Plugin.FingerprintApi.FetchIntroPreExtractTaskItems();
            var postExtractEpisodes = Plugin.FingerprintApi.FetchIntroFingerprintTaskItems();
            var episodes= preExtractEpisodes.Concat(postExtractEpisodes).ToList();
            var groupedBySeason = episodes.GroupBy(e => e.Season).ToList();
            var seasonTasks = new List<Task>();

            double totalSeasons = groupedBySeason.Count;
            double totalEpisodes = episodes.Count;

            _logger.Info($"IntroFingerprintExtract - Number of seasons: {totalSeasons}");
            _logger.Info($"IntroFingerprintExtract - Number of episodes: {totalEpisodes}");

            if (totalEpisodes > 0) IsRunning = true;

            var directoryService = new DirectoryService(_logger, _fileSystem);

            var episodeIndex = 0;
            var seasonIndex = 0;
            var processedEpisodes = 0;
            var processedSeasons = 0;
            var episodeSkipCount = 0;
            var seasonSkipCount = 0;
            var episodeWeight = !mediaInfoRestoreMode ? 0.8 : 1.0;
            var seasonWeight = !mediaInfoRestoreMode ? 0.2 : 0.0;
            var seasonProgressMap = new ConcurrentDictionary<Season, double>();

            foreach (var season in groupedBySeason)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                    return;
                }

                var episodeTasks = new List<Task>();
                var seasonSkip = mediaInfoRestoreMode;

                foreach (var episode in season)
                {
                    var taskEpisode = episode;

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
                        _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                        return;
                    }

                    var taskEpisodeIndex = ++episodeIndex;
                    var task = Task.Run(async () =>
                    {
                        bool? result1 = null;
                        Tuple<string, bool> result2 = null;

                        try
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                                return;
                            }

                            var deserializeResult = false;

                            if (!Plugin.LibraryApi.HasMediaInfo(taskEpisode))
                            {
                                result1 = await Plugin.LibraryApi
                                    .OrchestrateMediaInfoProcessAsync(taskEpisode, "IntroFingerprintExtract Task",
                                        cancellationToken).ConfigureAwait(false);

                                if (result1 is null)
                                {
                                    if (!mediaInfoRestoreMode)
                                    {
                                        _logger.Info(
                                            $"IntroFingerprintExtract - Episode skipped or non-existent: {taskEpisode.Name} - {taskEpisode.Path}");
                                    }
                                    
                                    Interlocked.Increment(ref episodeSkipCount);
                                    seasonSkip = true;
                                    return;
                                }
                            }
                            else if (persistMediaInfo)
                            {
                                deserializeResult = await Plugin.MediaInfoApi.DeserializeIntroMarker(taskEpisode,
                                    directoryService, "IntroFingerprintExtract Task").ConfigureAwait(false);
                            }

                            if (!deserializeResult && !Plugin.ChapterApi.HasIntro(taskEpisode))
                            {
                                if (!mediaInfoRestoreMode)
                                {
                                    result2 = await Plugin.FingerprintApi
                                        .CreateTitleFingerprint(taskEpisode, directoryService, cancellationToken)
                                        .ConfigureAwait(false);
                                }
                                else
                                {
                                    Interlocked.Increment(ref episodeSkipCount);
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.Info(
                                $"IntroFingerprintExtract - Episode cancelled: {taskEpisode.Name} - {taskEpisode.Path}");
                        }
                        catch (Exception e)
                        {
                            _logger.Error(
                                $"IntroFingerprintExtract - Episode failed: {taskEpisode.Name} - {taskEpisode.Path}");
                            _logger.Error(e.Message);
                            _logger.Debug(e.StackTrace);
                        }
                        finally
                        {
                            if ((result1 is true || result2?.Item2 is true) && cooldownSeconds.HasValue)
                            {
                                try
                                {
                                    await Task.Delay(cooldownSeconds.Value * 1000, cancellationToken)
                                        .ConfigureAwait(false);
                                }
                                catch
                                {
                                    // ignored
                                }
                            }

                            QueueManager.MasterSemaphore.Release();

                            var currentCount = Interlocked.Increment(ref processedEpisodes);

                            var totalSeasonFraction = seasonProgressMap.Values.Sum();
                            var currentProgress = episodeWeight * currentCount / totalEpisodes +
                                                  seasonWeight * (processedSeasons + totalSeasonFraction) /
                                                  totalSeasons;
                            progress.Report(currentProgress * 100);

                            if (!mediaInfoRestoreMode)
                            {
                                _logger.Info(
                                    $"IntroFingerprintExtract - Episode Progress {currentCount}/{totalEpisodes} - Task {taskEpisodeIndex}: {taskEpisode.Path}");
                            }
                        }
                    }, cancellationToken);
                    episodeTasks.Add(task);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                    return;
                }

                var taskSeason = season.Key;

                var seasonProgress = new Progress<double>(fraction =>
                {
                    seasonProgressMap[taskSeason] = fraction;
                    var totalSeasonFraction = seasonProgressMap.Values.Sum();

                    var currentProgress = episodeWeight * processedEpisodes / totalEpisodes +
                                          seasonWeight * (processedSeasons + totalSeasonFraction) / totalSeasons;
                    progress.Report(currentProgress * 100);
                });

                var taskSeasonIndex = ++seasonIndex;
                var seasonTask = Task.Run(async () =>
                {
                    await Task.WhenAll(episodeTasks).ConfigureAwait(false);

                    if (seasonSkip)
                    {
                        if (!mediaInfoRestoreMode)
                        {
                            _logger.Info(
                                $"IntroFingerprintExtract - Season Skipped: {taskSeason.Name} - {taskSeason.Path}");
                        }

                        Interlocked.Increment(ref seasonSkipCount);
                        return;
                    }

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
                        _logger.Info("IntroFingerprintExtract - Scheduled Task Cancelled");
                        return;
                    }

                    try
                    {
                        await Plugin.FingerprintApi
                            .UpdateIntroMarkerForSeason(taskSeason, cancellationToken, seasonProgress)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.Info("IntroFingerprintExtract - Season cancelled: " + taskSeason.Name + " - " +
                                     taskSeason.Path);
                    }
                    catch (Exception e)
                    {
                        _logger.Error("IntroFingerprintExtract - Season failed: " + taskSeason.Name + " - " +
                                      taskSeason.Path);
                        _logger.Error(e.Message);
                        _logger.Debug(e.StackTrace);
                    }
                    finally
                    {
                        QueueManager.Tier2Semaphore.Release();

                        seasonProgressMap.TryRemove(taskSeason, out _);
                        var currentCount = Interlocked.Increment(ref processedSeasons);

                        if (!mediaInfoRestoreMode)
                        {
                            _logger.Info(
                                $"IntroFingerprintExtract - Season Progress {currentCount}/{totalSeasons} - Task {taskSeasonIndex}: {taskSeason.Path}");
                        }
                    }
                }, cancellationToken);
                seasonTasks.Add(seasonTask);
            }

            await Task.WhenAll(seasonTasks).ConfigureAwait(false);

            if (episodes.Count > 0) IsRunning = false;

            progress.Report(100.0);
            _logger.Info($"IntroFingerprintExtract - Number of seasons skipped: {seasonSkipCount}");
            _logger.Info($"IntroFingerprintExtract - Number of episodes skipped: {episodeSkipCount}");
            _logger.Info("IntroFingerprintExtract - Scheduled Task Complete");
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "IntroFingerprintExtractTask";

        public string Description => Resources.ResourceManager.GetString(
            "ExtractIntroFingerprintTask_Description_Extracts_intro_fingerprint_from_episodes",
            Plugin.Instance.DefaultUICulture);

        public string Name => "Extract Intro Fingerprint";
        //public string Name =>
        //    Resources.ResourceManager.GetString("ExtractIntroFingerprintTask_Name_Extract_Intro_Fingerprint",
        //        Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public static bool IsRunning { get; private set; }
    }
}
