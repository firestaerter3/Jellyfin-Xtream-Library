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
using System.Globalization;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Resolves Live TV channel logo values. Local filesystem paths are rewritten to the
/// plugin's <c>ChannelLogo</c> proxy endpoint so remote Jellyfin clients can fetch them;
/// http(s) URLs are passed through unchanged. See issue #53.
/// </summary>
internal static class ChannelLogoResolver
{
    /// <summary>
    /// The route segment (after the server base URL) that serves a channel logo file.
    /// </summary>
    public const string LogoRoute = "XtreamLibrary/ChannelLogo";

    /// <summary>
    /// Returns true if the logo value is a local filesystem path rather than an http(s) URL.
    /// </summary>
    /// <param name="icon">The configured logo value.</param>
    /// <returns>True for local paths (including <c>file://</c>), false for http(s) URLs or empty input.</returns>
    public static bool IsLocalPath(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return false;
        }

        return !icon.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !icon.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a logo value to the URL that should appear in the M3U/XMLTV/tuner output.
    /// Local paths become <c>{baseUrl}/XtreamLibrary/ChannelLogo/{streamId}</c>; http(s) URLs
    /// and empty values are returned unchanged. If <paramref name="baseUrl"/> is empty, the
    /// original value is returned (no broken proxy URL is produced).
    /// </summary>
    /// <param name="icon">The configured logo value (may be null).</param>
    /// <param name="streamId">The channel stream ID.</param>
    /// <param name="baseUrl">The server base URL (no path), e.g. <c>http://127.0.0.1:8096</c>.</param>
    /// <returns>The resolved logo URL, or null if <paramref name="icon"/> is null/empty.</returns>
    public static string? ResolveDisplayUrl(string? icon, int streamId, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return icon;
        }

        if (!IsLocalPath(icon) || string.IsNullOrEmpty(baseUrl))
        {
            return icon;
        }

        var trimmed = baseUrl.TrimEnd('/');
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{trimmed}/{LogoRoute}/{streamId}");
    }
}
