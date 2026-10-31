// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
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
/// Every STRM URL carries the provider login. A changed password or username used to reach no
/// file at all: the incremental filter compares item checksums, the config fingerprint has no
/// login in it, and series smart skip goes by the snapshot entry or the number of files on disk.
/// The library kept playing the old login until the next full sync, and series even after it.
/// </summary>
[Collection("PluginSingletonTests")]
public class LoginChangeTests : IDisposable
{
    private readonly string _libraryPath;
    private readonly Mock<IXtreamClient> _client = new();

    public LoginChangeTests()
    {
        _libraryPath = Path.Combine(Path.GetTempPath(), "xtream-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_libraryPath);

        _client.Setup(c => c.GetVodCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Films" } });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new List<StreamInfo> { new() { StreamId = 101, Name = "Alpha (2020)", ContainerExtension = "mp4", CategoryId = 1 } });
        _client.Setup(c => c.GetSeriesCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 2, CategoryName = "Shows" } });
        _client.Setup(c => c.GetSeriesByCategoryAsync(It.IsAny<ConnectionInfo>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new List<Series> { new() { SeriesId = 7, Name = "Show A (2020)", CategoryId = 2 } });
        _client.Setup(c => c.GetSeriesStreamsBySeriesAsync(It.IsAny<ConnectionInfo>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SeriesStreamInfo
            {
                Seasons = new List<Season> { new() { SeasonNumber = 1 } },
                Episodes = new Dictionary<int, ICollection<Episode>>
                {
                    [1] = new List<Episode> { new() { EpisodeId = 71, EpisodeNum = 1, Season = 1, Title = "Pilot", ContainerExtension = "mkv" } },
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ANewPassword_ReachesEveryMovieAndEpisodeFile_OnTheNextSync(bool smartSkip)
    {
        await RunSyncAsync("old-pass", smartSkip).ConfigureAwait(true);
        StrmUrls().Should().OnlyContain(u => u.Contains("/u/old-pass/", StringComparison.Ordinal)).And.HaveCount(2);

        await RunSyncAsync("new-pass", smartSkip).ConfigureAwait(true);

        StrmUrls().Should().OnlyContain(u => u.Contains("/u/new-pass/", StringComparison.Ordinal), "the files must not keep the old login").And.HaveCount(2);
    }

    /// <summary>
    /// Removing the only provider and filling in another one (GitHub #184) keeps the library path.
    /// The new address has no snapshot yet, so nothing compares logins, and series smart skip used
    /// to keep any same-named series whose episode count had not grown, still on the old provider.
    /// </summary>
    [Fact]
    public async Task AnotherProviderAddress_ReachesEveryEpisodeFile_WithSmartSkipOn()
    {
        await RunSyncAsync("old-pass", smartSkip: true, baseUrl: "http://old-provider.test").ConfigureAwait(true);
        StrmUrls().Should().OnlyContain(u => u.StartsWith("http://old-provider.test/", StringComparison.Ordinal)).And.HaveCount(2);

        await RunSyncAsync("new-pass", smartSkip: true, baseUrl: "http://new-provider.test").ConfigureAwait(true);

        StrmUrls().Should().OnlyContain(u => u.StartsWith("http://new-provider.test/", StringComparison.Ordinal) && u.Contains("/u/new-pass/", StringComparison.Ordinal), "the files must not keep the old provider").And.HaveCount(2);
    }

    [Fact]
    public async Task TheSameLogin_LeavesTheNextSyncIncremental()
    {
        await RunSyncAsync("same", smartSkip: true).ConfigureAwait(true);
        var second = await RunSyncAsync("same", smartSkip: true).ConfigureAwait(true);

        second.MoviesSkipped.Should().Be(1, "nothing changed, so the movie is skipped as unchanged");
    }

    [Fact]
    public void ASnapshotWithoutARecordedLogin_CountsAsUnchanged()
    {
        var provider = new ProviderConfig { BaseUrl = "http://provider.test", Username = "u", Password = "p" };

        StrmSyncService.LoginChanged(provider, null).Should().BeFalse();
        StrmSyncService.LoginChanged(provider, new ContentSnapshot()).Should().BeFalse("snapshots from before the login was recorded must not force a re-read");
        StrmSyncService.LoginChanged(provider, new ContentSnapshot { LoginFingerprint = SnapshotService.CalculateLoginFingerprint(provider) }).Should().BeFalse();
        StrmSyncService.LoginChanged(provider, new ContentSnapshot { LoginFingerprint = SnapshotService.CalculateLoginFingerprint(new ProviderConfig { Username = "u", Password = "other" }) }).Should().BeTrue();
    }

    private List<string> StrmUrls()
        => Directory.GetFiles(_libraryPath, "*.strm", SearchOption.AllDirectories)
            .Select(f => File.ReadAllText(f).Trim())
            .ToList();

    private async Task<SyncResult> RunSyncAsync(string password, bool smartSkip, string baseUrl = "http://provider.test")
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
                BaseUrl = baseUrl,
                Username = "u",
                Password = password,
                LibraryPath = _libraryPath,
                SyncMovies = true,
                SyncSeries = true,
                CleanupOrphans = true,
                OrphanSafetyThreshold = 1.0,

                // Incremental sync on and nothing else changed between runs, so only the login
                // can make the second run rewrite anything.
                EnableIncrementalSync = true,

                FullSyncIntervalDays = 7,

                SmartSkipExisting = smartSkip,
                DownloadArtworkForUnmatched = false,
                SyncParallelism = 1,
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
