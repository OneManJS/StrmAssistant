using Emby.Media.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using StrmAssistant.Mod;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.CommonUtility;
using static StrmAssistant.Common.LanguageUtility;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.MetadataEnhanceOptions;
using static StrmAssistant.Options.Utility;
using CollectionExtensions = System.Collections.Generic.CollectionExtensions;

namespace StrmAssistant.Common
{
    public class LibraryApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IMediaMountManager _mediaMountManager;
        private readonly IUserManager _userManager;

        public static ExtraType[] IncludeExtraTypes =
        {
            ExtraType.AdditionalPart, ExtraType.BehindTheScenes, ExtraType.Clip, ExtraType.DeletedScene,
            ExtraType.Interview, ExtraType.Sample, ExtraType.Scene, ExtraType.ThemeSong, ExtraType.ThemeVideo,
            ExtraType.Trailer
        };

        public static MediaContainers[] ExcludeMediaContainers
        {
            get
            {
                return Plugin.Instance.MediaInfoExtractStore.GetOptions().ImageCaptureExcludeMediaContainers
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(c =>
                        Enum.TryParse<MediaContainers>(c.Trim(), true, out var container)
                            ? container
                            : (MediaContainers?)null)
                    .Where(container => container.HasValue)
                    .Select(container => container.Value)
                    .Concat(new[] { MediaContainers.Iso })
                    .Distinct()
                    .ToArray();
            }
        }

        public static string[] ExcludeMediaExtensions
        {
            get
            {
                return Plugin.Instance.MediaInfoExtractStore.GetOptions()
                    .ImageCaptureExcludeMediaContainers.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .SelectMany(c =>
                    {
                        if (Enum.TryParse<MediaContainers>(c.Trim(), true, out var container))
                        {
                            var aliases = container.GetAliases();
                            return aliases?.Where(a => !string.IsNullOrWhiteSpace(a)) ?? Array.Empty<string>();
                        }

                        return Array.Empty<string>();
                    })
                    .Concat(MediaContainers.Iso.GetAliases())
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Distinct()
                    .ToArray();
            }
        }

        public static readonly HashSet<string> ExcludedCollectionTypes = new HashSet<string>
        {
            CollectionType.Books.ToString(),
            CollectionType.Photos.ToString(),
            CollectionType.Games.ToString(),
            CollectionType.LiveTv.ToString(),
            CollectionType.Playlists.ToString(),
            CollectionType.BoxSets.ToString()
        };

        public static List<string> LibraryPathsInScope;
        public static Dictionary<User, bool> AllUsers = new Dictionary<User, bool>();
        public static string[] AdminOrderedViews = Array.Empty<string>();

        public LibraryApi(ILibraryManager libraryManager, IFileSystem fileSystem, IMediaMountManager mediaMountManager,
            IUserManager userManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _mediaMountManager = mediaMountManager;
            _userManager = userManager;

            UpdateLibraryPathsInScope(Plugin.Instance.MediaInfoExtractStore.GetOptions().LibraryScope);
            FetchUsers();
        }

        public void UpdateLibraryPathsInScope(string currentScope)
        {
            var validLibraryIds = GetValidLibraryIds(currentScope);

            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => !ExcludedCollectionTypes.Contains(f.CollectionType) &&
                            (!validLibraryIds.Any() || validLibraryIds.Contains(f.Id)))
                .ToList();

            LibraryPathsInScope = libraries.SelectMany(l => l.Locations)
                .Select(ls => ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? ls
                    : ls + Path.DirectorySeparatorChar)
                .ToList();
        }

        public void UpdateLibraryPathsInScope()
        {
            UpdateLibraryPathsInScope(Plugin.Instance.MediaInfoExtractStore.GetOptions().LibraryScope);
        }

        public bool IsLibraryInScope(BaseItem item)
        {
            return !string.IsNullOrEmpty(item.Path) && LibraryPathsInScope.Any(l => item.Path.StartsWith(l));
        }

        public void FetchUsers()
        {
            var userQuery = new UserQuery
            {
                IsDisabled = false,
            };
            var allUsers = _userManager.GetUserList(userQuery);

            foreach (var user in allUsers)
            {
                AllUsers[user] = _userManager.GetUserById(user.InternalId).Policy.IsAdministrator;
            }

            FetchAdminOrderedViews();
        }

        public void FetchAdminOrderedViews()
        {
            var firstAdmin = AllUsers.Where(kvp => kvp.Value).Select(u => u.Key).OrderBy(u => u.DateCreated)
                .FirstOrDefault();
            AdminOrderedViews = firstAdmin?.Configuration.OrderedViews ?? AdminOrderedViews;
        }

        public bool HasMediaInfo(BaseItem item)
        {
            if (!item.RunTimeTicks.HasValue) return false;

            if (item.Size == 0) return false;

            return item.GetMediaStreams().Any(i => i.Type == MediaStreamType.Video || i.Type == MediaStreamType.Audio);
        }

        public bool ImageCaptureEnabled(BaseItem item)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);
            var typeName = item.ExtraType == null ? item.GetType().Name : item.DisplayParent.GetType().Name;
            var typeOptions = libraryOptions.GetTypeOptions(typeName);

            return typeOptions?.ImageFetchers?.Any(f => f == "Image Capture" || f == "Embedded Images") is true;
        }

        private List<VirtualFolderInfo> GetLibrariesWithImageCapture(List<VirtualFolderInfo> libraries)
        {
            var librariesWithImageCapture = libraries.Where(l => l.LibraryOptions.TypeOptions.Any(t =>
                    (t.ImageFetchers.Contains("Image Capture") &&
                     ((l.CollectionType == CollectionType.TvShows.ToString() && t.Type == nameof(Episode)) ||
                      (l.CollectionType == CollectionType.Movies.ToString() && t.Type == nameof(Movie)) ||
                      (l.CollectionType == CollectionType.HomeVideos.ToString() && t.Type == nameof(Video)) ||
                      (l.CollectionType == CollectionType.MusicVideos.ToString() && t.Type == nameof(MusicVideo)) ||
                      (l.CollectionType is null && (t.Type == nameof(Episode) || t.Type == nameof(Movie))))) ||
                    (t.ImageFetchers.Contains("Embedded Images") &&
                     ((l.CollectionType == CollectionType.Music.ToString() && t.Type == nameof(Audio)) ||
                      (l.CollectionType == CollectionType.AudioBooks.ToString() && t.Type == nameof(Audio))))))
                .ToList();

            return librariesWithImageCapture;
        }

        public List<BaseItem> FetchExtractQueueItems(List<BaseItem> items)
        {
            var libraryIds = Plugin.Instance.MediaInfoExtractStore.GetOptions().LibraryScope
                ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();

            var includeFavorites = libraryIds == null || !libraryIds.Any() || libraryIds.Contains("-1");
            var includeExtra = Plugin.Instance.MediaInfoExtractStore.GetOptions().IncludeExtra;
            var enableImageCapture = Plugin.Instance.MediaInfoExtractStore.GetOptions().EnableImageCapture;

            var resultItems = new List<BaseItem>();

            if (IsCatchupTaskSelected(GeneralOptions.CatchupTask.MediaInfo))
            {
                if (includeFavorites) resultItems = ExpandFavorites(items, true, true, enableImageCapture);

                var incomingItems = items.Where(item => item is Video || item is Audio).ToList();

                if (libraryIds == null || !libraryIds.Any())
                {
                    resultItems = resultItems.Concat(incomingItems).ToList();
                }

                if (libraryIds != null && libraryIds.Any(id => id != "-1") && LibraryPathsInScope.Any())
                {
                    var filteredItems = incomingItems
                        .Where(i => LibraryPathsInScope.Any(p => i.ContainingFolderPath.StartsWith(p)))
                        .ToList();
                    resultItems = resultItems.Concat(filteredItems).ToList();
                }
            }

            if (IsCatchupTaskSelected(GeneralOptions.CatchupTask.IntroSkip))
            {
                var episodesIntroSkip = Plugin.ChapterApi.SeasonHasIntroCredits(items.OfType<Episode>().ToList());
                resultItems = resultItems.Concat(episodesIntroSkip).ToList();
            }

            resultItems = resultItems.Where(i => i.ExtraType is null).GroupBy(i => i.InternalId).Select(g => g.First())
                .ToList();

            var unprocessedItems = FilterUnprocessed(resultItems
                .Concat(includeExtra ? resultItems.SelectMany(f => f.GetExtras(IncludeExtraTypes)) : Enumerable.Empty<BaseItem>())
                .ToList());
            var orderedItems = OrderByDescending(unprocessedItems);

            return orderedItems;
        }

        public List<BaseItem> FetchPreExtractTaskItems()
        {
            var libraryIds = Plugin.Instance.MediaInfoExtractStore.GetOptions().LibraryScope
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => !libraryIds.Any() || libraryIds.Contains(f.Id)).ToList();
            var librariesWithImageCapture = GetLibrariesWithImageCapture(libraries);

            _logger.Info("MediaInfoExtract - LibraryScope: " +
                         (libraryIds.Any() ? string.Join(", ", libraries.Select(l => l.Name)) : "ALL"));

            var includeExtra = Plugin.Instance.MediaInfoExtractStore.GetOptions().IncludeExtra;
            _logger.Info("Include Extra: " + includeExtra);
            var enableImageCapture = Plugin.Instance.MediaInfoExtractStore.GetOptions().EnableImageCapture;

            var favoritesWithExtra = Array.Empty<BaseItem>();
            if (libraryIds.Contains("-1"))
            {
                var favorites = AllUsers.Select(e => e.Key)
                    .SelectMany(user => _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        User = user,
                        IsFavorite = true
                    })).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();

                var expanded = ExpandFavorites(favorites, false, true, enableImageCapture);

                favoritesWithExtra = expanded.Concat(includeExtra
                        ? expanded.SelectMany(f => f.GetExtras(IncludeExtraTypes))
                        : Enumerable.Empty<BaseItem>())
                    .ToArray();
            }

            var items = Array.Empty<BaseItem>();
            var extras = Array.Empty<BaseItem>();

            if (!libraryIds.Any() || libraryIds.Any(id => id != "-1"))
            {
                var itemsMediaInfoQuery = new InternalItemsQuery
                {
                    HasPath = true,
                    HasAudioStream = false,
                    MediaTypes = new[] { MediaType.Video, MediaType.Audio }
                };

                if (libraryIds.Any(id => id != "-1") && libraries.Any())
                {
                    itemsMediaInfoQuery.PathStartsWithAny = libraries.SelectMany(l => l.Locations).Select(ls =>
                        ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                            ? ls
                            : ls + Path.DirectorySeparatorChar).ToArray();
                }

                var itemsMediaInfo = _libraryManager.GetItemList(itemsMediaInfoQuery);

                var itemsImageCaptureQuery = new InternalItemsQuery
                {
                    HasPath = true,
                    MediaTypes = new[] { MediaType.Video, MediaType.Audio }
                };

                if (enableImageCapture && librariesWithImageCapture.Any())
                {
                    itemsImageCaptureQuery.PathStartsWithAny =
                        librariesWithImageCapture.SelectMany(l => l.Locations).Select(ls =>
                            ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                                ? ls
                                : ls + Path.DirectorySeparatorChar).ToArray();

                    var itemsImageCapture = _libraryManager.GetItemList(itemsImageCaptureQuery)
                        .Where(i => !i.HasImage(ImageType.Primary)).ToList();
                    items = itemsMediaInfo.Concat(itemsImageCapture).GroupBy(i => i.InternalId).Select(g => g.First())
                        .ToArray();
                }
                else
                {
                    items = itemsMediaInfo;
                }

                if (includeExtra)
                {
                    itemsMediaInfoQuery.ExtraTypes = IncludeExtraTypes;
                    var extrasMediaInfo = _libraryManager.GetItemList(itemsMediaInfoQuery);

                    if (enableImageCapture && librariesWithImageCapture.Any())
                    {
                        itemsImageCaptureQuery.ExtraTypes = IncludeExtraTypes;
                        var extrasImageCapture = _libraryManager.GetItemList(itemsImageCaptureQuery);
                        extras = extrasImageCapture.Concat(extrasMediaInfo).GroupBy(i => i.InternalId)
                            .Select(g => g.First()).ToArray();
                    }
                    else
                    {
                        extras = extrasMediaInfo;
                    }
                }
            }

            var combined = favoritesWithExtra.Concat(items).Concat(extras).GroupBy(i => i.InternalId)
                .Select(g => g.First()).ToList();
            var filtered = FilterUnprocessed(combined);
            var results = OrderByDescending(filtered);

            return results;
        }

        public List<BaseItem> FetchPostExtractTaskItems(bool includeAudio)
        {
            var libraryIds = Plugin.Instance.MediaInfoExtractStore.GetOptions().LibraryScope
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => !libraryIds.Any() || libraryIds.Contains(f.Id))
                .ToList();

            _logger.Info("MediaInfoExtract - LibraryScope: " +
                         (libraryIds.Any() ? string.Join(", ", libraries.Select(l => l.Name)) : "ALL"));

            var includeExtra = Plugin.Instance.MediaInfoExtractStore.GetOptions().IncludeExtra;
            _logger.Info("Include Extra: " + includeExtra);

            var favoritesWithExtra = new List<BaseItem>();
            if (libraryIds.Contains("-1"))
            {
                var favorites = AllUsers.Select(e => e.Key)
                    .SelectMany(user => _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        User = user, IsFavorite = true
                    }))
                    .GroupBy(i => i.InternalId)
                    .Select(g => g.First())
                    .ToList();

                var expanded = ExpandFavorites(favorites, false, false, false);

                favoritesWithExtra = expanded.Concat(includeExtra
                        ? expanded.SelectMany(f => f.GetExtras(IncludeExtraTypes))
                        : Enumerable.Empty<BaseItem>())
                    .Where(HasMediaInfo)
                    .ToList();
            }

            var itemsWithExtras = new List<BaseItem>();
            if (!libraryIds.Any() || libraryIds.Any(id => id != "-1"))
            {
                var itemsQuery = new InternalItemsQuery
                {
                    HasPath = true, HasAudioStream = true,
                    MediaTypes = includeAudio ? new[] { MediaType.Video, MediaType.Audio } : new[] { MediaType.Video }
                };

                if (libraryIds.Any(id => id != "-1") && libraries.Any())
                {
                    itemsQuery.PathStartsWithAny = libraries.SelectMany(l => l.Locations)
                        .Select(ls =>
                            ls.EndsWith(Path.DirectorySeparatorChar.ToString()) ? ls : ls + Path.DirectorySeparatorChar)
                        .ToArray();
                }

                itemsWithExtras = _libraryManager.GetItemList(itemsQuery).ToList();

                if (includeExtra)
                {
                    itemsQuery.ExtraTypes = IncludeExtraTypes;
                    itemsWithExtras = _libraryManager.GetItemList(itemsQuery).Concat(itemsWithExtras).ToList();
                }
            }

            var combined = favoritesWithExtra.Concat(itemsWithExtras)
                .GroupBy(i => i.InternalId)
                .Select(g => g.First())
                .ToList();
            var results = OrderByDescending(combined);

            return results;
        }

        private static List<BaseItem> OrderByDescending(List<BaseItem> items)
        {
            var results = items.OrderBy(i => i.ExtraType is null ? 0 : 1)
                .ThenByDescending(i =>
                    i is Episode e ? GetPremiereDateOrDefault(e) :
                    i.ExtraType != null ? i.DateCreated : i.PremiereDate ?? i.DateCreated)
                .ThenByDescending(i => i.IndexNumber ?? int.MinValue)
                .ToList();
            return results;
        }

        private static DateTimeOffset GetPremiereDateOrDefault(Episode item)
        {
            if (item.PremiereDate.HasValue && item.PremiereDate.Value != DateTimeOffset.MinValue)
                return item.PremiereDate.Value;

            if (item.Series != null)
            {
                if (item.Series.PremiereDate.HasValue && item.Series.PremiereDate.Value != DateTimeOffset.MinValue)
                    return item.Series.PremiereDate.Value;

                if (item.Series.ProductionYear is int year && year > 1 && year <= 9999)
                    return new DateTimeOffset(new DateTime(year, 1, 1), TimeSpan.Zero);
            }

            return item.DateCreated;
        }

        public static bool IsPremiereDateInScope(Episode item, DateTimeOffset lookBackTime, bool includeNoPrem)
        {
            if (item.PremiereDate.HasValue && item.PremiereDate.Value != DateTimeOffset.MinValue)
                return item.PremiereDate.Value > lookBackTime;

            if (item.Series != null)
            {
                if (item.Series.PremiereDate.HasValue && item.Series.PremiereDate.Value != DateTimeOffset.MinValue)
                    return item.Series.PremiereDate.Value > lookBackTime;

                if (item.Series.ProductionYear is int year && year > 1 && year <= 9999)
                    return year == lookBackTime.Year;
            }

            return includeNoPrem;
        }

        private List<BaseItem> FilterUnprocessed(List<BaseItem> items)
        {
            var enableImageCapture = Plugin.Instance.MediaInfoExtractStore.GetOptions().EnableImageCapture;

            var results = new List<BaseItem>();

            foreach (var item in items)
            {
                if (IsExtractNeeded(item, enableImageCapture))
                {
                    results.Add(item);
                }
                else if (item is Video)
                {
                    _logger.Debug("MediaInfoExtract - Item dropped: " + item.Name + " - " + item.Path); // video without audio
                }
            }

            _logger.Info("MediaInfoExtract - Number of items: " + results.Count);

            return results;
        }

        public bool IsExtractNeeded(BaseItem item, bool enableImageCapture)
        {
            if (item.MediaContainer.HasValue && ExcludeMediaContainers.Contains(item.MediaContainer.Value))
                return false;

            if (!item.IsShortcut && item.IsFileProtocol && !string.IsNullOrEmpty(item.Path))
            {
                var fileExtension = Path.GetExtension(item.Path).TrimStart('.');
                if (ExcludeMediaExtensions.Contains(fileExtension)) return false;
            }

            if (!HasMediaInfo(item)) return true;

            if (!enableImageCapture) return false;

            if (Plugin.Instance.MediaInfoExtractStore.GetOptions().PersistMediaInfoMode ==
                PersistMediaInfoOption.Restore.ToString()) return false;

            switch (item)
            {
                case Video _:
                    return !item.HasImage(ImageType.Primary) && ImageCaptureEnabled(item);
                case Audio _:
                    return !item.HasImage(ImageType.Primary) && ImageCaptureEnabled(item) &&
                           item.GetMediaStreams().Any(i => i.Type == MediaStreamType.EmbeddedImage);
                default:
                    return false;
            }
        }

        public List<BaseItem> ExpandFavorites(List<BaseItem> items, bool filterNeeded, bool? preExtract,
            bool enableImageCapture)
        {
            var itemsMultiVersions = items.SelectMany(v =>
                    _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        PresentationUniqueKey = v.PresentationUniqueKey
                    }))
                .ToList();

            var videos = itemsMultiVersions.OfType<Video>().Cast<BaseItem>().ToList();

            var seriesIds = itemsMultiVersions.OfType<Series>().Select(s => s.InternalId)
                .Union(itemsMultiVersions.OfType<Episode>().Select(e => e.SeriesId)).ToArray();

            var episodes = Array.Empty<BaseItem>();
            if (seriesIds.Length > 0)
            {
                var episodesMediaInfoQuery = new InternalItemsQuery
                {
                    HasPath = true,
                    HasAudioStream = !preExtract,
                    MediaTypes = new[] { MediaType.Video },
                    Recursive = true,
                    AncestorIds = seriesIds
                };
                var episodesMediaInfo = _libraryManager.GetItemList(episodesMediaInfoQuery);

                if (enableImageCapture)
                {
                    var episodesImageCaptureQuery = new InternalItemsQuery
                    {
                        HasPath = true,
                        MediaTypes = new[] { MediaType.Video },
                        Recursive = true,
                        AncestorIds = seriesIds
                    };
                    var episodesImageCapture = _libraryManager.GetItemList(episodesImageCaptureQuery)
                        .Where(i => !i.HasImage(ImageType.Primary)).ToList();
                    episodes = episodesMediaInfo.Concat(episodesImageCapture).GroupBy(i => i.InternalId)
                        .Select(g => g.First()).ToArray();
                }
                else
                {
                    episodes = episodesMediaInfo;
                }
            }

            var combined = videos.Concat(episodes).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();

            return filterNeeded ? FilterByFavorites(combined) : combined;
        }

        private List<BaseItem> FilterByFavorites(List<BaseItem> items)
        {
            var videos = AllUsers.Select(e => e.Key)
                .SelectMany(u => items.OfType<Video>().Where(i => i.IsFavoriteOrLiked(u)));

            var episodes = AllUsers.Select(e => e.Key)
                .SelectMany(u => items.OfType<Episode>()
                    .GroupBy(e => e.SeriesId)
                    .Where(g => g.Any(i => i.IsFavoriteOrLiked(u)) || g.First().Series.IsFavoriteOrLiked(u))
                    .SelectMany(g => g));

            var results = videos.Cast<BaseItem>()
                .Concat(episodes)
                .GroupBy(i => i.InternalId)
                .Select(g => g.First())
                .ToList();

            return results;
        }

        public List<User> GetUsersByFavorites(BaseItem item)
        {
            var itemsMultiVersion = _libraryManager.GetItemList(new InternalItemsQuery
            {
                PresentationUniqueKey = item.PresentationUniqueKey
            });

            var users = AllUsers.Select(e => e.Key)
                .Where(u => itemsMultiVersion.Any(i =>
                    (i is Movie || i is Series) && i.IsFavoriteOrLiked(u) || i is Episode e &&
                    (e.IsFavoriteOrLiked(u) || e.Series != null && e.Series.IsFavoriteOrLiked(u))))
                .ToList();

            return users;
        }

        public bool HasFileChanged(BaseItem item, IDirectoryService directoryService)
        {
            if (item.IsFileProtocol)
            {
                var file = directoryService.GetFile(item.Path);
                if (file != null && item.HasDateModifiedChanged(file.LastWriteTimeUtc))
                    return true;
            }

            return false;
        }

        public void UpdateDateModifiedLastSaved(BaseItem item, IDirectoryService directoryService)
        {
            if (item.IsFileProtocol)
            {
                var file = directoryService.GetFile(item.Path);
                if (file != null && file.LastWriteTimeUtc.ToUnixTimeSeconds() > 0L)
                {
                    item.DateModified = file.LastWriteTimeUtc;
                    _libraryManager.UpdateItems(new List<BaseItem> { item }, null,
                        ItemUpdateType.None, true, false, null, CancellationToken.None);
                }
            }
        }

        public async Task<bool?> OrchestrateMediaInfoProcessAsync(BaseItem taskItem, string source, CancellationToken cancellationToken)
        {
            var persistMediaInfoMode = Plugin.Instance.MediaInfoExtractStore.GetOptions().PersistMediaInfoMode;
            var persistMediaInfo = persistMediaInfoMode != PersistMediaInfoOption.None.ToString();
            var mediaInfoRestoreMode = persistMediaInfoMode == PersistMediaInfoOption.Restore.ToString();
            var enableImageCapture = Plugin.Instance.MediaInfoExtractStore.GetOptions().EnableImageCapture;

            ExclusiveExtract.AllowExtractInstance(taskItem);

            if (persistMediaInfo) ChapterChangeTracker.BypassInstance(taskItem);

            var filePath = taskItem.Path;
            if (taskItem.IsShortcut)
            {
                filePath = await GetStrmMountPath(filePath).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(filePath)) return null;

            var fileExtension = Path.GetExtension(filePath).TrimStart('.');
            var extractSkip = mediaInfoRestoreMode || ExcludeMediaExtensions.Contains(fileExtension);

            var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
            {
                EnableRemoteContentProbe = true,
                ReplaceAllMetadata = true,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false
            };

            var directoryService = refreshOptions.DirectoryService;

            if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri &&
                uri.Scheme == Uri.UriSchemeFile)
            {
                var file = directoryService.GetFile(filePath);
                if (file?.Exists != true) return null;
            }

            var imageCapture = false;

            if (!extractSkip && enableImageCapture && !taskItem.HasImage(ImageType.Primary))
            {
                EnableImageCapture.AllowImageCaptureInstance(taskItem);
                imageCapture = true;

                refreshOptions.ImageRefreshMode = MetadataRefreshMode.Default;
                refreshOptions.MetadataRefreshMode = MetadataRefreshMode.Default;
                refreshOptions.ReplaceAllImages = true;

                await taskItem.RefreshMetadata(refreshOptions, cancellationToken).ConfigureAwait(false);
            }

            if (!imageCapture)
            {
                if (persistMediaInfo)
                {
                    var deserializeResult = await Plugin.MediaInfoApi
                        .DeserializeMediaInfo(taskItem, directoryService, source, mediaInfoRestoreMode)
                        .ConfigureAwait(false);

                    if (deserializeResult)
                    {
                        if (Plugin.SubtitleApi.HasExternalSubtitleChanged(taskItem, directoryService, true))
                        {
                            await Plugin.SubtitleApi
                                .UpdateExternalSubtitles(taskItem, refreshOptions, false, true).ConfigureAwait(false);
                        }

                        return false;
                    }
                }

                if (extractSkip) return null;

                await Plugin.MediaInfoApi.GetPlaybackMediaSources(taskItem, cancellationToken).ConfigureAwait(false);
            }

            if (persistMediaInfo)
            {
                await Plugin.MediaInfoApi.SerializeMediaInfo(taskItem.InternalId, directoryService, true, source)
                    .ConfigureAwait(false);
            }

            return true;
        }

        public static bool IsFileShortcut(string path)
        {
            return path != null && string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<string> GetStrmMountPath(string strmPath)
        {
            var path = strmPath.AsMemory();

            using var mediaMount = await _mediaMountManager.Mount(path, null, CancellationToken.None);
            
            return mediaMount?.MountedPath;
        }

        public BaseItem[] GetItemsByIds(long[] itemIds)
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = itemIds });

            var dict = items.ToDictionary(i => i.InternalId, i => i);

            return itemIds.Select(id => CollectionExtensions.GetValueOrDefault(dict, id))
                .Where(item => item != null)
                .ToArray();
        }

        public List<CollectionFolder> GetMovieLibraries()
        {
            var libraries = _libraryManager
                .GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(CollectionFolder) } })
                .OfType<CollectionFolder>()
                .Where(l => l.CollectionType == CollectionType.Movies.ToString() || l.CollectionType is null)
                .ToList();

            return libraries;
        }

        public List<CollectionFolder> GetSeriesLibraries()
        {
            var libraries = _libraryManager
                .GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { nameof(CollectionFolder) } })
                .OfType<CollectionFolder>()
                .Where(l => l.CollectionType == CollectionType.TvShows.ToString() || l.CollectionType is null)
                .ToList();

            return libraries;
        }

        public List<Movie> FetchSplitMovieItems()
        {
            var libraries = GetMovieLibraries();

            if (!libraries.Any()) return new List<Movie>();

            _logger.Info("MergeMovie - Libraries: " + string.Join(", ", libraries.Select(l => l.Name)));

            var libraryIds = libraries.Select(l => l.InternalId).ToArray();

            var movieQuery = new InternalItemsQuery
            {
                Recursive = true,
                ParentIds = libraryIds,
                GroupByPresentationUniqueKey = true,
                IncludeItemTypes = new[] { nameof(Movie) }
            };

            var altMovies = _libraryManager.GetItemList(movieQuery).Cast<Movie>()
                .Where(m => m.GetAlternateVersionIds().Count > 0).ToList();

            return altMovies;
        }

        public void EnsureLibraryEnabledAutomaticSeriesGrouping()
        {
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null)
                .ToList();

            foreach (var library in libraries)
            {
                var options = library.LibraryOptions;

                if (!options.EnableAutomaticSeriesGrouping && long.TryParse(library.ItemId, out var itemId))
                {
                    options.EnableAutomaticSeriesGrouping = true;
                    CollectionFolder.SaveLibraryOptions(itemId, options);
                }
            }
        }

        private FileSystemMetadata[] GetRelatedPaths(string basename, string folder)
        {
            var extensions = new List<string>
            {
                ".nfo",
                ".xml",
                ".srt",
                ".vtt",
                ".sub",
                ".idx",
                ".txt",
                ".edl",
                ".bif",
                ".smi",
                ".ttml",
                ".ass",
                ".json"
            };

            extensions.AddRange(BaseItem.SupportedImageExtensions);
            return _fileSystem.GetFiles(folder, extensions.ToArray(), false, false)
                .Where(i => !string.IsNullOrEmpty(i.FullName) && Path.GetFileNameWithoutExtension(i.FullName)
                    .StartsWith(basename, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        public FileSystemMetadata[] GetDeletePaths(BaseItem item)
        {
            var basename = item.FileNameWithoutExtension;
            var folder = _fileSystem.GetDirectoryName(item.Path);
            var relatedFiles = GetRelatedPaths(basename, folder);

            return new[] { new FileSystemMetadata { FullName = item.Path, IsDirectory = item.IsFolder } }
                .Concat(relatedFiles)
                .ToArray();
        }

        public FileSystemMetadata[] GetDeletePaths(string path)
        {
            var folder = _fileSystem.GetDirectoryName(path);

            if (!_fileSystem.DirectoryExists(folder)) return Array.Empty<FileSystemMetadata>();

            var basename = Path.GetFileNameWithoutExtension(path);
            var relatedFiles = GetRelatedPaths(basename, folder);

            return new[] { _fileSystem.GetFileInfo(path) }.Concat(relatedFiles).Where(f => f.Exists).ToArray();
        }

        public Dictionary<string, bool> PrepareDeepDelete(BaseItem item)
        {
            return PrepareDeepDelete(item, null);
        }

        public Dictionary<string, bool> PrepareDeepDelete(BaseItem item, string[] scope)
        {
            var deleteItems = new List<BaseItem> { item };

            if (item.IsFolder)
            {
                deleteItems.AddRange(((Folder)item).GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    ForceOriginalFolders = item is Playlist || item is BoxSet
                }));
            }

            deleteItems = deleteItems.Where(i => i is IHasMediaSources).ToList();

            var mountPaths = new Dictionary<string, bool>();
            var single = scope is null;

            foreach (var workItem in deleteItems)
            {
                var mediaSources =
                    workItem.GetMediaSources(!single, false, _libraryManager.GetLibraryOptions(workItem));
                mediaSources = mediaSources.Where(s =>
                        single || scope?.Any(p => s.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is true)
                    .ToList();

                var staticMediaSources = Plugin.MediaInfoApi.GetStaticMediaSources(workItem, !single)
                    .ToDictionary(s => s.Id, s => s.Path);

                foreach (var source in mediaSources)
                {
                    if (IsFileShortcut(source.Path))
                    {
                        if (staticMediaSources.TryGetValue(source.Id, out var mountPath) &&
                            Uri.TryCreate(mountPath, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri &&
                            !string.IsNullOrEmpty(mountPath) && !IsFileShortcut(mountPath))
                        {
                            mountPaths.Add(mountPath, uri.Scheme == Uri.UriSchemeFile);
                        }
                    }
                    else if (IsSymlink(source.Path))
                    {
                        var targetPath = GetSymlinkTarget(source.Path);

                        if (!string.IsNullOrEmpty(targetPath) &&
                            Uri.TryCreate(targetPath, UriKind.Absolute, out var uri) && uri.IsAbsoluteUri &&
                            uri.Scheme == Uri.UriSchemeFile)
                        {
                            mountPaths.Add(targetPath, true);
                        }
                    }
                }
            }

            return mountPaths;
        }

        public void ExecuteDeepDelete(HashSet<string> mountPaths)
        {
            var deletePaths = new HashSet<FileSystemMetadata>(new FileSystemMetadataComparer());

            foreach (var mountPath in mountPaths)
            {
                foreach (var path in GetDeletePaths(mountPath))
                {
                    deletePaths.Add(path);
                    var folderPath = _fileSystem.GetDirectoryName(path.FullName);

                    if (folderPath != null)
                    {
                        deletePaths.Add(new FileSystemMetadata { FullName = folderPath, IsDirectory = true });
                    }
                }
            }

            foreach (var path in deletePaths.Where(p => !p.IsDirectory))
            {
                try
                {
                    _logger.Info("DeepDelete - Attempting to delete file: " + path.FullName);
                    _fileSystem.DeleteFile(path.FullName, true);
                }
                catch (Exception e)
                {
                    _logger.Error("DeepDelete - Failed to delete file: " + path.FullName);
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            var folderPaths = new HashSet<FileSystemMetadata>(deletePaths.Where(p => p.IsDirectory),
                new FileSystemMetadataComparer());

            while (folderPaths.Any())
            {
                var path = folderPaths.First();

                try
                {
                    if (IsDirectoryEmpty(path.FullName))
                    {
                        _logger.Info("DeepDelete - Attempting to delete empty folder: " + path.FullName);
                        _fileSystem.DeleteDirectory(path.FullName, true, true);

                        var parentPath = _fileSystem.GetDirectoryName(path.FullName);
                        if (parentPath != null)
                        {
                            folderPaths.Add(new FileSystemMetadata { FullName = parentPath, IsDirectory = true });
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.Error("DeepDelete - Failed to delete empty folder: " + path.FullName);
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }

                folderPaths.Remove(path);
            }
        }

        private bool EpisodeNeedsRefresh(Episode item, DateTimeOffset lookBackTime,
            HashSet<EpisodeRefreshOption> episodeRefreshOptions, bool includeNoPrem)
        {
            var name = item.Name;
            var overview = item.Overview;
            var primaryImageInfo = item.GetImageInfo(ImageType.Primary, 0);
            var dimensionsMatch = item.Width > 0 && item.Height > 0 && item.Width == primaryImageInfo?.Width &&
                                  item.Height == primaryImageInfo?.Height;
            var hasProviderIds = item.Series != null && item.Series.ProviderIds.Count > 0;

            var needsRefresh = string.IsNullOrWhiteSpace(overview) ||
                               (episodeRefreshOptions.Contains(EpisodeRefreshOption.NoImage) &&
                                primaryImageInfo is null) ||
                               (episodeRefreshOptions.Contains(EpisodeRefreshOption.NonChineseOverview) &&
                                !IsChinese(overview)) ||
                               (episodeRefreshOptions.Contains(EpisodeRefreshOption.DefaultEpisodeName) &&
                                IsDefaultChineseEpisodeName(name)) ||
                               (episodeRefreshOptions.Contains(EpisodeRefreshOption.ReplaceCapturedImage) &&
                                dimensionsMatch);

            return needsRefresh && hasProviderIds && IsPremiereDateInScope(item, lookBackTime, includeNoPrem);
        }

        public List<Episode> FetchEpisodeRefreshTaskItems()
        {
            var lookBackDays = Plugin.Instance.MetadataEnhanceStore.GetOptions().EpisodeRefreshLookBackDays;
            _logger.Info("EpisodeRefresh - Look back days: " + lookBackDays);
            var lookBackTime = DateTimeOffset.UtcNow.AddDays(-lookBackDays);

            var episodeRefreshScope = Plugin.Instance.MetadataEnhanceStore.GetOptions().EpisodeRefreshScope;
            _logger.Info("EpisodeRefresh - Scope: " + episodeRefreshScope);
            var episodeRefreshOptions = Enum.GetValues<EpisodeRefreshOption>()
                .Where(o => episodeRefreshScope?.Contains(o.ToString(), StringComparison.OrdinalIgnoreCase) is true)
                .ToHashSet();

            var itemsToRefresh = _libraryManager
                .GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) }, HasIndexNumber = true, IsLocked = false
                })
                .OfType<Episode>()
                .Where(e => EpisodeNeedsRefresh(e, lookBackTime, episodeRefreshOptions, true))
                .OrderByDescending(GetPremiereDateOrDefault)
                .ToList();

            _logger.Info("EpisodeRefresh - Number of items: " + itemsToRefresh.Count);

            return itemsToRefresh;
        }

        public List<Episode> FetchEpisodeRefreshQueueItems(List<Episode> items)
        {
            const int lookBackDays = 90;
            _logger.Info("EpisodeRefresh - Look back days: " + lookBackDays);
            var lookBackTime = DateTimeOffset.UtcNow.AddDays(-lookBackDays);

            var episodeRefreshScope = Plugin.Instance.MetadataEnhanceStore.GetOptions().EpisodeRefreshScope;
            _logger.Info("EpisodeRefresh - Scope: " + episodeRefreshScope);
            var episodeRefreshOptions = Enum.GetValues<EpisodeRefreshOption>()
                .Where(o => episodeRefreshScope?.Contains(o.ToString(), StringComparison.OrdinalIgnoreCase) is true)
                .ToHashSet();

            var groupedBySeason = items.GroupBy(i => i.Season);
            var itemsToRefresh = new List<Episode>();

            foreach (var group in groupedBySeason)
            {
                var season = group.Key;

                var episodes = season.GetEpisodes(new InternalItemsQuery
                    {
                        ExcludeItemIds = group.Select(e => e.InternalId).ToArray(),
                        IncludeItemTypes = new[] { nameof(Episode) },
                        HasIndexNumber = true,
                        IsLocked = false,
                        OrderBy = new (string, SortOrder)[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
                    })
                    .Items.OfType<Episode>()
                    .Where(e => EpisodeNeedsRefresh(e, lookBackTime, episodeRefreshOptions, false));

                itemsToRefresh.AddRange(episodes);
            }

            _logger.Info("EpisodeRefresh - Number of items: " + itemsToRefresh.Count);

            return itemsToRefresh;
        }
    }
}
