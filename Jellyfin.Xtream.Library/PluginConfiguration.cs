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
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Xtream.Library;

/// <summary>
/// Plugin configuration for Xtream Library.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the base URL of the Xtream provider (including protocol and port, no trailing slash).
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the username for Xtream authentication.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the password for Xtream authentication.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library path where STRM files will be created.
    /// </summary>
    public string LibraryPath { get; set; } = "/config/xtream-library";

    /// <summary>
    /// Gets or sets a value indicating whether to sync movies/VOD content.
    /// </summary>
    public bool SyncMovies { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to sync series content.
    /// </summary>
    public bool SyncSeries { get; set; } = true;

    /// <summary>
    /// Gets or sets the sync interval in minutes.
    /// </summary>
    public int SyncIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether to trigger a Jellyfin library scan after sync.
    /// </summary>
    public bool TriggerLibraryScan { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to remove orphaned STRM files (content removed from provider).
    /// </summary>
    public bool CleanupOrphans { get; set; } = true;

    /// <summary>
    /// Gets or sets an optional custom User-Agent string for API requests.
    /// </summary>
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the array of selected VOD category IDs to sync.
    /// Empty array means sync all categories (backward compatible).
    /// </summary>
    public int[] SelectedVodCategoryIds { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Gets or sets the array of selected Series category IDs to sync.
    /// Empty array means sync all categories (backward compatible).
    /// </summary>
    public int[] SelectedSeriesCategoryIds { get; set; } = Array.Empty<int>();
}
