using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.MediaFiles
{
    public static class ArchiveFileExtensions
    {
        private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip",
            ".zipx",
            ".rar",
            ".7z",
            ".tar",
            ".gz",
            ".tgz",
            ".bz2",
            ".tbz2",
            ".xz",
            ".txz",
            ".zst"
        };

        private static readonly Regex SplitRarPartRegex = new(@"^\.r\d{2}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool IsArchive(string pathOrExtension)
        {
            if (string.IsNullOrWhiteSpace(pathOrExtension))
            {
                return false;
            }

            var extension = Path.GetExtension(pathOrExtension);

            return Extensions.Contains(extension) || SplitRarPartRegex.IsMatch(extension);
        }
    }
}
