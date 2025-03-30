using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.IntroSkip;
using StrmAssistant.Mod;
using StrmAssistant.Options;
using StrmAssistant.Options.Store;
using StrmAssistant.Options.View;
using StrmAssistant.Properties;
using StrmAssistant.ScheduledTask;
using StrmAssistant.Web.Helper;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using static StrmAssistant.Options.ExperienceEnhanceOptions;
using static StrmAssistant.Options.GeneralOptions;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant
{
    public class Plugin : BasePlugin, IHasThumbImage, IHasUIPages
    {
        private List<IPluginUIPageController> _pages;
        public readonly PluginOptionsStore MainOptionsStore;
        public readonly MediaInfoExtractOptionsStore MediaInfoExtractStore;
        public readonly MetadataEnhanceOptionsStore MetadataEnhanceStore;
        public readonly IntroSkipOptionsStore IntroSkipStore;
        public readonly ExperienceEnhanceOptionsStore ExperienceEnhanceStore;

        public static Plugin Instance { get; private set; }
        public static LibraryApi LibraryApi { get; private set; }
        public static MediaInfoApi MediaInfoApi { get; private set; }
        public static ChapterApi ChapterApi { get; private set; }
        public static FingerprintApi FingerprintApi { get; private set; }
        public static NotificationApi NotificationApi { get; private set; }
        public static SubtitleApi SubtitleApi { get; private set; }
        public static PlaySessionMonitor PlaySessionMonitor { get; private set; }
        public static MetadataApi MetadataApi { get; private set; }
        public static VideoThumbnailApi VideoThumbnailApi { get; private set; }

        private readonly Guid _id = new Guid("63c322b7-a371-41a3-b11f-04f8418b37d8");

        public readonly ILogger Logger;
        public readonly IApplicationHost ApplicationHost;
        public readonly IApplicationPaths ApplicationPaths;

        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly ITaskManager _taskManager;
        private readonly ISessionManager _sessionManager;

        public Plugin(IApplicationHost applicationHost, IApplicationPaths applicationPaths, ILogManager logManager,
            IFileSystem fileSystem, ILibraryManager libraryManager, ISessionManager sessionManager,
            IItemRepository itemRepository, INotificationManager notificationManager, ILibraryMonitor libraryMonitor,
            IMediaSourceManager mediaSourceManager, IMediaMountManager mediaMountManager,
            IProviderManager providerManager, IMediaProbeManager mediaProbeManager,
            ILocalizationManager localizationManager, IUserManager userManager, IUserDataManager userDataManager,
            IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder, IJsonSerializer jsonSerializer,
            IHttpClient httpClient, IServerApplicationHost serverApplicationHost,
            IServerConfigurationManager configurationManager, ITaskManager taskManager,
            IImageExtractionManager imageExtractionManager, IServerApplicationPaths serverApplicationPaths)
        {
            Instance = this;
            Logger = logManager.GetLogger(Name);
            Logger.Info("Plugin is getting loaded.");
            ApplicationHost = applicationHost;
            ApplicationPaths = applicationPaths;

            _libraryManager = libraryManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
            _providerManager = providerManager;
            _sessionManager = sessionManager;
            _fileSystem = fileSystem;
            _taskManager = taskManager;

            MainOptionsStore = new PluginOptionsStore(applicationHost, Logger, Name);
            MediaInfoExtractStore =
                new MediaInfoExtractOptionsStore(applicationHost, Logger, Name + "_" + nameof(MediaInfoExtractOptions));
            MetadataEnhanceStore =
                new MetadataEnhanceOptionsStore(applicationHost, Logger, Name + "_" + nameof(MetadataEnhanceOptions));
            IntroSkipStore = new IntroSkipOptionsStore(applicationHost, Logger, Name + "_" + nameof(IntroSkipOptions));
            ExperienceEnhanceStore = new ExperienceEnhanceOptionsStore(applicationHost, Logger,
                Name + "_" + nameof(ExperienceEnhanceOptions));

            if (MainOptionsStore.GetOptions().AboutOptions.DebugMode)
            {
                DebugMode = true;
                MainOptionsStore.GetOptions().AboutOptions.DebugMode = false;
                MainOptionsStore.SavePluginOptionsSuppress();
            }
            else if (Debugger.IsAttached)
            {
                DebugMode = true;
            }

            if (IsModSupported) PatchManager.Initialize();

            LibraryApi = new LibraryApi(libraryManager, fileSystem, mediaMountManager, userManager);
            MediaInfoApi = new MediaInfoApi(libraryManager, fileSystem, providerManager, mediaSourceManager,
                itemRepository, jsonSerializer, libraryMonitor);
            ChapterApi = new ChapterApi(libraryManager, itemRepository, jsonSerializer);
            FingerprintApi = new FingerprintApi(libraryManager, fileSystem, applicationPaths, ffmpegManager,
                mediaEncoder, mediaMountManager, jsonSerializer, serverApplicationHost);
            PlaySessionMonitor = new PlaySessionMonitor(libraryManager, userManager, sessionManager);
            NotificationApi = new NotificationApi(notificationManager, userManager, sessionManager);
            SubtitleApi = new SubtitleApi(libraryManager, fileSystem, mediaProbeManager, localizationManager,
                itemRepository);
            MetadataApi = new MetadataApi(libraryManager, fileSystem, configurationManager, localizationManager,
                jsonSerializer, httpClient);
            VideoThumbnailApi = new VideoThumbnailApi(libraryManager, fileSystem, imageExtractionManager, itemRepository,
                mediaMountManager, serverApplicationPaths, libraryMonitor, ffmpegManager);
            ShortcutMenuHelper.Initialize(configurationManager);

            if (MainOptionsStore.GetOptions().GeneralOptions.CatchupMode)
            {
                UpdateCatchupScope(MainOptionsStore.GetOptions().GeneralOptions.CatchupTaskScope);
                QueueManager.Initialize();
            }

            if (IntroSkipStore.GetOptions().EnableIntroSkip)
            {
                UpdateIntroSkipPreferences(IntroSkipStore.GetOptions().IntroSkipPreferences);
                PlaySessionMonitor.Initialize();
            }

            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnItemUpdated;
            _libraryManager.ItemRemoved += OnItemRemoved;
            _providerManager.RefreshCompleted += OnRefreshCompleted;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;
            _userManager.UserCreated += OnUserCreated;
            _userManager.UserDeleted += OnUserDeleted;
            _userManager.UserConfigurationUpdated += OnUserConfigurationUpdated;
            _userDataManager.UserDataSaved += OnUserDataSaved;
            CollectionFolder.LibraryOptionsUpdated += OnLibraryOptionsUpdated;
        }

        private void OnRefreshCompleted(object sender, GenericEventArgs<RefreshProgressInfo> e)
        {
            var item = e.Argument.Item;

            if (MediaInfoExtractStore.GetOptions().PersistMediaInfoMode != PersistMediaInfoOption.None.ToString() &&
                (item is Video || item is Audio) && item.DateLastRefreshed != DateTimeOffset.MinValue)
            {
                var directoryService = new DirectoryService(Logger, _fileSystem);

                if (!LibraryApi.HasMediaInfo(item))
                {
                    _ = MediaInfoApi.DeserializeMediaInfo(item, directoryService, "OnRefreshCompleted Restore", false)
                        .ConfigureAwait(false);
                }
                else
                {
                    _ = MediaInfoApi.SerializeMediaInfo(item.InternalId, directoryService, false,
                        "OnRefreshCompleted Non-existent").ConfigureAwait(false);
                }
            }

            if (_libraryManager.IsScanRunning) return;

            var options = ExperienceEnhanceStore.GetOptions();

            if (options.MergeMultiVersion && item.IsTopParent)
            {
                var library = e.Argument.CollectionFolders.OfType<CollectionFolder>().FirstOrDefault();

                if (library != null && (library.CollectionType == CollectionType.Movies.ToString() ||
                                        library.CollectionType == CollectionType.TvShows.ToString() &&
                                        options.MergeSeriesPreference == MergeSeriesScopeOption.GlobalScope ||
                                        library.CollectionType is null))
                {
                    MergeMultiVersionTask.CurrentScanLibrary.Value = library;

                    var mergeMoviesTask = _taskManager.ScheduledTasks.FirstOrDefault(t =>
                        t.ScheduledTask is MergeMultiVersionTask);

                    if (mergeMoviesTask != null)
                    {
                        _ = _taskManager.Execute(mergeMoviesTask, new TaskOptions());
                    }
                }
            }
        }

        private void OnUserCreated(object sender, GenericEventArgs<User> e)
        {
            LibraryApi.FetchUsers();
        }

        private void OnUserDeleted(object sender, GenericEventArgs<User> e)
        {
            LibraryApi.FetchUsers();
        }

        private void OnUserConfigurationUpdated(object sender, GenericEventArgs<User> e)
        {
            if (e.Argument.Policy.IsAdministrator) LibraryApi.FetchAdminOrderedViews();
        }

        private void OnLibraryOptionsUpdated(object sender, GenericEventArgs<Tuple<CollectionFolder, LibraryOptions>> e)
        {
            var library = e.Argument.Item1;

            if (!LibraryApi.ExcludedCollectionTypes.Contains(library.CollectionType))
            {
                LibraryApi.UpdateLibraryPathsInScope();

                if (library.CollectionType == CollectionType.TvShows.ToString() || library.CollectionType is null)
                {
                    PlaySessionMonitor.UpdateLibraryPathsInScope();
                    FingerprintApi.UpdateLibraryPathsInScope();

                    if (IntroSkipStore.GetOptions().UnlockIntroSkip)
                        FingerprintApi.UpdateLibraryIntroDetectionFingerprintLength();

                    if (ExperienceEnhanceStore.GetOptions().MergeMultiVersion)
                        LibraryApi.EnsureLibraryEnabledAutomaticSeriesGrouping();
                }
            }
        }

        private async void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            try
            {
                if (e.Item is Video || e.Item is Audio)
                {
                    var deserializeResult = LibraryApi.HasMediaInfo(e.Item);

                    if (MediaInfoExtractStore.GetOptions().PersistMediaInfoMode != PersistMediaInfoOption.None.ToString())
                    {
                        var directoryService = new DirectoryService(Logger, _fileSystem);

                        if (!deserializeResult)
                        {
                            deserializeResult = await MediaInfoApi.DeserializeMediaInfo(e.Item, directoryService,
                                "OnItemAdded Restore", true).ConfigureAwait(false);
                        }
                        else
                        {
                            _ = MediaInfoApi.SerializeMediaInfo(e.Item.InternalId, directoryService, true,
                                "OnItemAdded Overwrite").ConfigureAwait(false);
                        }
                    }

                    if (MainOptionsStore.PluginOptions.GeneralOptions.CatchupMode)
                    {
                        if (IntroSkipStore.IntroSkipOptions.UnlockIntroSkip &&
                            IsCatchupTaskSelected(CatchupTask.Fingerprint) &&
                            e.Item is Episode && FingerprintApi.IsLibraryInScope(e.Item) &&
                            (!deserializeResult || FingerprintApi.IsExtractNeeded(e.Item)))
                        {
                            QueueManager.FingerprintItemQueue.Enqueue(e.Item);
                        }
                        else
                        {
                            if (IsCatchupTaskSelected(CatchupTask.MediaInfo) && !deserializeResult)
                            {
                                QueueManager.MediaInfoExtractItemQueue.Enqueue(e.Item);
                            }

                            if (IsCatchupTaskSelected(CatchupTask.IntroSkip) &&
                                e.Item is Episode && PlaySessionMonitor.IsLibraryInScope(e.Item))
                            {
                                if (!deserializeResult)
                                {
                                    QueueManager.MediaInfoExtractItemQueue.Enqueue(e.Item);
                                }
                                else if (e.Item is Episode episode && ChapterApi.SeasonHasIntroCredits(episode))
                                {
                                    QueueManager.IntroSkipItemQueue.Enqueue(episode);
                                }
                            }
                        }

                        if (IsCatchupTaskSelected(CatchupTask.EpisodeRefresh) && e.Item is Episode ep &&
                            LibraryApi.IsPremiereDateInScope(ep, DateTimeOffset.UtcNow.AddDays(-90), false))
                        {
                            QueueManager.EpisodeRefreshItemQueue.Enqueue(ep);
                        }
                    }
                }

                if (e.Item is Movie || e.Item is Series || e.Item is Episode)
                {
                    NotificationApi.FavoritesUpdateSendNotification(e.Item);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex.Message);
                Logger.Debug(ex.StackTrace);
            }
        }

        private void OnPlaybackStopped(object sender, PlaybackProgressEventArgs e)
        {
            if (MediaInfoExtractStore.MediaInfoExtractOptions.ExclusiveExtract &&
                IsExclusiveFeatureSelected(MediaInfoExtractOptions.ExclusiveControl.ExtractAlternative))
            {
                var targetItem = MediaInfoApi.GetItemByMediaSourceId(e.Item, e.MediaSourceId);

                if (targetItem != null)
                {
                    var refreshOptions = new MetadataRefreshOptions(new DirectoryService(Logger, _fileSystem))
                    {
                        EnableRemoteContentProbe = true,
                        ReplaceAllMetadata = true,
                        EnableThumbnailImageExtraction = false,
                        EnableSubtitleDownloading = false,
                        ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                        MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                        ReplaceAllImages = false
                    };

                    MediaInfoApi.QueueRefreshAlternateVersions(targetItem, refreshOptions, false);
                }
            }
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs e)
        {
            if (MetadataEnhanceStore.GetOptions().EnhanceMovieDbPerson &&
                (e.UpdateReason & (ItemUpdateType.MetadataDownload | ItemUpdateType.MetadataImport)) != 0)
            {
                if (e.Item is Season season && season.IndexNumber > 0)
                {
                    MetadataApi.UpdateSeriesPeople(season.Parent as Series);
                }
                else if (e.Item is Series series)
                {
                    MetadataApi.UpdateSeriesPeople(series);
                }
            }
        }

        private void OnItemRemoved(object sender, ItemChangeEventArgs e)
        {
            if ((e.Item is Video || e.Item is Audio) && MediaInfoExtractStore.GetOptions().PersistMediaInfoMode !=
                PersistMediaInfoOption.Restore.ToString())
            {
                var directoryService = new DirectoryService(Logger, _fileSystem);
                MediaInfoApi.DeleteMediaInfoJson(e.Item, directoryService, "Item Removed Event");
            }

            if (e.Item is CollectionFolder library)
            {
                if (library.CollectionType == CollectionType.BoxSets.ToString() &&
                    !ExperienceEnhanceStore.GetOptions().UIFunctionOptions.NoBoxsetsAutoCreation)
                {
                    PatchManager.NoBoxsetsAutoCreation.Patch();
                    ExperienceEnhanceStore.GetOptions().UIFunctionOptions.NoBoxsetsAutoCreation = true;
                    ExperienceEnhanceStore.SavePluginOptionsSuppress();
                }

                if (!LibraryApi.ExcludedCollectionTypes.Contains(library.CollectionType))
                {
                    LibraryApi.UpdateLibraryPathsInScope();

                    if (library.CollectionType == CollectionType.TvShows.ToString() || library.CollectionType is null)
                    {
                        PlaySessionMonitor.UpdateLibraryPathsInScope();
                        FingerprintApi.UpdateLibraryPathsInScope();
                    }
                }
            }
        }

        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            if (e.UserData.IsFavorite)
            {
                if (MainOptionsStore.PluginOptions.GeneralOptions.CatchupMode &&
                    IntroSkipStore.IntroSkipOptions.UnlockIntroSkip && IsCatchupTaskSelected(CatchupTask.Fingerprint) &&
                    FingerprintApi.IsLibraryInScope(e.Item) && (e.Item is Series || e.Item is Episode))
                {
                    QueueManager.FingerprintItemQueue.Enqueue(e.Item);
                }
                else if (MainOptionsStore.PluginOptions.GeneralOptions.CatchupMode &&
                         IsCatchupTaskSelected(CatchupTask.MediaInfo))
                {
                    QueueManager.MediaInfoExtractItemQueue.Enqueue(e.Item);
                }
            }
        }

        public override void OnUninstalling()
        {
            if (MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearch)
            {
                _ = NotificationApi.SendMessageToAdmins(
                    $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {Resources.Uninstall_Warning}", 10000);
            }

            base.OnUninstalling();
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public override string Description => "Extract MediaInfo and Enable IntroSkip";

        public override Guid Id => _id;

        public sealed override string Name => "Strm Assistant";

        public string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString();

        public string UserAgent => $"{Name}/{CurrentVersion}";

        public CultureInfo DefaultUICulture =>
            new CultureInfo(MainOptionsStore.GetOptions().AboutOptions.DefaultUICulture);

        public bool DebugMode;

        public bool IsModSupported => RuntimeInformation.ProcessArchitecture == Architecture.X64;

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Properties.thumb.png");
        }

        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers
        {
            get
            {
                if (_pages == null)
                {
                    _pages = new List<IPluginUIPageController>
                    {
                        new MainPageController(GetPluginInfo(), _libraryManager, MainOptionsStore,
                            MediaInfoExtractStore, MetadataEnhanceStore, IntroSkipStore, ExperienceEnhanceStore)
                    };
                }

                return _pages.AsReadOnly();
            }
        }
    }
}
