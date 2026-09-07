using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Books.Calibre
{
    public static class CalibreSeriesSelector
    {
        private static readonly HashSet<string> StopTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and"
        };

        private static readonly char[] TokenSeparators = { ' ', ':', '-', ',', '.', '(', ')' };

        public static SeriesBookLink Select(Book book)
        {
            var links = book?.SeriesLinks?
                .Where(x => x?.Series?.Value?.Title.IsNotNullOrWhiteSpace() == true)
                .ToList();

            if (links == null || links.Count == 0)
            {
                return null;
            }

            var bookTokens = Tokenize(book.Title);

            // Providers hand back several series per work: translated edition series,
            // box-set splits, and umbrella universes. Prefer the broadest series so the
            // whole run of an author's related books lands on one consistent sequence.
            return links
                .OrderBy(x => ContainsNonAscii(x.Series.Value.Title) ? 1 : 0)
                .ThenByDescending(x => x.Series.Value.WorkCount)
                .ThenByDescending(x => Tokenize(x.Series.Value.Title).Intersect(bookTokens).Any() ? 1 : 0)
                .ThenByDescending(x => x.IsPrimary)
                .ThenBy(x => x.SeriesPosition)
                .FirstOrDefault();
        }

        private static HashSet<string> Tokenize(string value)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (value.IsNullOrWhiteSpace())
            {
                return tokens;
            }

            foreach (var token in value.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length > 2 && !StopTokens.Contains(token))
                {
                    tokens.Add(token);
                }
            }

            return tokens;
        }

        private static bool ContainsNonAscii(string value)
        {
            return value?.Any(c => c > 127) == true;
        }
    }
}
