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
    /// Gets or sets the orphan safety threshold (0.0 to 1.0).
    /// If more than this fraction of content would be deleted, orphan cleanup is skipped as a safety measure.
    /// Default: 0.20 (20% of content).
    /// </summary>
    public double OrphanSafetyThreshold { get; set; } = 0.20;

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
    public int SyncParallelism { get; set; } = 6;

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
    public bool EnableMetadataLookup { get; set; } = true;

    /// <summary>
    /// Gets or sets the metadata cache age in days before refresh.
    /// Cached lookup results older than this will be re-fetched.
    /// </summary>
    public int MetadataCacheAgeDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets the number of parallel metadata lookups.
    /// Higher values speed up first sync but may trigger API rate limits.
    /// </summary>
    public int MetadataParallelism { get; set; } = 3;

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
    /// Gets or sets the number of categories to process per batch during sync.
    /// Lower values reduce memory usage but may increase sync duration.
    /// Set to 0 to disable batching (process all categories at once).
    /// </summary>
    public int CategoryBatchSize { get; set; } = 10;

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

    // =====================
    // Incremental Sync Settings
    // =====================

    /// <summary>
    /// Gets or sets a value indicating whether incremental sync is enabled.
    /// When enabled, only new, modified, and removed content is processed after the first full sync.
    /// </summary>
    public bool EnableIncrementalSync { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of days between forced full syncs.
    /// Even with incremental sync enabled, a full sync is performed periodically to ensure consistency.
    /// </summary>
    public int FullSyncIntervalDays { get; set; } = 7;

    /// <summary>
    /// Gets or sets the change threshold (0.0 to 1.0) that triggers a full sync.
    /// If more than this fraction of content changed in a delta, fall back to full sync
    /// as a safety measure against provider data corruption.
    /// Default: 0.50 (50% of content).
    /// </summary>
    public double FullSyncChangeThreshold { get; set; } = 0.50;

    // =====================
    // Live TV Settings
    // =====================

    /// <summary>
    /// Gets or sets a value indicating whether Live TV support is enabled.
    /// When enabled, M3U and EPG endpoints become available.
    /// </summary>
    public bool EnableLiveTv { get; set; }

    /// <summary>
    /// Gets or sets the array of selected Live TV category IDs.
    /// Empty array means include all Live TV categories.
    /// </summary>
    public int[] SelectedLiveCategoryIds { get; set; } = Array.Empty<int>();

    /// <summary>
    /// Gets or sets a value indicating whether to generate EPG data.
    /// </summary>
    public bool EnableEpg { get; set; } = true;

    /// <summary>
    /// Gets or sets the M3U playlist cache duration in minutes.
    /// </summary>
    public int M3UCacheMinutes { get; set; } = 15;

    /// <summary>
    /// Gets or sets the EPG cache duration in minutes.
    /// </summary>
    public int EpgCacheMinutes { get; set; } = 30;

    /// <summary>
    /// Gets or sets the number of days of EPG data to fetch.
    /// </summary>
    public int EpgDaysToFetch { get; set; } = 2;

    /// <summary>
    /// Gets or sets the Live TV output format (m3u8 or ts).
    /// </summary>
    public string LiveTvOutputFormat { get; set; } = "m3u8";

    /// <summary>
    /// Gets or sets a value indicating whether to include adult channels.
    /// </summary>
    public bool IncludeAdultChannels { get; set; }

    /// <summary>
    /// Gets or sets the number of parallel EPG fetch requests.
    /// </summary>
    public int EpgParallelism { get; set; } = 5;

    /// <summary>
    /// Gets or sets a value indicating whether channel name cleaning is enabled.
    /// Removes country prefixes, quality tags, codec info from channel names.
    /// </summary>
    public bool EnableChannelNameCleaning { get; set; } = true;

    /// <summary>
    /// Gets or sets custom terms to remove from channel names.
    /// One term per line.
    /// </summary>
    public string ChannelRemoveTerms { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets channel overrides.
    /// Format: StreamId=Name|Number|LogoUrl (one per line, fields optional).
    /// Example: "123=BBC One|1|http://logo.png".
    /// </summary>
    public string ChannelOverrides { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether catch-up/timeshift is enabled.
    /// Only channels with tv_archive=1 support catch-up.
    /// </summary>
    public bool EnableCatchup { get; set; }

    /// <summary>
    /// Gets or sets the number of catch-up days to show.
    /// Limited by the channel's tv_archive_duration.
    /// </summary>
    public int CatchupDays { get; set; } = 7;

    // =====================
    // Rate Limiting Settings
    // =====================

    /// <summary>
    /// Gets or sets the delay in milliseconds between API requests.
    /// Helps prevent rate limiting (429 errors) from the provider.
    /// Set to 0 for no delay. Default: 50ms.
    /// </summary>
    public int RequestDelayMs { get; set; } = 50;

    /// <summary>
    /// Gets or sets the maximum number of retries for rate-limited requests.
    /// When a 429 response is received, the request will be retried after a delay.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Gets or sets the initial retry delay in milliseconds after a 429 response.
    /// Each subsequent retry doubles this delay (exponential backoff).
    /// </summary>
    public int RetryDelayMs { get; set; } = 1000;

    /// <summary>
    /// Validates and clamps all configuration values to safe ranges.
    /// Call this before using configuration values to ensure they are within valid bounds.
    /// </summary>
    public void Validate()
    {
        // Clamp parallelism settings to reasonable values
        SyncParallelism = Math.Clamp(SyncParallelism, 1, 20);
        MetadataParallelism = Math.Clamp(MetadataParallelism, 1, 10);
        EpgParallelism = Math.Clamp(EpgParallelism, 1, 20);

        // Clamp time intervals to positive values
        SyncIntervalMinutes = Math.Max(SyncIntervalMinutes, 1);
        MetadataCacheAgeDays = Math.Max(MetadataCacheAgeDays, 0);
        M3UCacheMinutes = Math.Max(M3UCacheMinutes, 1);
        EpgCacheMinutes = Math.Max(EpgCacheMinutes, 1);
        EpgDaysToFetch = Math.Clamp(EpgDaysToFetch, 1, 14);
        CatchupDays = Math.Clamp(CatchupDays, 1, 30);

        // Clamp batch size to reasonable values (0 = unlimited)
        if (CategoryBatchSize < 0)
        {
            CategoryBatchSize = 0;
        }
        else if (CategoryBatchSize > 100)
        {
            CategoryBatchSize = 100;
        }

        // Clamp daily schedule to valid time ranges
        SyncDailyHour = Math.Clamp(SyncDailyHour, 0, 23);
        SyncDailyMinute = Math.Clamp(SyncDailyMinute, 0, 59);

        // Clamp rate limiting settings
        RequestDelayMs = Math.Max(RequestDelayMs, 0);
        MaxRetries = Math.Clamp(MaxRetries, 0, 10);
        RetryDelayMs = Math.Max(RetryDelayMs, 0);

        // Clamp orphan safety threshold to 0.0-1.0
        OrphanSafetyThreshold = Math.Clamp(OrphanSafetyThreshold, 0.0, 1.0);

        // Clamp incremental sync settings
        FullSyncIntervalDays = Math.Clamp(FullSyncIntervalDays, 1, 30);
        FullSyncChangeThreshold = Math.Clamp(FullSyncChangeThreshold, 0.0, 1.0);
    }
}
