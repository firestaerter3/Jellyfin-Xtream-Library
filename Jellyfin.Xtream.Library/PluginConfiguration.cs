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

    /// <summary>
    /// Gets or sets the number of parallel API requests during sync.
    /// Higher values speed up sync but may overload the provider.
    /// </summary>
    public int SyncParallelism { get; set; } = 3;

    /// <summary>
    /// Gets or sets a value indicating whether to skip series that already have STRM files.
    /// This avoids unnecessary API calls for existing content.
    /// </summary>
    public bool SmartSkipExisting { get; set; } = true;

    /// <summary>
    /// Gets or sets folder name to TMDb ID overrides for movies.
    /// Format: one mapping per line, "FolderName=TmdbID".
    /// Example: "The Matrix (1999)=603" forces TMDb ID 603 for that folder.
    /// </summary>
    public string TmdbFolderIdOverrides { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets folder name to TVDb ID overrides for series.
    /// Format: one mapping per line, "FolderName=TvdbID".
    /// Example: "Breaking Bad (2008)=81189" forces TVDb ID 81189 for that folder.
    /// </summary>
    public string TvdbFolderIdOverrides { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether automatic metadata ID lookup is enabled.
    /// When enabled, uses Jellyfin's configured metadata providers to automatically
    /// look up TMDb/TVDb IDs during sync.
    /// </summary>
    public bool EnableMetadataLookup { get; set; }

    /// <summary>
    /// Gets or sets the metadata cache age in days before refresh.
    /// Cached lookup results older than this will be re-fetched.
    /// </summary>
    public int MetadataCacheAgeDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets a value indicating whether to download artwork from the provider
    /// for content that could not be matched to TMDb/TVDb.
    /// This ensures unmatched content still has posters and thumbnails.
    /// </summary>
    public bool DownloadArtworkForUnmatched { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to proactively fetch media info
    /// (resolution, codec, audio channels) during sync and write NFO sidecar files.
    /// This allows Jellyfin to display media info without first-time playback.
    /// </summary>
    public bool EnableProactiveMediaInfo { get; set; }

    /// <summary>
    /// Gets or sets the sync schedule type.
    /// "Interval" = run every X minutes, "Daily" = run at specific time each day.
    /// </summary>
    public string SyncScheduleType { get; set; } = "Interval";

    /// <summary>
    /// Gets or sets the hour (0-23) to run the daily sync.
    /// Only used when SyncScheduleType is "Daily".
    /// </summary>
    public int SyncDailyHour { get; set; } = 3;

    /// <summary>
    /// Gets or sets the minute (0-59) to run the daily sync.
    /// Only used when SyncScheduleType is "Daily".
    /// </summary>
    public int SyncDailyMinute { get; set; } = 0;

    /// <summary>
    /// Gets or sets the movie folder mode.
    /// "Single" = all movies sync to root Movies folder.
    /// "Multiple" = movies sync to custom subfolders based on category mappings.
    /// </summary>
    public string MovieFolderMode { get; set; } = "Single";

    /// <summary>
    /// Gets or sets the movie folder mappings.
    /// Format: one mapping per line, "FolderName=CategoryId1,CategoryId2,CategoryId3".
    /// Categories can appear in multiple folder mappings (content synced to multiple locations).
    /// Example: "Kids=10,15,20" creates Movies/Kids/ with categories 10, 15, and 20.
    /// </summary>
    public string MovieFolderMappings { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the series folder mode.
    /// "Single" = all series sync to root Series folder.
    /// "Multiple" = series sync to custom subfolders based on category mappings.
    /// </summary>
    public string SeriesFolderMode { get; set; } = "Single";

    /// <summary>
    /// Gets or sets the series folder mappings.
    /// Format: one mapping per line, "FolderName=CategoryId1,CategoryId2,CategoryId3".
    /// Categories can appear in multiple folder mappings (content synced to multiple locations).
    /// Example: "Kids=5,8" creates Series/Kids/ with categories 5 and 8.
    /// </summary>
    public string SeriesFolderMappings { get; set; } = string.Empty;
}
