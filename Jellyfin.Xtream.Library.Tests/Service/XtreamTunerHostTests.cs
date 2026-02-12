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

using System.Net.Http;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class XtreamTunerHostTests : IDisposable
{
    private readonly Mock<IXtreamClient> _mockClient;
    private readonly LiveTvService _liveTvService;
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory;
    private readonly XtreamTunerHost _tunerHost;

    public XtreamTunerHostTests()
    {
        // Initialize Plugin.Instance so Plugin.Instance.Configuration works
        var tempPath = Path.Combine(Path.GetTempPath(), "claude", "test-tuner-config");
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

        // Create plugin to set Plugin.Instance
        _ = new Plugin(appPaths.Object, xmlSerializer.Object);

        _mockClient = new Mock<IXtreamClient>();
        _liveTvService = new LiveTvService(_mockClient.Object, NullLogger<LiveTvService>.Instance);
        _mockHttpClientFactory = new Mock<IHttpClientFactory>();
        _tunerHost = new XtreamTunerHost(
            _liveTvService,
            _mockHttpClientFactory.Object,
            NullLogger<XtreamTunerHost>.Instance);
    }

    public void Dispose()
    {
        _liveTvService.Dispose();
        GC.SuppressFinalize(this);
    }

    #region Properties

    [Fact]
    public void Name_ReturnsXtreamLibrary()
    {
        _tunerHost.Name.Should().Be("Xtream Library");
    }

    [Fact]
    public void Type_ReturnsXtreamLibrary()
    {
        _tunerHost.Type.Should().Be("xtream-library");
    }

    [Fact]
    public void IsSupported_ReturnsTrue()
    {
        _tunerHost.IsSupported.Should().BeTrue();
    }

    #endregion

    #region GetChannels

    [Fact]
    public async Task GetChannels_ReturnsEmpty_WhenLiveTvDisabled()
    {
        var config = Plugin.Instance.Configuration;
        config.EnableLiveTv = false;
        config.EnableNativeTuner = true;

        var result = await _tunerHost.GetChannels(false, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetChannels_ReturnsEmpty_WhenNativeTunerDisabled()
    {
        var config = Plugin.Instance.Configuration;
        config.EnableLiveTv = true;
        config.EnableNativeTuner = false;

        var result = await _tunerHost.GetChannels(false, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetChannels_ReturnsMappedChannels_WhenEnabled()
    {
        var config = Plugin.Instance.Configuration;
        config.EnableLiveTv = true;
        config.EnableNativeTuner = true;
        config.BaseUrl = "http://test.example.com";
        config.Username = "testuser";
        config.Password = "testpass";
        config.EnableChannelNameCleaning = false;

        var channels = new List<LiveStreamInfo>
        {
            new LiveStreamInfo
            {
                StreamId = 100,
                Name = "BBC One",
                Num = 1,
                StreamIcon = "http://logo.com/bbc.png",
            },
            new LiveStreamInfo
            {
                StreamId = 200,
                Name = "CNN",
                Num = 2,
                StreamIcon = string.Empty,
            },
        };

        _mockClient
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channels);

        var result = await _tunerHost.GetChannels(false, CancellationToken.None);

        result.Should().HaveCount(2);

        result[0].Id.Should().Be("xtream_100");
        result[0].Name.Should().Be("BBC One");
        result[0].Number.Should().Be("1");
        result[0].ImageUrl.Should().Be("http://logo.com/bbc.png");

        result[1].Id.Should().Be("xtream_200");
        result[1].Name.Should().Be("CNN");
        result[1].Number.Should().Be("2");
        result[1].ImageUrl.Should().BeNull();
    }

    #endregion

    #region GetChannelStreamMediaSources

    [Fact]
    public async Task GetChannelStreamMediaSources_ReturnsSingleSource_WithProbingDisabled()
    {
        var config = Plugin.Instance.Configuration;
        config.BaseUrl = "http://test.example.com";
        config.Username = "testuser";
        config.Password = "testpass";
        config.LiveTvOutputFormat = "m3u8";

        var result = await _tunerHost.GetChannelStreamMediaSources("xtream_100", CancellationToken.None);

        result.Should().HaveCount(1);

        var source = result[0];
        source.SupportsProbing.Should().BeFalse();
        source.IsRemote.Should().BeTrue();
        source.IsInfiniteStream.Should().BeTrue();
        source.SupportsDirectPlay.Should().BeFalse();
        source.SupportsDirectStream.Should().BeFalse();
        source.SupportsTranscoding.Should().BeTrue();
        source.Container.Should().Be("mpegts");
        source.Path.Should().Contain("/live/testuser/testpass/100.m3u8");
    }

    [Fact]
    public async Task GetChannelStreamMediaSources_HasVideoAndAudioStreams()
    {
        var config = Plugin.Instance.Configuration;
        config.BaseUrl = "http://test.example.com";
        config.Username = "testuser";
        config.Password = "testpass";

        var result = await _tunerHost.GetChannelStreamMediaSources("xtream_100", CancellationToken.None);

        var source = result[0];
        source.MediaStreams.Should().HaveCount(2);

        var video = source.MediaStreams.First(s => s.Type == MediaStreamType.Video);
        video.Codec.Should().Be("h264");
        video.Width.Should().Be(1920);
        video.Height.Should().Be(1080);
        video.IsDefault.Should().BeTrue();

        var audio = source.MediaStreams.First(s => s.Type == MediaStreamType.Audio);
        audio.Codec.Should().Be("aac");
        audio.Channels.Should().Be(2);
        audio.SampleRate.Should().Be(44100);
        audio.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task GetChannelStreamMediaSources_UseTsFormat_WhenConfigured()
    {
        var config = Plugin.Instance.Configuration;
        config.BaseUrl = "http://test.example.com";
        config.Username = "testuser";
        config.Password = "testpass";
        config.LiveTvOutputFormat = "ts";

        var result = await _tunerHost.GetChannelStreamMediaSources("xtream_100", CancellationToken.None);

        result[0].Path.Should().Contain("/live/testuser/testpass/100.ts");
    }

    #endregion

    #region DiscoverDevices

    [Fact]
    public async Task DiscoverDevices_ReturnsEmptyList()
    {
        var result = await _tunerHost.DiscoverDevices(1000, CancellationToken.None);

        result.Should().BeEmpty();
    }

    #endregion
}
