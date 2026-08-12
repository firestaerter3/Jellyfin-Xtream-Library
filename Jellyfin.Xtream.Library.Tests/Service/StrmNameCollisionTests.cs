// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using System.IO;
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
/// Two provider streams that share a title and a recognised quality tag produce the same STRM
/// file name, because the name is built from the folder name and the version label alone. Before
/// the guard, the second stream silently overwrote the first: the File.Exists branch compares the
/// file contents against a stream URL that embeds the stream id, so it can never match across two
/// different streams and always fell through to the overwrite. Nothing was logged and no counter
/// moved, and with SyncParallelism above 1 the surviving stream flipped between runs (GitHub #74).
/// </summary>
[Collection("PluginSingletonTests")]
public class StrmNameCollisionTests : IDisposable
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

    public StrmNameCollisionTests()
    {
        _libraryPath = Path.Combine(Path.GetTempPath(), "xtream-collision-" + Guid.NewGuid().ToString("N"));
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
    public async Task TwoStreamsSharingTitleAndQualityTag_WriteOneFileAndCountTheCollision()
    {
        // Both resolve to "Duplicate Movie (2024)" with version label "FHD", so both want
        // "Duplicate Movie (2024) - FHD.strm".
        var result = await RunMovieSyncAsync(
            new StreamInfo { StreamId = 100, Name = "Duplicate Movie (2024) - [FHD]", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "Duplicate Movie (2024) - [FHD]", ContainerExtension = "mp4" })
            .ConfigureAwait(true);

        var movieFolder = Path.Combine(_libraryPath, "Movies", "Duplicate Movie (2024)");
        var written = Directory.Exists(movieFolder)
            ? Directory.GetFiles(movieFolder, "*.strm")
            : Array.Empty<string>();

        written.Should().HaveCount(1, "the two streams collapse to one name");
        result.MovieNameCollisions.Should().Be(1, "the refusal has to be counted, not silent");
        result.MoviesCreated.Should().Be(1);
        result.Errors.Should().Be(0, "a collision is a provider quirk, not a sync failure");

        // The counter reaches the caller only if it is aggregated into the global result as well
        // as the per-provider one. That wiring is easy to forget and invisible to a unit test.
        _log.Should().Contain(l => l.StartsWith("[Warning] STRM name collision for movie", StringComparison.Ordinal));

        // The surviving file belongs to exactly one of the two streams, and was not rewritten
        // by the other. Before the fix the content was whichever stream happened to finish last.
        var content = await File.ReadAllTextAsync(written[0]).ConfigureAwait(true);
        content.Should().Match(c => c.Contains("/100.mp4", StringComparison.Ordinal) || c.Contains("/200.mp4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoStreamsWithDifferentQualityTags_BothWriteAndNothingCollides()
    {
        // The regression guard for #74's actual feature: distinct tags still group into one
        // folder as separate versions, which is what the version dropdown is built on.
        var result = await RunMovieSyncAsync(
            new StreamInfo { StreamId = 100, Name = "Variant Movie (2024) - [FHD]", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "Variant Movie (2024) - [4K]", ContainerExtension = "mp4" })
            .ConfigureAwait(true);

        var movieFolder = Path.Combine(_libraryPath, "Movies", "Variant Movie (2024)");
        Directory.GetFiles(movieFolder, "*.strm").Should().HaveCount(2);
        result.MovieNameCollisions.Should().Be(0);
        result.MoviesCreated.Should().Be(2);
    }

    [Fact]
    public async Task TwoStreamsSharingAVersionTag_CollideTheSameWayAsAQualityTag()
    {
        // V1/V2 became version labels in #75, so they can collide exactly like FHD does.
        var result = await RunMovieSyncAsync(
            new StreamInfo { StreamId = 100, Name = "Tagged Movie (2024) - [V1]", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "Tagged Movie (2024) - [V1]", ContainerExtension = "mp4" })
            .ConfigureAwait(true);

        result.MovieNameCollisions.Should().Be(1);
        Directory.GetFiles(Path.Combine(_libraryPath, "Movies", "Tagged Movie (2024)"), "*.strm")
            .Should().HaveCount(1);
    }

    private async Task<SyncResult> RunMovieSyncAsync(params StreamInfo[] streams)
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
        plugin.Configuration.Providers =
        [
            new ProviderConfig
            {
                Name = "test",
                BaseUrl = "http://provider.test",
                Username = "u",
                Password = "p",
                LibraryPath = _libraryPath,
                SyncMovies = true,
                SyncSeries = false,
                CleanupOrphans = false,
                EnableIncrementalSync = false,
                SmartSkipExisting = false,
                DownloadArtworkForUnmatched = false,
                SyncParallelism = 1,
            },
        ];
        plugin.Configuration.EnableLiveTv = false;
        plugin.Configuration.EnableMetadataLookup = false;

        _client.Setup(c => c.GetVodCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Movies" } });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo>(streams));

        var service = new StrmSyncService(
            _client.Object,
            new Mock<IDispatcharrClient>().Object,
            new Mock<ILibraryManager>().Object,
            new Mock<IMetadataLookupService>().Object,
            new SnapshotService(appPaths.Object, NullLogger<SnapshotService>.Instance),
            new DeltaCalculator(NullLogger<DeltaCalculator>.Instance),
            new LiveTvService(_client.Object, appPaths.Object, MockAppHost(), NullLogger<LiveTvService>.Instance),
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
