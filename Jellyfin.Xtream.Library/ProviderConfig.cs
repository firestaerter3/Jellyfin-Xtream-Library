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

namespace Jellyfin.Xtream.Library;

/// <summary>
/// Per-provider configuration for an Xtream source.
/// </summary>
public class ProviderConfig
{
    /// <summary>
    /// Gets or sets the display name for this provider.
    /// </summary>
    public string Name { get; set; } = "Provider 1";

    /// <summary>
    /// Gets or sets a value indicating whether this provider is enabled for sync.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    // =====================
    // Connection
    // =====================

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
    /// Gets or sets an optional custom User-Agent string for API requests.
    /// </summary>
    public string UserAgent { get; set; } = string.Empty;

    // =====================
    // Output
    // =====================

    /// <summary>
    /// Gets or sets the library path where STRM files will be created for this provider.
    /// </summary>
    public string LibraryPath { get; set; } = "/config/xtream-library";

    // =====================
    // Content
    // =====================

    /// <summary>
    /// Gets or sets a value indicating whether to sync movies/VOD content.
    /// </summary>
    public bool SyncMovies { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to sync series content.
    /// </summary>
    public bool SyncSeries { get; set; } = true;

    /// <summary>
    /// Gets or sets the array of selected VOD category IDs to sync.
    /// Empty array means sync all categories.
    /// </summary>
    public int[] SelectedVodCategoryIds { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Gets or sets the array of selected Series category IDs to sync.
    /// Empty array means sync all categories.
    /// </summary>
    public int[] SelectedSeriesCategoryIds { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Gets or sets VOD stream IDs to exclude from sync, even if their category is selected.
    /// Empty array means no per-movie exclusions (sync every movie in the selected categories).
    /// </summary>
    public int[] ExcludedVodStreamIds { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Gets or sets series IDs to exclude from sync, even if their category is selected.
    /// Empty array means no per-series exclusions (sync every series in the selected categories).
    /// </summary>
    public int[] ExcludedSeriesIds { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Gets or sets a value indicating whether to skip series that already have STRM files.
    /// </summary>
    public bool SmartSkipExisting { get; set; } = true;

    // =====================
    // Folder Organization
    // =====================

    /// <summary>
    /// Gets or sets the movie folder mode.
    /// "Single" = all movies sync to root Movies folder.
    /// "Multiple" = movies sync to custom subfolders based on category mappings.
    /// </summary>
    public string MovieFolderMode { get; set; } = "Single";

    /// <summary>
    /// Gets or sets the movie folder mappings.
    /// Format: one mapping per line, "FolderName=CategoryId1,CategoryId2,CategoryId3".
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
    /// </summary>
    public string SeriesFolderMappings { get; set; } = string.Empty;

    // =====================
    // Metadata
    // =====================

    /// <summary>
    /// Gets or sets folder name to TMDb ID overrides for movies.
    /// Format: one mapping per line, "FolderName=TmdbID".
    /// </summary>
    public string TmdbFolderIdOverrides { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets folder name to TVDb ID overrides for series.
    /// Format: one mapping per line, "FolderName=TvdbID".
    /// </summary>
    public string TvdbFolderIdOverrides { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets custom terms to remove from movie and series titles.
    /// One term per line.
    /// </summary>
    public string CustomTitleRemoveTerms { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets regex patterns to remove from STRM file names.
    /// One .NET regex per line.
    /// </summary>
    public string RegexRemovalPatterns { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to download artwork from the provider
    /// for content that could not be matched to TMDb/TVDb.
    /// </summary>
    public bool DownloadArtworkForUnmatched { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to proactively fetch media info
    /// during sync and write NFO sidecar files.
    /// </summary>
    public bool EnableProactiveMediaInfo { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to retry metadata lookup without the year
    /// if the year-qualified lookup returns no result.
    /// </summary>
    public bool FallbackToYearlessLookup { get; set; }

    // =====================
    // Dispatcharr Mode
    // =====================

    /// <summary>
    /// Gets or sets a value indicating whether Dispatcharr mode is enabled.
    /// </summary>
    public bool EnableDispatcharrMode { get; set; }

    /// <summary>
    /// Gets or sets the Dispatcharr REST API username.
    /// </summary>
    public string DispatcharrApiUser { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Dispatcharr REST API password.
    /// </summary>
    public string DispatcharrApiPass { get; set; } = string.Empty;

    // =====================
    // Incremental Sync
    // =====================

    /// <summary>
    /// Gets or sets a value indicating whether incremental sync is enabled.
    /// </summary>
    public bool EnableIncrementalSync { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of days between forced full syncs.
    /// </summary>
    public int FullSyncIntervalDays { get; set; } = 7;

    /// <summary>
    /// Gets or sets the change threshold (0.0 to 1.0) that triggers a full sync.
    /// </summary>
    public double FullSyncChangeThreshold { get; set; } = 0.50;

    // =====================
    // Orphan Cleanup
    // =====================

    /// <summary>
    /// Gets or sets a value indicating whether to remove orphaned STRM files.
    /// </summary>
    public bool CleanupOrphans { get; set; } = true;

    /// <summary>
    /// Gets or sets the orphan safety threshold (0.0 to 1.0).
    /// </summary>
    public double OrphanSafetyThreshold { get; set; } = 0.20;

    // =====================
    // Performance
    // =====================

    /// <summary>
    /// Gets or sets the number of parallel API requests during sync.
    /// </summary>
    public int SyncParallelism { get; set; } = 10;

    /// <summary>
    /// Gets or sets the number of categories to process per batch during sync.
    /// Set to 0 to disable batching.
    /// </summary>
    public int CategoryBatchSize { get; set; } = 25;

    /// <summary>
    /// Gets or sets the delay in milliseconds between API requests.
    /// </summary>
    public int RequestDelayMs { get; set; } = 50;

    /// <summary>
    /// Gets or sets the maximum number of retries for rate-limited requests.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Gets or sets the initial retry delay in milliseconds after a 429 response.
    /// </summary>
    public int RetryDelayMs { get; set; } = 1000;

    /// <summary>
    /// Validates and clamps all configuration values to safe ranges.
    /// </summary>
    public void Validate()
    {
        SyncParallelism = Math.Clamp(SyncParallelism, 1, 20);

        if (CategoryBatchSize < 0)
        {
            CategoryBatchSize = 0;
        }
        else if (CategoryBatchSize > 100)
        {
            CategoryBatchSize = 100;
        }

        RequestDelayMs = Math.Max(RequestDelayMs, 0);
        MaxRetries = Math.Clamp(MaxRetries, 0, 10);
        RetryDelayMs = Math.Max(RetryDelayMs, 0);

        OrphanSafetyThreshold = Math.Clamp(OrphanSafetyThreshold, 0.0, 1.0);

        FullSyncIntervalDays = Math.Clamp(FullSyncIntervalDays, 1, 30);
        FullSyncChangeThreshold = Math.Clamp(FullSyncChangeThreshold, 0.0, 1.0);
    }
}
