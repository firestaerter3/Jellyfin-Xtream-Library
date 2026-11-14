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
/// Derives a language label ("DE", "EN") for a movie version from the provider categories the
/// stream sits in.
/// <para>
/// Providers commonly carry the same film in a German and an English category. With TMDB grouping
/// both land in one folder, and without a language in the file name Jellyfin's version dropdown
/// cannot tell them apart. The label is written in front of the quality label, so the files read
/// "Title (Year) [tmdbid-N] - DE.strm" and "… - EN HEVC 4K.strm".
/// </para>
/// </summary>
internal static partial class VersionLanguageLabeler
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Parses the rule list: one rule per line, "regex=LABEL", split at the last '='.
    /// Lines with an invalid regex or no label are skipped; the order of the lines is kept,
    /// because the first matching rule wins.
    /// </summary>
    /// <param name="rules">The rule text as entered in the settings.</param>
    /// <returns>The valid rules in list order.</returns>
    internal static IReadOnlyList<(Regex Pattern, string Label)> ParseRules(string? rules)
    {
        var parsed = new List<(Regex Pattern, string Label)>();
        if (string.IsNullOrWhiteSpace(rules))
        {
            return parsed;
        }

        foreach (var rawLine in rules.Split('\n'))
        {
            string line = rawLine.Trim();
            int separator = line.LastIndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string pattern = line[..separator].Trim();
            string label = line[(separator + 1)..].Trim();
            if (pattern.Length == 0 || label.Length == 0)
            {
                continue;
            }

            try
            {
                parsed.Add((new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout), label));
            }
            catch (ArgumentException)
            {
                // Invalid regex syntax: skip this rule, keep the others.
            }
        }

        return parsed;
    }

    /// <summary>
    /// Picks the language label for a stream. Rules are tried in list order and the first rule that
    /// matches any of the stream's category names wins, so the result never depends on the order
    /// the categories arrive in. Only when no category name is known is the stream name tried
    /// against the same rules; otherwise, and when that fails too, <paramref name="defaultLabel"/>
    /// is returned.
    /// </summary>
    /// <param name="categoryNames">Names of the categories the stream sits in.</param>
    /// <param name="streamName">The provider's stream name, used as a fallback.</param>
    /// <param name="rules">Rules from <see cref="ParseRules"/>.</param>
    /// <param name="defaultLabel">Label when nothing matches; null or empty means no label.</param>
    /// <returns>The language label, or null when the stream gets none.</returns>
    internal static string? Resolve(
        IEnumerable<string> categoryNames,
        string? streamName,
        IReadOnlyList<(Regex Pattern, string Label)> rules,
        string? defaultLabel)
    {
        var names = new List<string>();
        foreach (var name in categoryNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        foreach (var (pattern, label) in rules)
        {
            foreach (var name in names)
            {
                if (IsMatch(pattern, name))
                {
                    return label;
                }
            }
        }

        // The title is read only when no category name is known. A known category that no rule
        // matches gets the default: a title such as "De Palma" says nothing about the language.
        if (names.Count == 0 && !string.IsNullOrEmpty(streamName))
        {
            foreach (var (pattern, label) in rules)
            {
                if (IsMatch(pattern, streamName))
                {
                    return label;
                }
            }
        }

        return string.IsNullOrWhiteSpace(defaultLabel) ? null : defaultLabel.Trim();
    }

    /// <summary>
    /// The index of the first rule that matches a category name, or the number of rules when none
    /// does. Categories are fetched in batches and a stream is labelled in the first batch it turns
    /// up in, so the sync orders categories by this rank: the batch where a stream first appears
    /// then always holds its highest-ranked category.
    /// </summary>
    /// <param name="categoryName">The category name.</param>
    /// <param name="rules">Rules from <see cref="ParseRules"/>.</param>
    /// <returns>The rank; lower comes first.</returns>
    internal static int RuleRank(string categoryName, IReadOnlyList<(Regex Pattern, string Label)> rules)
    {
        for (int i = 0; i < rules.Count; i++)
        {
            if (IsMatch(rules[i].Pattern, categoryName))
            {
                return i;
            }
        }

        return rules.Count;
    }

    /// <summary>
    /// Writes resolution tokens as words: 2160p becomes 4K, 1080p FHD, 720p HD, 576p and 480p SD.
    /// Jellyfin makes the version whose name carries a number followed by p or i the default, which
    /// would let an "EN 1080p" file win over a plain "DE" file.
    /// </summary>
    /// <param name="label">The version label.</param>
    /// <returns>The label without digit resolution tokens.</returns>
    internal static string NormalizeResolutionTokens(string label)
        => ResolutionTokenPattern().Replace(label, match => match.Groups[1].Value switch
        {
            "2160" => "4K",
            "1080" => "FHD",
            "720" => "HD",
            _ => "SD",
        });

    /// <summary>
    /// Joins the language label and the quality label, language first.
    /// </summary>
    /// <param name="languageLabel">The language label, or null.</param>
    /// <param name="qualityLabel">The quality label, or null.</param>
    /// <returns>The combined label, or null when both parts are empty.</returns>
    internal static string? Combine(string? languageLabel, string? qualityLabel)
    {
        bool hasLanguage = !string.IsNullOrWhiteSpace(languageLabel);
        bool hasQuality = !string.IsNullOrWhiteSpace(qualityLabel);
        if (hasLanguage && hasQuality)
        {
            return languageLabel!.Trim() + " " + qualityLabel!.Trim();
        }

        if (hasLanguage)
        {
            return languageLabel!.Trim();
        }

        return hasQuality ? qualityLabel!.Trim() : null;
    }

    private static bool IsMatch(Regex pattern, string input)
    {
        try
        {
            return pattern.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            // Pattern was valid but ran too long: treat as no match.
            return false;
        }
    }

    [GeneratedRegex(@"\b(2160|1080|720|576|480)[ip]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResolutionTokenPattern();
}
