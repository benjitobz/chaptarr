using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Books.Calibre
{
    public class CalibreLibraryCanonicalizer : IHandle<AuthorScannedEvent>
    {
        private readonly IRootFolderService _rootFolderService;
        private readonly IMediaFileService _mediaFileService;
        private readonly ICalibreProxy _calibreProxy;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public CalibreLibraryCanonicalizer(IRootFolderService rootFolderService,
                                           IMediaFileService mediaFileService,
                                           ICalibreProxy calibreProxy,
                                           IConfigService configService,
                                           Logger logger)
        {
            _rootFolderService = rootFolderService;
            _mediaFileService = mediaFileService;
            _calibreProxy = calibreProxy;
            _configService = configService;
            _logger = logger;
        }

        public void Handle(AuthorScannedEvent message)
        {
            var author = message.Author;

            if (author == null || author.Path.IsNullOrWhiteSpace() || !_configService.CanonicalizeCalibreLibraryMetadata)
            {
                return;
            }

            RootFolder rootFolder;

            try
            {
                rootFolder = _rootFolderService.GetBestRootFolder(author.Path);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to resolve root folder for {0}", author.Path);
                return;
            }

            if (rootFolder == null || !rootFolder.IsCalibreLibrary || rootFolder.CalibreSettings == null)
            {
                return;
            }

            var settings = rootFolder.CalibreSettings;
            var files = _mediaFileService.GetFilesByAuthor(author.Id)
                .Where(f => f != null && f.Path.IsNotNullOrWhiteSpace())
                .ToList();

            if (!files.Any())
            {
                return;
            }

            var groups = new Dictionary<int, List<BookFile>>();

            foreach (var file in files)
            {
                var calibreId = file.CalibreId;

                if (calibreId == 0)
                {
                    try
                    {
                        calibreId = _calibreProxy.GetCalibreIdForPath(file.Path, settings);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Unable to resolve calibre id for {0}", file.Path);
                        continue;
                    }

                    if (calibreId == 0)
                    {
                        continue;
                    }

                    file.CalibreId = calibreId;
                    _mediaFileService.Update(file);
                }

                if (!groups.TryGetValue(calibreId, out var group))
                {
                    groups[calibreId] = group = new List<BookFile>();
                }

                group.Add(file);
            }

            var canonicalized = 0;

            foreach (var pair in groups)
            {
                try
                {
                    if (CanonicalizeBook(pair.Key, pair.Value, settings))
                    {
                        canonicalized++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Unable to canonicalize calibre book {0} for {1}", pair.Key, author.Name);
                }
            }

            if (canonicalized > 0)
            {
                _logger.Info("Canonicalized calibre metadata for {0} book(s) under {1}", canonicalized, author.Name);
            }
        }

        private bool CanonicalizeBook(int calibreId, List<BookFile> files, CalibreSettings settings)
        {
            var reference = files.FirstOrDefault(f => f.Edition?.Title.IsNotNullOrWhiteSpace() == true);

            if (reference == null)
            {
                return false;
            }

            var calibreBook = _calibreProxy.GetBook(calibreId, settings);

            if (calibreBook == null)
            {
                return false;
            }

            var canonicalTitle = reference.Edition.Title;
            var canonicalAuthor = reference.Author?.Name;
            var titleMatches = calibreBook.Title != null &&
                               calibreBook.Title.Equals(canonicalTitle, StringComparison.OrdinalIgnoreCase);
            var authorMatches = canonicalAuthor.IsNullOrWhiteSpace() ||
                                (calibreBook.Authors?.Any(a => a.Equals(canonicalAuthor, StringComparison.OrdinalIgnoreCase)) ?? false);

            if (titleMatches && authorMatches)
            {
                return false;
            }

            _calibreProxy.SetFields(reference, settings, updateCover: true, embed: false);
            RefreshTrackedPaths(calibreId, files, settings);
            return true;
        }

        private void RefreshTrackedPaths(int calibreId, List<BookFile> files, CalibreSettings settings)
        {
            var refreshed = _calibreProxy.GetBook(calibreId, settings);
            var formats = refreshed?.Formats;

            if (formats == null || formats.Count == 0)
            {
                return;
            }

            var updatedCount = 0;

            foreach (var file in files)
            {
                var extension = (Path.GetExtension(file.Path) ?? string.Empty).TrimStart('.');

                if (extension.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var format = formats.FirstOrDefault(kvp => kvp.Key.Equals(extension, StringComparison.OrdinalIgnoreCase)).Value;

                if (format?.Path == null || format.Path.PathEquals(file.Path))
                {
                    continue;
                }

                if (_mediaFileService.GetFileWithPath(format.Path) != null)
                {
                    // The folder watcher already re-tracked the renamed file; the stale
                    // row this loop holds will be purged by the scan's cleanup instead.
                    continue;
                }

                file.Path = format.Path;

                try
                {
                    _mediaFileService.Update(file);
                    updatedCount++;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Unable to update tracked path to {0}", format.Path);
                }
            }

            if (updatedCount > 0)
            {
                _logger.Debug("Updated {0} tracked file path(s) after calibre rename of book {1}", updatedCount, calibreId);
            }
        }
    }
}
