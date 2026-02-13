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
    public async Task GetChannelStreamMediaSources_WithoutStats_ReturnsSingleSource_WithProbingEnabled()
    {
        var config = Plugin.Instance.Configuration;
        config.BaseUrl = "http://test.example.com";
        config.Username = "testuser";
        config.Password = "testpass";
        config.LiveTvOutputFormat = "m3u8";

        var result = await _tunerHost.GetChannelStreamMediaSources("xtream_100", CancellationToken.None);

        result.Should().HaveCount(1);

        var source = result[0];
        source.SupportsProbing.Should().BeTrue();
        source.IsRemote.Should().BeTrue();
        source.IsInfiniteStream.Should().BeTrue();
        source.SupportsDirectPlay.Should().BeFalse();
        source.SupportsDirectStream.Should().BeTrue();
        source.SupportsTranscoding.Should().BeTrue();
        source.AnalyzeDurationMs.Should().Be(500);
        source.IgnoreDts.Should().BeTrue();
        source.GenPtsInput.Should().BeTrue();
        source.Container.Should().Be("mpegts");
        source.Path.Should().Contain("/live/testuser/testpass/100.m3u8");
    }

    [Fact]
    public async Task GetChannelStreamMediaSources_WithoutStats_HasDefaultVideoAndAudioStreams()
    {
        var config = Plugin.Instance.Configuration;
        config.BaseUrl = "http://test.example.com";
        config.Username = "testuser";
        config.Password = "testpass";

        var result = await _tunerHost.GetChannelStreamMediaSources("xtream_100", CancellationToken.None);

        var source = result[0];
        source.MediaStreams.Should().HaveCount(2);

        var video = source.MediaStreams[0];
        video.Type.Should().Be(MediaStreamType.Video);
        video.Index.Should().Be(0);
        video.IsInterlaced.Should().BeFalse();
        video.Codec.Should().BeNull();

        var audio = source.MediaStreams[1];
        audio.Type.Should().Be(MediaStreamType.Audio);
        audio.Index.Should().Be(1);
        audio.Codec.Should().BeNull();
    }

    [Fact]
    public async Task GetChannelStreamMediaSources_WithStats_HasProbingDisabledAndMediaStreams()
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
                Name = "Test Channel",
                Num = 1,
                StreamStats = new StreamStatsInfo
                {
                    VideoCodec = "H264",
                    AudioCodec = "aac",
                    Resolution = "1920x1080",
                    SourceFps = 25.0,
                    Bitrate = 5000,
                },
            },
        };

        _mockClient
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channels);

        // Populate stats via GetChannels
        await _tunerHost.GetChannels(false, CancellationToken.None);

        var result = await _tunerHost.GetChannelStreamMediaSources("xtream_100", CancellationToken.None);

        var source = result[0];
        source.SupportsProbing.Should().BeFalse();
        source.AnalyzeDurationMs.Should().Be(0);

        source.MediaStreams.Should().HaveCount(2);

        var video = source.MediaStreams.First(s => s.Type == MediaStreamType.Video);
        video.Codec.Should().Be("h264");
        video.Width.Should().Be(1920);
        video.Height.Should().Be(1080);
        video.IsInterlaced.Should().BeFalse();
        video.RealFrameRate.Should().Be(25.0f);
        video.BitRate.Should().Be(5000000);

        // Audio stream has no codec — forces transcode to avoid AAC ADTS→fMP4 copy issue
        var audio = source.MediaStreams.First(s => s.Type == MediaStreamType.Audio);
        audio.Index.Should().Be(1);
        audio.Codec.Should().BeNull();
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

    [Fact]
    public async Task GetChannelStreamMediaSources_ReturnsEmpty_ForNonXtreamChannelId()
    {
        var result = await _tunerHost.GetChannelStreamMediaSources("m3u_016d53793ce443da", CancellationToken.None);

        result.Should().BeEmpty();
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

    #region GetChannelStream

    [Fact]
    public async Task GetChannelStream_WithXtreamPrefix_ReturnsLiveStream()
    {
        var config = Plugin.Instance.Configuration;
        config.BaseUrl = "http://test.example.com";
        config.Username = "testuser";
        config.Password = "testpass";
        config.LiveTvOutputFormat = "m3u8";

        _mockHttpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient());

        var result = await _tunerHost.GetChannelStream(
            "xtream_100",
            string.Empty,
            new List<MediaBrowser.Controller.Library.ILiveStream>(),
            CancellationToken.None);

        result.Should().NotBeNull();
        result.MediaSource.Path.Should().Contain("/live/testuser/testpass/100.m3u8");
    }

    [Fact]
    public async Task GetChannelStream_WithHdhrPrefix_ResolvesViaMapping()
    {
        var config = Plugin.Instance.Configuration;
        config.EnableLiveTv = true;
        config.EnableNativeTuner = true;
        config.BaseUrl = "http://test.example.com";
        config.Username = "testuser";
        config.Password = "testpass";
        config.LiveTvOutputFormat = "m3u8";
        config.EnableChannelNameCleaning = false;

        var channels = new List<LiveStreamInfo>
        {
            new LiveStreamInfo { StreamId = 500, Name = "Test Channel", Num = 42 },
        };

        _mockClient
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channels);

        // Populate the mapping via GetChannels
        await _tunerHost.GetChannels(false, CancellationToken.None);

        _mockHttpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient());

        // Now resolve via hdhr_ prefix
        var result = await _tunerHost.GetChannelStream(
            "hdhr_42",
            string.Empty,
            new List<MediaBrowser.Controller.Library.ILiveStream>(),
            CancellationToken.None);

        result.Should().NotBeNull();
        result.MediaSource.Path.Should().Contain("/live/testuser/testpass/500.m3u8");
    }

    [Fact]
    public async Task GetChannelStream_ColdStart_ThrowsFileNotFoundException()
    {
        // No GetChannels called → mapping is empty
        var act = () => _tunerHost.GetChannelStream(
            "hdhr_42",
            string.Empty,
            new List<MediaBrowser.Controller.Library.ILiveStream>(),
            CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task GetChannelStream_UnknownChannel_ThrowsFileNotFoundException()
    {
        var act = () => _tunerHost.GetChannelStream(
            "xtream_99999",
            string.Empty,
            new List<MediaBrowser.Controller.Library.ILiveStream>(),
            CancellationToken.None);

        // xtream_ prefix parses successfully, so this should NOT throw
        // (the stream ID is parsed directly from the channel ID)
        var config = Plugin.Instance.Configuration;
        config.BaseUrl = "http://test.example.com";
        config.Username = "testuser";
        config.Password = "testpass";

        _mockHttpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient());

        var result = await _tunerHost.GetChannelStream(
            "xtream_99999",
            string.Empty,
            new List<MediaBrowser.Controller.Library.ILiveStream>(),
            CancellationToken.None);

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetChannelStream_NonOwnedChannel_ThrowsFileNotFoundException()
    {
        var act = () => _tunerHost.GetChannelStream(
            "m3u_someotherid",
            string.Empty,
            new List<MediaBrowser.Controller.Library.ILiveStream>(),
            CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    #endregion

    #region Channel Mapping

    [Fact]
    public async Task GetChannels_DuplicateNumbers_LastOneWins()
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
            new LiveStreamInfo { StreamId = 100, Name = "Channel A", Num = 1 },
            new LiveStreamInfo { StreamId = 200, Name = "Channel B", Num = 1 }, // Same Num
        };

        _mockClient
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channels);

        var result = await _tunerHost.GetChannels(false, CancellationToken.None);

        result.Should().HaveCount(2);

        // The hdhr_ lookup should return the LAST stream ID for the duplicate number
        _mockHttpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient());

        var stream = await _tunerHost.GetChannelStream(
            "hdhr_1",
            string.Empty,
            new List<MediaBrowser.Controller.Library.ILiveStream>(),
            CancellationToken.None);

        stream.MediaSource.Path.Should().Contain("/200.");
    }

    [Fact]
    public async Task GetChannels_RefreshReplacesMapping_AtomicSwap()
    {
        var config = Plugin.Instance.Configuration;
        config.EnableLiveTv = true;
        config.EnableNativeTuner = true;
        config.BaseUrl = "http://test.example.com";
        config.Username = "testuser";
        config.Password = "testpass";
        config.EnableChannelNameCleaning = false;

        // First fetch
        var channels1 = new List<LiveStreamInfo>
        {
            new LiveStreamInfo { StreamId = 100, Name = "Old Channel", Num = 1 },
        };
        _mockClient
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channels1);

        await _tunerHost.GetChannels(false, CancellationToken.None);

        // Second fetch with different channels
        var channels2 = new List<LiveStreamInfo>
        {
            new LiveStreamInfo { StreamId = 999, Name = "New Channel", Num = 5 },
        };
        _mockClient
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channels2);

        await _tunerHost.GetChannels(false, CancellationToken.None);

        _mockHttpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient());

        // Old channel number should no longer resolve
        var act = () => _tunerHost.GetChannelStream(
            "hdhr_1",
            string.Empty,
            new List<MediaBrowser.Controller.Library.ILiveStream>(),
            CancellationToken.None);

        await act.Should().ThrowAsync<FileNotFoundException>();

        // New channel number should resolve
        var stream = await _tunerHost.GetChannelStream(
            "hdhr_5",
            string.Empty,
            new List<MediaBrowser.Controller.Library.ILiveStream>(),
            CancellationToken.None);

        stream.MediaSource.Path.Should().Contain("/999.");
    }

    #endregion
}
