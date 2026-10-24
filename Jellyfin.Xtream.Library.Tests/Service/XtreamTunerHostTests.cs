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

#pragma warning disable CS0618 // Legacy config fields used in test setup; Providers[0] is populated separately
using System.IO;
using System.Linq;
using System.Net.Http;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Tests.Helpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

[Collection("PluginSingletonTests")]
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

        // Seed Providers[0] so GetCreds(0) works in tests that enable Live TV
        Plugin.Instance.Configuration.Providers.Add(TestDataBuilder.CreateProviderConfig());

        _mockClient = new Mock<IXtreamClient>();
        var serverAppPaths = new Mock<IServerApplicationPaths>();
        serverAppPaths.Setup(p => p.DataPath).Returns(tempPath);
        var appHostMock = new Mock<IServerApplicationHost>();
        appHostMock.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");
        _liveTvService = new LiveTvService(_mockClient.Object, new Mock<IDispatcharrClient>().Object, serverAppPaths.Object, appHostMock.Object, NullLogger<LiveTvService>.Instance);
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
            new LiveStreamInfo
            {
                StreamId = 300,
                Name = "Fox",
                Num = 3,
                StreamIcon = "/share/logos/fox.png",
            },
        };

        _mockClient
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channels);

        var result = await _tunerHost.GetChannels(false, CancellationToken.None);

        result.Should().HaveCount(3);

        result[0].Id.Should().Be("xtream_100");
        result[0].Name.Should().Be("BBC One");
        result[0].Number.Should().Be("1");
        result[0].ImageUrl.Should().Be("http://logo.com/bbc.png");

        result[1].Id.Should().Be("xtream_200");
        result[1].Name.Should().Be("CNN");
        result[1].Number.Should().Be("2");
        result[1].ImageUrl.Should().BeNull();

        // Local-path logo is rewritten to the ChannelLogo proxy endpoint (issue #53).
        result[2].Id.Should().Be("xtream_300");
        result[2].ImageUrl.Should().Be("http://127.0.0.1:8096/XtreamLibrary/ChannelLogo/300");
    }

    // GitHub #86. The channel number is both the guide's sort key and the key the tuner resolves
    // hdhr_<number> back to a stream with, so a provider that numbers from 1 inside every category
    // interleaves the guide and, worse, makes one of two same-numbered channels unreachable.
    [Fact]
    public async Task GetChannels_PrefixesTheCategoryId_WhenNumberByCategoryIsEnabled()
    {
        var config = Plugin.Instance.Configuration;
        config.EnableLiveTv = true;
        config.EnableNativeTuner = true;
        config.EnableChannelNameCleaning = false;
        config.LiveTvNumberByCategory = true;

        _mockClient
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveStreamInfo>
            {
                new() { StreamId = 100, Name = "Sports One", Num = 1, CategoryId = 5 },
                new() { StreamId = 200, Name = "News One", Num = 1, CategoryId = 6 },
                new() { StreamId = 300, Name = "Sports Two", Num = 2, CategoryId = 5 },
            });

        var result = await _tunerHost.GetChannels(false, CancellationToken.None);

        result.Select(c => c.Number).Should().Equal("5001", "6001", "5002");
        result.Select(c => c.Number).Should().OnlyHaveUniqueItems(
            "a duplicate number silently makes one of the two channels unreachable");
    }

    [Fact]
    public async Task GetChannels_KeepsProviderNumbers_WhenNumberByCategoryIsOff()
    {
        var config = Plugin.Instance.Configuration;
        config.EnableLiveTv = true;
        config.EnableNativeTuner = true;
        config.EnableChannelNameCleaning = false;
        config.LiveTvNumberByCategory = false;

        _mockClient
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveStreamInfo>
            {
                new() { StreamId = 100, Name = "Sports One", Num = 1, CategoryId = 5 },
                new() { StreamId = 200, Name = "News One", Num = 1, CategoryId = 6 },
            });

        var result = await _tunerHost.GetChannels(false, CancellationToken.None);

        // Unchanged behaviour for anyone who does not turn the option on, collision included.
        result.Select(c => c.Number).Should().Equal("1", "1");
    }

    // BUG-011: native tuner channels were ungrouped because ChannelGroup was never set.
    [Fact]
    public async Task GetChannels_SetsChannelGroup_FromCategoryName()
    {
        var config = Plugin.Instance.Configuration;
        config.EnableLiveTv = true;
        config.EnableNativeTuner = true;
        config.EnableChannelNameCleaning = false;

        var channels = new List<LiveStreamInfo>
        {
            new LiveStreamInfo { StreamId = 100, Name = "BBC One", Num = 1, CategoryId = 10 },
            new LiveStreamInfo { StreamId = 200, Name = "CNN", Num = 2, CategoryId = 99 },
            new LiveStreamInfo { StreamId = 300, Name = "Fox", Num = 3, CategoryId = null },
        };

        _mockClient
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(channels);
        _mockClient
            .Setup(c => c.GetLiveCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 10, CategoryName = "Sports" } });

        var result = await _tunerHost.GetChannels(false, CancellationToken.None);

        result[0].ChannelGroup.Should().Be("Sports"); // known category
        result[1].ChannelGroup.Should().BeNull();      // category id not in map
        result[2].ChannelGroup.Should().BeNull();      // null category id
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
        source.SupportsDirectPlay.Should().BeFalse("no stats, nothing for a client to decide on");
        source.SupportsDirectStream.Should().BeFalse("a live direct stream is an endless mp4 the iOS player rejects");
        source.SupportsTranscoding.Should().BeTrue();
        source.AnalyzeDurationMs.Should().Be(500);
        source.IgnoreDts.Should().BeTrue();
        source.GenPtsInput.Should().BeTrue();
        source.Container.Should().Be("hls");
        source.Path.Should().Contain("/live/testuser/testpass/100.m3u8");
    }

    // GitHub #107. The placeholders that used to live here carried index 0 and 1, which is exactly
    // what makes Jellyfin skip the probe (MediaSourceManager:522), so the codec stayed unknown and
    // clients rejected streams they could have played.

    [Fact]
    public async Task GetChannelStreamMediaSources_WithoutStats_LeavesMediaStreamsEmptyAndRequiresOpening()
    {
        var config = Plugin.Instance.Configuration;
        config.BaseUrl = "http://test.example.com";
        config.Username = "user";
        config.Password = "pass";

        var result = await _tunerHost.GetChannelStreamMediaSources("xtream_100", CancellationToken.None);

        var source = result[0];

        // Empty, not "some harmless default": any entry with an index other than -1 suppresses the
        // probe. Jellyfin fills in its own index -1 entries afterwards.
        source.MediaStreams.Should().BeEmpty();
        source.SupportsProbing.Should().BeTrue();

        // Without this, PlaybackInfo never opens the source, so the probe is unreachable whatever
        // SupportsProbing says.
        source.RequiresOpening.Should().BeTrue();
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

        // Opened anyway: opening connects nothing, and an unopened source made Jellyfin wait
        // about 1.7 s before starting ffmpeg (homelab-ops #550).
        source.RequiresOpening.Should().BeTrue();
        source.AnalyzeDurationMs.Should().Be(0);
        source.SupportsDirectPlay.Should().BeFalse("a direct-play client would open the provider URL, login included, itself");

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
        audio.Index.Should().Be(-1);
        audio.Codec.Should().BeNull();
    }

    [Fact]
    public async Task GetChannelStreamMediaSources_WithStatsButHls_StillProbes()
    {
        // A source that skips the probe keeps the container named by the plugin. Dispatcharr serves
        // MPEG-TS whatever the extension, so with HLS selected the stats must not be used, or
        // Jellyfin is told "hls" for a TS stream (homelab-ops #550).
        var config = Plugin.Instance.Configuration;
        config.EnableLiveTv = true;
        config.EnableNativeTuner = true;
        config.EnableChannelNameCleaning = false;
        config.LiveTvOutputFormat = "m3u8";

        _mockClient
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveStreamInfo>
            {
                new() { StreamId = 100, Name = "Test Channel", Num = 1, StreamStats = new StreamStatsInfo { VideoCodec = "h264" } },
            });

        await _tunerHost.GetChannels(false, CancellationToken.None);
        var source = (await _tunerHost.GetChannelStreamMediaSources("xtream_100", CancellationToken.None))[0];

        source.SupportsProbing.Should().BeTrue();
        source.RequiresOpening.Should().BeTrue();
        source.MediaStreams.Should().BeNullOrEmpty();
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

    // BUG-008: when the user configured their provider via the multi-provider UI introduced in
    // v1.32 (and never touched the legacy fields), the tuner used to emit "/live///{streamId}.ts"
    // because BuildStreamUrl read config.BaseUrl/Username/Password directly. Pin the multi-provider
    // path here so a regression would surface immediately.
    [Fact]
    public async Task GetChannelStreamMediaSources_MultiProviderOnly_UsesProviderCredentials()
    {
        var config = Plugin.Instance.Configuration;
        config.BaseUrl = string.Empty;
        config.Username = string.Empty;
        config.Password = string.Empty;
        config.LiveTvOutputFormat = "ts";

        // Replace the default seeded provider with one whose values do not match any legacy field.
        config.Providers.Clear();
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://multi.example.com:5656",
            Username = "multiuser",
            Password = "multipass",
            IsEnabled = true,
        });

        var result = await _tunerHost.GetChannelStreamMediaSources("xtream_100", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Path.Should().Be("http://multi.example.com:5656/live/multiuser/multipass/100.ts");
        result[0].Path.Should().NotContain("///");
    }

    [Fact]
    public async Task GetChannelStreamMediaSources_AdditionalProviderChannel_UsesProviderCredentials()
    {
        var config = Plugin.Instance.Configuration;
        config.BaseUrl = string.Empty;
        config.Username = string.Empty;
        config.Password = string.Empty;
        config.LiveTvOutputFormat = "ts";
        config.Providers.Clear();
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://provider-a.example.com",
            Username = "user-a",
            Password = "pass-a",
            IsEnabled = true,
        });
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://provider-b.example.com",
            Username = "user-b",
            Password = "pass-b",
            IsEnabled = true,
        });

        var result = await _tunerHost.GetChannelStreamMediaSources("xtream_1_100", CancellationToken.None);

        result.Should().HaveCount(1);
        result[0].Path.Should().Be("http://provider-b.example.com/live/user-b/pass-b/100.ts");
    }

    [Fact]
    public async Task GetChannels_DuplicateStreamIdsAcrossProviders_ReturnsUniqueChannelIds()
    {
        var config = Plugin.Instance.Configuration;
        config.EnableLiveTv = true;
        config.EnableNativeTuner = true;
        config.EnableChannelNameCleaning = false;
        config.Providers.Clear();
        config.Providers.Add(new ProviderConfig { BaseUrl = "http://provider-a.example.com", Username = "user-a", Password = "pass-a", IsEnabled = true });
        config.Providers.Add(new ProviderConfig { BaseUrl = "http://provider-b.example.com", Username = "user-b", Password = "pass-b", IsEnabled = true });

        _mockClient
            .SetupSequence(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveStreamInfo> { new() { StreamId = 100, Name = "Provider A", Num = 1 } })
            .ReturnsAsync(new List<LiveStreamInfo> { new() { StreamId = 100, Name = "Provider B", Num = 2 } });

        var result = await _tunerHost.GetChannels(false, CancellationToken.None);

        result.Select(c => c.Id).Should().Equal("xtream_100", "xtream_1_100");
    }

    [Fact]
    public async Task GetChannelStream_WithHdhrPrefix_UsesMappedProviderCredentials()
    {
        var config = Plugin.Instance.Configuration;
        config.EnableLiveTv = true;
        config.EnableNativeTuner = true;
        config.EnableChannelNameCleaning = false;
        config.LiveTvOutputFormat = "m3u8";
        config.Providers.Clear();
        config.Providers.Add(new ProviderConfig { BaseUrl = "http://provider-a.example.com", Username = "user-a", Password = "pass-a", IsEnabled = true });
        config.Providers.Add(new ProviderConfig { BaseUrl = "http://provider-b.example.com", Username = "user-b", Password = "pass-b", IsEnabled = true });

        _mockClient
            .SetupSequence(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveStreamInfo> { new() { StreamId = 100, Name = "Provider A", Num = 1 } })
            .ReturnsAsync(new List<LiveStreamInfo> { new() { StreamId = 200, Name = "Provider B", Num = 2 } });

        await _tunerHost.GetChannels(false, CancellationToken.None);
        _mockHttpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient());

        var result = await _tunerHost.GetChannelStream(
            "hdhr_2",
            string.Empty,
            new List<MediaBrowser.Controller.Library.ILiveStream>(),
            CancellationToken.None);

        result.MediaSource.Path.Should().Be("http://provider-b.example.com/live/user-b/pass-b/200.m3u8");
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

    [Fact]
    public void DispatcharrStats_FillChannelsWithoutStats_AndKeepTheProvidersOwn()
    {
        var listing = new List<DispatcharrChannel>
        {
            new()
            {
                Id = 100, Uuid = "u100", Streams = [new DispatcharrChannelSource { StreamId = 900, StreamStatsJson = Newtonsoft.Json.Linq.JToken.Parse("""{"video_codec":"h264"}""") }],
            },
            new()
            {
                Id = 200, Uuid = "u200", Streams = [new DispatcharrChannelSource { StreamId = 901, StreamStatsJson = Newtonsoft.Json.Linq.JToken.Parse("""{"video_codec":"hevc"}""") }],
            },
        };
        var channels = new List<LiveStreamInfo>
        {
            new() { StreamId = 100 },
            new() { StreamId = 200, StreamStats = new StreamStatsInfo { VideoCodec = "mpeg2video" } },
            new() { StreamId = 300 },
        };

        _liveTvService.StampDispatcharrStats(listing, channels);

        channels[0].StreamStats!.VideoCodec.Should().Be("h264");
        channels[1].StreamStats!.VideoCodec.Should().Be("mpeg2video");
        channels[2].StreamStats.Should().BeNull();
    }

    #region Measured tuning (homelab-ops #550)

    private static StreamStatsInfo Stats(string audioJson) =>
        Newtonsoft.Json.JsonConvert.DeserializeObject<StreamStatsInfo>(
            "{\"video_codec\":\"h264\",\"resolution\":\"1920x1080\"," + audioJson + "}")!;

    [Fact]
    public void AnalyzeDuration_ComesFromTheSetting_OnlyForSourcesWithStats()
    {
        var config = new PluginConfiguration { LiveTvAnalyzeDurationMs = 3000 };
        XtreamTunerHost.CreateMediaSourceInfo("xtream_1", "http://x/1.ts", Stats("\"audio_codec\":\"aac\""), false, config, NullLogger.Instance)
            .AnalyzeDurationMs.Should().Be(3000);
        XtreamTunerHost.CreateMediaSourceInfo("xtream_1", "http://x/1.ts", null, false, config, NullLogger.Instance)
            .AnalyzeDurationMs.Should().Be(500);
        XtreamTunerHost.CreateMediaSourceInfo("xtream_1", "http://x/1.ts", Stats("\"audio_codec\":\"aac\""), false, new PluginConfiguration(), NullLogger.Instance)
            .AnalyzeDurationMs.Should().Be(0);
    }

    [Fact]
    public void ATransportStreamSourceIsNamedTs_SoJellyfinFixesCopiedAacForFmp4()
    {
        // EncodingHelper adds -bsf:a aac_adtstoasc only for a source container of ts, aac or hls.
        XtreamTunerHost.CreateMediaSourceInfo("xtream_1", "http://x/1.ts", Stats("\"audio_codec\":\"aac\""), false, new PluginConfiguration(), NullLogger.Instance)
            .Container.Should().Be("ts");
    }

    [Theory]
    [InlineData("\"audio_codec\":\"ac3\",\"audio_channels\":2,\"audio_sample_rate\":48000,\"audio_bitrate\":448", "ac3", 2, 48000)]
    [InlineData("\"audio_codec\":\"aac\",\"audio_channels\":\"6\",\"audio_sample_rate\":44100.0", "aac", 6, 44100)]
    [InlineData("\"audio_codec\":\"mp2\",\"audio_channels\":\"stereo\"", "mp2", null, null)]
    public void CopyAudio_DeclaresTheCodecFromTheStats(string audioJson, string codec, int? channels, int? sampleRate)
    {
        var config = new PluginConfiguration { LiveTvCopyAudio = true };
        var audio = XtreamTunerHost.CreateMediaSourceInfo("xtream_1", "http://x/1.ts", Stats(audioJson), false, config, NullLogger.Instance)
            .MediaStreams.Single(m => m.Type == MediaStreamType.Audio);

        audio.Codec.Should().Be(codec);
        audio.Channels.Should().Be(channels);
        audio.SampleRate.Should().Be(sampleRate);
        audio.Index.Should().Be(-1);
    }

    [Fact]
    public void CopyAudio_LeavesUnknownCodecsAndTheDefaultToJellyfin()
    {
        var unknown = XtreamTunerHost.CreateMediaSourceInfo("xtream_1", "http://x/1.ts", Stats("\"audio_codec\":\"opus\""),
            false, new PluginConfiguration { LiveTvCopyAudio = true }, NullLogger.Instance);
        unknown.MediaStreams.Single(m => m.Type == MediaStreamType.Audio).Codec.Should().BeNull();

        var off = XtreamTunerHost.CreateMediaSourceInfo("xtream_1", "http://x/1.ts", Stats("\"audio_codec\":\"aac\""),
            false, new PluginConfiguration(), NullLogger.Instance);
        off.MediaStreams.Single(m => m.Type == MediaStreamType.Audio).Codec.Should().BeNull();
    }

    #endregion

    #region Startup warm-up (homelab-ops #550)

    [Fact]
    public async Task Warmup_WithNativeTunerOn_LoadsTheChannelsBeforeFirstUse()
    {
        var config = Plugin.Instance.Configuration;
        config.EnableLiveTv = true;
        config.EnableNativeTuner = true;
        config.EnableChannelNameCleaning = false;
        _mockClient
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveStreamInfo> { new() { StreamId = 100, Name = "BBC One", Num = 1 } });

        using var warmup = new NativeTunerWarmup([_tunerHost], NullLogger<NativeTunerWarmup>.Instance, TimeSpan.Zero);
        await warmup.StartAsync(CancellationToken.None);
        await warmup.Running!;

        _mockClient.Verify(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()), Times.Once);

        // The first real request is served from what the warm-up loaded.
        var channels = await _tunerHost.GetChannels(true, CancellationToken.None);
        channels.Should().ContainSingle();
        _mockClient.Verify(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Warmup_WithNativeTunerOff_FetchesNothing()
    {
        var config = Plugin.Instance.Configuration;
        config.EnableLiveTv = true;
        config.EnableNativeTuner = false;

        using var warmup = new NativeTunerWarmup([_tunerHost], NullLogger<NativeTunerWarmup>.Instance, TimeSpan.Zero);
        await warmup.StartAsync(CancellationToken.None);
        await warmup.Running!;

        _mockClient.Verify(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Warmup_WhenTheProviderFails_DoesNotThrow()
    {
        var config = Plugin.Instance.Configuration;
        config.EnableLiveTv = true;
        config.EnableNativeTuner = true;
        _mockClient
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("provider down"));

        using var warmup = new NativeTunerWarmup([_tunerHost], NullLogger<NativeTunerWarmup>.Instance, TimeSpan.Zero);
        await warmup.StartAsync(CancellationToken.None);
        var act = async () => await warmup.Running!;

        await act.Should().NotThrowAsync();
    }

    #endregion

    [Fact]
    public void BuildProxyStreamUrl_WithUuid_ReturnsDispatcharrUrlWithoutCredentials()
    {
        // Clients such as Moonfin open the media source Path themselves for live channels, so it
        // must not carry the provider login (homelab-ops #550).
        var config = new PluginConfiguration();
        config.Providers.Add(new ProviderConfig { BaseUrl = "http://xtream.example.com:9191/", Username = "user-a", IsEnabled = true });
        var channel = new LiveStreamInfo { StreamId = 100, DispatcharrUuid = "086aab97-3c36-41f0-a417-b97f39a69f0f" };

        var url = XtreamTunerHost.BuildProxyStreamUrl(config, channel);

        url.Should().Be("http://xtream.example.com:9191/proxy/ts/stream/086aab97-3c36-41f0-a417-b97f39a69f0f");
        url.Should().NotContain("user-a");
    }

    [Fact]
    public void BuildProxyStreamUrl_UsesTheDispatcharrBaseUrl_WhenSet()
    {
        var config = new PluginConfiguration();
        config.Providers.Add(new ProviderConfig
        {
            BaseUrl = "http://xtream.example.com",
            DispatcharrBaseUrl = "http://dispatcharr.example.com:9191",
            IsEnabled = true,
        });
        var channel = new LiveStreamInfo { StreamId = 100, DispatcharrUuid = "abc" };

        XtreamTunerHost.BuildProxyStreamUrl(config, channel)
            .Should().Be("http://dispatcharr.example.com:9191/proxy/ts/stream/abc");
    }

    [Fact]
    public async Task GetChannelStreamMediaSources_FollowsAChangedDispatcharrBaseUrl_WithoutReloadingChannels()
    {
        // Only the uuid is cached, so moving Dispatcharr takes effect at the next play rather than
        // at the next channel reload (PR #167 review).
        var config = Plugin.Instance.Configuration;
        config.EnableLiveTv = true;
        config.EnableNativeTuner = true;
        config.Providers[0].DispatcharrBaseUrl = "http://old-dispatcharr:9191";
        _mockClient
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveStreamInfo> { new() { StreamId = 100, Name = "Test Channel", Num = 1, DispatcharrUuid = "abc" } });
        await _tunerHost.GetChannels(false, CancellationToken.None);

        config.Providers[0].DispatcharrBaseUrl = "http://new-dispatcharr:9191";
        var result = await _tunerHost.GetChannelStreamMediaSources("xtream_100", CancellationToken.None);

        result[0].Path.Should().Be("http://new-dispatcharr:9191/proxy/ts/stream/abc");
    }

    [Fact]
    public void BuildProxyStreamUrl_WithoutUuid_ReturnsNull()
    {
        var config = new PluginConfiguration();
        config.Providers.Add(new ProviderConfig { BaseUrl = "http://xtream.example.com", IsEnabled = true });

        XtreamTunerHost.BuildProxyStreamUrl(config, new LiveStreamInfo { StreamId = 100 }).Should().BeNull();
    }
}
