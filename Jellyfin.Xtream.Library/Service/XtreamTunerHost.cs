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
using Jellyfin.Xtream.Library.Client.Models;
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

    internal const string ChannelIdPrefix = "xtream_";
    private const string JellyfinTunerPrefix = "hdhr_";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly LiveTvService _liveTvService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<XtreamTunerHost> _logger;
    private volatile Dictionary<string, int> _channelNumberToStreamId = new();
    private volatile Dictionary<int, StreamStatsInfo> _streamStats = new();
    private List<ChannelInfo>? _cachedChannels;
    private DateTime _cacheTime = DateTime.MinValue;

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

        // Return cached channels if available and not expired
        if (enableCache && _cachedChannels != null && DateTime.UtcNow - _cacheTime < CacheDuration)
        {
            _logger.LogDebug("Returning cached channel list ({Count} channels)", _cachedChannels.Count);
            return _cachedChannels;
        }

        _logger.LogInformation("Fetching channels from Xtream API for native tuner");

        var channels = await _liveTvService.GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);

        var newMap = new Dictionary<string, int>(channels.Count);
        var newStats = new Dictionary<int, StreamStatsInfo>(channels.Count);
        int statsCount = 0;

        var result = channels.Select(channel =>
        {
            var channelNumber = channel.Num.ToString(CultureInfo.InvariantCulture);

            if (!newMap.TryAdd(channelNumber, channel.StreamId))
            {
                _logger.LogWarning(
                    "Duplicate channel number {Num} — stream {OldStreamId} overwritten by {NewStreamId}",
                    channelNumber,
                    newMap[channelNumber],
                    channel.StreamId);
                newMap[channelNumber] = channel.StreamId;
            }

            if (channel.StreamStats != null)
            {
                newStats[channel.StreamId] = channel.StreamStats;
                statsCount++;
            }

            var cleanName = ChannelNameCleaner.CleanChannelName(
                channel.Name,
                config.ChannelRemoveTerms,
                config.EnableChannelNameCleaning);

            return new ChannelInfo
            {
                Id = ChannelIdPrefix + channel.StreamId.ToString(CultureInfo.InvariantCulture),
                Name = cleanName,
                Number = channelNumber,
                ImageUrl = string.IsNullOrEmpty(channel.StreamIcon) ? null : channel.StreamIcon,
                ChannelType = ChannelType.TV,
                TunerHostId = Type,
            };
        }).ToList();

        _channelNumberToStreamId = newMap;
        _streamStats = newStats;
        _cachedChannels = result;
        _cacheTime = DateTime.UtcNow;
        _logger.LogInformation("Channel list cached with {Count} channels ({StatsCount} with stream stats)", result.Count, statsCount);

        return result;
    }

    /// <inheritdoc />
    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        await EnsureChannelsLoadedAsync(cancellationToken).ConfigureAwait(false);

        if (!TryParseStreamId(channelId, out var streamId))
        {
            return new List<MediaSourceInfo>();
        }

        var config = Plugin.Instance.Configuration;
        var streamUrl = BuildStreamUrl(config, streamId);
        _streamStats.TryGetValue(streamId, out var stats);

        var mediaSource = CreateMediaSourceInfo(streamId, streamUrl, stats, _logger);

        return new List<MediaSourceInfo> { mediaSource };
    }

    /// <inheritdoc />
    public async Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        await EnsureChannelsLoadedAsync(cancellationToken).ConfigureAwait(false);

        if (!TryParseStreamId(channelId, out var parsedStreamId))
        {
            throw new System.IO.FileNotFoundException($"Channel {channelId} not found in Xtream tuner");
        }

        var config = Plugin.Instance.Configuration;
        var streamUrl = BuildStreamUrl(config, parsedStreamId);
        _streamStats.TryGetValue(parsedStreamId, out var stats);

        var mediaSource = CreateMediaSourceInfo(parsedStreamId, streamUrl, stats, _logger);

        var httpClient = _httpClientFactory.CreateClient();
        ILiveStream liveStream = new XtreamLiveStream(mediaSource, httpClient);

        _logger.LogInformation("Opening live stream for channel {ChannelId} (stream {StreamId})", channelId, parsedStreamId);

        return liveStream;
    }

    /// <inheritdoc />
    public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken cancellationToken)
    {
        return Task.FromResult(new List<TunerHostInfo>());
    }

    private async Task EnsureChannelsLoadedAsync(CancellationToken cancellationToken)
    {
        if (_cachedChannels == null)
        {
            _logger.LogInformation("Channel cache empty (first use after restart), fetching channels from Dispatcharr");
            await GetChannels(true, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryParseStreamId(string channelId, out int streamId)
    {
        streamId = 0;

        // Our own prefix: xtream_<streamId>
        if (channelId.StartsWith(ChannelIdPrefix, StringComparison.Ordinal))
        {
            return int.TryParse(channelId.AsSpan(ChannelIdPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out streamId);
        }

        // Jellyfin remaps tuner channel IDs to hdhr_<channelNumber>
        if (channelId.StartsWith(JellyfinTunerPrefix, StringComparison.Ordinal))
        {
            var channelNumber = channelId.Substring(JellyfinTunerPrefix.Length);
            var currentMap = _channelNumberToStreamId;

            if (currentMap.TryGetValue(channelNumber, out streamId))
            {
                _logger.LogDebug("Resolved {ChannelId} via hdhr_ prefix to stream {StreamId}", channelId, streamId);
                return true;
            }

            if (currentMap.Count == 0)
            {
                _logger.LogWarning(
                    "Channel {ChannelId} lookup failed — channel mapping is empty (GetChannels has not run yet). Guide refresh will populate it.",
                    channelId);
            }
            else
            {
                _logger.LogWarning(
                    "Channel {ChannelId} not found in mapping ({MapCount} entries). Channel number {ChannelNumber} may not exist.",
                    channelId,
                    currentMap.Count,
                    channelNumber);
            }

            return false;
        }

        return false;
    }

    private static string BuildStreamUrl(PluginConfiguration config, int streamId)
    {
        var extension = string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase) ? "ts" : "m3u8";
        return string.Create(CultureInfo.InvariantCulture, $"{config.BaseUrl}/live/{config.Username}/{config.Password}/{streamId}.{extension}");
    }

    private static MediaSourceInfo CreateMediaSourceInfo(int streamId, string streamUrl, StreamStatsInfo? stats, ILogger logger)
    {
        var sourceId = "xtream_live_" + streamId.ToString(CultureInfo.InvariantCulture);
        bool hasStats = stats?.VideoCodec != null;

        var mediaSource = new MediaSourceInfo
        {
            Id = sourceId,
            Path = streamUrl,
            Protocol = MediaProtocol.Http,
            Container = "mpegts",
            SupportsProbing = !hasStats,
            IsRemote = true,
            IsInfiniteStream = true,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            AnalyzeDurationMs = hasStats ? 0 : 500,
            IgnoreDts = true,
            GenPtsInput = true,
        };

        if (hasStats)
        {
            var mediaStreams = new List<MediaStream>();

            // Parse resolution (e.g. "1920x1080")
            int width = 0, height = 0;
            if (!string.IsNullOrEmpty(stats!.Resolution))
            {
                var parts = stats.Resolution.Split('x');
                if (parts.Length == 2)
                {
                    int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width);
                    int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height);
                }
            }

            // Map Dispatcharr codec names to Jellyfin codec names
            var videoCodec = MapVideoCodec(stats.VideoCodec!);

            var videoStream = new MediaStream
            {
                Type = MediaStreamType.Video,
                Index = 0,
                Codec = videoCodec,
                Width = width > 0 ? width : null,
                Height = height > 0 ? height : null,
                IsInterlaced = false,
                RealFrameRate = stats.SourceFps.HasValue ? (float)stats.SourceFps.Value : null,
                AverageFrameRate = stats.SourceFps.HasValue ? (float)stats.SourceFps.Value : null,
                BitRate = stats.Bitrate.HasValue ? stats.Bitrate.Value * 1000 : null,
            };
            mediaStreams.Add(videoStream);

            // Add audio stream without codec to ensure audio is included in output.
            // Omitting codec forces Jellyfin to transcode audio (fast) rather than copy,
            // which avoids the AAC ADTS→fMP4 issue (missing aac_adtstoasc BSF).
            mediaStreams.Add(new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = 1,
            });

            mediaSource.MediaStreams = mediaStreams;

            logger.LogDebug(
                "Stream {StreamId}: using stats — {VideoCodec} {Width}x{Height} @{Fps}fps, audio {AudioCodec}",
                streamId,
                videoCodec,
                width,
                height,
                stats.SourceFps,
                stats.AudioCodec ?? "unknown");
        }
        else
        {
            // No stats — provide defaults with IsInterlaced=false.
            // Codec is left null: Jellyfin will transcode video (no stream copy without known codec)
            // but without yadif deinterlacing, transcode runs at ~2.5x vs ~0.7x with yadif.
            // Audio stream with null codec ensures audio is included and transcoded.
            mediaSource.MediaStreams = new List<MediaStream>
            {
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    IsInterlaced = false,
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                },
            };
            logger.LogDebug("Stream {StreamId}: no stats available, will probe", streamId);
        }

        return mediaSource;
    }

    private static string MapVideoCodec(string dispatcharrCodec)
    {
        return dispatcharrCodec.ToUpperInvariant() switch
        {
            "H264" or "AVC" => "h264",
            "HEVC" or "H265" => "hevc",
            "MPEG2VIDEO" => "mpeg2video",
            _ => dispatcharrCodec.ToLowerInvariant(),
        };
    }
}
