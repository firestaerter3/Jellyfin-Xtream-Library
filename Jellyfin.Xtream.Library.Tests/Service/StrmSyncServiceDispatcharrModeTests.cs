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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// Covers GetMovieAdvancedInfoAsync: when Dispatcharr Mode is on the REST provider-info endpoint
/// is tried first, falling back to the classic Xtream get_vod_info call only when Dispatcharr Mode
/// is off or the REST call comes back empty. Series have no such path: they always use
/// get_series_info (GitHub #153), see <see cref="SeriesIdRepairTests"/>.
/// </summary>
public class StrmSyncServiceDispatcharrModeTests
{
    private readonly Mock<IXtreamClient> _mockClient;
    private readonly Mock<IDispatcharrClient> _mockDispatcharrClient;
    private readonly StrmSyncService _syncService;

    public StrmSyncServiceDispatcharrModeTests()
    {
        _mockClient = new Mock<IXtreamClient>();
        _mockDispatcharrClient = new Mock<IDispatcharrClient>();

        var appPathsMock = new Mock<IServerApplicationPaths>();
        appPathsMock.Setup(p => p.DataPath).Returns("/tmp");
        var snapshotService = new SnapshotService(appPathsMock.Object, NullLogger<SnapshotService>.Instance);

        var appHostMock = new Mock<IServerApplicationHost>();
        appHostMock.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");
        var liveTvService = new LiveTvService(_mockClient.Object, _mockDispatcharrClient.Object, appPathsMock.Object, appHostMock.Object, NullLogger<LiveTvService>.Instance);

        _syncService = new StrmSyncService(
            _mockClient.Object,
            _mockDispatcharrClient.Object,
            new Mock<ILibraryManager>().Object,
            new Mock<IMetadataLookupService>().Object,
            snapshotService,
            liveTvService,
            appPathsMock.Object,
            NullLogger<StrmSyncService>.Instance);
    }

    private static ProviderConfig MakeProvider(bool dispatcharrMode) => new()
    {
        BaseUrl = "http://xtream.example.com",
        Username = "user",
        Password = "pass",
        EnableDispatcharrMode = dispatcharrMode,
        DispatcharrApiUser = dispatcharrMode ? "jellyfin-sync" : string.Empty,
        DispatcharrApiPass = dispatcharrMode ? "secret" : string.Empty,
    };

    private static readonly ConnectionInfo Connection = new("http://xtream.example.com", "user", "pass");

    [Fact]
    public async Task GetMovieAdvancedInfo_DispatcharrModeOff_UsesClassicClientOnly()
    {
        var classicInfo = new VodInfoResponse();
        _mockClient
            .Setup(c => c.GetVodInfoAsync(It.IsAny<ConnectionInfo>(), 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(classicInfo);

        var result = await _syncService.GetMovieAdvancedInfoAsync(MakeProvider(false), Connection, enableDispatcharrMode: false, 42, CancellationToken.None);

        result.Should().BeSameAs(classicInfo);
        _mockDispatcharrClient.Verify(d => d.GetMovieProviderInfoAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetMovieAdvancedInfo_DispatcharrModeOn_RestSucceeds_ClassicClientNeverCalled()
    {
        var restInfo = new VodInfoResponse();
        _mockDispatcharrClient
            .Setup(d => d.GetMovieProviderInfoAsync(It.IsAny<string>(), 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restInfo);

        var result = await _syncService.GetMovieAdvancedInfoAsync(MakeProvider(true), Connection, enableDispatcharrMode: true, 42, CancellationToken.None);

        result.Should().BeSameAs(restInfo);
        _mockClient.Verify(c => c.GetVodInfoAsync(It.IsAny<ConnectionInfo>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetMovieAdvancedInfo_DispatcharrModeOn_RestReturnsNull_FallsBackToClassic()
    {
        _mockDispatcharrClient
            .Setup(d => d.GetMovieProviderInfoAsync(It.IsAny<string>(), 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync((VodInfoResponse?)null);
        var classicInfo = new VodInfoResponse();
        _mockClient
            .Setup(c => c.GetVodInfoAsync(It.IsAny<ConnectionInfo>(), 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(classicInfo);

        var result = await _syncService.GetMovieAdvancedInfoAsync(MakeProvider(true), Connection, enableDispatcharrMode: true, 42, CancellationToken.None);

        result.Should().BeSameAs(classicInfo);
        _mockClient.Verify(c => c.GetVodInfoAsync(It.IsAny<ConnectionInfo>(), 42, It.IsAny<CancellationToken>()), Times.Once);
    }
}
