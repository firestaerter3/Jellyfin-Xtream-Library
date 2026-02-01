// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Service to clean channel names by removing common prefixes, quality tags, and codec info.
/// </summary>
public static partial class ChannelNameCleaner
{
    private static readonly char[] LineSeparators = ['\n', '\r'];

    // Country/region prefixes (common IPTV naming conventions)
    [GeneratedRegex(@"^(UK|US|DE|FR|NL|ES|IT|CA|AU|BE|CH|AT|PT|BR|MX|AR|PL|CZ|RO|HU|TR|GR|SE|NO|DK|FI|IE|IN|PK|AF|ZA|AE|SA|EG|MA|NG|KE|JP|KR|CN|TW|HK|SG|MY|TH|VN|PH|ID|NZ|RU|UA|BY|KZ|IL|IR|IQ)\s*[:\|\-]\s*", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex CountryPrefixRegex();

    // Quality tags with separators: | HD |, | FHD |, | 4K |, etc.
    [GeneratedRegex(@"\s*\|\s*(HD|FHD|UHD|4K|SD|720p|1080p|2160p|HEVC|H\.?264|H\.?265)\s*\|?\s*", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex QualityTagSeparatorRegex();

    // Quality tags without separators but at end: HD, FHD, 4K at word boundaries
    [GeneratedRegex(@"\s+(HD|FHD|UHD|4K|SD)$", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex QualityTagEndRegex();

    // Resolution suffixes
    [GeneratedRegex(@"\s*(1080[pi]?|720[pi]?|4K|2160[pi]?)\s*$", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex ResolutionSuffixRegex();

    // Codec info
    [GeneratedRegex(@"\s*(HEVC|H\.?264|H\.?265|AVC|MPEG-?[24]|VP9|AV1)\s*", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex CodecInfoRegex();

    // Bracketed quality/codec tags: [HD], [FHD], (HD), (4K), etc.
    [GeneratedRegex(@"\s*[\[\(](HD|FHD|UHD|4K|SD|HEVC|H\.?264|H\.?265|720p|1080p)[\]\)]\s*", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex BracketedTagsRegex();

    // Leading/trailing pipes with spaces
    [GeneratedRegex(@"(^\s*\|\s*|\s*\|\s*$)")]
    private static partial Regex LeadingTrailingPipeRegex();

    // Multiple spaces
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultipleSpacesRegex();

    /// <summary>
    /// Cleans a channel name by removing common prefixes, quality tags, codec info, etc.
    /// </summary>
    /// <param name="name">The original channel name.</param>
    /// <param name="userRemoveTerms">Optional user-defined terms to remove (one per line).</param>
    /// <param name="enableCleaning">Whether cleaning is enabled.</param>
    /// <returns>The cleaned channel name.</returns>
    public static string CleanChannelName(string name, string? userRemoveTerms = null, bool enableCleaning = true)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        if (!enableCleaning)
        {
            return name.Trim();
        }

        string result = name;

        // Apply user-defined removals first
        if (!string.IsNullOrWhiteSpace(userRemoveTerms))
        {
            foreach (var term in ParseUserTerms(userRemoveTerms))
            {
                if (!string.IsNullOrWhiteSpace(term))
                {
                    result = result.Replace(term, string.Empty, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // Apply built-in cleaning patterns
        result = CountryPrefixRegex().Replace(result, string.Empty);
        result = QualityTagSeparatorRegex().Replace(result, " ");
        result = BracketedTagsRegex().Replace(result, string.Empty);
        result = CodecInfoRegex().Replace(result, string.Empty);
        result = ResolutionSuffixRegex().Replace(result, string.Empty);
        result = QualityTagEndRegex().Replace(result, string.Empty);
        result = LeadingTrailingPipeRegex().Replace(result, string.Empty);

        // Clean up whitespace
        result = MultipleSpacesRegex().Replace(result, " ");
        result = result.Trim();

        // If we somehow ended up with an empty string, return original
        return string.IsNullOrWhiteSpace(result) ? name.Trim() : result;
    }

    /// <summary>
    /// Parses user-defined removal terms from a newline-separated string.
    /// </summary>
    /// <param name="userTerms">Newline-separated list of terms to remove.</param>
    /// <returns>List of terms to remove.</returns>
    public static IEnumerable<string> ParseUserTerms(string? userTerms)
    {
        if (string.IsNullOrWhiteSpace(userTerms))
        {
            yield break;
        }

        var lines = userTerms.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                yield return trimmed;
            }
        }
    }
}
