using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Notifications.Grimmory
{
    public class Grimmory : NotificationBase<GrimmorySettings>
    {
        private readonly IGrimmoryProxy _proxy;
        private readonly Logger _logger;

        public Grimmory(IGrimmoryProxy proxy, Logger logger)
        {
            _proxy = proxy;
            _logger = logger;
        }

        public override string Name => "Grimmory";
        public override string Link => "https://github.com/grimmory-tools/grimmory";

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            if (message.BookFiles?.Any(x => IsEbook(x.Quality)) == true)
            {
                RefreshLibrary("import");
            }
        }

        public override void OnRename(Author author, List<RenamedBookFile> renamedFiles)
        {
            if (renamedFiles?.Any(x => x?.BookFile != null && IsEbook(x.BookFile.Quality)) == true)
            {
                RefreshLibrary("rename");
            }
        }

        public override void OnBookDelete(BookDeleteMessage message)
        {
            if (message.DeletedFiles && message.Book?.MediaType != BookMediaType.Audiobook)
            {
                RefreshLibrary("book delete");
            }
        }

        public override void OnBookFileDelete(BookFileDeleteMessage message)
        {
            if (IsEbook(message.BookFile?.Quality))
            {
                RefreshLibrary("file delete");
            }
        }

        public override void OnBookRetag(BookRetagMessage message)
        {
            if (IsEbook(message.BookFile?.Quality))
            {
                RefreshLibrary("retag");
            }
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            failures.AddIfNotNull(_proxy.Test(Settings));

            return new ValidationResult(failures);
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "getLibraries")
            {
                if (Settings.Url.IsNullOrWhiteSpace() || Settings.Username.IsNullOrWhiteSpace() || Settings.Password.IsNullOrWhiteSpace())
                {
                    return new { options = new List<object>() };
                }

                try
                {
                    var libraries = _proxy.GetLibraries(Settings);

                    return new
                    {
                        options = libraries
                            .OrderBy(l => l.Name, StringComparer.InvariantCultureIgnoreCase)
                            .Select(l => new
                            {
                                Value = l.Id,
                                Name = l.Name
                            })
                    };
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to retrieve libraries from Grimmory");
                    return new { options = new List<object>() };
                }
            }

            return new { };
        }

        private void RefreshLibrary(string reason)
        {
            try
            {
                _logger.Debug("Grimmory: triggering library {0} refresh after {1}", Settings.LibraryId, reason);
                _proxy.RefreshLibrary(Settings, Settings.LibraryId);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to trigger Grimmory library refresh after {0}", reason);
            }
        }

        private static bool IsEbook(QualityModel quality)
        {
            return quality?.Quality != null && QualityMediaTypeHelper.IsEbookFileQuality(quality.Quality);
        }
    }
}
