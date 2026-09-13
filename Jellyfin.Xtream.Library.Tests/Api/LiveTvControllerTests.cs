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
using System.IO;
using System.Net;
using FluentAssertions;
using Jellyfin.Xtream.Library.Api;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Api;

/// <summary>
/// Tests for the <see cref="LiveTvController.GetChannelLogo"/> proxy endpoint (issue #53).
/// Shares the Plugin.Instance static singleton, so it runs in the serialized collection.
/// </summary>
[Collection("PluginSingletonTests")]
public class LiveTvControllerTests : IDisposable
{
    private readonly LiveTvService _liveTvService;
    private readonly LiveTvController _controller;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvControllerTests"/> class.
    /// </summary>
    public LiveTvControllerTests()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "claude", "test-livetvcontroller-config");
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(tempPath);
        appPaths.Setup(p => p.DataPath).Returns(tempPath);
        appPaths.Setup(p => p.ProgramDataPath).Returns(tempPath);
        appPaths.Setup(p => p.CachePath).Returns(tempPath);
        appPaths.Setup(p => p.LogDirectoryPath).Returns(tempPath);
        appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(tempPath);
        appPaths.Setup(p => p.TempDirectory).Returns(tempPath);
        appPaths.Setup(p => p.PluginsPath).Returns(tempPath);
        appPaths.Setup(p => p.WebPath).Returns(tempPath);
        appPaths.Setup(p => p.ProgramSystemPath).Returns(tempPath);
        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer
            .Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(new PluginConfiguration());

        // Create plugin to set Plugin.Instance.
        _ = new Plugin(appPaths.Object, xmlSerializer.Object);
        Plugin.Instance.Configuration.ChannelOverrides = string.Empty;

        // Empty is the shipped default and means no restriction, but say it out loud: the
        // allow-list tests below set it themselves, and the rest of this class depends on these
        // endpoints answering (GitHub #109).
        Plugin.Instance.Configuration.LiveTvEndpointAllowedIps = string.Empty;

        var mockClient = new Mock<IXtreamClient>();
        var serverAppPaths = new Mock<IServerApplicationPaths>();
        serverAppPaths.Setup(p => p.DataPath).Returns(tempPath);
        var appHostMock = new Mock<IServerApplicationHost>();
        appHostMock.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");
        _liveTvService = new LiveTvService(mockClient.Object, serverAppPaths.Object, appHostMock.Object, NullLogger<LiveTvService>.Instance);
        _controller = new LiveTvController(_liveTvService, mockClient.Object, NullLogger<LiveTvController>.Instance);
    }

    /// <summary>
    /// Releases resources held by the test fixture.
    /// </summary>
    public void Dispose()
    {
        _liveTvService.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetChannelLogo_NoOverride_ReturnsNotFound()
    {
        Plugin.Instance.Configuration.ChannelOverrides = string.Empty;

        var result = _controller.GetChannelLogo(999999);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void GetChannelLogo_HttpOverride_ReturnsNotFound()
    {
        Plugin.Instance.Configuration.ChannelOverrides = "5=Name|1|http://logo.com/x.png";

        var result = _controller.GetChannelLogo(5);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void GetChannelLogo_LocalFileExists_ReturnsImageFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
        try
        {
            Plugin.Instance.Configuration.ChannelOverrides = "5=Name|1|" + tmp;

            var result = _controller.GetChannelLogo(5);

            var fileResult = result.Should().BeOfType<PhysicalFileResult>().Subject;
            fileResult.ContentType.Should().Be("image/png");
            fileResult.FileName.Should().Be(tmp);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void GetChannelLogo_LocalFileMissing_ReturnsNotFound()
    {
        Plugin.Instance.Configuration.ChannelOverrides = "5=Name|1|/nonexistent/path/zzz.png";

        var result = _controller.GetChannelLogo(5);

        result.Should().BeOfType<NotFoundResult>();
    }

    /// <summary>
    /// Tests for the endpoint IP allow-list (issue #109).
    /// </summary>
    [Fact]
    public void GetChannelLogo_NoAllowListConfigured_IgnoresRemoteIp()
    {
        // Default behaviour must not change: an unset allow-list serves the request
        // regardless of what the caller's remote IP looks like.
        Plugin.Instance.Configuration.LiveTvEndpointAllowedIps = string.Empty;
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
        try
        {
            Plugin.Instance.Configuration.ChannelOverrides = "5=Name|1|" + tmp;
            SetRemoteIp(IPAddress.Parse("203.0.113.5"));

            var result = _controller.GetChannelLogo(5);

            result.Should().BeOfType<PhysicalFileResult>();
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void GetChannelLogo_AllowListConfigured_NonMatchingIp_ReturnsNotFound()
    {
        Plugin.Instance.Configuration.LiveTvEndpointAllowedIps = "10.0.0.0/8";
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
        try
        {
            Plugin.Instance.Configuration.ChannelOverrides = "5=Name|1|" + tmp;
            SetRemoteIp(IPAddress.Parse("203.0.113.5"));

            var result = _controller.GetChannelLogo(5);

            result.Should().BeOfType<NotFoundResult>();
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void GetChannelLogo_AllowListConfigured_MatchingIp_ReturnsImageFile()
    {
        Plugin.Instance.Configuration.LiveTvEndpointAllowedIps = "10.0.0.0/8";
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
        try
        {
            Plugin.Instance.Configuration.ChannelOverrides = "5=Name|1|" + tmp;
            SetRemoteIp(IPAddress.Parse("10.30.30.8"));

            var result = _controller.GetChannelLogo(5);

            result.Should().BeOfType<PhysicalFileResult>();
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task GetM3UPlaylist_AllowListConfigured_NonMatchingIp_ReturnsNotFound()
    {
        // The ACL check must run and reject before the existing "Live TV disabled"/
        // "no credentials" BadRequest checks further down the same action.
        Plugin.Instance.Configuration.LiveTvEndpointAllowedIps = "10.0.0.0/8";
        Plugin.Instance.Configuration.EnableLiveTv = true;
        SetRemoteIp(IPAddress.Parse("203.0.113.5"));

        var result = await _controller.GetM3UPlaylist(System.Threading.CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async System.Threading.Tasks.Task GetM3UPlaylist_AllowListConfiguredWithOnlyInvalidEntries_ReturnsNotFound()
    {
        // A non-blank setting that parses to zero valid entries (a typo) must fail
        // closed, not silently fall back to the "unset" open behaviour - that would
        // defeat the restriction the operator explicitly configured. This has to be
        // rejected before even looking at the caller's IP, so deliberately not calling
        // SetRemoteIp here: an unresolved HttpContext must not accidentally pass.
        Plugin.Instance.Configuration.LiveTvEndpointAllowedIps = "not-an-ip\n# just a comment, still not a real entry";
        Plugin.Instance.Configuration.EnableLiveTv = true;

        var result = await _controller.GetM3UPlaylist(System.Threading.CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async System.Threading.Tasks.Task GetCatchupM3UPlaylist_AllowListConfiguredWithOnlyInvalidEntries_ReturnsNotFound()
    {
        Plugin.Instance.Configuration.LiveTvEndpointAllowedIps = "300.0.0.0/8";
        Plugin.Instance.Configuration.EnableLiveTv = true;
        Plugin.Instance.Configuration.EnableCatchup = true;

        var result = await _controller.GetCatchupM3UPlaylist(System.Threading.CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void GetChannelLogo_AllowListConfiguredWithOnlyInvalidEntries_ReturnsNotFound()
    {
        Plugin.Instance.Configuration.LiveTvEndpointAllowedIps = "999.999.999.999";
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(tmp, new byte[] { 1, 2, 3 });
        try
        {
            Plugin.Instance.Configuration.ChannelOverrides = "5=Name|1|" + tmp;

            var result = _controller.GetChannelLogo(5);

            result.Should().BeOfType<NotFoundResult>();
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    private void SetRemoteIp(IPAddress address)
    {
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                Connection = { RemoteIpAddress = address },
            },
        };
    }
}
