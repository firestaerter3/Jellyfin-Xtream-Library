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
/// In Multiple folder mode the folder mappings decide which categories are synced, not just where
/// the files land. Before this, the sync consulted Selected*CategoryIds only, and read an empty
/// array as "no filter configured, sync everything" - so a provider whose folders held no
/// categories ingested the entire catalogue into the library root (GitHub #78).
/// <para>
/// Single folder mode keeps the original meaning, where an empty selection genuinely does mean
/// "sync everything" in both Include and Exclude mode (GitHub #76). Several tests here exist only
/// to hold that line.
/// </para>
/// </summary>
[Collection("PluginSingletonTests")]
public class FolderModeCategoryFilterTests : IDisposable
{
    private const int KidsCategoryId = 10;
    private const int ActionCategoryId = 20;

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

    public FolderModeCategoryFilterTests()
    {
        _libraryPath = Path.Combine(Path.GetTempPath(), "xtream-foldermode-" + Guid.NewGuid().ToString("N"));
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
    public async Task MultipleFolderMode_NoCategoryAssignedToAnyFolder_SyncsNothing()
    {
        // Exactly what the config page persists when no folder holds a category: the mappings
        // string is empty and so is the selection array.
        await RunMovieSyncAsync(p =>
        {
            p.MovieFolderMode = "Multiple";
            p.MovieFolderMappings = string.Empty;
            p.SelectedVodCategoryIds = [];
        }).ConfigureAwait(true);

        WrittenMovieStrms().Should().BeEmpty("an empty folder assignment means sync nothing, not sync everything");

        _log.Should().Contain(
            l => l.StartsWith("[Warning] No movies will be synced", StringComparison.Ordinal),
            "a sync that deliberately does nothing has to say so");
    }

    [Fact]
    public async Task MultipleFolderMode_MappedCategories_SyncOnlyThoseIntoTheirFolders()
    {
        await RunMovieSyncAsync(p =>
        {
            p.MovieFolderMode = "Multiple";
            p.MovieFolderMappings = "Kids=" + KidsCategoryId;
            p.SelectedVodCategoryIds = [KidsCategoryId];
        }).ConfigureAwait(true);

        var written = WrittenMovieStrms();
        written.Should().ContainSingle();
        written[0].Should().Contain(Path.Combine("Movies", "Kids"), "the mapped folder is the target, not the library root");
        written[0].Should().Contain("Kid Movie");

        _client.Verify(
            c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), ActionCategoryId, It.IsAny<CancellationToken>()),
            Times.Never,
            "an unmapped category should not even be fetched");
    }

    [Fact]
    public async Task MultipleFolderMode_IgnoresAStaleSelectedCategoryIdsArray()
    {
        // The two fields always agree for a config the config page wrote since v1.16.0.0. Before
        // that the mappings lived in a free-text box independent of the category checkboxes, so a
        // config that has not been saved since can still disagree. The mappings are the source of
        // truth, and the categories that lose out have to be named rather than silently dropped.
        await RunMovieSyncAsync(p =>
        {
            p.MovieFolderMode = "Multiple";
            p.MovieFolderMappings = "Kids=" + KidsCategoryId;
            p.SelectedVodCategoryIds = [KidsCategoryId, ActionCategoryId];
        }).ConfigureAwait(true);

        var written = WrittenMovieStrms();
        written.Should().ContainSingle("only the mapped category is in scope");
        written[0].Should().Contain("Kid Movie");

        _log.Should().Contain(
            l => l.StartsWith("[Warning] Multiple folder mode: 1 previously selected VOD category IDs", StringComparison.Ordinal)
                 && l.Contains(ActionCategoryId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "dropping a category the config still names is exactly the kind of silent narrowing this issue is about");
    }

    [Fact]
    public async Task MultipleFolderMode_SelectionMatchingTheMappings_LogsNoUnassignedWarning()
    {
        // The common case, and the one that must stay quiet: every save since v1.16.0.0 writes the
        // selection as the union of the folder-assigned categories, so the two agree.
        await RunMovieSyncAsync(p =>
        {
            p.MovieFolderMode = "Multiple";
            p.MovieFolderMappings = "Kids=" + KidsCategoryId;
            p.SelectedVodCategoryIds = [KidsCategoryId];
        }).ConfigureAwait(true);

        _log.Should().NotContain(l => l.Contains("previously selected VOD category IDs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MultipleFolderMode_ExcludeModeInStoredConfig_IsStillTreatedAsInclude()
    {
        // Unreachable through the config page, which hides the mode and force-writes Include in
        // Multiple folder mode, but representable in a hand-edited config file. Honouring Exclude
        // here would sync every category that has no folder, straight into the library root.
        await RunMovieSyncAsync(p =>
        {
            p.MovieFolderMode = "Multiple";
            p.MovieFolderMappings = "Kids=" + KidsCategoryId;
            p.MovieCategoriesMode = "Exclude";
            p.SelectedVodCategoryIds = [KidsCategoryId];
        }).ConfigureAwait(true);

        var written = WrittenMovieStrms();
        written.Should().ContainSingle();
        written[0].Should().Contain("Kid Movie", "a folder mapping is an inclusion list by construction");
    }

    [Fact]
    public async Task SingleFolderMode_EmptySelection_StillSyncsEverything()
    {
        await RunMovieSyncAsync(p =>
        {
            p.MovieFolderMode = "Single";
            p.SelectedVodCategoryIds = [];
        }).ConfigureAwait(true);

        WrittenMovieStrms().Should().HaveCount(2, "in Single folder mode an empty selection still means sync everything");
    }

    [Fact]
    public async Task SingleFolderMode_ExcludeWithEmptySelection_StillSyncsEverything()
    {
        // Excluding nothing excludes nothing - the behaviour agreed in GitHub #76.
        await RunMovieSyncAsync(p =>
        {
            p.MovieFolderMode = "Single";
            p.MovieCategoriesMode = "Exclude";
            p.SelectedVodCategoryIds = [];
        }).ConfigureAwait(true);

        WrittenMovieStrms().Should().HaveCount(2);
    }

    [Fact]
    public async Task SingleFolderMode_ExcludeWithSelection_SkipsOnlyTheSelected()
    {
        await RunMovieSyncAsync(p =>
        {
            p.MovieFolderMode = "Single";
            p.MovieCategoriesMode = "Exclude";
            p.SelectedVodCategoryIds = [ActionCategoryId];
        }).ConfigureAwait(true);

        var written = WrittenMovieStrms();
        written.Should().ContainSingle();
        written[0].Should().Contain("Kid Movie");
    }

    [Fact]
    public async Task SeriesMultipleFolderMode_NoCategoryAssigned_SyncsNothing()
    {
        await RunSeriesSyncAsync(p =>
        {
            p.SeriesFolderMode = "Multiple";
            p.SeriesFolderMappings = string.Empty;
            p.SelectedSeriesCategoryIds = [];
        }).ConfigureAwait(true);

        Directory.GetFiles(Path.Combine(_libraryPath, "Series"), "*.strm", SearchOption.AllDirectories)
            .Should().BeEmpty();

        _log.Should().Contain(l => l.StartsWith("[Warning] No series will be synced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectionThatSyncsNothing_DoesNotDeleteTheExistingLibrary()
    {
        // The upgrade hazard: a user who was silently syncing the whole catalogue now resolves to
        // "sync nothing", which makes every file already on disk an orphan. The safety threshold is
        // pinned at 100% here so it cannot be what saves them - only the empty-selection guard can.
        var seeded = SeedMovieStrms(12);

        var result = await RunMovieSyncAsync(p =>
        {
            p.MovieFolderMode = "Multiple";
            p.MovieFolderMappings = string.Empty;
            p.SelectedVodCategoryIds = [];
            p.CleanupOrphans = true;
            p.OrphanSafetyThreshold = 1.0;
        }).ConfigureAwait(true);

        seeded.Should().OnlyContain(f => File.Exists(f), "a misconfiguration is not a deletion instruction");
        result.FilesDeleted.Should().Be(0);

        _log.Should().Contain(
            l => l.StartsWith("[Warning] Skipping movie orphan cleanup: the movie category selection resolves to nothing", StringComparison.Ordinal),
            "the reason has to be distinguishable from a threshold-triggered skip");

        // The blocked files still have to be counted and reported. Leaving them out is the exact
        // silence GitHub #77 exists to remove, and a naive merge of the two fixes produces it:
        // the counters end up inside the threshold branch and an empty-selection skip reports zero.
        result.MovieOrphansSkipped.Should().Be(12);
        result.MovieOrphansExamined.Should().Be(12);
        result.OrphanCleanupSkipped.Should().BeTrue();

        // Which reason blocked it decides the advice the user is given, so the two must not be
        // conflated. Raising the safety threshold is the fix for a threshold block and would let
        // the next sync delete the library after this one.
        result.OrphanCleanupBlockedByEmptySelection.Should().BeTrue();
        result.OrphanCleanupBlockedByThreshold.Should().BeFalse();
        result.OrphanSafetyThresholdApplied.Should().Be(0, "no threshold was involved in this decision");

        _log.Should().Contain(
            l => l.Contains("Do not raise Orphan Safety Threshold", StringComparison.Ordinal),
            "the end-of-run summary must not hand out the threshold remedy for this reason");
        _log.Should().NotContain(
            l => l.Contains("Raise Orphan Safety Threshold in the plugin settings", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThresholdSkip_StillReportsTheThresholdAndItsRemedy()
    {
        // The counterpart of the test above: an ordinary threshold block must keep #77's wording
        // and keep recording the limit that caused it, now that a second skip reason shares the
        // same counters.
        var seeded = SeedMovieStrms(12);

        var result = await RunMovieSyncAsync(p =>
        {
            // A healthy selection, so the empty-selection guard cannot be what fires. The provider
            // simply stops offering the seeded movies, which is the provider-glitch case.
            p.MovieFolderMode = "Single";
            p.SelectedVodCategoryIds = [];
            p.CleanupOrphans = true;
            p.OrphanSafetyThreshold = 0.20;
        }).ConfigureAwait(true);

        seeded.Should().OnlyContain(f => File.Exists(f));
        result.OrphanCleanupBlockedByThreshold.Should().BeTrue();
        result.OrphanCleanupBlockedByEmptySelection.Should().BeFalse();
        result.OrphanSafetyThresholdApplied.Should().Be(0.20);

        _log.Should().Contain(
            l => l.Contains("Raise Orphan Safety Threshold in the plugin settings", StringComparison.Ordinal));
    }

    private List<string> WrittenMovieStrms()
    {
        var moviesPath = Path.Combine(_libraryPath, "Movies");
        return Directory.Exists(moviesPath)
            ? Directory.GetFiles(moviesPath, "*.strm", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList()
            : [];
    }

    private List<string> SeedMovieStrms(int count)
    {
        var seeded = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var folder = Path.Combine(_libraryPath, "Movies", $"Dead Movie {i} (2020)");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"Dead Movie {i} (2020).strm");
            File.WriteAllText(path, $"http://provider0.test/movie/u/p/{9000 + i}.mp4");
            seeded.Add(path);
        }

        return seeded;
    }

    private Task<SyncResult> RunMovieSyncAsync(Action<ProviderConfig> configure)
    {
        _client.Setup(c => c.GetVodCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category>
            {
                new() { CategoryId = KidsCategoryId, CategoryName = "Kids" },
                new() { CategoryId = ActionCategoryId, CategoryName = "Action" },
            });

        // One Setup per literal category id, so a category that is filtered out is observable
        // through Moq's Verify rather than silently returning the same list.
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), KidsCategoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo>
            {
                new() { StreamId = 110, Name = "Kid Movie (2024)", ContainerExtension = "mp4", CategoryId = KidsCategoryId },
            });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), ActionCategoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo>
            {
                new() { StreamId = 210, Name = "Action Movie (2024)", ContainerExtension = "mp4", CategoryId = ActionCategoryId },
            });

        return RunSyncAsync(syncMovies: true, syncSeries: false, configure);
    }

    private Task<SyncResult> RunSeriesSyncAsync(Action<ProviderConfig> configure)
    {
        _client.Setup(c => c.GetSeriesCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category>
            {
                new() { CategoryId = KidsCategoryId, CategoryName = "Kids" },
                new() { CategoryId = ActionCategoryId, CategoryName = "Action" },
            });
        _client.Setup(c => c.GetSeriesByCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Series> { new() { SeriesId = 7, Name = "Some Show (2024)" } });
        _client.Setup(c => c.GetSeriesStreamsBySeriesAsync(It.IsAny<ConnectionInfo>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesStreamInfo
            {
                Seasons = new List<Season> { new() { SeasonNumber = 1 } },
                Episodes = new Dictionary<int, ICollection<Episode>>
                {
                    [1] = new List<Episode>
                    {
                        new() { EpisodeId = 11, EpisodeNum = 1, Season = 1, Title = "Pilot", ContainerExtension = "mkv" },
                    },
                },
            });

        return RunSyncAsync(syncMovies: false, syncSeries: true, configure);
    }

    private async Task<SyncResult> RunSyncAsync(bool syncMovies, bool syncSeries, Action<ProviderConfig> configure)
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

        var provider = new ProviderConfig
        {
            Name = "test0",
            BaseUrl = "http://provider0.test",
            Username = "u",
            Password = "p",
            LibraryPath = _libraryPath,
            SyncMovies = syncMovies,
            SyncSeries = syncSeries,
            CleanupOrphans = false,

            // Both of these bulk-add existing STRM files to the synced set as orphan protection,
            // which would stop the seeded files from ever being seen as orphans.
            EnableIncrementalSync = false,
            SmartSkipExisting = false,
            DownloadArtworkForUnmatched = false,
            SyncParallelism = 1,
        };
        configure(provider);

        plugin.Configuration.Providers = [provider];
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
