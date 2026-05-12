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
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Xtream.Library;

/// <summary>
/// Plugin configuration for Xtream Library.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // =====================
    // Multi-Provider List
    // =====================

    /// <summary>
    /// Gets or sets the list of configured Xtream providers.
    /// </summary>
    public List<ProviderConfig> Providers { get; set; } = new();

    // =====================
    // Global: Schedule
    // =====================

    /// <summary>
    /// Gets or sets the sync interval in minutes.
    /// </summary>
    public int SyncIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether to trigger a Jellyfin library scan after sync.
    /// </summary>
    public bool TriggerLibraryScan { get; set; }

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
    public int SyncDailyMinute { get; set; }

    // =====================
    // Global: Metadata
    // =====================

    /// <summary>
    /// Gets or sets a value indicating whether automatic metadata ID lookup is enabled.
    /// </summary>
    public bool EnableMetadataLookup { get; set; } = true;

    /// <summary>
    /// Gets or sets the metadata cache age in days before refresh.
    /// </summary>
    public int MetadataCacheAgeDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets the number of parallel metadata lookups.
    /// </summary>
    public int MetadataParallelism { get; set; } = 3;

    // =====================
    // Live TV Settings
    // =====================

    /// <summary>
    /// Gets or sets a value indicating whether Live TV support is enabled.
    /// </summary>
    public bool EnableLiveTv { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the native tuner host is enabled.
    /// </summary>
    public bool EnableNativeTuner { get; set; }

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
    public string LiveTvOutputFormat { get; set; } = "ts";

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
    /// </summary>
    public bool EnableChannelNameCleaning { get; set; } = true;

    /// <summary>
    /// Gets or sets custom terms to remove from channel names. One term per line.
    /// </summary>
    public string ChannelRemoveTerms { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets channel overrides.
    /// Format: StreamId=Name|Number|LogoUrl (one per line, fields optional).
    /// </summary>
    public string ChannelOverrides { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether catch-up/timeshift is enabled.
    /// </summary>
    public bool EnableCatchup { get; set; }

    /// <summary>
    /// Gets or sets the number of catch-up days to show.
    /// </summary>
    public int CatchupDays { get; set; } = 7;

    // =====================
    // Legacy fields — kept for XML deserialization during migration only.
    // Read by MigrateConfigurationIfNeeded() in Plugin.cs, then cleared.
    // =====================

#pragma warning disable CS0618, SA1623
    /// <summary>Gets or sets the legacy base URL. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].BaseUrl. Will be removed in a future version.")]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the legacy username. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].Username. Will be removed in a future version.")]
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the legacy password. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].Password. Will be removed in a future version.")]
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets the legacy user agent. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].UserAgent. Will be removed in a future version.")]
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>Gets or sets the legacy library path. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].LibraryPath. Will be removed in a future version.")]
    public string LibraryPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the legacy sync movies flag. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].SyncMovies. Will be removed in a future version.")]
    public bool SyncMovies { get; set; } = true;

    /// <summary>Gets or sets the legacy sync series flag. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].SyncSeries. Will be removed in a future version.")]
    public bool SyncSeries { get; set; } = true;

    /// <summary>Gets or sets the legacy cleanup orphans flag. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].CleanupOrphans. Will be removed in a future version.")]
    public bool CleanupOrphans { get; set; } = true;

    /// <summary>Gets or sets the legacy orphan safety threshold. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].OrphanSafetyThreshold. Will be removed in a future version.")]
    public double OrphanSafetyThreshold { get; set; } = 0.20;

    /// <summary>Gets or sets the legacy selected VOD category IDs. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].SelectedVodCategoryIds. Will be removed in a future version.")]
    public int[] SelectedVodCategoryIds { get; set; } = Array.Empty<int>();

    /// <summary>Gets or sets the legacy selected Series category IDs. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].SelectedSeriesCategoryIds. Will be removed in a future version.")]
    public int[] SelectedSeriesCategoryIds { get; set; } = Array.Empty<int>();

    /// <summary>Gets or sets the legacy sync parallelism. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].SyncParallelism. Will be removed in a future version.")]
    public int SyncParallelism { get; set; } = 10;

    /// <summary>Gets or sets the legacy smart skip existing flag. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].SmartSkipExisting. Will be removed in a future version.")]
    public bool SmartSkipExisting { get; set; } = true;

    /// <summary>Gets or sets the legacy TMDb folder ID overrides. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].TmdbFolderIdOverrides. Will be removed in a future version.")]
    public string TmdbFolderIdOverrides { get; set; } = string.Empty;

    /// <summary>Gets or sets the legacy TVDb folder ID overrides. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].TvdbFolderIdOverrides. Will be removed in a future version.")]
    public string TvdbFolderIdOverrides { get; set; } = string.Empty;

    /// <summary>Gets or sets the legacy custom title remove terms. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].CustomTitleRemoveTerms. Will be removed in a future version.")]
    public string CustomTitleRemoveTerms { get; set; } = string.Empty;

    /// <summary>Gets or sets the legacy regex removal patterns. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].RegexRemovalPatterns. Will be removed in a future version.")]
    public string RegexRemovalPatterns { get; set; } = string.Empty;

    /// <summary>Gets or sets the legacy download artwork flag. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].DownloadArtworkForUnmatched. Will be removed in a future version.")]
    public bool DownloadArtworkForUnmatched { get; set; } = true;

    /// <summary>Gets or sets the legacy proactive media info flag. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].EnableProactiveMediaInfo. Will be removed in a future version.")]
    public bool EnableProactiveMediaInfo { get; set; }

    /// <summary>Gets or sets the legacy category batch size. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].CategoryBatchSize. Will be removed in a future version.")]
    public int CategoryBatchSize { get; set; } = 25;

    /// <summary>Gets or sets the legacy movie folder mode. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].MovieFolderMode. Will be removed in a future version.")]
    public string MovieFolderMode { get; set; } = "Single";

    /// <summary>Gets or sets the legacy movie folder mappings. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].MovieFolderMappings. Will be removed in a future version.")]
    public string MovieFolderMappings { get; set; } = string.Empty;

    /// <summary>Gets or sets the legacy series folder mode. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].SeriesFolderMode. Will be removed in a future version.")]
    public string SeriesFolderMode { get; set; } = "Single";

    /// <summary>Gets or sets the legacy series folder mappings. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].SeriesFolderMappings. Will be removed in a future version.")]
    public string SeriesFolderMappings { get; set; } = string.Empty;

    /// <summary>Gets or sets the legacy fallback to yearless lookup flag. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].FallbackToYearlessLookup. Will be removed in a future version.")]
    public bool FallbackToYearlessLookup { get; set; }

    /// <summary>Gets or sets the legacy Dispatcharr mode flag. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].EnableDispatcharrMode. Will be removed in a future version.")]
    public bool EnableDispatcharrMode { get; set; }

    /// <summary>Gets or sets the legacy Dispatcharr API user. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].DispatcharrApiUser. Will be removed in a future version.")]
    public string DispatcharrApiUser { get; set; } = string.Empty;

    /// <summary>Gets or sets the legacy Dispatcharr API password. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].DispatcharrApiPass. Will be removed in a future version.")]
    public string DispatcharrApiPass { get; set; } = string.Empty;

    /// <summary>Gets or sets the legacy incremental sync flag. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].EnableIncrementalSync. Will be removed in a future version.")]
    public bool EnableIncrementalSync { get; set; } = true;

    /// <summary>Gets or sets the legacy full sync interval days. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].FullSyncIntervalDays. Will be removed in a future version.")]
    public int FullSyncIntervalDays { get; set; } = 7;

    /// <summary>Gets or sets the legacy full sync change threshold. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].FullSyncChangeThreshold. Will be removed in a future version.")]
    public double FullSyncChangeThreshold { get; set; } = 0.50;

    /// <summary>Gets or sets the legacy request delay ms. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].RequestDelayMs. Will be removed in a future version.")]
    public int RequestDelayMs { get; set; } = 50;

    /// <summary>Gets or sets the legacy max retries. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].MaxRetries. Will be removed in a future version.")]
    public int MaxRetries { get; set; } = 3;

    /// <summary>Gets or sets the legacy retry delay ms. Migrated to Providers[0].</summary>
    [Obsolete("Migrated to Providers[0].RetryDelayMs. Will be removed in a future version.")]
    public int RetryDelayMs { get; set; } = 1000;
#pragma warning restore CS0618, SA1623

    // =====================
    // Helpers
    // =====================

    /// <summary>
    /// Gets or sets a value indicating whether any two providers share the same LibraryPath.
    /// Set by Validate(). Callers should log a warning when this is true.
    /// </summary>
    public bool HasDuplicateLibraryPaths { get; set; }

    /// <summary>
    /// Gets the first provider with a configured BaseUrl, used for Live TV.
    /// </summary>
    /// <returns>The first enabled provider, or null if none configured.</returns>
    public ProviderConfig? GetLiveTvProvider() =>
        Providers.FirstOrDefault(p => !string.IsNullOrEmpty(p.BaseUrl));

    /// <summary>
    /// Validates and clamps all configuration values to safe ranges.
    /// </summary>
    public void Validate()
    {
        // Global: metadata
        MetadataParallelism = Math.Clamp(MetadataParallelism, 1, 10);
        SyncIntervalMinutes = Math.Max(SyncIntervalMinutes, 1);
        MetadataCacheAgeDays = Math.Max(MetadataCacheAgeDays, 0);

        // Global: Live TV
        EpgParallelism = Math.Clamp(EpgParallelism, 1, 20);
        M3UCacheMinutes = Math.Max(M3UCacheMinutes, 1);
        EpgCacheMinutes = Math.Max(EpgCacheMinutes, 1);
        EpgDaysToFetch = Math.Clamp(EpgDaysToFetch, 1, 14);
        CatchupDays = Math.Clamp(CatchupDays, 1, 30);

        // Global: daily schedule
        SyncDailyHour = Math.Clamp(SyncDailyHour, 0, 23);
        SyncDailyMinute = Math.Clamp(SyncDailyMinute, 0, 59);

        // Per-provider validation
        foreach (var provider in Providers)
        {
            provider.Validate();
        }

        // Warn on duplicate library paths (orphan cleanup cross-contamination)
        var paths = Providers
            .Where(p => !string.IsNullOrEmpty(p.LibraryPath))
            .Select(p => p.LibraryPath)
            .ToList();
        HasDuplicateLibraryPaths = paths.Count != paths.Distinct(StringComparer.OrdinalIgnoreCase).Count();
    }
}
