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
using Jellyfin.Xtream.Library.Tests.Helpers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// Orphan cleanup that trips OrphanSafetyThreshold is skipped on purpose, but before this the
/// counts existed only as locals at the moment of the decision. The run then reported Success
/// with 0 deleted and 0 errors, which is byte-identical to a genuinely clean run, and the only
/// trace was a single log line. A provider base-URL change left 165,998 dead STRM files in place
/// for months that way (GitHub #77). These tests pin the counters that make the refusal visible.
/// </summary>
[Collection("PluginSingletonTests")]
public class OrphanCleanupSkipTests : IDisposable
{
    private readonly string _libraryPath;
    private readonly Mock<IXtreamClient> _client = new();
    private readonly List<string> _log = [];

    private sealed class ListLogger(List<string> sink) : Microsoft.Extensions.Logging.ILogger<StrmSyncService>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => sink.Add($"[{logLevel}] {formatter(state, exception)}");
    }

    public OrphanCleanupSkipTests()
    {
        _libraryPath = Path.Combine(Path.GetTempPath(), "xtream-orphanskip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_libraryPath);
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
    public async Task BlockedMovieCleanup_CountsTheOrphansItRefusedToDelete()
    {
        // 12 dead files, one live movie from the provider. The skip is gated on more than 10
        // existing files as well as the ratio, so a smaller fixture would leave every counter at
        // zero and pass an assertion written the wrong way round.
        var dead = SeedDeadMovies(12);

        var result = await RunMovieSyncAsync(orphanSafetyThreshold: 0.20).ConfigureAwait(true);

        result.Success.Should().BeTrue("the sync itself did not fail, which is exactly why the counters are needed");
        result.Errors.Should().Be(0);
        result.MoviesDeleted.Should().Be(0, "cleanup was blocked");
        result.FilesDeleted.Should().Be(0);

        result.MovieOrphansSkipped.Should().Be(12);
        result.MovieOrphansExamined.Should().Be(12, "the denominator has to survive the decision too");
        result.OrphansSkipped.Should().Be(12);
        result.OrphanCleanupSkipped.Should().BeTrue();
        result.OrphanSafetyThresholdApplied.Should().Be(0.20);

        // The counters only reach the caller if the per-provider result is merged into the global
        // one. That second hop is easy to forget and invisible to a unit test of the decision.
        dead.Should().OnlyContain(f => File.Exists(f), "the files are still on disk, which is the whole problem");
        _log.Should().Contain(l => l.StartsWith("[Warning] Sync finished with orphan cleanup blocked", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanupUnderTheThreshold_DeletesAndCountsNothingAsSkipped()
    {
        var dead = SeedDeadMovies(12);

        // A threshold of 100% cannot be exceeded, so the same fixture takes the delete path.
        var result = await RunMovieSyncAsync(orphanSafetyThreshold: 1.0).ConfigureAwait(true);

        result.MoviesDeleted.Should().Be(12);
        result.MovieOrphansSkipped.Should().Be(0);
        result.MovieOrphansExamined.Should().Be(0);
        result.OrphanCleanupSkipped.Should().BeFalse();
        result.OrphanSafetyThresholdApplied.Should().Be(0);
        dead.Should().OnlyContain(f => !File.Exists(f));
    }

    [Fact]
    public async Task SmallLibraryUnderTheTenFileGate_IsNotTreatedAsBlocked()
    {
        // 5 orphans out of 5 is a 100% ratio, but the guard ignores libraries of 10 or fewer
        // files. Nothing was refused, so nothing may be reported as refused.
        var dead = SeedDeadMovies(5);

        var result = await RunMovieSyncAsync(orphanSafetyThreshold: 0.20).ConfigureAwait(true);

        result.MoviesDeleted.Should().Be(5);
        result.OrphanCleanupSkipped.Should().BeFalse("a run that deleted everything it found has no warning to show");
        dead.Should().OnlyContain(f => !File.Exists(f));
    }

    [Fact]
    public async Task BlockedEpisodeCleanup_IsCountedSeparatelyFromMovies()
    {
        // Movies and episodes are gated independently, so the episode counter needs its own run:
        // a library can have its episodes blocked while its movies clean normally.
        var dead = SeedDeadEpisodes(12);

        var result = await RunSeriesSyncAsync(orphanSafetyThreshold: 0.20).ConfigureAwait(true);

        result.EpisodesDeleted.Should().Be(0);
        result.EpisodeOrphansSkipped.Should().Be(12);
        result.EpisodeOrphansExamined.Should().Be(12);
        result.MovieOrphansSkipped.Should().Be(0, "no movie path was involved");
        result.OrphansSkipped.Should().Be(12);
        result.OrphanCleanupSkipped.Should().BeTrue();
        dead.Should().OnlyContain(f => File.Exists(f));
    }

    [Fact]
    public async Task TwoProvidersBlocking_SumTheCountsAndReportTheStricterThreshold()
    {
        SeedDeadMovies(12);

        // Different limits, both tripped. The number a user acts on is the one that blocked the
        // most, so the merged result carries the stricter of the two.
        var result = await RunMovieSyncAsync(orphanSafetyThreshold: 0.20, secondProviderThreshold: 0.50)
            .ConfigureAwait(true);

        result.OrphanSafetyThresholdApplied.Should().Be(0.20, "the most conservative limit in play is the one shown");
        result.MovieOrphansSkipped.Should().BeGreaterThan(12, "both providers' counts are summed, not overwritten");
        result.MoviesDeleted.Should().Be(0);
    }

    [Fact]
    public async Task AProviderSetToZeroPercent_StillReportsTheLimitThatBlockedIt()
    {
        // A 0% threshold blocks every cleanup, and it is reachable: the config UI writes
        // parseInt(value)/100 and Validate() clamps to [0, 1], so typing 0 lands here. Merging on
        // "threshold is non-zero" would drop it and leave the panel quoting the other provider's
        // 50% as the limit that blocked the run.
        SeedDeadMovies(12);

        var result = await RunMovieSyncAsync(orphanSafetyThreshold: 0.0, secondProviderThreshold: 0.50)
            .ConfigureAwait(true);

        result.OrphanCleanupSkipped.Should().BeTrue();
        result.OrphanSafetyThresholdApplied.Should().Be(0.0, "0% is a real limit, not an unset value");
    }

    private List<string> SeedDeadMovies(int count)
    {
        var created = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var folder = Path.Combine(_libraryPath, "Movies", $"Dead Movie {i:D2} (2020)");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"Dead Movie {i:D2} (2020).strm");
            File.WriteAllText(path, $"http://decommissioned.test/movie/{i}.mp4");
            created.Add(path);
        }

        return created;
    }

    private List<string> SeedDeadEpisodes(int count)
    {
        var created = new List<string>();
        var folder = Path.Combine(_libraryPath, "Series", "Dead Show (2020)", "Season 01");
        Directory.CreateDirectory(folder);
        for (int i = 1; i <= count; i++)
        {
            var path = Path.Combine(folder, $"Dead Show (2020) - S01E{i:D2}.strm");
            File.WriteAllText(path, $"http://decommissioned.test/series/{i}.mkv");
            created.Add(path);
        }

        return created;
    }

    private Task<SyncResult> RunMovieSyncAsync(double orphanSafetyThreshold, double? secondProviderThreshold = null)
    {
        _client.Setup(c => c.GetVodCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Movies" } });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo>
            {
                new() { StreamId = 100, Name = "Live Movie (2024)", ContainerExtension = "mp4" },
            });

        return RunSyncAsync(syncMovies: true, syncSeries: false, orphanSafetyThreshold, secondProviderThreshold);
    }

    private Task<SyncResult> RunSeriesSyncAsync(double orphanSafetyThreshold)
    {
        _client.Setup(c => c.GetSeriesCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Shows" } });
        _client.Setup(c => c.GetSeriesByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Series> { new() { SeriesId = 7, Name = "Live Show (2024)" } });
        _client.Setup(c => c.GetSeriesStreamsBySeriesAsync(It.IsAny<ConnectionInfo>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesStreamInfo
            {
                Seasons = new List<Season> { new() { SeasonNumber = 1 } },
                Episodes = new Dictionary<int, ICollection<Episode>>
                {
                    [1] = new List<Episode>
                    {
                        new() { EpisodeId = 11, EpisodeNum = 1, Season = 1, Title = "Alive", ContainerExtension = "mkv" },
                    },
                },
            });

        return RunSyncAsync(syncMovies: false, syncSeries: true, orphanSafetyThreshold, secondProviderThreshold: null);
    }

    private async Task<SyncResult> RunSyncAsync(
        bool syncMovies,
        bool syncSeries,
        double orphanSafetyThreshold,
        double? secondProviderThreshold)
    {
        var appPaths = new Mock<IServerApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(_libraryPath);
        appPaths.Setup(p => p.DataPath).Returns(_libraryPath);
        appPaths.Setup(p => p.ProgramDataPath).Returns(_libraryPath);
        appPaths.Setup(p => p.CachePath).Returns(_libraryPath);
        appPaths.Setup(p => p.TempDirectory).Returns(_libraryPath);
        appPaths.Setup(p => p.PluginsPath).Returns(_libraryPath);

        // Constructing the plugin publishes Plugin.Instance, which SyncAsync reads.
        var plugin = new Plugin(appPaths.Object, new RealXmlSerializer());

        var thresholds = secondProviderThreshold.HasValue
            ? new[] { orphanSafetyThreshold, secondProviderThreshold.Value }
            : new[] { orphanSafetyThreshold };

        plugin.Configuration.Providers = thresholds.Select((threshold, i) => new ProviderConfig
        {
            Name = $"test{i}",
            BaseUrl = $"http://provider{i}.test",
            Username = "u",
            Password = "p",
            LibraryPath = _libraryPath,
            SyncMovies = syncMovies,
            SyncSeries = syncSeries,
            CleanupOrphans = true,
            OrphanSafetyThreshold = threshold,

            // Both of these bulk-add existing STRM files to the synced set as orphan protection,
            // which would stop the seeded files from ever being seen as orphans.
            EnableIncrementalSync = false,
            SmartSkipExisting = false,
            DownloadArtworkForUnmatched = false,
            SyncParallelism = 1,
        }).ToList();
        plugin.Configuration.EnableLiveTv = false;
        plugin.Configuration.EnableMetadataLookup = false;

        var service = new StrmSyncService(
            _client.Object,
            new Mock<IDispatcharrClient>().Object,
            new Mock<ILibraryManager>().Object,
            new Mock<IMetadataLookupService>().Object,
            new SnapshotService(appPaths.Object, NullLogger<SnapshotService>.Instance),
            new LiveTvService(_client.Object, new Mock<IDispatcharrClient>().Object, appPaths.Object, MockAppHost(), NullLogger<LiveTvService>.Instance),
            appPaths.Object,
            new ListLogger(_log));

        return await service.SyncAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private static IServerApplicationHost MockAppHost()
    {
        var host = new Mock<IServerApplicationHost>();
        host.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");
        return host.Object;
    }
}
