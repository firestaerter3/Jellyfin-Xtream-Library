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
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Decides which posters get the library badge, and what their image tag becomes.
/// </summary>
/// <remarks>
/// Only the Primary image of a movie or series whose path lies under one of the plugin's own
/// <c>Movies</c> or <c>Series</c> folders gets a badge. The check is a path comparison, so it
/// costs nothing for the other images the server serves.
/// </remarks>
internal static class LibraryBadge
{
    /// <summary>
    /// Version of the badge drawing. Raising it changes every badged image tag, so clients fetch
    /// the new drawing instead of keeping the old one.
    /// </summary>
    internal const int DrawingVersion = 1;

    /// <summary>The longest badge text drawn; anything longer is cut.</summary>
    internal const int MaxTextLength = 12;

    /// <summary>
    /// Returns the badge text to draw, or null when the badge is off or the text is empty.
    /// </summary>
    /// <param name="config">The plugin configuration, or null when the plugin is not loaded.</param>
    /// <returns>The trimmed, length-limited text, or null.</returns>
    internal static string? TextFor(PluginConfiguration? config)
    {
        if (config is null || !config.EnableLibraryBadge)
        {
            return null;
        }

        string text = (config.LibraryBadgeText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        return text.Length > MaxTextLength ? text[..MaxTextLength] : text;
    }

    /// <summary>
    /// Returns the folders whose movies and series get the badge: <c>Movies</c> and <c>Series</c>
    /// under every provider's library path.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The folders, without a trailing separator.</returns>
    internal static IReadOnlyList<string> LibraryRoots(PluginConfiguration config)
    {
        var roots = new List<string>();
        foreach (var provider in config.Providers)
        {
            string libraryPath = (provider?.LibraryPath ?? string.Empty).Trim();
            if (libraryPath.Length == 0)
            {
                continue;
            }

            roots.Add(Path.TrimEndingDirectorySeparator(Path.Combine(libraryPath, "Movies")));
            roots.Add(Path.TrimEndingDirectorySeparator(Path.Combine(libraryPath, "Series")));
        }

        return roots;
    }

    /// <summary>
    /// Whether an image gets the badge.
    /// </summary>
    /// <param name="kind">The item's kind.</param>
    /// <param name="itemPath">The item's path (a .strm file for a movie, a folder for a series).</param>
    /// <param name="imageType">The image type.</param>
    /// <param name="roots">The folders from <see cref="LibraryRoots"/>.</param>
    /// <returns>True for the Primary image of a movie or series under one of the folders.</returns>
    internal static bool AppliesTo(BaseItemKind kind, string? itemPath, ImageType imageType, IReadOnlyList<string> roots)
    {
        if (imageType != ImageType.Primary ||
            (kind != BaseItemKind.Movie && kind != BaseItemKind.Series) ||
            string.IsNullOrEmpty(itemPath))
        {
            return false;
        }

        foreach (var root in roots)
        {
            if (itemPath.Length > root.Length + 1 &&
                itemPath.StartsWith(root, StringComparison.Ordinal) &&
                (itemPath[root.Length] == '/' || itemPath[root.Length] == Path.DirectorySeparatorChar))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the image tag for a badged image. Jellyfin's tag only depends on the item's path and
    /// the image file's date, so without this a client that already holds the plain poster would
    /// keep showing it; and switching the badge off or changing its text would not reach clients
    /// either.
    /// </summary>
    /// <param name="tag">The tag Jellyfin computed.</param>
    /// <param name="text">The badge text.</param>
    /// <returns>A 32-character hex tag that differs from <paramref name="tag"/>.</returns>
    internal static string CombineTag(string tag, string text)
    {
        string input = string.Create(
            CultureInfo.InvariantCulture,
            $"{tag}|xtream-library-badge|{DrawingVersion}|{text}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)).AsSpan(0, 16));
    }
}
