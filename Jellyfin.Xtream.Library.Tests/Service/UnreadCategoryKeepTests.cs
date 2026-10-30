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
using System.Net.Http;
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
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// A category whose listing fails, or comes back empty although it had items before, used to make
/// orphan cleanup delete every file of that category; the next good sync wrote them again, without
/// their watched state and artwork (GitHub #171). Its items are now kept until the category
/// answers again or has been unread longer than the full sync interval.
/// <para>
/// Every run forces a full sync by changing the exclusion list, so these tests go through the
/// snapshot that exists on full syncs too, not the incremental one. The safety threshold is 100%:
/// at the default 20% it would block the deletion itself, and the tests would pass without the
/// change. The assertions read files and snapshot JSON only, so they compile against the code
/// before the change and can be shown to fail there.
/// </para>
/// </summary>
[Collection("PluginSingletonTests")]
public class UnreadCategoryKeepTests : IDisposable
{
    private readonly string _libraryPath;
    private readonly Mock<IXtreamClient> _client = new();
    private int _run;
    private bool _listFlakyVodCategory = true;

    public UnreadCategoryKeepTests()
    {
        _libraryPath = Path.Combine(Path.GetTempPath(), "xtream-unread-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_libraryPath);

        _client.Setup(c => c.GetVodCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _listFlakyVodCategory
                ? new List<Category>
                {
                    new() { CategoryId = 1, CategoryName = "Flaky" },
                    new() { CategoryId = 2, CategoryName = "Steady" },
                }
                : new List<Category> { new() { CategoryId = 2, CategoryName = "Steady" } });
        _client.Setup(c => c.GetSeriesCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new List<Category>
            {
                new() { CategoryId = 1, CategoryName = "Flaky" },
                new() { CategoryId = 2, CategoryName = "Steady" },
            });

        foreach (var seriesId in new[] { 7, 8, 9 })
        {
            _client.Setup(c => c.GetSeriesStreamsBySeriesAsync(It.IsAny<ConnectionInfo>(), seriesId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new SeriesStreamInfo
                {
                    Seasons = new List<Season> { new() { SeasonNumber = 1 } },
                    Episodes = new Dictionary<int, ICollection<Episode>>
                    {
                        [1] = new List<Episode>
                        {
                            new() { EpisodeId = (seriesId * 10) + 1, EpisodeNum = 1, Season = 1, Title = "Pilot", ContainerExtension = "mkv" },
                        },
                    },
                });
        }
    }

    public enum Answer
    {
        Items,
        Empty,
        Throws,
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
    [InlineData(Answer.Empty)]
    [InlineData(Answer.Throws)]
    public async Task UnreadMovieCategory_KeepsItsFilesAndSnapshotEntries_WhileOtherOrphansGo(Answer answer)
    {
        await RunMoviesAsync(Answer.Items, steadyKeepsDelta: true).ConfigureAwait(true);
        MovieFile("Alpha").Should().NotBeNull("the first run writes everything");

        var result = await RunMoviesAsync(answer, steadyKeepsDelta: false).ConfigureAwait(true);

        MovieFile("Alpha").Should().NotBeNull("its category was not read, so it is not gone");
        MovieFile("Beta").Should().NotBeNull();
        MovieFile("Gamma").Should().NotBeNull();
        MovieFile("Delta").Should().BeNull("a category that answered dropped it, so cleanup still runs");

        var snapshot = LatestSnapshot();
        ((JObject)snapshot["Movies"]!).Properties().Select(p => p.Name).Should().Contain(["101", "102"], "the next run has to see that the category had items");
        (snapshot["UnreadMovieCategoriesSince"]?["1"]).Should().NotBeNull();
        (JObject.FromObject(result)["MoviesKeptForUnreadCategories"]?.Value<int>()).Should().Be(2);
    }

    [Fact]
    public async Task WithIncrementalSyncAndGroupingOff_TheFilesAreStillKept()
    {
        // The snapshot used to be loaded and saved only for those two options, so with both off
        // nothing knew the category had items (codex review on #178).
        await RunMoviesAsync(Answer.Items, steadyKeepsDelta: true, incremental: false).ConfigureAwait(true);
        await RunMoviesAsync(Answer.Empty, steadyKeepsDelta: false, incremental: false).ConfigureAwait(true);

        MovieFile("Alpha").Should().NotBeNull();
        MovieFile("Beta").Should().NotBeNull();
        MovieFile("Delta").Should().BeNull();
    }

    [Fact]
    public async Task ACategoryLeftOutOfTheProvidersCategoryList_IsKeptToo()
    {
        await RunMoviesAsync(Answer.Items, steadyKeepsDelta: true).ConfigureAwait(true);

        _listFlakyVodCategory = false;
        await RunMoviesAsync(Answer.Items, steadyKeepsDelta: false).ConfigureAwait(true);

        MovieFile("Alpha").Should().NotBeNull("the category is still selected, the provider just did not list it this time");
        MovieFile("Beta").Should().NotBeNull();
        MovieFile("Delta").Should().BeNull();
    }

    [Fact]
    public async Task ACategoryTheUserDeselected_IsCleanedUpAsBefore()
    {
        await RunMoviesAsync(Answer.Items, steadyKeepsDelta: true).ConfigureAwait(true);

        await RunMoviesAsync(Answer.Items, steadyKeepsDelta: true, selectedVod: [2]).ConfigureAwait(true);

        MovieFile("Alpha").Should().BeNull("a deselected category is out of scope, not unread");
        MovieFile("Gamma").Should().NotBeNull();
    }

    [Theory]
    [InlineData("http://h/movie/u/p/101.mkv", 101)]
    [InlineData("http://h/movie/u/p/101.mkv?x=1", 101)]
    [InlineData("http://h/series/u/p/7001.mp4", 7001)]
    [InlineData("http://h/proxy/vod/movie/086aab97-3c36-41f0-a417-b97f39a69f0f?stream_id=5", null)]
    [InlineData("", null)]
    public void StreamIdInStrmUrl_ReadsTheIdTheUrlEndsIn(string url, int? expected)
        => StrmSyncService.StreamIdInStrmUrl(url).Should().Be(expected);

    [Fact]
    public void ProtectOwnStrmFilesIn_LeavesAnotherStreamsFileInASharedFolder()
    {
        // A TMDB group's folder holds files of several streams. Keeping an unread owner must not
        // keep a guest whose own category answered and dropped it (codex review on #178).
        var folder = Path.Combine(_libraryPath, "Movies", "Shared (2020) [tmdbid-1]");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Shared (2020) [tmdbid-1].strm"), "http://h/movie/u/p/101.mkv");
        File.WriteAllText(Path.Combine(folder, "Shared (2020) [tmdbid-1] - 102.strm"), "http://h/movie/u/p/102.mkv");
        File.WriteAllText(Path.Combine(folder, "Shared (2020) [tmdbid-1] - Version 2.strm"), "http://h/proxy/vod/movie/abc?stream_id=9");
        File.WriteAllText(Path.Combine(folder, "Shared (2020) [tmdbid-1] - 103.strm"), "http://h/proxy/vod/movie/def?stream_id=8");
        var kept = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

        StrmSyncService.ProtectOwnStrmFilesIn(folder, 101, kept);

        kept.Keys.Select(Path.GetFileName).Should().BeEquivalentTo(
            "Shared (2020) [tmdbid-1].strm",
            "Shared (2020) [tmdbid-1] - Version 2.strm");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultipleFolderMode_KeepsTheCopyInEveryMappedFolder(bool stillListedInSteady)
    {
        // Alpha is listed in both categories, so it has a copy in each folder; the snapshot keeps
        // only its own category (1). Category 1 then comes back empty. Whether or not category 2
        // still lists it, the copy in FolderA must stay (codex review on #178).
        const string Folders = "FolderA=1\nFolderB=2";
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo> { Movie(101, "Alpha (2020)", 1) });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo> { Movie(101, "Alpha (2020)", 1), Movie(201, "Gamma (2019)", 2) });
        await RunSyncAsync(syncMovies: true, syncSeries: false, forceFullSync: true, movieFolders: Folders).ConfigureAwait(true);
        MovieFiles("Alpha").Should().Equal("FolderA", "FolderB");

        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo>());
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(stillListedInSteady
                ? new List<StreamInfo> { Movie(101, "Alpha (2020)", 1), Movie(201, "Gamma (2019)", 2) }
                : new List<StreamInfo> { Movie(201, "Gamma (2019)", 2) });
        await RunSyncAsync(syncMovies: true, syncSeries: false, forceFullSync: true, movieFolders: Folders).ConfigureAwait(true);

        MovieFiles("Alpha").Should().Equal("FolderA", "FolderB");
    }

    [Fact]
    public async Task AMovieKeptForAnUnreadCategory_KeepsItsPrimary_WhenANewStreamSharesTheName()
    {
        // 101 holds the plain file. Its category then comes back empty, and in the same run a new
        // stream 202 with the same name appears and writes an alternative next to it. 101 is kept
        // for its unread category, so it is not gone, and 202 must not take its primary over
        // (#175 merged with #178).
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo> { Movie(101, "Shared (2020)", 1) });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo> { Movie(201, "Gamma (2019)", 2) });
        await RunSyncAsync(syncMovies: true, syncSeries: false, forceFullSync: true).ConfigureAwait(true);

        var folder = Path.GetDirectoryName(MovieFile("Shared (2020)"))!;
        var primary = Path.Combine(folder, "Shared (2020).strm");
        File.ReadAllText(primary).Should().EndWith("/101.mp4");

        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo>());
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo> { Movie(201, "Gamma (2019)", 2), Movie(202, "Shared (2020)", 2) });
        await RunSyncAsync(syncMovies: true, syncSeries: false, forceFullSync: true).ConfigureAwait(true);

        File.ReadAllText(primary).Should().EndWith("/101.mp4", "101 was kept for its unread category, so it still holds its primary");
        File.Exists(Path.Combine(folder, "Shared (2020) - 202.strm")).Should().BeTrue("the new stream sits next to it");
    }

    [Fact]
    public async Task IncrementalSync_KeepsTheFilesToo()
    {
        // Same exclusion list each run, so the second run is incremental: the default setup, where
        // the incremental filter runs as well.
        await RunMoviesAsync(Answer.Items, steadyKeepsDelta: true, forceFullSync: false).ConfigureAwait(true);
        await RunMoviesAsync(Answer.Empty, steadyKeepsDelta: false, forceFullSync: false).ConfigureAwait(true);

        MovieFile("Alpha").Should().NotBeNull();
        MovieFile("Beta").Should().NotBeNull();
        MovieFile("Gamma").Should().NotBeNull();
        MovieFile("Delta").Should().BeNull();
    }

    [Fact]
    public async Task TwoUnreadRunsInARow_StillKeepTheFiles_AndKeepTheFirstTime()
    {
        await RunMoviesAsync(Answer.Items, steadyKeepsDelta: true).ConfigureAwait(true);
        await RunMoviesAsync(Answer.Empty, steadyKeepsDelta: true).ConfigureAwait(true);
        var firstSince = LatestSnapshot()["UnreadMovieCategoriesSince"]?["1"]?.Value<DateTime>();

        await RunMoviesAsync(Answer.Empty, steadyKeepsDelta: true).ConfigureAwait(true);

        MovieFile("Alpha").Should().NotBeNull();
        MovieFile("Beta").Should().NotBeNull();
        firstSince.Should().NotBeNull();
        (LatestSnapshot()["UnreadMovieCategoriesSince"]?["1"]?.Value<DateTime>())
            .Should().Be(firstSince, "restarting the clock on every unread run would mean it never runs out");
    }

    [Fact]
    public async Task CategoryAnswersAgain_ClearsTheEntry_AndCleansNormally()
    {
        await RunMoviesAsync(Answer.Items, steadyKeepsDelta: true).ConfigureAwait(true);
        await RunMoviesAsync(Answer.Empty, steadyKeepsDelta: true).ConfigureAwait(true);

        // The category is back, without Beta: Beta is a real removal now.
        await RunMoviesAsync(Answer.Items, steadyKeepsDelta: true, flakyKeepsBeta: false).ConfigureAwait(true);

        MovieFile("Alpha").Should().NotBeNull();
        MovieFile("Beta").Should().BeNull();
        ((JObject?)LatestSnapshot()["UnreadMovieCategoriesSince"]).Should().NotBeNull().And.BeEmpty("the category answered, so its clock is cleared");
    }

    [Theory]
    [InlineData(Answer.Empty)]
    [InlineData(Answer.Throws)]
    public async Task UnreadLongerThanTheFullSyncInterval_IsCleanedUp(Answer answer)
    {
        await RunMoviesAsync(Answer.Items, steadyKeepsDelta: true).ConfigureAwait(true);
        await RunMoviesAsync(answer, steadyKeepsDelta: true).ConfigureAwait(true);
        MovieFile("Alpha").Should().NotBeNull();

        // Eight days ago, one more than the interval.
        BackdateUnread("UnreadMovieCategoriesSince", DateTime.UtcNow.AddDays(-8));

        await RunMoviesAsync(answer, steadyKeepsDelta: true).ConfigureAwait(true);

        MovieFile("Alpha").Should().BeNull("a category empty this long was really emptied");
        MovieFile("Beta").Should().BeNull();
        MovieFile("Gamma").Should().NotBeNull();
        var snapshot = LatestSnapshot();
        ((JObject)snapshot["Movies"]!).Properties().Select(p => p.Name).Should().NotContain(["101", "102"]);
        (snapshot["UnreadMovieCategoriesSince"]?["1"]).Should().BeNull();
    }

    [Theory]
    [InlineData(Answer.Empty)]
    [InlineData(Answer.Throws)]
    public async Task UnreadSeriesCategory_KeepsItsEpisodes_WhileOtherOrphansGo(Answer answer)
    {
        await RunSeriesAsync(Answer.Items, steadyKeepsShowC: true).ConfigureAwait(true);
        EpisodeFile("Show A").Should().NotBeNull("the first run writes everything");

        var result = await RunSeriesAsync(answer, steadyKeepsShowC: false).ConfigureAwait(true);

        EpisodeFile("Show A").Should().NotBeNull("its category was not read, so it is not gone");
        EpisodeFile("Show B").Should().NotBeNull();
        EpisodeFile("Show C").Should().BeNull("a category that answered dropped it, so cleanup still runs");

        var snapshot = LatestSnapshot();
        ((JObject)snapshot["Series"]!).Properties().Select(p => p.Name).Should().Contain("7");
        (snapshot["UnreadSeriesCategoriesSince"]?["1"]).Should().NotBeNull();
        (JObject.FromObject(result)["SeriesKeptForUnreadCategories"]?.Value<int>()).Should().Be(1);

        // A second unread run still keeps it, because the entry was carried forward.
        await RunSeriesAsync(answer, steadyKeepsShowC: false).ConfigureAwait(true);
        EpisodeFile("Show A").Should().NotBeNull();
    }

    [Fact]
    public void Find_EmptyIsUnreadOnlyWhenItHadItemsBefore()
    {
        var counts = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 5 };

        UnreadCategories.Find(counts, [4], new HashSet<int> { 1, 3 })
            .Should().BeEquivalentTo([1, 4], "2 was always empty, 3 answered, 4 threw");
    }

    private string[] MovieFiles(string title)
        => Directory.GetFiles(Path.Combine(_libraryPath, "Movies"), "*.strm", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).StartsWith(title, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Path.Combine(_libraryPath, "Movies"), f).Split(Path.DirectorySeparatorChar)[0])
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    private string? MovieFile(string title)
        => Directory.GetFiles(Path.Combine(_libraryPath, "Movies"), "*.strm", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).StartsWith(title, StringComparison.Ordinal));

    private string? EpisodeFile(string show)
        => Directory.GetFiles(Path.Combine(_libraryPath, "Series"), "*.strm", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).StartsWith(show, StringComparison.Ordinal));

    private string LatestSnapshotPath()
        => Directory.GetFiles(_libraryPath, "snapshot_*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Last();

    private JObject LatestSnapshot() => JObject.Parse(File.ReadAllText(LatestSnapshotPath()));

    private void BackdateUnread(string field, DateTime since)
    {
        var path = LatestSnapshotPath();
        var snapshot = JObject.Parse(File.ReadAllText(path));
        snapshot[field]!["1"] = new JValue(DateTime.SpecifyKind(since, DateTimeKind.Utc));
        File.WriteAllText(path, snapshot.ToString());
    }

    private Task<SyncResult> RunMoviesAsync(Answer flaky, bool steadyKeepsDelta, bool flakyKeepsBeta = true, bool forceFullSync = true, bool incremental = true, int[]? selectedVod = null)
    {
        var flakySetup = _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()));
        switch (flaky)
        {
            case Answer.Throws:
                flakySetup.ThrowsAsync(new HttpRequestException("provider timed out"));
                break;
            case Answer.Empty:
                flakySetup.ReturnsAsync(() => new List<StreamInfo>());
                break;
            default:
                var listed = new List<StreamInfo> { Movie(101, "Alpha (2020)", 1) };
                if (flakyKeepsBeta)
                {
                    listed.Add(Movie(102, "Beta (2021)", 1));
                }

                flakySetup.ReturnsAsync(listed);
                break;
        }

        var steady = new List<StreamInfo> { Movie(201, "Gamma (2019)", 2) };
        if (steadyKeepsDelta)
        {
            steady.Add(Movie(202, "Delta (2018)", 2));
        }

        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(steady);

        return RunSyncAsync(syncMovies: true, syncSeries: false, forceFullSync, incremental, selectedVod);
    }

    private Task<SyncResult> RunSeriesAsync(Answer flaky, bool steadyKeepsShowC)
    {
        var flakySetup = _client.Setup(c => c.GetSeriesByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()));
        switch (flaky)
        {
            case Answer.Throws:
                flakySetup.ThrowsAsync(new HttpRequestException("provider timed out"));
                break;
            case Answer.Empty:
                flakySetup.ReturnsAsync(() => new List<Series>());
                break;
            default:
                flakySetup.ReturnsAsync(() => new List<Series> { Show(7, "Show A (2020)", 1) });
                break;
        }

        var steady = new List<Series> { Show(8, "Show B (2021)", 2) };
        if (steadyKeepsShowC)
        {
            steady.Add(Show(9, "Show C (2022)", 2));
        }

        _client.Setup(c => c.GetSeriesByCategoryAsync(It.IsAny<ConnectionInfo>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(steady);

        return RunSyncAsync(syncMovies: false, syncSeries: true, forceFullSync: true);
    }

    // The snapshot records the category from the item's own category_id, so the fixture sets it.
    private static StreamInfo Movie(int id, string name, int categoryId)
        => new() { StreamId = id, Name = name, ContainerExtension = "mp4", CategoryId = categoryId };

    private static Series Show(int id, string name, int categoryId)
        => new() { SeriesId = id, Name = name, CategoryId = categoryId };

    private async Task<SyncResult> RunSyncAsync(bool syncMovies, bool syncSeries, bool forceFullSync, bool incremental = true, int[]? selectedVod = null, string? movieFolders = null)
    {
        if (forceFullSync)
        {
            _run++;
        }

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
                SyncMovies = syncMovies,
                SyncSeries = syncSeries,
                CleanupOrphans = true,
                OrphanSafetyThreshold = 1.0,

                // Incremental sync on, so the snapshot is loaded and saved; a changed exclusion
                // list each run changes the config fingerprint and makes every run a full sync.
                EnableIncrementalSync = incremental,
                SelectedVodCategoryIds = selectedVod ?? Array.Empty<int>(),
                MovieFolderMode = movieFolders is null ? "Single" : "Multiple",
                MovieFolderMappings = movieFolders ?? string.Empty,
                FullSyncIntervalDays = 7,
                ExcludedVodStreamIds = new[] { 1000 + _run },
                ExcludedSeriesIds = new[] { 1000 + _run },
                SmartSkipExisting = false,
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
