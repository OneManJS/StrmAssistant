using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using StrmAssistant.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace StrmAssistant.IntroSkip
{
    public class PlaySessionMonitor
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly ISessionManager _sessionManager;
        private readonly ILogger _logger;

        private readonly TimeSpan _updateInterval = TimeSpan.FromSeconds(10);
        private readonly ConcurrentDictionary<string, PlaySessionData> _playSessionData = new ConcurrentDictionary<string, PlaySessionData>();
        private readonly ConcurrentDictionary<Episode, Task> _ongoingIntroUpdates = new ConcurrentDictionary<Episode, Task>();
        private readonly ConcurrentDictionary<Episode, Task> _ongoingCreditsUpdates = new ConcurrentDictionary<Episode, Task>();
        private readonly ConcurrentDictionary<Episode, DateTime> _lastIntroUpdateTimes = new ConcurrentDictionary<Episode, DateTime>();
        private readonly ConcurrentDictionary<Episode, DateTime> _lastCreditsUpdateTimes = new ConcurrentDictionary<Episode, DateTime>();
        private readonly object _introLock = new object();
        private readonly object _creditsLock = new object();

        private static Task _introSkipProcessTask;

        public static List<string> LibraryPathsInScope;
        public static User[] UsersInScope;
        public static HashSet<string> ClientsInScope;

        public PlaySessionMonitor(ILibraryManager libraryManager, IUserManager userManager,
            ISessionManager sessionManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _sessionManager = sessionManager;

            UpdateLibraryPathsInScope(Plugin.Instance.IntroSkipStore.GetOptions().LibraryScope);
            UpdateUsersInScope(Plugin.Instance.IntroSkipStore.GetOptions().UserScope);
            UpdateClientInScope(Plugin.Instance.IntroSkipStore.GetOptions().ClientScope);
        }

        public void UpdateLibraryPathsInScope(string currentScope)
        {
            var libraryIds = currentScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();
            LibraryPathsInScope = _libraryManager.GetVirtualFolders()
                .Where(f => libraryIds != null && libraryIds.Any()
                    ? libraryIds.Contains(f.Id)
                    : f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null)
                .SelectMany(l => l.Locations)
                .Select(ls => ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? ls
                    : ls + Path.DirectorySeparatorChar)
                .ToList();
        }

        public void UpdateLibraryPathsInScope()
        {
            UpdateLibraryPathsInScope(Plugin.Instance.IntroSkipStore.GetOptions().LibraryScope);
        }

        public void UpdateUsersInScope(string currentScope)
        {
            var userIds = Plugin.Instance.IntroSkipStore.GetOptions().UserScope
                ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray();

            var userQuery = new UserQuery
            {
                IsDisabled = false
            };

            if (userIds != null && userIds.Any())
            {
                userQuery.UserIds = userIds;
                UsersInScope = _userManager.GetUserList(userQuery);
            }
            else
            {
                UsersInScope = Array.Empty<User>();
            }
        }

        public void UpdateClientInScope(string currentScope)
        {
            ClientsInScope = new HashSet<string>(
                currentScope.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim()), StringComparer.OrdinalIgnoreCase);
        }

        public void Initialize()
        {
            Dispose();

            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;

            if (_introSkipProcessTask == null || _introSkipProcessTask.IsCompleted)
            {
                QueueManager.IntroSkipItemQueue.Clear();
                _introSkipProcessTask = QueueManager.IntroSkip_ProcessItemQueueAsync();
            }
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            if (!(e.Item is Episode episode) || !e.PlaybackPositionTicks.HasValue || !episode.IndexNumber.HasValue ||
                !(episode.ParentIndexNumber > 0))
            {
                return;
            }

            var options = Plugin.Instance.IntroSkipStore.GetOptions();

            _logger.Info("IntroSkip - Client Name: " + e.ClientName);
            _logger.Info("IntroSkip - Allowed Clients: " + options.ClientScope);

            var intoSkipLibraryScope = string.Join(", ",
                options.LibraryScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => options.LibraryList
                        .FirstOrDefault(option => option.Value == v)
                        ?.Name) ?? Enumerable.Empty<string>());
            _logger.Info("IntroSkip - LibraryScope is set to {0}",
                string.IsNullOrEmpty(intoSkipLibraryScope) ? "ALL" : intoSkipLibraryScope);

            var introSkipUserScope = string.Join(", ",
                options.UserScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => options.UserList
                        .FirstOrDefault(option => option.Value == v)
                        ?.Name) ?? Enumerable.Empty<string>());
            _logger.Info("IntroSkip - UserScope is set to {0}",
                string.IsNullOrEmpty(introSkipUserScope) ? "ALL" : introSkipUserScope);

            _playSessionData.TryRemove(e.PlaySessionId, out _);
            var playSessionData = GetPlaySessionData(e);
            if (playSessionData is null)
            {
                _logger.Info("IntroSkip - No detection as not in scope");
                return;
            }

            playSessionData.PlaybackStartTicks = e.PlaybackPositionTicks.Value;
            playSessionData.PreviousPositionTicks = e.PlaybackPositionTicks.Value;
            playSessionData.PreviousEventTime = DateTime.UtcNow;

            if (playSessionData.NoDetectionButReset)
            {
                _logger.Info("IntroSkip - No detection but allows pause to reset");
                return;
            }

            if (playSessionData.IntroStart.HasValue && playSessionData.CreditsStart.HasValue)
            {
                _logger.Info("IntroSkip - Intro marker and Credits marker already exist");
            }
            else
            {
                _logger.Info("Playback start time: " +
                             new TimeSpan(playSessionData.PlaybackStartTicks).ToString(@"hh\:mm\:ss\.fff"));
                _logger.Info("IntroSkip - Detection Started");
                _logger.Info("IntroSkip - Intro marker is " +
                             (playSessionData.IntroStart.HasValue ? "available" : "not available"));
                _logger.Info("IntroSkip - Credits marker is " +
                             (playSessionData.CreditsStart.HasValue ? "available" : "not available"));
            }
        }

        private void OnPlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            //_logger.Debug("IntroSkip - EventName: " + e.EventName);

            if (!(e.Item is Episode episode) || e.EventName != ProgressEvent.TimeUpdate && e.EventName != ProgressEvent.Unpause &&
                e.EventName != ProgressEvent.PlaybackRateChange && e.EventName != ProgressEvent.Pause ||
                !e.PlaybackPositionTicks.HasValue || e.PlaybackPositionTicks.Value == 0)
                return;

            var playSessionData = GetPlaySessionData(e);
            if (playSessionData is null) return;

            var currentPositionTicks = e.PlaybackPositionTicks.Value;
            var currentEventTime = DateTime.UtcNow;
            var introStart = playSessionData.IntroStart;
            var introEnd = playSessionData.IntroEnd;
            var creditsStart = playSessionData.CreditsStart;

            if (!playSessionData.NoDetectionButReset && e.EventName == ProgressEvent.TimeUpdate && !introEnd.HasValue)
            {
                var elapsedTime = (currentEventTime - playSessionData.PreviousEventTime).TotalSeconds;
                var positionTimeDiff = TimeSpan.FromTicks(currentPositionTicks - playSessionData.PreviousPositionTicks)
                    .TotalSeconds;

                if (Math.Abs(positionTimeDiff) - elapsedTime > 5 &&
                    currentPositionTicks < playSessionData.MaxIntroDurationTicks)
                {
                    _logger.Info(
                        positionTimeDiff > 0
                            ? "Fast-forward {0} seconds by {1}."
                            : "Rewind {0} seconds by {1}.", positionTimeDiff - elapsedTime, e.Session.UserName);

                    if (!playSessionData.FirstJumpPositionTicks.HasValue &&
                        TimeSpan.FromTicks(playSessionData.PlaybackStartTicks).TotalSeconds < 5 &&
                        positionTimeDiff > 0) //fast-forward only
                    {
                        playSessionData.FirstJumpPositionTicks = playSessionData.PreviousPositionTicks;
                        if (playSessionData.PreviousPositionTicks > playSessionData.MinOpeningPlotDurationTicks)
                        {
                            _logger.Info("First jump start time: " +
                                         new TimeSpan(playSessionData.FirstJumpPositionTicks.Value).ToString(
                                             @"hh\:mm\:ss\.fff"));
                            playSessionData.MaxIntroDurationTicks += playSessionData.PreviousPositionTicks;
                            _logger.Info("MaxIntroDurationSeconds is extended to: {0} ({1})",
                                TimeSpan.FromTicks(playSessionData.MaxIntroDurationTicks).TotalSeconds
                                , TimeSpan.FromTicks(playSessionData.MaxIntroDurationTicks).ToString(@"hh\:mm\:ss\.fff"));
                        }
                    }

                    playSessionData.LastJumpPositionTicks = currentPositionTicks;
                    _logger.Info("Last jump to time: " +
                                 new TimeSpan(playSessionData.LastJumpPositionTicks.Value).ToString(@"hh\:mm\:ss\.fff"));
                }

                if (currentPositionTicks >= playSessionData.MaxIntroDurationTicks)
                {
                    if (playSessionData.LastJumpPositionTicks.HasValue)
                    {
                        UpdateIntroTask(episode, e.Session, playSessionData,
                            playSessionData.FirstJumpPositionTicks.HasValue &&
                            playSessionData.FirstJumpPositionTicks.Value > playSessionData.MinOpeningPlotDurationTicks
                                ? playSessionData.FirstJumpPositionTicks.Value
                                : new TimeSpan(0, 0, 0).Ticks,
                            playSessionData.LastJumpPositionTicks.Value);
                    }
                }

                playSessionData.PreviousPositionTicks = currentPositionTicks;
                playSessionData.PreviousEventTime = currentEventTime;
            }

            if (e.EventName == ProgressEvent.Pause)
            {
                playSessionData.LastPauseEventTime = currentEventTime;
                return;
            }

            if (e.EventName == ProgressEvent.PlaybackRateChange)
            {
                playSessionData.LastPlaybackRateChangeEventTime = currentEventTime;
                return;
            }

            if (e.EventName == ProgressEvent.Unpause && playSessionData.LastPauseEventTime.HasValue &&
                (currentEventTime - playSessionData.LastPauseEventTime.Value).TotalMilliseconds <
                (playSessionData.LastPlaybackRateChangeEventTime.HasValue ? 1500 : 500))
            {
                playSessionData.LastPauseEventTime = null;
                return;
            }

            if (!playSessionData.NoDetectionButReset && e.EventName == ProgressEvent.Unpause &&
                playSessionData.LastPauseEventTime.HasValue &&
                (currentEventTime - playSessionData.LastPauseEventTime.Value).TotalMilliseconds > 500 &&
                (currentEventTime - playSessionData.LastPauseEventTime.Value).TotalMilliseconds < 5000 &&
                introStart.HasValue && introStart.Value < currentPositionTicks && introEnd.HasValue &&
                currentPositionTicks < Math.Max(playSessionData.MaxIntroDurationTicks, introEnd.Value) &&
                Math.Abs(TimeSpan.FromTicks(currentPositionTicks - introEnd.Value).TotalMilliseconds) >
                (playSessionData.LastPlaybackRateChangeEventTime.HasValue ? 500 : 0))
            {
                UpdateIntroTask(episode, e.Session, playSessionData, introStart.Value, currentPositionTicks);
            }

            if (playSessionData.NoDetectionButReset && e.EventName == ProgressEvent.Unpause &&
                playSessionData.LastPauseEventTime.HasValue &&
                (currentEventTime - playSessionData.LastPauseEventTime.Value).TotalMilliseconds > 500 &&
                (currentEventTime - playSessionData.LastPauseEventTime.Value).TotalMilliseconds < 5000 &&
                currentPositionTicks < playSessionData.MaxIntroDurationTicks &&
                (!introEnd.HasValue ||
                 Math.Abs(TimeSpan.FromTicks(currentPositionTicks - introEnd.Value).TotalMilliseconds) >
                 (playSessionData.LastPlaybackRateChangeEventTime.HasValue ? 500 : 0)))
            {
                UpdateIntroTask(episode, e.Session, playSessionData, new TimeSpan(0, 0, 0).Ticks,
                    currentPositionTicks);
            }

            if (e.EventName == ProgressEvent.Unpause && episode.RunTimeTicks.HasValue &&
                playSessionData.LastPauseEventTime.HasValue &&
                (currentEventTime - playSessionData.LastPauseEventTime.Value).TotalMilliseconds > 500 &&
                (currentEventTime - playSessionData.LastPauseEventTime.Value).TotalMilliseconds < 5000 &&
                currentPositionTicks > episode.RunTimeTicks - playSessionData.MaxCreditsDurationTicks &&
                (creditsStart.HasValue || playSessionData.NoDetectionButReset))
            {
                if (episode.RunTimeTicks.Value > currentPositionTicks)
                {
                    UpdateCreditsTask(episode, e.Session, playSessionData,
                        episode.RunTimeTicks.Value - currentPositionTicks);
                }
            }
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            if (!(e.Item is Episode episode) || !e.PlaybackPositionTicks.HasValue || !episode.RunTimeTicks.HasValue) return;

            var playSessionData = GetPlaySessionData(e);
            if (playSessionData is null) return;

            if (!playSessionData.CreditsStart.HasValue)
            {
                var currentPositionTicks = e.PlaybackPositionTicks.Value;
                if (currentPositionTicks > episode.RunTimeTicks - playSessionData.MaxCreditsDurationTicks)
                {
                    if (episode.RunTimeTicks.Value > currentPositionTicks)
                    {
                        UpdateCreditsTask(episode, e.Session, playSessionData,
                            episode.RunTimeTicks.Value - currentPositionTicks);
                    }
                }
            }

            _playSessionData.TryRemove(e.PlaySessionId, out _);
            _lastIntroUpdateTimes.TryRemove(episode, out _);
            _lastCreditsUpdateTimes.TryRemove(episode, out _);
        }

        private PlaySessionData GetPlaySessionData(PlaybackProgressEventArgs e)
        {
            if (!IsLibraryInScope(e.Item) || !IsUserInScope(e.Session.UserInternalId) ||
                !IsClientInScope(e.ClientName)) return null;
            
            var playSessionId = e.PlaySessionId;

            if (!_playSessionData.ContainsKey(playSessionId))
            {
                _playSessionData[playSessionId] = new PlaySessionData(e.Item);
            }

            var playSessionData = _playSessionData[playSessionId];

            return playSessionData;
        }

        public bool IsLibraryInScope(BaseItem item)
        {
            if (string.IsNullOrEmpty(item.ContainingFolderPath)) return false;

            var isLibraryInScope = LibraryPathsInScope.Any(l => item.ContainingFolderPath.StartsWith(l));

            return isLibraryInScope;
        }

        public bool IsUserInScope(long userInternalId)
        {
            if (!UsersInScope.Any())
                return true;

            var isUserInScope = UsersInScope.Any(u => u.InternalId == userInternalId);

            return isUserInScope;
        }

        public bool IsClientInScope(string clientName)
        {
            return ClientsInScope.Any(c => clientName.Contains(c, StringComparison.OrdinalIgnoreCase));
        }

        private void UpdateIntroTask(Episode episode, SessionInfo session, PlaySessionData playSessionData,
            long introStartPositionTicks,
            long introEndPositionTicks)
        {
            var now = DateTime.UtcNow;

            lock (_introLock)
            {
                if (_ongoingIntroUpdates.ContainsKey(episode))
                {
                    return;
                }

                if (_lastIntroUpdateTimes.TryGetValue(episode, out var lastUpdateTime))
                {
                    if (now - lastUpdateTime < _updateInterval)
                    {
                        return;
                    }
                }

                var task = new Task(() =>
                {
                    try
                    {
                        Plugin.ChapterApi.UpdateIntro(episode, session, introStartPositionTicks,
                            introEndPositionTicks);
                        playSessionData.IntroStart = Plugin.ChapterApi.GetIntroStart(episode);
                        playSessionData.IntroEnd = Plugin.ChapterApi.GetIntroEnd(episode);
                    }
                    catch (Exception e)
                    {
                        _logger.Debug(e.Message);
                        _logger.Debug(e.StackTrace);
                    }
                });

                if (_ongoingIntroUpdates.TryAdd(episode, task))
                {
                    task.ContinueWith(t => { _ongoingIntroUpdates.TryRemove(episode, out _); },
                        TaskContinuationOptions.ExecuteSynchronously);
                    _lastIntroUpdateTimes[episode] = now;
                    task.Start();
                }
            }
        }

        private void UpdateCreditsTask(Episode episode, SessionInfo session, PlaySessionData playSessionData,
            long creditsDurationTicks)
        {
            var now = DateTime.UtcNow;

            lock (_creditsLock)
            {
                if (_ongoingCreditsUpdates.ContainsKey(episode))
                {
                    return;
                }

                if (_lastCreditsUpdateTimes.TryGetValue(episode, out var lastUpdateTime))
                {
                    if (now - lastUpdateTime < _updateInterval)
                    {
                        return;
                    }
                }

                var task = new Task(() =>
                {
                    try
                    {
                        Plugin.ChapterApi.UpdateCredits(episode, session, creditsDurationTicks);
                        playSessionData.CreditsStart = Plugin.ChapterApi.GetCreditsStart(episode);
                    }
                    catch (Exception e)
                    {
                        _logger.Debug(e.Message);
                        _logger.Debug(e.StackTrace);
                    }
                });

                if (_ongoingCreditsUpdates.TryAdd(episode, task))
                {
                    task.ContinueWith(t => { _ongoingCreditsUpdates.TryRemove(episode, out _); },
                        TaskContinuationOptions.ExecuteSynchronously);
                    _lastCreditsUpdateTimes[episode] = now;
                    task.Start();
                }
            }
        }

        public void Dispose()
        {
            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            if (QueueManager.IntroSkipTokenSource != null) QueueManager.IntroSkipTokenSource.Cancel();
        }
    }
}
