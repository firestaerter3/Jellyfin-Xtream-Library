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
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Custom ITunerHost that provides Xtream live TV channels to Jellyfin
/// with pre-populated media info to skip FFmpeg probing for faster channel switching.
/// </summary>
public class XtreamTunerHost : ITunerHost
{
    /// <summary>
    /// The tuner type identifier used for registration and channel ID prefixing.
    /// </summary>
    internal const string TunerType = "xtream-library";

    private const string ChannelIdPrefix = "xtream_";

    private readonly LiveTvService _liveTvService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<XtreamTunerHost> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="XtreamTunerHost"/> class.
    /// </summary>
    /// <param name="liveTvService">The Live TV service for channel data.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger instance.</param>
    public XtreamTunerHost(
        LiveTvService liveTvService,
        IHttpClientFactory httpClientFactory,
        ILogger<XtreamTunerHost> logger)
    {
        _liveTvService = liveTvService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Xtream Library";

    /// <inheritdoc />
    public string Type => TunerType;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;

        if (!config.EnableLiveTv || !config.EnableNativeTuner)
        {
            return new List<ChannelInfo>();
        }

        _logger.LogDebug("Fetching channels for Xtream native tuner");

        var channels = await _liveTvService.GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);

        return channels.Select(channel =>
        {
            var cleanName = ChannelNameCleaner.CleanChannelName(
                channel.Name,
                config.ChannelRemoveTerms,
                config.EnableChannelNameCleaning);

            return new ChannelInfo
            {
                Id = ChannelIdPrefix + channel.StreamId.ToString(CultureInfo.InvariantCulture),
                Name = cleanName,
                Number = channel.Num.ToString(CultureInfo.InvariantCulture),
                ImageUrl = string.IsNullOrEmpty(channel.StreamIcon) ? null : channel.StreamIcon,
                ChannelType = ChannelType.TV,
                TunerHostId = Type,
            };
        }).ToList();
    }

    /// <inheritdoc />
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        if (!TryParseStreamId(channelId, out var streamId))
        {
            return Task.FromResult(new List<MediaSourceInfo>());
        }

        var config = Plugin.Instance.Configuration;
        var streamUrl = BuildStreamUrl(config, streamId);

        var mediaSource = CreateMediaSourceInfo(streamId, streamUrl);

        return Task.FromResult(new List<MediaSourceInfo> { mediaSource });
    }

    /// <inheritdoc />
    public Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        if (!TryParseStreamId(channelId, out var parsedStreamId))
        {
            throw new ArgumentException($"Channel ID '{channelId}' is not owned by this tuner", nameof(channelId));
        }

        var config = Plugin.Instance.Configuration;
        var streamUrl = BuildStreamUrl(config, parsedStreamId);

        var mediaSource = CreateMediaSourceInfo(parsedStreamId, streamUrl);

        var httpClient = _httpClientFactory.CreateClient();
        ILiveStream liveStream = new XtreamLiveStream(mediaSource, httpClient);

        _logger.LogInformation("Opening live stream for channel {ChannelId} (stream {StreamId})", channelId, parsedStreamId);

        return Task.FromResult(liveStream);
    }

    /// <inheritdoc />
    public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken cancellationToken)
    {
        return Task.FromResult(new List<TunerHostInfo>());
    }

    private static bool TryParseStreamId(string channelId, out int streamId)
    {
        streamId = 0;

        if (!channelId.StartsWith(ChannelIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(channelId.AsSpan(ChannelIdPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out streamId);
    }

    private static string BuildStreamUrl(PluginConfiguration config, int streamId)
    {
        var extension = string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase) ? "ts" : "m3u8";
        return string.Create(CultureInfo.InvariantCulture, $"{config.BaseUrl}/live/{config.Username}/{config.Password}/{streamId}.{extension}");
    }

    private static MediaSourceInfo CreateMediaSourceInfo(int streamId, string streamUrl)
    {
        var sourceId = "xtream_live_" + streamId.ToString(CultureInfo.InvariantCulture);

        return new MediaSourceInfo
        {
            Id = sourceId,
            Path = streamUrl,
            Protocol = MediaProtocol.Http,
            Container = "mpegts",
            SupportsProbing = false,
            IsRemote = true,
            IsInfiniteStream = true,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            AnalyzeDurationMs = 500,
            IgnoreDts = true,
            GenPtsInput = true,
            MediaStreams = new List<MediaStream>
            {
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Codec = "h264",
                    Index = 0,
                    Width = 1920,
                    Height = 1080,
                    IsDefault = true,
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Codec = "aac",
                    Index = 1,
                    Channels = 2,
                    SampleRate = 44100,
                    IsDefault = true,
                },
            },
        };
    }
}
