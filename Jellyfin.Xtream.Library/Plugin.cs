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
    private static volatile Plugin? _instance;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        _instance = this;
        MigrateConfigurationIfNeeded();
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

    private void MigrateConfigurationIfNeeded()
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
