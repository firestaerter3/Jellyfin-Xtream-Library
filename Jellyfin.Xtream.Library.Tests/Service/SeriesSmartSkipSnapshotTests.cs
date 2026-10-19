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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Service.Models;
using Jellyfin.Xtream.Library.Tests.Helpers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// A series smart-skipped during a full sync used to go into the new snapshot with
/// EpisodeCount 0, so the next full sync fetched it again: every other full sync asked
/// get_series_info for the whole catalogue.
/// </summary>
[Collection("PluginSingletonTests")]
public class SeriesSmartSkipSnapshotTests : IDisposable
{
    private static readonly DateTime Modified = new(2025, 1, 5, 11, 9, 15, DateTimeKind.Utc);

    private readonly string _libraryPath;
    private readonly Mock<IXtreamClient> _client = new();

    public SeriesSmartSkipSnapshotTests()
    {
        _libraryPath = Path.Combine(Path.GetTempPath(), "xtream-smartskip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_libraryPath);

        _client.Setup(c => c.GetSeriesCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Shows" } });
        _client.Setup(c => c.GetSeriesByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new List<Series> { new() { SeriesId = 7, Name = "Skip Show (2024)", LastModified = Modified } });
        _client.Setup(c => c.GetSeriesStreamsBySeriesAsync(It.IsAny<ConnectionInfo>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SeriesStreamInfo
            {
                Seasons = new List<Season> { new() { SeasonNumber = 1 } },
                Episodes = new Dictionary<int, ICollection<Episode>>
                {
                    [1] = new List<Episode>
                    {
                        new() { EpisodeId = 11, EpisodeNum = 1, Season = 1, Title = "Pilot", ContainerExtension = "mkv" },
                        new() { EpisodeId = 12, EpisodeNum = 2, Season = 1, Title = "Second", ContainerExtension = "mkv" },
                    },
                },
            });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_libraryPath))
            {
                Directory.Delete(_libraryPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; a leftover temp directory must not fail the suite.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task FullSyncs_SkippedSeriesKeepsItsEpisodeCount_AndIsNotFetchedAgain()
    {
        // Each run changes the exclusion list, which changes the config fingerprint and forces a
        // full sync with the previous snapshot as the smart-skip hint.
        await RunFullSyncAsync(1).ConfigureAwait(true);
        await RunFullSyncAsync(2).ConfigureAwait(true);
        await RunFullSyncAsync(3).ConfigureAwait(true);

        _client.Verify(
            c => c.GetSeriesStreamsBySeriesAsync(It.IsAny<ConnectionInfo>(), 7, It.IsAny<CancellationToken>()),
            Times.Once());
        LatestSnapshotText().Should().MatchRegex("\"EpisodeCount\"\\s*:\\s*2");
    }

    [Fact]
    public void EffectiveEpisodeCount_FetchedWithoutEpisodes_IsUnknown()
    {
        var info = new ConcurrentDictionary<int, SeriesStreamInfo>();
        info[7] = new SeriesStreamInfo { Episodes = null };

        StrmSyncService.EffectiveEpisodeCount(info, Previous(2, Modified), Listed(Modified)).Should().Be(0);
    }

    [Fact]
    public void EffectiveEpisodeCount_PrefersInfoFetchedThisRun()
    {
        var info = new ConcurrentDictionary<int, SeriesStreamInfo>();
        info[7] = new SeriesStreamInfo
        {
            Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new(), new(), new() } },
        };

        StrmSyncService.EffectiveEpisodeCount(info, Previous(2, Modified), Listed(Modified)).Should().Be(3);
    }

    [Fact]
    public void EffectiveEpisodeCount_NotFetched_CarriesTheCountForward()
    {
        StrmSyncService.EffectiveEpisodeCount(new ConcurrentDictionary<int, SeriesStreamInfo>(), Previous(2, Modified), Listed(Modified))
            .Should().Be(2);
    }

    [Fact]
    public void EffectiveEpisodeCount_NotFetchedButChanged_IsUnknown()
    {
        // A changed series has to be read again, so its old count must not survive.
        StrmSyncService.EffectiveEpisodeCount(new ConcurrentDictionary<int, SeriesStreamInfo>(), Previous(2, Modified), Listed(Modified.AddHours(1)))
            .Should().Be(0);
    }

    [Fact]
    public void EffectiveEpisodeCount_NoPreviousSnapshot_IsUnknown()
    {
        StrmSyncService.EffectiveEpisodeCount(new ConcurrentDictionary<int, SeriesStreamInfo>(), null, Listed(Modified))
            .Should().Be(0);
    }

    private static Series Listed(DateTime modified) => new() { SeriesId = 7, Name = "Skip Show (2024)", LastModified = modified };

    private static ContentSnapshot Previous(int episodeCount, DateTime modified)
    {
        var snapshot = new ContentSnapshot();
        snapshot.Series[7] = new SeriesSnapshot { SeriesId = 7, EpisodeCount = episodeCount, LastModified = modified };
        return snapshot;
    }

    private string LatestSnapshotText()
        => File.ReadAllText(Directory.GetFiles(_libraryPath, "snapshot_*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Last());

    private async Task<SyncResult> RunFullSyncAsync(int run)
    {
        var appPaths = new Mock<IServerApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(_libraryPath);
        appPaths.Setup(p => p.DataPath).Returns(_libraryPath);
        appPaths.Setup(p => p.ProgramDataPath).Returns(_libraryPath);
        appPaths.Setup(p => p.CachePath).Returns(_libraryPath);
        appPaths.Setup(p => p.TempDirectory).Returns(_libraryPath);
        appPaths.Setup(p => p.PluginsPath).Returns(_libraryPath);

        var plugin = new Plugin(appPaths.Object, new RealXmlSerializer());
        plugin.Configuration.Providers = new List<ProviderConfig>
        {
            new()
            {
                Name = "test",
                BaseUrl = "http://provider.test",
                Username = "u",
                Password = "p",
                LibraryPath = _libraryPath,
                SyncMovies = false,
                SyncSeries = true,
                EnableIncrementalSync = true,
                SmartSkipExisting = true,
                DownloadArtworkForUnmatched = false,
                SyncParallelism = 1,
                ExcludedSeriesIds = new[] { 1000 + run },
            },
        };
        plugin.Configuration.EnableLiveTv = false;
        plugin.Configuration.EnableMetadataLookup = false;

        var host = new Mock<IServerApplicationHost>();
        host.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");

        var service = new StrmSyncService(
            _client.Object,
            new Mock<IDispatcharrClient>().Object,
            new Mock<ILibraryManager>().Object,
            new Mock<IMetadataLookupService>().Object,
            new SnapshotService(appPaths.Object, NullLogger<SnapshotService>.Instance),
            new LiveTvService(_client.Object, new Mock<IDispatcharrClient>().Object, appPaths.Object, host.Object, NullLogger<LiveTvService>.Instance),
            appPaths.Object,
            NullLogger<StrmSyncService>.Instance);

        return await service.SyncAsync(CancellationToken.None).ConfigureAwait(true);
    }
}
