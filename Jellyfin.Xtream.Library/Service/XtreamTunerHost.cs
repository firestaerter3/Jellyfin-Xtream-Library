// CS0618: Legacy PluginConfiguration fields still used here; Phase 5 migrates to GetLiveTvProvider().
#pragma warning disable CS0618

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
    private volatile Dictionary<string, string> _channelNumberToChannelId = new();
    private volatile Dictionary<string, StreamStatsInfo> _streamStats = new();
    private volatile Dictionary<string, string> _dispatcharrUuids = new();
    private List<ChannelInfo>? _cachedChannels;
    private DateTime _cacheTime = DateTime.MinValue;
    private bool _cachedNumberByCategory;

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
        // The numbering flag is part of the cache identity: flipping it changes every number, and
        // serving the previous list would leave the numbers and the resolver map disagreeing.
        if (enableCache
            && _cachedChannels != null
            && _cachedNumberByCategory == config.LiveTvNumberByCategory
            && DateTime.UtcNow - _cacheTime < CacheDuration)
        {
            _logger.LogDebug("Returning cached channel list ({Count} channels)", _cachedChannels.Count);
            return _cachedChannels;
        }

        _logger.LogInformation("Fetching channels from Xtream API for native tuner");

        var channels = await _liveTvService.GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
        var categoryNames = await _liveTvService.GetCategoryNameMapAsync(cancellationToken).ConfigureAwait(false);

        int stride = ChannelNumbering.ComputeStride(channels);
        var newMap = new Dictionary<string, string>(channels.Count);
        var newStats = new Dictionary<string, StreamStatsInfo>(channels.Count);
        var newUuids = new Dictionary<string, string>(channels.Count);
        int proxyCount = 0;
        int statsCount = 0;

        var result = new List<ChannelInfo>(channels.Count);
        foreach (var channel in channels)
        {
            var channelNumber = ChannelNumbering
                .Resolve(channel, stride, config.LiveTvNumberByCategory)
                .ToString(CultureInfo.InvariantCulture);
            var channelId = BuildChannelId(channel.ProviderIndex, channel.StreamId);

            if (!newMap.TryAdd(channelNumber, channelId))
            {
                _logger.LogWarning(
                    "Duplicate channel number {Num} - channel {OldChannelId} overwritten by {NewChannelId}",
                    channelNumber,
                    newMap[channelNumber],
                    channelId);
                newMap[channelNumber] = channelId;
            }

            if (channel.StreamStats != null)
            {
                newStats[channelId] = channel.StreamStats;
                statsCount++;
            }

            if (!string.IsNullOrEmpty(channel.DispatcharrUuid))
            {
                newUuids[channelId] = channel.DispatcharrUuid;
                if (BuildProxyStreamUrl(config, channel) != null)
                {
                    proxyCount++;
                }
            }

            var cleanName = ChannelNameCleaner.CleanChannelName(
                channel.Name,
                config.ChannelRemoveTerms,
                config.EnableChannelNameCleaning);

            result.Add(new ChannelInfo
            {
                Id = channelId,
                Name = cleanName,
                Number = channelNumber,
                ImageUrl = _liveTvService.ResolveChannelLogoUrl(channel.StreamIcon, channel.StreamId),
                // GitHub #112. Jellyfin keeps radio in its own section, so a station filed as TV
                // is not just mislabelled, it is in the wrong list entirely.
                ChannelType = LiveStreamKind.IsRadio(channel.StreamType) ? ChannelType.Radio : ChannelType.TV,
                TunerHostId = Type,
                ChannelGroup = channel.CategoryId is int catId && categoryNames.TryGetValue(catId, out var categoryName)
                    ? categoryName
                    : null,
                Tags = channel.Tags,
            });
        }

        _channelNumberToChannelId = newMap;
        _streamStats = newStats;
        _dispatcharrUuids = newUuids;
        _cachedChannels = result;
        _cacheTime = DateTime.UtcNow;
        _cachedNumberByCategory = config.LiveTvNumberByCategory;
        _logger.LogInformation(
            "Channel list cached with {Count} channels ({StatsCount} with stream stats, {ProxyCount} with a Dispatcharr URL without credentials)",
            result.Count,
            statsCount,
            proxyCount);

        return result;
    }

    /// <inheritdoc />
    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        await EnsureChannelsLoadedAsync(cancellationToken).ConfigureAwait(false);

        if (!TryParseChannelId(channelId, out var providerIndex, out var streamId))
        {
            return new List<MediaSourceInfo>();
        }

        var config = Plugin.Instance.Configuration;
        var canonicalChannelId = BuildChannelId(providerIndex, streamId);
        var (streamUrl, isHls) = ResolveStreamUrl(config, providerIndex, streamId, canonicalChannelId);
        _streamStats.TryGetValue(canonicalChannelId, out var stats);

        var mediaSource = CreateMediaSourceInfo(canonicalChannelId, streamUrl, stats, isHls, config, _logger);

        return new List<MediaSourceInfo> { mediaSource };
    }

    /// <inheritdoc />
    public async Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        await EnsureChannelsLoadedAsync(cancellationToken).ConfigureAwait(false);

        if (!TryParseChannelId(channelId, out var providerIndex, out var parsedStreamId))
        {
            throw new System.IO.FileNotFoundException($"Channel {channelId} not found in Xtream tuner");
        }

        var config = Plugin.Instance.Configuration;
        var canonicalChannelId = BuildChannelId(providerIndex, parsedStreamId);
        var (streamUrl, isHls) = ResolveStreamUrl(config, providerIndex, parsedStreamId, canonicalChannelId);
        _streamStats.TryGetValue(canonicalChannelId, out var stats);

        var mediaSource = CreateMediaSourceInfo(canonicalChannelId, streamUrl, stats, isHls, config, _logger);

        var httpClient = _httpClientFactory.CreateClient();
        ILiveStream liveStream = new XtreamLiveStream(mediaSource, httpClient, _logger);

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

    private bool TryParseChannelId(string channelId, out int providerIndex, out int streamId)
    {
        providerIndex = 0;
        streamId = 0;

        // Our own prefixes: xtream_<streamId> for provider 0, xtream_<providerIndex>_<streamId> for others.
        if (channelId.StartsWith(ChannelIdPrefix, StringComparison.Ordinal))
        {
            var idPart = channelId.AsSpan(ChannelIdPrefix.Length);
            var separator = idPart.IndexOf('_');
            if (separator < 0)
            {
                return int.TryParse(idPart, NumberStyles.None, CultureInfo.InvariantCulture, out streamId);
            }

            return int.TryParse(idPart[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out providerIndex)
                && int.TryParse(idPart[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out streamId);
        }

        // Jellyfin remaps tuner channel IDs to hdhr_<channelNumber>
        if (channelId.StartsWith(JellyfinTunerPrefix, StringComparison.Ordinal))
        {
            var channelNumber = channelId.Substring(JellyfinTunerPrefix.Length);
            var currentMap = _channelNumberToChannelId;

            if (currentMap.TryGetValue(channelNumber, out var mappedChannelId)
                && TryParseChannelId(mappedChannelId, out providerIndex, out streamId))
            {
                _logger.LogDebug("Resolved {ChannelId} via hdhr_ prefix to {MappedChannelId}", channelId, mappedChannelId);
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

    /// <summary>
    /// The URL a channel is played from: Dispatcharr's own channel URL when the channel was
    /// matched to a Dispatcharr uuid, otherwise the Xtream URL.
    /// <para>
    /// This URL is the media source's Path, and clients see it. Some open it themselves instead of
    /// asking Jellyfin for the stream: Swiftfin 1.6.1 with VLC, and Moonfin for every live
    /// channel whatever SupportsDirectPlay says. With the Xtream URL that hands the provider login
    /// to the app (homelab-ops #550). Dispatcharr's channel URL carries no login, the same reason
    /// the M3U playlist uses it (GitHub #113). It always serves MPEG-TS, so it is never HLS.
    /// </para>
    /// </summary>
    private (string Url, bool IsHls) ResolveStreamUrl(PluginConfiguration config, int providerIndex, int streamId, string channelId)
    {
        // Only the uuid is cached; the URL is built from the current settings, so a changed or
        // removed Dispatcharr base URL takes effect without waiting for the channel list to reload.
        if (_dispatcharrUuids.TryGetValue(channelId, out var uuid)
            && BuildProxyStreamUrl(config, providerIndex, uuid) is { } proxyUrl)
        {
            return (proxyUrl, false);
        }

        var isHls = !string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase);
        return (BuildStreamUrl(config, providerIndex, streamId), isHls);
    }

    /// <summary>
    /// Dispatcharr's channel URL for a channel, or null when it has no uuid or its provider has
    /// no Dispatcharr base URL.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="channel">The channel.</param>
    /// <returns>The URL, or null.</returns>
    internal static string? BuildProxyStreamUrl(PluginConfiguration config, LiveStreamInfo channel)
        => BuildProxyStreamUrl(config, channel.ProviderIndex, channel.DispatcharrUuid);

    private static string? BuildProxyStreamUrl(PluginConfiguration config, int providerIndex, string? uuid)
    {
        if (string.IsNullOrEmpty(uuid))
        {
            return null;
        }

        var dispatcharrBase = LiveTvService.ResolveDispatcharrBaseUrl(config, providerIndex);
        return string.IsNullOrEmpty(dispatcharrBase)
            ? null
            : DispatcharrChannelMap.BuildProxyStreamUrl(dispatcharrBase, uuid);
    }

    private static string BuildStreamUrl(PluginConfiguration config, int providerIndex, int streamId)
    {
        var (baseUrl, username, password) = LiveTvService.ResolveLiveTvProvider(config, providerIndex);
        var extension = string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase) ? "ts" : "m3u8";
        return string.Create(CultureInfo.InvariantCulture, $"{baseUrl}/live/{username}/{password}/{streamId}.{extension}");
    }

    internal static string BuildChannelId(int providerIndex, int streamId)
    {
        return providerIndex == 0
            ? ChannelIdPrefix + streamId.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{ChannelIdPrefix}{providerIndex}_{streamId}");
    }

    internal static MediaSourceInfo CreateMediaSourceInfo(string channelId, string streamUrl, StreamStatsInfo? stats, bool isHls, PluginConfiguration config, ILogger logger)
    {
        var sourceId = "xtream_live_" + channelId;

        // Stats only with Stream Format TS. A source that skips the probe keeps the container named
        // here, and Dispatcharr serves MPEG-TS whatever the extension, so with HLS selected Jellyfin
        // would be told "hls" for a TS stream (homelab-ops #550). With probing it finds out itself.
        bool hasStats = stats?.VideoCodec != null && !isHls;

        var mediaSource = new MediaSourceInfo
        {
            Id = sourceId,
            Path = streamUrl,
            Protocol = MediaProtocol.Http,
            // "ts", not "mpegts": Jellyfin adds -bsf:a aac_adtstoasc for copied AAC going into fMP4
            // segments only when the source container is "ts", "aac" or "hls"
            // (EncodingHelper.GetAudioBsfArguments). With "mpegts" a copied AAC track made ffmpeg
            // fail on fMP4 (homelab-ops #550).
            Container = isHls ? "hls" : "ts",
            SupportsProbing = !hasStats,

            // GitHub #107. Jellyfin only probes a live source while opening it, and PlaybackInfo
            // only opens one that asks to be opened (MediaInfoController:223, inside the
            // AutoOpenLiveStream branch). Without this the probe below is unreachable no matter
            // what SupportsProbing says. A source with stats is opened too: opening costs nothing
            // (XtreamLiveStream.Open connects lazily) and a source that was never opened made
            // Jellyfin wait about 1.7 s before starting ffmpeg (homelab-ops #550).
            RequiresOpening = true,
            IsRemote = true,
            IsInfiniteStream = true,
            // No direct play: a client that direct plays (Swiftfin with VLC) opens Path itself, so
            // it gets the provider URL with the login in it and bypasses Jellyfin, which then
            // cannot close the connection. On 2026-10-18 Swiftfin opened 2 to 3 connections per
            // channel that way and the provider blocked the account with HTTP 407 (homelab-ops #550).
            SupportsDirectPlay = false,
            // No direct stream: for a live source Jellyfin's direct stream is a remux into one
            // endless fragmented mp4, which the iOS player rejects (AVFoundation -11850, seen on
            // RTL 5 once the audio codec was declared). Without it Jellyfin uses HLS, which still
            // copies video and audio when the client accepts them (homelab-ops #550).
            SupportsDirectStream = false,
            SupportsTranscoding = true,
            // 0 leaves it to Jellyfin's FFmpeg:analyzeduration; a positive value wins over it
            // (EncodingHelper.GetFfmpegAnalyzeDurationArg), and only for these sources.
            AnalyzeDurationMs = hasStats ? Math.Max(0, config.LiveTvAnalyzeDurationMs) : 500,
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

                // -1, as Jellyfin's own M3U tuner leaves probed streams: Jellyfin then passes no
                // -map and ffmpeg picks the video and audio itself. A real index is used verbatim
                // as -map 0:N, and Dispatcharr's MPEG-TS puts a data stream at 0:0, so index 0/1
                // mapped data and video and dropped the audio (homelab-ops #550).
                Index = -1,
                Codec = videoCodec,
                Width = width > 0 ? width : null,
                Height = height > 0 ? height : null,
                IsInterlaced = false,
                RealFrameRate = stats.SourceFps.HasValue ? (float)stats.SourceFps.Value : null,
                AverageFrameRate = stats.SourceFps.HasValue ? (float)stats.SourceFps.Value : null,
                BitRate = stats.Bitrate.HasValue ? (int)Math.Round(stats.Bitrate.Value * 1000) : null,
            };
            mediaStreams.Add(videoStream);

            // Without a codec Jellyfin re-encodes the audio, which avoided the AAC ADTS to fMP4
            // problem (missing aac_adtstoasc). With LiveTvCopyAudio the codec from the stats is
            // declared, so Jellyfin can copy it when the client accepts it (homelab-ops #550).
            var audioCodec = config.LiveTvCopyAudio ? MapAudioCodec(stats.AudioCodec) : null;
            mediaStreams.Add(new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = -1,
                Codec = audioCodec,
                Channels = audioCodec is null ? null : stats.AudioChannels,
                SampleRate = audioCodec is null ? null : stats.AudioSampleRate,
                BitRate = audioCodec is null || stats.AudioBitrate is null ? null : stats.AudioBitrate * 1000,
            });

            mediaSource.MediaStreams = mediaStreams;

            logger.LogDebug(
                "Channel {ChannelId}: using stats - {VideoCodec} {Width}x{Height} @{Fps}fps, audio {AudioCodec}",
                channelId,
                videoCodec,
                width,
                height,
                stats.SourceFps,
                stats.AudioCodec ?? "unknown");
        }
        else
        {
            // GitHub #113, for whoever routes this path through Dispatcharr next: probing must be
            // switched off for a Dispatcharr proxy URL. The probe opens a short-lived connection
            // that Dispatcharr counts as a client and tears the channel down when it closes, which
            // turns into a retry storm. That is in direct tension with everything below. This tuner
            // now plays matched channels from Dispatcharr's own URL (homelab-ops #550), and before
            // that a Dispatcharr Xtream endpoint served its /live/ URLs through the same proxy, so
            // the teardown between probe and playback (about 1.6 s) applies either way. It only
            // concerns channels without stats; with stats there is no probe.
            //
            // GitHub #107. No stats, so leave this empty and let Jellyfin find out for itself.
            //
            // It decides whether to probe with
            //     if (MediaStreams.Any(i => i.Index != -1) || !SupportsProbing) -> skip the probe
            // (MediaSourceManager:522). The placeholders that used to sit here carried index 0 and
            // 1, which satisfied that first test, so the probe never ran and the codec stayed
            // unknown. Clients then refused the stream as an unsupported codec and the server
            // transcoded material they could have played untouched, without hardware acceleration
            // because it did not know what it was decoding either.
            //
            // Leaving it empty is safe: LiveTvMediaSourceProvider.Normalize fills in its own
            // entries when the list is empty, at index -1, which does not satisfy that test.
            //
            // The cost is on the path where the probe fails. Jellyfin's own placeholder assumes
            // interlaced, where this used to assert progressive, so a failed probe now deinterlaces
            // and transcodes slower. That is the rarer path, and guessing progressive was only ever
            // a guess.
            mediaSource.MediaStreams = new List<MediaStream>();
            logger.LogDebug("Channel {ChannelId}: no stats available, probing enabled", channelId);
        }

        return mediaSource;
    }

    private static string? MapAudioCodec(string? dispatcharrCodec)
    {
        if (string.IsNullOrWhiteSpace(dispatcharrCodec))
        {
            return null;
        }

        return dispatcharrCodec.ToLowerInvariant() switch
        {
            "aac" or "aac_latm" => "aac",
            "ac3" or "ac-3" => "ac3",
            "eac3" or "e-ac-3" => "eac3",
            "mp2" or "mp2float" => "mp2",
            "mp3" or "mp3float" => "mp3",
            _ => null, // unknown: leave it to Jellyfin, which then re-encodes
        };
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
