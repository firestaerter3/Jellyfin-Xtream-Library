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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using Jellyfin.Xtream.Library.Client;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Xtream.Library;

/// <summary>
/// The Xtream Library plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    // BUG-009: candidate filenames the orphan-config importer will look at, in addition to
    // (and never equal to) whatever ConfigurationFilePath currently resolves to.
    private static readonly string[] OrphanCandidateFileNames =
    {
        "Jellyfin.Xtream.Library.xml",
        "Jellyfin.Xtream.xml",
    };

    private static volatile Plugin? _instance;

    private readonly IApplicationPaths _appPaths;
    private readonly IXmlSerializer _xmlSerializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        _instance = this;
        _appPaths = applicationPaths;
        _xmlSerializer = xmlSerializer;
        ImportOrphanedLegacyConfigIfNeeded();
        MigrateProvidersIfNeeded();
        MigrateLiveChannelModeIfNeeded();
    }

    /// <inheritdoc />
    public override string Name => "Xtream Library";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("63ba5fcd-c8ce-421a-83e8-ba0b11030d53");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin Instance => _instance ?? throw new InvalidOperationException("Plugin instance not available");

    /// <summary>
    /// Gets the Xtream connection info for the specified provider index.
    /// </summary>
    /// <param name="providerIndex">Zero-based index into <see cref="PluginConfiguration.Providers"/>.</param>
    /// <returns>The <see cref="ConnectionInfo"/> for the requested provider.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="providerIndex"/> is out of range.</exception>
    public ConnectionInfo GetCreds(int providerIndex)
    {
        var providers = Configuration.Providers;
        if (providerIndex < 0 || providerIndex >= providers.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerIndex),
                providerIndex,
                $"Provider index {providerIndex} is out of range (0-{providers.Count - 1}).");
        }

        var p = providers[providerIndex];
        return new ConnectionInfo(p.BaseUrl, p.Username, p.Password);
    }

    private void MigrateProvidersIfNeeded()
    {
        var config = Configuration;

        if (config.Providers.Count != 0 || string.IsNullOrEmpty(config.BaseUrl))
        {
            return;
        }

        config.Providers.Add(new ProviderConfig
        {
            Name = "Provider 1",
            IsEnabled = true,
            BaseUrl = config.BaseUrl,
            Username = config.Username,
            Password = config.Password,
            UserAgent = config.UserAgent,
            LibraryPath = !string.IsNullOrEmpty(config.LibraryPath) ? config.LibraryPath : "/config/xtream-library",
            SyncMovies = config.SyncMovies,
            SyncSeries = config.SyncSeries,
            SelectedVodCategoryIds = config.SelectedVodCategoryIds,
            SelectedSeriesCategoryIds = config.SelectedSeriesCategoryIds,
            SmartSkipExisting = config.SmartSkipExisting,
            MovieFolderMode = config.MovieFolderMode,
            MovieFolderMappings = config.MovieFolderMappings,
            SeriesFolderMode = config.SeriesFolderMode,
            SeriesFolderMappings = config.SeriesFolderMappings,
            TmdbFolderIdOverrides = config.TmdbFolderIdOverrides,
            TvdbFolderIdOverrides = config.TvdbFolderIdOverrides,
            CustomTitleRemoveTerms = config.CustomTitleRemoveTerms,
            RegexRemovalPatterns = config.RegexRemovalPatterns,
            DownloadArtworkForUnmatched = config.DownloadArtworkForUnmatched,
            EnableProactiveMediaInfo = config.EnableProactiveMediaInfo,
            FallbackToYearlessLookup = config.FallbackToYearlessLookup,
            EnableDispatcharrMode = config.EnableDispatcharrMode,
            DispatcharrApiUser = config.DispatcharrApiUser,
            DispatcharrApiPass = config.DispatcharrApiPass,
            EnableIncrementalSync = config.EnableIncrementalSync,
            FullSyncIntervalDays = config.FullSyncIntervalDays,
            FullSyncChangeThreshold = config.FullSyncChangeThreshold,
            CleanupOrphans = config.CleanupOrphans,
            OrphanSafetyThreshold = config.OrphanSafetyThreshold,
            SyncParallelism = config.SyncParallelism,
            CategoryBatchSize = config.CategoryBatchSize,
            RequestDelayMs = config.RequestDelayMs,
            MaxRetries = config.MaxRetries,
            RetryDelayMs = config.RetryDelayMs,
        });
        SaveConfiguration();
    }

    private void MigrateLiveChannelModeIfNeeded()
    {
        var config = Configuration;

        // Pre-overhaul (v1.34.0.0 and earlier) configs default to IncludeAll because the field
        // is new. If they have any Live TV selection state, the user clearly *intended* selective
        // sync — promote them to Custom mode so the new empty-means-none semantics don't
        // suddenly drop their previously-selected categories.
        if (PluginConfiguration.ShouldMigrateToCustomMode(
                config.LiveChannelMode,
                config.SelectedLiveCategoryIds.Length,
                config.ExcludedLiveStreamIds.Length))
        {
            config.LiveChannelMode = LiveChannelSelectionMode.Custom;
            SaveConfiguration();
        }
    }

    // BUG-009 (GitHub #49). Some users who upgraded across the v1.33.3.0 GUID change ended up
    // with the active plugin config on disk diverging from the file Jellyfin actually loads —
    // their pre-v1.32 settings (folder mappings, etc.) sat orphaned in a sibling XML file in
    // the configurations directory while the new plugin started against an empty in-memory
    // Configuration. MigrateProvidersIfNeeded only operates on what Jellyfin already loaded,
    // so it had no way to recover that orphan.
    //
    // This method runs before MigrateProvidersIfNeeded. If the in-memory Configuration looks
    // completely fresh (no Providers, no legacy BaseUrl, no folder mappings), we scan the
    // configurations directory for a known-name orphan file, validate it against a schema
    // sentinel that distinguishes our PluginConfiguration from the upstream Kevin Jilissen
    // Jellyfin.Xtream plugin (which shares the root element name), copy its legacy fields
    // into the in-memory Configuration, and rename the orphan to a .bak so it isn't picked
    // up again next boot. MigrateProvidersIfNeeded then runs as normal and persists the
    // recovered fields into Providers[0].
    private void ImportOrphanedLegacyConfigIfNeeded()
    {
        var config = Configuration;

        // Trigger gate: completely fresh in-memory config only. Any signal of existing data
        // means we don't second-guess the active config — the user might be on a working
        // install and a silent merge from a stale orphan would be much worse than the current
        // bug.
        if (config.Providers.Count != 0
            || !string.IsNullOrEmpty(config.BaseUrl)
            || !string.IsNullOrEmpty(config.MovieFolderMappings)
            || !string.IsNullOrEmpty(config.SeriesFolderMappings))
        {
            return;
        }

        string activeConfigPath;
        try
        {
            activeConfigPath = ConfigurationFilePath;
        }
        catch (Exception ex)
        {
            LogOrphan($"Could not resolve ConfigurationFilePath ({ex.GetType().Name}: {ex.Message}); skipping orphan scan.");
            return;
        }

        var activeConfigName = Path.GetFileName(activeConfigPath);
        var configDir = _appPaths.PluginConfigurationsPath;
        if (string.IsNullOrEmpty(configDir) || !Directory.Exists(configDir))
        {
            return;
        }

        var candidates = new List<(string Path, PluginConfiguration Config, DateTime MTime)>();
        foreach (var fileName in OrphanCandidateFileNames)
        {
            if (string.Equals(fileName, activeConfigName, StringComparison.OrdinalIgnoreCase))
            {
                // Never re-import the file Jellyfin already loaded — that's the active config.
                continue;
            }

            var path = Path.Combine(configDir, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            PluginConfiguration? parsed = null;
            try
            {
                parsed = _xmlSerializer.DeserializeFromFile(typeof(PluginConfiguration), path) as PluginConfiguration;
            }
            catch (Exception ex) when (ex is InvalidOperationException or XmlException or IOException)
            {
                LogOrphan($"Could not parse candidate orphan {fileName} ({ex.GetType().Name}: {ex.Message}); skipping.");
                continue;
            }

            if (parsed == null)
            {
                continue;
            }

            // Schema sentinel. The upstream Kevin Jilissen Jellyfin.Xtream plugin shares the
            // <PluginConfiguration> root element name and many overlapping field names (BaseUrl,
            // Username, etc.), so deserialising its file as our PluginConfiguration would
            // succeed and silently cross-pollinate credentials. None of these four fields exist
            // in his schema, so requiring at least one of them is both a positive identity
            // check ("this is our shape") and ensures there's something worth recovering.
            var hasOurSchemaMarker =
                !string.IsNullOrEmpty(parsed.MovieFolderMappings)
                || !string.IsNullOrEmpty(parsed.SeriesFolderMappings)
                || !string.IsNullOrEmpty(parsed.TmdbFolderIdOverrides)
                || !string.IsNullOrEmpty(parsed.TvdbFolderIdOverrides);

            if (!hasOurSchemaMarker)
            {
                LogOrphan($"Candidate {fileName} parsed but no schema sentinel found; skipping (likely upstream Jellyfin.Xtream config).");
                continue;
            }

            DateTime mtime;
            try
            {
                mtime = File.GetLastWriteTimeUtc(path);
            }
            catch (IOException)
            {
                mtime = DateTime.MinValue;
            }

            candidates.Add((path, parsed, mtime));
        }

        if (candidates.Count == 0)
        {
            return;
        }

        // Newer mtime wins; deterministic alphabetical tiebreak so behaviour is reproducible.
        var chosen = candidates
            .OrderByDescending(c => c.MTime)
            .ThenBy(c => Path.GetFileName(c.Path), StringComparer.Ordinal)
            .First();

        CopyLegacyFieldsInto(chosen.Config, config);
        SaveConfiguration();

        LogOrphan($"Imported legacy fields from orphan config '{Path.GetFileName(chosen.Path)}' into active config '{activeConfigName}'.");

        // Cleanup. Rename to a .bak suffix that does NOT match OrphanCandidateFileNames so
        // we don't re-import next boot. Rename failure is non-fatal — the import already
        // succeeded and SaveConfiguration() above persisted the recovered data.
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var backupPath = chosen.Path + $".migrated-{stamp}.bak";
        try
        {
            File.Move(chosen.Path, backupPath);
            LogOrphan($"Renamed orphan to '{Path.GetFileName(backupPath)}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogOrphan($"Could not rename orphan to '{Path.GetFileName(backupPath)}' ({ex.GetType().Name}: {ex.Message}); data was recovered into the active config — please remove the orphan manually.");
        }

        // Leave the not-chosen candidates alone. They weren't read, so we don't want to imply
        // we've migrated them by renaming them. Surface them so the user can investigate.
        foreach (var rejected in candidates.Where(c => !ReferenceEquals(c.Path, chosen.Path)))
        {
            LogOrphan($"Other orphan candidate '{Path.GetFileName(rejected.Path)}' was not imported (newer candidate won); left untouched on disk.");
        }
    }

    // Mirror of the legacy-field assignment block at the top of MigrateProvidersIfNeeded, but
    // from a source PluginConfiguration into the in-memory one. We deliberately do NOT touch
    // Providers here — the next call to MigrateProvidersIfNeeded handles that, using the same
    // logic it uses for the normal pre-v1.32 upgrade path.
    private static void CopyLegacyFieldsInto(PluginConfiguration source, PluginConfiguration target)
    {
        target.BaseUrl = source.BaseUrl;
        target.Username = source.Username;
        target.Password = source.Password;
        target.UserAgent = source.UserAgent;
        target.LibraryPath = source.LibraryPath;
        target.SyncMovies = source.SyncMovies;
        target.SyncSeries = source.SyncSeries;
        target.SelectedVodCategoryIds = source.SelectedVodCategoryIds;
        target.SelectedSeriesCategoryIds = source.SelectedSeriesCategoryIds;
        target.SmartSkipExisting = source.SmartSkipExisting;
        target.MovieFolderMode = source.MovieFolderMode;
        target.MovieFolderMappings = source.MovieFolderMappings;
        target.SeriesFolderMode = source.SeriesFolderMode;
        target.SeriesFolderMappings = source.SeriesFolderMappings;
        target.TmdbFolderIdOverrides = source.TmdbFolderIdOverrides;
        target.TvdbFolderIdOverrides = source.TvdbFolderIdOverrides;
        target.CustomTitleRemoveTerms = source.CustomTitleRemoveTerms;
        target.RegexRemovalPatterns = source.RegexRemovalPatterns;
        target.DownloadArtworkForUnmatched = source.DownloadArtworkForUnmatched;
        target.EnableProactiveMediaInfo = source.EnableProactiveMediaInfo;
        target.FallbackToYearlessLookup = source.FallbackToYearlessLookup;
        target.EnableDispatcharrMode = source.EnableDispatcharrMode;
        target.DispatcharrApiUser = source.DispatcharrApiUser;
        target.DispatcharrApiPass = source.DispatcharrApiPass;
        target.EnableIncrementalSync = source.EnableIncrementalSync;
        target.FullSyncIntervalDays = source.FullSyncIntervalDays;
        target.FullSyncChangeThreshold = source.FullSyncChangeThreshold;
        target.CleanupOrphans = source.CleanupOrphans;
        target.OrphanSafetyThreshold = source.OrphanSafetyThreshold;
        target.SyncParallelism = source.SyncParallelism;
        target.CategoryBatchSize = source.CategoryBatchSize;
        target.RequestDelayMs = source.RequestDelayMs;
        target.MaxRetries = source.MaxRetries;
        target.RetryDelayMs = source.RetryDelayMs;
    }

    // Plugin constructor runs before any ILogger is available via DI on this type, so stdout
    // is the most reliable channel — Jellyfin captures it into the server log.
    private static void LogOrphan(string message)
    {
        Console.WriteLine($"[Xtream.Library.OrphanImport] {message}");
    }

    private static PluginPageInfo CreateStatic(string name) => new()
    {
        Name = name,
        EmbeddedResourcePath = string.Format(
            CultureInfo.InvariantCulture,
            "{0}.Configuration.Web.{1}",
            typeof(Plugin).Namespace,
            name),
    };

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            CreateStatic("config.html"),
            CreateStatic("config.js"),
        };
    }
}
