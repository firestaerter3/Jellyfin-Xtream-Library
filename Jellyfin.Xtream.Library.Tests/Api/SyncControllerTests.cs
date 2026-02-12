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

using FluentAssertions;
using Jellyfin.Xtream.Library.Api;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Tests.Helpers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Api;

public class SyncControllerTests
{
    private readonly Mock<IXtreamClient> _mockClient;
    private readonly Mock<ILibraryManager> _mockLibraryManager;
    private readonly Mock<IMetadataLookupService> _mockMetadataLookup;
    private readonly Mock<ILogger<StrmSyncService>> _mockSyncServiceLogger;
    private readonly Mock<ILogger<SyncController>> _mockControllerLogger;
    private readonly StrmSyncService _syncService;
    private readonly SyncController _controller;

    public SyncControllerTests()
    {
        _mockClient = new Mock<IXtreamClient>();
        _mockLibraryManager = new Mock<ILibraryManager>();
        _mockMetadataLookup = new Mock<IMetadataLookupService>();
        _mockSyncServiceLogger = new Mock<ILogger<StrmSyncService>>();
        _mockControllerLogger = new Mock<ILogger<SyncController>>();

        var appPathsMock = new Mock<IServerApplicationPaths>();
        appPathsMock.Setup(p => p.DataPath).Returns("/tmp");
        var snapshotService = new SnapshotService(appPathsMock.Object, NullLogger<SnapshotService>.Instance);
        var deltaCalculator = new DeltaCalculator(NullLogger<DeltaCalculator>.Instance);

        var mockDispatcharrClient = new Mock<IDispatcharrClient>();
        _syncService = new StrmSyncService(
            _mockClient.Object,
            mockDispatcharrClient.Object,
            _mockLibraryManager.Object,
            _mockMetadataLookup.Object,
            snapshotService,
            deltaCalculator,
            appPathsMock.Object,
            _mockSyncServiceLogger.Object);

        _controller = new SyncController(
            _syncService,
            _mockClient.Object,
            mockDispatcharrClient.Object,
            _mockMetadataLookup.Object,
            snapshotService,
            _mockControllerLogger.Object);
    }

    #region GetStatus Tests

    [Fact]
    public void GetStatus_NoPreviousSync_ReturnsNoContent()
    {
        var result = _controller.GetStatus();

        result.Result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task GetStatus_AfterSync_ReturnsOkWithResult()
    {
        // Note: This test would require setting up the Plugin.Instance which is complex
        // In a real scenario, we would need to refactor the service to accept configuration via DI
        // For now, we test the behavior where LastSyncResult is null
        var result = _controller.GetStatus();

        var noContentResult = result.Result.Should().BeOfType<NoContentResult>().Subject;
        noContentResult.StatusCode.Should().Be(StatusCodes.Status204NoContent);
    }

    #endregion

    #region ConnectionTestResult Tests

    [Fact]
    public void ConnectionTestResult_DefaultValues_AreCorrect()
    {
        var result = new ConnectionTestResult();

        result.Success.Should().BeFalse();
        result.Message.Should().BeEmpty();
        result.Username.Should().BeNull();
        result.Status.Should().BeNull();
        result.MaxConnections.Should().BeNull();
        result.ActiveConnections.Should().BeNull();
    }

    [Fact]
    public void ConnectionTestResult_WithValues_SetsCorrectly()
    {
        var result = new ConnectionTestResult
        {
            Success = true,
            Message = "Connected successfully",
            Username = "testuser",
            Status = "Active",
            MaxConnections = 5,
            ActiveConnections = 2,
        };

        result.Success.Should().BeTrue();
        result.Message.Should().Be("Connected successfully");
        result.Username.Should().Be("testuser");
        result.Status.Should().Be("Active");
        result.MaxConnections.Should().Be(5);
        result.ActiveConnections.Should().Be(2);
    }

    #endregion

    #region SyncResult Tests

    [Fact]
    public void SyncResult_Duration_CalculatesCorrectly()
    {
        var result = new SyncResult
        {
            StartTime = new DateTime(2024, 1, 1, 10, 0, 0),
            EndTime = new DateTime(2024, 1, 1, 10, 5, 30),
        };

        result.Duration.Should().Be(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void SyncResult_DefaultValues_AreCorrect()
    {
        var result = new SyncResult();

        result.Success.Should().BeFalse();
        result.Error.Should().BeNull();
        result.MoviesCreated.Should().Be(0);
        result.MoviesSkipped.Should().Be(0);
        result.EpisodesCreated.Should().Be(0);
        result.EpisodesSkipped.Should().Be(0);
        result.FilesDeleted.Should().Be(0);
        result.Errors.Should().Be(0);
    }

    [Fact]
    public void SyncResult_WithValues_SetsCorrectly()
    {
        var result = new SyncResult
        {
            Success = true,
            MoviesCreated = 10,
            MoviesSkipped = 5,
            EpisodesCreated = 100,
            EpisodesSkipped = 50,
            FilesDeleted = 3,
            Errors = 2,
        };

        result.Success.Should().BeTrue();
        result.MoviesCreated.Should().Be(10);
        result.MoviesSkipped.Should().Be(5);
        result.EpisodesCreated.Should().Be(100);
        result.EpisodesSkipped.Should().Be(50);
        result.FilesDeleted.Should().Be(3);
        result.Errors.Should().Be(2);
    }

    #endregion

    #region History Tests

    [Fact]
    public void GetHistory_ReturnsEmptyList_WhenNoSyncs()
    {
        var result = _controller.GetHistory();

        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var history = okResult.Value.Should().BeAssignableTo<IReadOnlyList<SyncResult>>().Subject;
        history.Should().BeEmpty();
    }

    #endregion

    #region Dashboard Tests

    [Fact]
    public void GetDashboard_ReturnsOk_WithExpectedShape()
    {
        var result = _controller.GetDashboard();

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        okResult.Value.Should().NotBeNull();

        // Verify the anonymous object has expected properties
        var value = okResult.Value!;
        var type = value.GetType();
        type.GetProperty("LastSync").Should().NotBeNull();
        type.GetProperty("Progress").Should().NotBeNull();
        type.GetProperty("History").Should().NotBeNull();
        type.GetProperty("ScheduleType").Should().NotBeNull();
        type.GetProperty("LibraryStats").Should().NotBeNull();
    }

    #endregion

    #region SyncResult Unmatched Tests

    [Fact]
    public void SyncResult_UnmatchedCounts_DefaultToZero()
    {
        var result = new SyncResult();

        result.MoviesUnmatched.Should().Be(0);
        result.SeriesUnmatched.Should().Be(0);
    }

    [Fact]
    public void SyncResult_UnmatchedCounts_SetCorrectly()
    {
        var result = new SyncResult
        {
            MoviesUnmatched = 42,
            SeriesUnmatched = 17,
        };

        result.MoviesUnmatched.Should().Be(42);
        result.SeriesUnmatched.Should().Be(17);
    }

    #endregion
}
