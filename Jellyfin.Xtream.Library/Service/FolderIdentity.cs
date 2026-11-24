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

using System.Text.RegularExpressions;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Reads the metadata ids back out of a folder name.
/// <para>
/// Folder names are the only record of an item's identity for a library synced before the snapshot
/// started carrying it, so this is what backfills those items on the next sync (GitHub #88).
/// </para>
/// </summary>
public static partial class FolderIdentity
{
    /// <summary>
    /// Reads the TMDB id out of a folder name such as "Some Movie (2024) [tmdbid-1234]".
    /// </summary>
    /// <param name="folderName">Folder name to read.</param>
    /// <param name="tmdbId">The id, when the name carries one.</param>
    /// <returns>True when an id was found.</returns>
    public static bool TryParseTmdbId(string? folderName, out int tmdbId)
        => TryParse(TmdbIdPattern(), folderName, out tmdbId);

    /// <summary>
    /// Reads the TVDB id out of a folder name such as "Some Show [tvdbid-1234]".
    /// </summary>
    /// <param name="folderName">Folder name to read.</param>
    /// <param name="tvdbId">The id, when the name carries one.</param>
    /// <returns>True when an id was found.</returns>
    public static bool TryParseTvdbId(string? folderName, out int tvdbId)
        => TryParse(TvdbIdPattern(), folderName, out tvdbId);

    /// <summary>
    /// Removes a trailing metadata id such as " [tmdbid-1234]" from a folder name. Any other
    /// bracket is part of the title and stays (GitHub #205).
    /// </summary>
    /// <param name="folderName">Folder name to read.</param>
    /// <returns>The name without its id.</returns>
    public static string StripIdTag(string folderName) => IdTagPattern().Replace(folderName, string.Empty);

    private static bool TryParse(Regex pattern, string? folderName, out int id)
    {
        id = 0;
        if (string.IsNullOrEmpty(folderName))
        {
            return false;
        }

        var match = pattern.Match(folderName);
        return match.Success && int.TryParse(match.Groups[1].ValueSpan, out id);
    }

    [GeneratedRegex(@"\[tmdbid-(\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbIdPattern();

    [GeneratedRegex(@"\[tvdbid-(\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex TvdbIdPattern();

    // The other spellings are ones Jellyfin reads too, so a folder named by hand is still found.
    [GeneratedRegex(@"(?<=.) \[(?:tmdb|tvdb|imdb)id[-=]\w+\]\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdTagPattern();
}
