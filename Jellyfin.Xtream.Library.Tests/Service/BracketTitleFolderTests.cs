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
/// The scan of existing folders cut a name at its last " [", so "Show [MULTI-SUB]" was indexed as
/// "Show". Its own sync never found it, and the series called "Show" found it instead (GitHub #205).
/// </summary>
[Collection("PluginSingletonTests")]
public class BracketTitleFolderTests : IDisposable
{
    private static readonly DateTime Modified = new(2025, 1, 5, 11, 9, 15, DateTimeKind.Utc);

    private readonly string _libraryPath;
    private readonly Mock<IXtreamClient> _client = new();
    private List<Series> _series = new();
    private List<StreamInfo> _movies = new();

    public BracketTitleFolderTests()
    {
        _libraryPath = Path.Combine(Path.GetTempPath(), "xtream-bracket-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_libraryPath);

        _client.Setup(c => c.GetSeriesCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Shows" } });
        _client.Setup(c => c.GetSeriesByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _series);
        _client.Setup(c => c.GetSeriesStreamsBySeriesAsync(It.IsAny<ConnectionInfo>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConnectionInfo _, int id, CancellationToken _) => new SeriesStreamInfo
            {
                Seasons = new List<Season> { new() { SeasonNumber = 1 } },
                Episodes = new Dictionary<int, ICollection<Episode>>
                {
                    [1] = new List<Episode>
                    {
                        new() { EpisodeId = (id * 10) + 1, EpisodeNum = 1, Season = 1, Title = "Pilot", ContainerExtension = "mkv" },
                    },
                },
            });

        _client.Setup(c => c.GetVodCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Films" } });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _movies);
        _client.Setup(c => c.GetVodInfoAsync(It.IsAny<ConnectionInfo>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new VodInfoResponse());
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
    [InlineData("Show [MULTI-SUB]", "Show [MULTI-SUB]")]
    [InlineData("Show [tvdbid-123]", "Show")]
    [InlineData("Show (2020) [tmdbid-9]", "Show (2020)")]
    [InlineData("Show [MULTI-SUB] [tmdbid-9]", "Show [MULTI-SUB]")]
    [InlineData("Show [TMDBID-9]", "Show")]
    [InlineData("Show [imdbid-tt0123456]", "Show")]
    [InlineData("Show [tmdbid=9]", "Show")]
    [InlineData("Show [tmdbid-9] extra", "Show [tmdbid-9] extra")]
    [InlineData("[tmdbid-9]", "[tmdbid-9]")]
    [InlineData("Show", "Show")]
    public void StripIdTag_RemovesOnlyATrailingId(string folderName, string expected)
        => FolderIdentity.StripIdTag(folderName).Should().Be(expected);

    [Fact]
    public async Task IncrementalSync_UnchangedSeriesWithABracketedTitle_IsNotFetchedAgain()
    {
        _series = new() { Listed(7, "Show [MULTI-SUB]") };
        await RunSyncAsync().ConfigureAwait(true);
        Directory.Exists(SeriesDir("Show [MULTI-SUB]")).Should().BeTrue("the title keeps its bracket");

        await RunSyncAsync().ConfigureAwait(true);

        VerifyFetched(7, Times.Once());
    }

    [Fact]
    public async Task SeriesWithTheUnbracketedTitle_GetsItsOwnFolder()
    {
        _series = new() { Listed(7, "Show [MULTI-SUB]") };
        await RunSyncAsync().ConfigureAwait(true);

        _series = new() { Listed(7, "Show [MULTI-SUB]"), Listed(8, "Show") };
        await RunSyncAsync().ConfigureAwait(true);

        Urls("Show [MULTI-SUB]").Should().ContainSingle().Which.Should().EndWith("/71.mkv");
        Urls("Show").Should().ContainSingle().Which.Should().EndWith("/81.mkv");
    }

    [Fact]
    public async Task MovieWithTheUnbracketedTitle_GetsItsOwnFolder()
    {
        _movies = new() { Film(101, "Film [MULTI-SUB]") };
        await RunSyncAsync(movies: true).ConfigureAwait(true);

        _movies = new() { Film(101, "Film [MULTI-SUB]"), Film(102, "Film") };
        await RunSyncAsync(movies: true).ConfigureAwait(true);

        Urls("Film [MULTI-SUB]", "Movies").Should().ContainSingle().Which.Should().EndWith("/101.mkv");
        Urls("Film", "Movies").Should().ContainSingle().Which.Should().EndWith("/102.mkv");
    }

    private static StreamInfo Film(int id, string name) => new() { StreamId = id, Name = name, ContainerExtension = "mkv" };

    private static Series Listed(int id, string name) => new() { SeriesId = id, Name = name, LastModified = Modified };

    private string SeriesDir(string name, string root = "Series") => Path.Combine(_libraryPath, root, name);

    private List<string> Urls(string name, string root = "Series") => Directory.Exists(SeriesDir(name, root))
        ? Directory.GetFiles(SeriesDir(name, root), "*.strm", SearchOption.AllDirectories).Select(f => File.ReadAllText(f).Trim()).ToList()
        : new List<string>();

    private void VerifyFetched(int id, Times times)
        => _client.Verify(c => c.GetSeriesStreamsBySeriesAsync(It.IsAny<ConnectionInfo>(), id, It.IsAny<CancellationToken>()), times);

    private async Task<SyncResult> RunSyncAsync(bool movies = false)
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
                SyncMovies = movies,
                SyncSeries = !movies,
                EnableIncrementalSync = true,
                SmartSkipExisting = true,
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
