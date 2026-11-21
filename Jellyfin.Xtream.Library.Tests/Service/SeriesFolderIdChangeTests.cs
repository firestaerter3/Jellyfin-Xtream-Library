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
/// A series already on disk keeps its folder when the id the sync would name it by changes.
/// <para>
/// The folder name carries the id: "[tvdbid-X]" from a name lookup, nothing when the lookup
/// failed, "[tmdbid-Y]" once the provider supplies a TMDB id. A provider that starts sending one
/// (or a plugin that starts reading it from get_series) changes the name the sync builds for every
/// series. Writing under that new name would put each series in the library twice, and orphan
/// cleanup would then delete the original folder's files, taking watched state and artwork with
/// them in Jellyfin. These tests pin that the existing folder is found and reused instead.
/// </para>
/// </summary>
[Collection("PluginSingletonTests")]
public class SeriesFolderIdChangeTests : IDisposable
{
    private const int SeriesId = 7;
    private const int CategoryId = 1;
    private const string SeriesTitle = "Show (2020)";
    private static readonly DateTime FirstModified = new(2025, 1, 5, 11, 9, 15, DateTimeKind.Utc);

    private readonly string _libraryPath;
    private readonly Mock<IXtreamClient> _client = new();
    private readonly Mock<IMetadataLookupService> _lookup = new();
    private readonly List<Episode> _episodes = new() { NewEpisode(11, 1, "Pilot") };

    // What the provider and the name lookup answer; changed between the two syncs of a test.
    private string? _providerTmdbId;
    private int? _lookupTvdbId;
    private DateTime _lastModified = FirstModified;

    public SeriesFolderIdChangeTests()
    {
        _libraryPath = Path.Combine(Path.GetTempPath(), "xtream-folderid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_libraryPath);

        _client.Setup(c => c.GetSeriesCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = CategoryId, CategoryName = "Shows" } });
        _client.Setup(c => c.GetSeriesByCategoryAsync(It.IsAny<ConnectionInfo>(), CategoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new List<Series>
            {
                new() { SeriesId = SeriesId, Name = SeriesTitle, CategoryId = CategoryId, LastModified = _lastModified },
            });
        _client.Setup(c => c.GetSeriesStreamsBySeriesAsync(It.IsAny<ConnectionInfo>(), SeriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SeriesStreamInfo
            {
                Info = new SeriesInfo { Name = SeriesTitle, Tmdb = _providerTmdbId },
                Seasons = new List<Season> { new() { SeasonNumber = 1 } },
                Episodes = new Dictionary<int, ICollection<Episode>> { [1] = _episodes.ToList() },
            });

        _lookup.Setup(l => l.LookupSeriesTvdbIdAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _lookupTvdbId);
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
    public async Task TvdbFolder_ProviderStartsSendingTmdbId_KeepsFolder()
    {
        await AssertFolderKeptWhenProviderStartsSendingTmdbIdAsync(
            lookupTvdbId: 1,
            expectedFolder: "Show (2020) [tvdbid-1]").ConfigureAwait(true);
    }

    [Fact]
    public async Task FolderWithoutId_ProviderStartsSendingTmdbId_KeepsFolder()
    {
        await AssertFolderKeptWhenProviderStartsSendingTmdbIdAsync(
            lookupTvdbId: null,
            expectedFolder: "Show (2020)").ConfigureAwait(true);
    }

    [Fact]
    public async Task MultipleFolderMode_ProviderStartsSendingTmdbId_KeepsFolder()
    {
        await AssertFolderKeptWhenProviderStartsSendingTmdbIdAsync(
            lookupTvdbId: 1,
            expectedFolder: "Show (2020) [tvdbid-1]",
            configure: p =>
            {
                p.SeriesFolderMode = "Multiple";
                p.SeriesFolderMappings = $"Kids={CategoryId}";
            },
            subfolder: "Kids").ConfigureAwait(true);
    }

    [Fact]
    public async Task FullSyncWithoutSmartSkip_ProviderStartsSendingTmdbId_KeepsFolder()
    {
        // Every series goes through the write path: no incremental filter, no smart skip.
        await AssertFolderKeptWhenProviderStartsSendingTmdbIdAsync(
            lookupTvdbId: 1,
            expectedFolder: "Show (2020) [tvdbid-1]",
            configure: p =>
            {
                p.EnableIncrementalSync = false;
                p.SmartSkipExisting = false;
            }).ConfigureAwait(true);
    }

    [Fact]
    public async Task SmartSkipAndCleanupOff_ProviderStartsSendingTmdbId_KeepsFolder()
    {
        // The existing-folder index is only built when smart skip or orphan cleanup is on.
        await AssertFolderKeptWhenProviderStartsSendingTmdbIdAsync(
            lookupTvdbId: 1,
            expectedFolder: "Show (2020) [tvdbid-1]",
            configure: p =>
            {
                p.SmartSkipExisting = false;
                p.CleanupOrphans = false;
            }).ConfigureAwait(true);
    }

    [Fact]
    public async Task SmartSkipAndCleanupOff_UnchangedSeries_IsNotReadAgain()
    {
        // Same gate, other symptom: the incremental filter judges a series complete by the file
        // count the index records, so with the index unbuilt every series was read again each run.
        _lookupTvdbId = 1;
        Action<ProviderConfig> bothOff = p =>
        {
            p.SmartSkipExisting = false;
            p.CleanupOrphans = false;
        };

        var first = await RunSyncAsync(bothOff).ConfigureAwait(true);
        first.Success.Should().BeTrue(first.Error);
        var second = await RunSyncAsync(bothOff).ConfigureAwait(true);
        second.Success.Should().BeTrue(second.Error);

        second.WasIncrementalSync.Should().BeTrue();
        second.SeriesSkipped.Should().Be(1);
        _client.Verify(
            c => c.GetSeriesStreamsBySeriesAsync(It.IsAny<ConnectionInfo>(), SeriesId, It.IsAny<CancellationToken>()),
            Times.Once());
    }

    private static Episode NewEpisode(int id, int number, string title)
        => new() { EpisodeId = id, EpisodeNum = number, Season = 1, Title = title, ContainerExtension = "mkv" };

    private static string[] FolderNames(string parent)
        => Directory.GetDirectories(parent).Select(d => Path.GetFileName(d)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private async Task AssertFolderKeptWhenProviderStartsSendingTmdbIdAsync(
        int? lookupTvdbId,
        string expectedFolder,
        Action<ProviderConfig>? configure = null,
        string subfolder = "")
    {
        // First sync: the provider sends no TMDB id, so the folder is named by the lookup.
        _lookupTvdbId = lookupTvdbId;
        var first = await RunSyncAsync(configure).ConfigureAwait(true);
        first.Success.Should().BeTrue(first.Error);

        string parent = Path.Combine(_libraryPath, "Series", subfolder);
        FolderNames(parent).Should().Equal(expectedFolder);
        string folder = Path.Combine(parent, expectedFolder);
        string nfoPath = Path.Combine(folder, "tvshow.nfo");
        string? nfoBefore = File.Exists(nfoPath) ? await File.ReadAllTextAsync(nfoPath).ConfigureAwait(true) : null;

        // Second sync: the provider now sends a TMDB id, and a new episode, so the series is
        // read and written again rather than skipped as unchanged.
        _providerTmdbId = "9";
        _lastModified = FirstModified.AddHours(1);
        _episodes.Add(NewEpisode(12, 2, "Second"));
        var second = await RunSyncAsync(configure).ConfigureAwait(true);
        second.Success.Should().BeTrue(second.Error);

        FolderNames(parent).Should().Equal(
            new[] { expectedFolder },
            "the series must stay in its existing folder, not move to one named after the new id");
        File.Exists(Path.Combine(folder, "Season 1", "Show - S01E01 - Pilot.strm")).Should().BeTrue("the existing episode stays");
        File.Exists(Path.Combine(folder, "Season 1", "Show - S01E02 - Second.strm")).Should().BeTrue("the new episode goes into the existing folder");
        second.FilesDeleted.Should().Be(0);
        second.EpisodesDeleted.Should().Be(0);

        string? nfoAfter = File.Exists(nfoPath) ? await File.ReadAllTextAsync(nfoPath).ConfigureAwait(true) : null;
        nfoAfter.Should().Be(nfoBefore, "tvshow.nfo of an existing folder is not rewritten");
    }

    private async Task<SyncResult> RunSyncAsync(Action<ProviderConfig>? configure)
    {
        var appPaths = new Mock<IServerApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(_libraryPath);
        appPaths.Setup(p => p.DataPath).Returns(_libraryPath);
        appPaths.Setup(p => p.ProgramDataPath).Returns(_libraryPath);
        appPaths.Setup(p => p.CachePath).Returns(_libraryPath);
        appPaths.Setup(p => p.TempDirectory).Returns(_libraryPath);
        appPaths.Setup(p => p.PluginsPath).Returns(_libraryPath);

        var provider = new ProviderConfig
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
            CleanupOrphans = true,
            DownloadArtworkForUnmatched = false,
            SyncParallelism = 1,
        };
        configure?.Invoke(provider);

        var plugin = new Plugin(appPaths.Object, new RealXmlSerializer());
        plugin.Configuration.Providers = new List<ProviderConfig> { provider };
        plugin.Configuration.EnableLiveTv = false;
        plugin.Configuration.EnableMetadataLookup = true;

        var host = new Mock<IServerApplicationHost>();
        host.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");

        var service = new StrmSyncService(
            _client.Object,
            new Mock<IDispatcharrClient>().Object,
            new Mock<ILibraryManager>().Object,
            _lookup.Object,
            new SnapshotService(appPaths.Object, NullLogger<SnapshotService>.Instance),
            new LiveTvService(_client.Object, new Mock<IDispatcharrClient>().Object, appPaths.Object, host.Object, NullLogger<LiveTvService>.Instance),
            appPaths.Object,
            NullLogger<StrmSyncService>.Instance);

        return await service.SyncAsync(CancellationToken.None).ConfigureAwait(true);
    }
}
