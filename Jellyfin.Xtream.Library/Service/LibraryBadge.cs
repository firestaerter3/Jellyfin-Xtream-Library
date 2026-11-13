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
/// Only the Primary image of a movie, series, season or episode whose path lies under one of the
/// plugin's own <c>Movies</c> or <c>Series</c> folders gets a badge, and only for the item types
/// switched on in the settings. The check is a path comparison, so it
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

    // Paths are case-insensitive on Windows only.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
    /// Returns the corner the badge is drawn in. An empty or unknown value gives the default,
    /// bottom right.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The corner.</returns>
    internal static LibraryBadgeCorner CornerFor(PluginConfiguration config)
    {
        // Matched by name only: Enum.TryParse would also take a number or a comma-separated list.
        foreach (var corner in Enum.GetValues<LibraryBadgeCorner>())
        {
            if (string.Equals(corner.ToString(), config.LibraryBadgeCorner, StringComparison.OrdinalIgnoreCase))
            {
                return corner;
            }
        }

        return LibraryBadgeCorner.BottomRight;
    }

    /// <summary>
    /// Whether the badge is switched on for an item type. Only movies, series, seasons and episodes
    /// can get it.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="kind">The item's kind.</param>
    /// <returns>True when the setting for that type is on.</returns>
    internal static bool KindEnabled(PluginConfiguration config, BaseItemKind kind) => kind switch
    {
        BaseItemKind.Movie => config.LibraryBadgeOnMovies,
        BaseItemKind.Series => config.LibraryBadgeOnSeries,
        BaseItemKind.Season => config.LibraryBadgeOnSeasons,
        BaseItemKind.Episode => config.LibraryBadgeOnEpisodes,
        _ => false,
    };

    /// <summary>
    /// Returns how the badge is drawn, or null when it is off, its text is empty or no item type
    /// has it switched on.
    /// </summary>
    /// <param name="config">The plugin configuration, or null when the plugin is not loaded.</param>
    /// <returns>The badge style, or null.</returns>
    internal static LibraryBadgeStyle? StyleFor(PluginConfiguration? config)
    {
        string? text = TextFor(config);
        if (text is null || config is null)
        {
            return null;
        }

        var kinds = new StringBuilder(4);
        foreach (var (kind, letter) in new[]
        {
            (BaseItemKind.Movie, 'M'), (BaseItemKind.Series, 'S'), (BaseItemKind.Season, 'N'), (BaseItemKind.Episode, 'E'),
        })
        {
            if (KindEnabled(config, kind))
            {
                kinds.Append(letter);
            }
        }

        return kinds.Length == 0 ? null : new LibraryBadgeStyle(text, CornerFor(config), kinds.ToString());
    }

    /// <summary>
    /// Returns the folders whose items get the badge: <c>Movies</c> and <c>Series</c> under every
    /// provider's library path. Seasons and episodes lie under <c>Series</c>.
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
    /// <param name="itemPath">The item's path (a .strm file for a movie or episode, a folder for a series or season).</param>
    /// <param name="imageType">The image type.</param>
    /// <param name="roots">The folders from <see cref="LibraryRoots"/>.</param>
    /// <returns>True for the Primary image of a movie, series, season or episode under one of the folders.</returns>
    internal static bool AppliesTo(BaseItemKind kind, string? itemPath, ImageType imageType, IReadOnlyList<string> roots)
    {
        if (imageType != ImageType.Primary || !IsBadgeKind(kind) || string.IsNullOrEmpty(itemPath))
        {
            return false;
        }

        foreach (var root in roots)
        {
            if (itemPath.Length > root.Length + 1 &&
                itemPath.StartsWith(root, PathComparison) &&
                (itemPath[root.Length] == '/' || itemPath[root.Length] == Path.DirectorySeparatorChar))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether an item type can get the badge at all.
    /// </summary>
    /// <param name="kind">The item's kind.</param>
    /// <returns>True for a movie, series, season or episode.</returns>
    internal static bool IsBadgeKind(BaseItemKind kind) =>
        kind is BaseItemKind.Movie or BaseItemKind.Series or BaseItemKind.Season or BaseItemKind.Episode;

    /// <summary>
    /// Whether an image is a local file in a format the badge is drawn on. A remote image, or a GIF
    /// or SVG, is served plain, so it must keep the server's own tag.
    /// </summary>
    /// <param name="imagePath">The image's path.</param>
    /// <returns>True for a local JPEG, PNG or WebP file path.</returns>
    internal static bool IsDrawableLocalImage(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !Path.IsPathRooted(imagePath) ||
            imagePath.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        return LibraryBadgeRenderer.FormatFor(Path.GetExtension(imagePath)) is not null;
    }

    /// <summary>
    /// Returns the image tag for a badged image. Jellyfin's tag only depends on the item's path and
    /// the image file's date, so without this a client that already holds the plain poster would
    /// keep showing it; and switching the badge off or changing its text would not reach clients
    /// either. The corner and the item types that have the badge are part of it too, so changing
    /// any badge setting makes clients fetch the posters again.
    /// </summary>
    /// <param name="tag">The tag Jellyfin computed.</param>
    /// <param name="style">The badge style.</param>
    /// <returns>A 32-character hex tag that differs from <paramref name="tag"/>.</returns>
    internal static string CombineTag(string tag, LibraryBadgeStyle style)
    {
        string input = string.Create(
            CultureInfo.InvariantCulture,
            $"{tag}|xtream-library-badge|{DrawingVersion}|{style.Text}|{style.Corner}|{style.Kinds}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)).AsSpan(0, 16));
    }
}
