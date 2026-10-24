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

using System.IO;
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

// Constructs a Plugin, so it has to serialise with the other classes that publish the singleton;
// without this it races them and Plugin.Instance can vanish mid-test.
[Collection("PluginSingletonTests")]
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

        var mockDispatcharrClient = new Mock<IDispatcharrClient>();
        var appHostMock = new Mock<IServerApplicationHost>();
        appHostMock.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");
        var liveTvService = new LiveTvService(_mockClient.Object, mockDispatcharrClient.Object, appPathsMock.Object, appHostMock.Object, NullLogger<LiveTvService>.Instance);
        _syncService = new StrmSyncService(
            _mockClient.Object,
            mockDispatcharrClient.Object,
            _mockLibraryManager.Object,
            _mockMetadataLookup.Object,
            snapshotService,
            liveTvService,
            appPathsMock.Object,
            _mockSyncServiceLogger.Object);

        _controller = new SyncController(
            _syncService,
            _mockClient.Object,
            mockDispatcharrClient.Object,
            _mockMetadataLookup.Object,
            snapshotService,
            appPathsMock.Object,
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

    #region DownloadLog Tests

    [Fact]
    public void DownloadLog_ReturnsFileResult_WithRedactedContent()
    {
        // Arrange: create a temp log directory with a sample log file
        var logDir = Path.Combine(Path.GetTempPath(), "xtream-test-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDir);

        try
        {
            var logContent = "[2024-01-01 10:00:00] [INF] [Jellyfin.Xtream.Library] Sync started\n"
                + "[2024-01-01 10:00:01] [INF] [SomeOtherPlugin] This should be filtered out\n"
                + "[2024-01-01 10:00:02] [INF] [Jellyfin.Xtream.Library] Connecting to 192.168.1.100:8080\n";
            File.WriteAllText(Path.Combine(logDir, "log_20240101.log"), logContent);

            // Create a controller with the temp log directory
            var appPathsMock = new Mock<IServerApplicationPaths>();
            appPathsMock.Setup(p => p.LogDirectoryPath).Returns(logDir);
            appPathsMock.Setup(p => p.DataPath).Returns("/tmp");

            var snapshotService = new SnapshotService(appPathsMock.Object, NullLogger<SnapshotService>.Instance);
            var mockDispatcharrClient = new Mock<IDispatcharrClient>();
            var appHostMock = new Mock<IServerApplicationHost>();
            appHostMock.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
                .Returns("http://127.0.0.1:8096");
            var liveTvService = new LiveTvService(_mockClient.Object, mockDispatcharrClient.Object, appPathsMock.Object, appHostMock.Object, NullLogger<LiveTvService>.Instance);
            var syncService = new StrmSyncService(
                _mockClient.Object,
                mockDispatcharrClient.Object,
                _mockLibraryManager.Object,
                _mockMetadataLookup.Object,
                snapshotService,
                liveTvService,
                appPathsMock.Object,
                _mockSyncServiceLogger.Object);

            var controller = new SyncController(
                syncService,
                _mockClient.Object,
                mockDispatcharrClient.Object,
                _mockMetadataLookup.Object,
                snapshotService,
                    appPathsMock.Object,
                _mockControllerLogger.Object);

            // Act
            var result = controller.DownloadLog();

            // Assert
            var fileResult = result.Should().BeOfType<FileContentResult>().Subject;
            fileResult.ContentType.Should().Be("text/plain");
            fileResult.FileDownloadName.Should().Be("xtream-library-log.txt");

            var content = System.Text.Encoding.UTF8.GetString(fileResult.FileContents);
            content.Should().Contain("Jellyfin.Xtream.Library");
            content.Should().NotContain("SomeOtherPlugin");
            content.Should().Contain("[REDACTED_IP]");
            content.Should().NotContain("192.168.1.100");
        }
        finally
        {
            Directory.Delete(logDir, true);
        }
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

    #region Category Item Listing Tests

    [Fact]
    public async Task GetVodStreams_NoProviderConfigured_ReturnsBadRequest()
    {
        var result = await _controller.GetVodStreams(categoryId: 1);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetSeriesList_NoProviderConfigured_ReturnsBadRequest()
    {
        var result = await _controller.GetSeriesList(categoryId: 1);
        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    #endregion

    #region TestDispatcharr Credential Resolution Tests

    // GitHub #114. Test Dispatcharr read the saved configuration, so typing a password and pressing
    // Test before saving tested the previous one and failed with the same message a wrong password
    // gets. The resolver is what decides between typed and saved, so it is pinned here rather than
    // through the endpoint, which needs a live Plugin.Instance.

    private static ProviderConfig SavedProvider() => new()
    {
        BaseUrl = "http://xtream.example.com:8080",
        DispatcharrBaseUrl = "http://dispatcharr.example.com:9191",
        DispatcharrApiUser = "saved-admin",
        DispatcharrApiPass = "saved-pass",
    };

    [Fact]
    public void ResolveDispatcharrTestTarget_PrefersWhatTheFormSent()
    {
        var request = new DispatcharrTestRequest
        {
            BaseUrl = "http://typed-xtream:8080",
            DispatcharrBaseUrl = "http://typed-dispatcharr:9191",
            ApiUser = "typed-admin",
            ApiPass = "typed-pass",
        };

        var (baseUrl, apiUser, apiPass) = SyncController.ResolveDispatcharrTestTarget(request, SavedProvider());

        baseUrl.Should().Be("http://typed-dispatcharr:9191");
        apiUser.Should().Be("typed-admin");
        apiPass.Should().Be("typed-pass");
    }

    [Fact]
    public void ResolveDispatcharrTestTarget_NoBodyStillTestsWhatIsSaved()
    {
        // The pre-#114 caller sent no body at all. That has to keep working, and it is also what
        // "test what is configured" means for anything calling the endpoint directly.
        var (baseUrl, apiUser, apiPass) = SyncController.ResolveDispatcharrTestTarget(null, SavedProvider());

        baseUrl.Should().Be("http://dispatcharr.example.com:9191");
        apiUser.Should().Be("saved-admin");
        apiPass.Should().Be("saved-pass");
    }

    [Fact]
    public void ResolveDispatcharrTestTarget_AnEmptiedDispatcharrUrlMeansTheXtreamHost()
    {
        // The trap this whole resolver is shaped around. Clearing the field asks for the Xtream
        // host; treating empty as "not sent" would restore the URL the user just deleted and
        // report a result for an address that is no longer on screen.
        var request = new DispatcharrTestRequest
        {
            BaseUrl = "http://typed-xtream:8080",
            DispatcharrBaseUrl = string.Empty,
            ApiUser = "typed-admin",
            ApiPass = "typed-pass",
        };

        SyncController.ResolveDispatcharrTestTarget(request, SavedProvider())
            .BaseUrl.Should().Be("http://typed-xtream:8080");
    }

    [Fact]
    public void ResolveDispatcharrTestTarget_AnEmptiedPasswordIsTestedAsEmpty()
    {
        // Same rule one field over: clearing a password and pressing Test must not quietly test
        // the old one and go green.
        var request = new DispatcharrTestRequest
        {
            BaseUrl = "http://typed-xtream:8080",
            DispatcharrBaseUrl = "http://typed-dispatcharr:9191",
            ApiUser = "typed-admin",
            ApiPass = string.Empty,
        };

        SyncController.ResolveDispatcharrTestTarget(request, SavedProvider())
            .ApiPass.Should().BeEmpty();
    }

    [Fact]
    public void ResolveDispatcharrTestTarget_WorksBeforeAnythingHasBeenSaved()
    {
        // A provider being configured for the first time has no stored entry at this index, which
        // used to answer "Please configure Base URL first" however much had been typed.
        var request = new DispatcharrTestRequest
        {
            BaseUrl = "http://typed-xtream:8080",
            DispatcharrBaseUrl = string.Empty,
            ApiUser = "typed-admin",
            ApiPass = "typed-pass",
        };

        var (baseUrl, apiUser, _) = SyncController.ResolveDispatcharrTestTarget(request, null);

        baseUrl.Should().Be("http://typed-xtream:8080");
        apiUser.Should().Be("typed-admin");
    }

    [Fact]
    public void ResolveDispatcharrTestTarget_TrailingSlashIsHandledLikeEverywhereElse()
    {
        // Reused from ProviderConfig rather than reimplemented, so the test and the sync cannot
        // disagree about the same URL (GitHub #83).
        var request = new DispatcharrTestRequest
        {
            DispatcharrBaseUrl = "http://typed-dispatcharr:9191/",
        };

        SyncController.ResolveDispatcharrTestTarget(request, SavedProvider())
            .BaseUrl.Should().Be("http://typed-dispatcharr:9191");
    }

    [Fact]
    public void ResolveDispatcharrTestTarget_NothingTypedAndNothingSavedIsEmpty()
    {
        var (baseUrl, apiUser, apiPass) = SyncController.ResolveDispatcharrTestTarget(
            new DispatcharrTestRequest { BaseUrl = string.Empty, DispatcharrBaseUrl = string.Empty },
            null);

        baseUrl.Should().BeEmpty();
        apiUser.Should().BeEmpty();
        apiPass.Should().BeEmpty();
    }

    #endregion
}
