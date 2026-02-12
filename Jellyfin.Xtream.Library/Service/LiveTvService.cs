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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Service for generating M3U playlists and XMLTV EPG files for Live TV.
/// </summary>
public class LiveTvService : IDisposable
{
    private readonly IXtreamClient _client;
    private readonly ILogger<LiveTvService> _logger;
    private readonly SemaphoreSlim _m3uLock = new(1, 1);
    private readonly SemaphoreSlim _epgLock = new(1, 1);

    private string? _cachedM3U;
    private string? _cachedCatchupM3U;
    private string? _cachedEpgXml;
    private DateTime _m3uCacheTime = DateTime.MinValue;
    private DateTime _catchupCacheTime = DateTime.MinValue;
    private DateTime _epgCacheTime = DateTime.MinValue;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvService"/> class.
    /// </summary>
    /// <param name="client">The Xtream API client.</param>
    /// <param name="logger">The logger instance.</param>
    public LiveTvService(IXtreamClient client, ILogger<LiveTvService> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Gets the M3U playlist for Live TV channels.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The M3U playlist content.</returns>
    public async Task<string> GetM3UPlaylistAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;

        await _m3uLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Check cache
            if (_cachedM3U != null && DateTime.UtcNow - _m3uCacheTime < TimeSpan.FromMinutes(config.M3UCacheMinutes))
            {
                _logger.LogDebug("Returning cached M3U playlist");
                return _cachedM3U;
            }

            _logger.LogInformation("Generating M3U playlist");
            var channels = await GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
            var m3u = GenerateM3U(channels, config, catchupOnly: false);

            _cachedM3U = m3u;
            _m3uCacheTime = DateTime.UtcNow;

            return m3u;
        }
        finally
        {
            _m3uLock.Release();
        }
    }

    /// <summary>
    /// Gets the M3U playlist for catch-up enabled channels only.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The catch-up M3U playlist content.</returns>
    public async Task<string> GetCatchupM3UPlaylistAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;

        await _m3uLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedCatchupM3U != null && DateTime.UtcNow - _catchupCacheTime < TimeSpan.FromMinutes(config.M3UCacheMinutes))
            {
                _logger.LogDebug("Returning cached Catchup M3U playlist");
                return _cachedCatchupM3U;
            }

            _logger.LogInformation("Generating Catchup M3U playlist");
            var channels = await GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
            var m3u = GenerateM3U(channels, config, catchupOnly: true);

            _cachedCatchupM3U = m3u;
            _catchupCacheTime = DateTime.UtcNow;

            return m3u;
        }
        finally
        {
            _m3uLock.Release();
        }
    }

    /// <summary>
    /// Gets the XMLTV EPG for Live TV channels.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The XMLTV EPG content.</returns>
    public async Task<string> GetXmltvEpgAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;

        await _epgLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedEpgXml != null && DateTime.UtcNow - _epgCacheTime < TimeSpan.FromMinutes(config.EpgCacheMinutes))
            {
                _logger.LogDebug("Returning cached XMLTV EPG");
                return _cachedEpgXml;
            }

            _logger.LogInformation("Generating XMLTV EPG");
            var channels = await GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
            var epgXml = await GenerateXmltvAsync(channels, config, cancellationToken).ConfigureAwait(false);

            _cachedEpgXml = epgXml;
            _epgCacheTime = DateTime.UtcNow;

            return epgXml;
        }
        finally
        {
            _epgLock.Release();
        }
    }

    /// <summary>
    /// Invalidates the M3U and EPG caches.
    /// </summary>
    public void InvalidateCache()
    {
        _cachedM3U = null;
        _cachedCatchupM3U = null;
        _cachedEpgXml = null;
        _m3uCacheTime = DateTime.MinValue;
        _catchupCacheTime = DateTime.MinValue;
        _epgCacheTime = DateTime.MinValue;
        _logger.LogInformation("Live TV cache invalidated");
    }

    internal async Task<List<LiveStreamInfo>> GetFilteredChannelsAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var connectionInfo = Plugin.Instance.Creds;

        List<LiveStreamInfo> allChannels;

        // Fetch channels by category or all
        if (config.SelectedLiveCategoryIds.Length > 0)
        {
            allChannels = new List<LiveStreamInfo>();
            using var semaphore = new SemaphoreSlim(config.EpgParallelism);
            var tasks = config.SelectedLiveCategoryIds.Select(async categoryId =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var categoryChannels = await _client.GetLiveStreamsByCategoryAsync(connectionInfo, categoryId, cancellationToken).ConfigureAwait(false);
                    return categoryChannels;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var result in results)
            {
                allChannels.AddRange(result);
            }

            // Remove duplicates by StreamId
            allChannels = allChannels.GroupBy(c => c.StreamId).Select(g => g.First()).ToList();
        }
        else
        {
            allChannels = await _client.GetAllLiveStreamsAsync(connectionInfo, cancellationToken).ConfigureAwait(false);
        }

        // Filter adult channels
        if (!config.IncludeAdultChannels)
        {
            allChannels = allChannels.Where(c => !c.IsAdult).ToList();
        }

        // Apply channel overrides
        var overrides = ChannelOverrideParser.Parse(config.ChannelOverrides);
        foreach (var channel in allChannels)
        {
            if (overrides.TryGetValue(channel.StreamId, out var channelOverride))
            {
                ChannelOverrideParser.ApplyOverride(channel, channelOverride);
            }
        }

        _logger.LogInformation("Fetched {Count} Live TV channels", allChannels.Count);
        return allChannels;
    }

    private static string GenerateM3U(List<LiveStreamInfo> channels, PluginConfiguration config, bool catchupOnly)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");

        var filteredChannels = catchupOnly
            ? channels.Where(c => c.TvArchive && c.TvArchiveDuration > 0).ToList()
            : channels;

        foreach (var channel in filteredChannels.OrderBy(c => c.Num))
        {
            var cleanName = ChannelNameCleaner.CleanChannelName(
                channel.Name,
                config.ChannelRemoveTerms,
                config.EnableChannelNameCleaning);

            var epgId = !string.IsNullOrEmpty(channel.EpgChannelId) ? channel.EpgChannelId : channel.StreamId.ToString(CultureInfo.InvariantCulture);

            var extinf = new StringBuilder();
            extinf.Append("#EXTINF:-1");
            extinf.Append(CultureInfo.InvariantCulture, $" tvg-id=\"{EscapeAttribute(epgId)}\"");
            extinf.Append(CultureInfo.InvariantCulture, $" tvg-name=\"{EscapeAttribute(cleanName)}\"");
            extinf.Append(CultureInfo.InvariantCulture, $" tvg-chno=\"{channel.Num}\"");

            if (!string.IsNullOrEmpty(channel.StreamIcon))
            {
                extinf.Append(CultureInfo.InvariantCulture, $" tvg-logo=\"{EscapeAttribute(channel.StreamIcon)}\"");
            }

            // Add catch-up attributes if enabled and channel supports it
            if (config.EnableCatchup && channel.TvArchive && channel.TvArchiveDuration > 0)
            {
                var catchupDays = Math.Min(config.CatchupDays, channel.TvArchiveDuration);
                extinf.Append(" catchup=\"default\"");
                extinf.Append(CultureInfo.InvariantCulture, $" catchup-days=\"{catchupDays}\"");

                // Build catch-up source URL
                var catchupSource = BuildCatchupUrl(config, channel);
                extinf.Append(CultureInfo.InvariantCulture, $" catchup-source=\"{EscapeAttribute(catchupSource)}\"");
            }

            extinf.Append(CultureInfo.InvariantCulture, $",{cleanName}");

            sb.AppendLine(extinf.ToString());

            // Stream URL
            var streamUrl = BuildStreamUrl(config, channel);
            sb.AppendLine(streamUrl);
        }

        return sb.ToString();
    }

    internal static string BuildStreamUrl(PluginConfiguration config, LiveStreamInfo channel)
    {
        var extension = string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase) ? "ts" : "m3u8";
        return string.Create(CultureInfo.InvariantCulture, $"{config.BaseUrl}/live/{config.Username}/{config.Password}/{channel.StreamId}.{extension}");
    }

    private static string BuildCatchupUrl(PluginConfiguration config, LiveStreamInfo channel)
    {
        // Xtream timeshift URL format
        // {utc} = unix timestamp of requested time
        // {start} = program start timestamp
        // {end} = program end timestamp
        // {duration} = duration in seconds
        return string.Create(CultureInfo.InvariantCulture, $"{config.BaseUrl}/timeshift/{config.Username}/{config.Password}/{{duration}}/{{start}}/{channel.StreamId}.ts");
    }

    private async Task<string> GenerateXmltvAsync(List<LiveStreamInfo> channels, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<tv generator-info-name=\"Jellyfin Xtream Library\">");

        // Channel definitions
        foreach (var channel in channels.OrderBy(c => c.Num))
        {
            var cleanName = ChannelNameCleaner.CleanChannelName(
                channel.Name,
                config.ChannelRemoveTerms,
                config.EnableChannelNameCleaning);

            var channelId = !string.IsNullOrEmpty(channel.EpgChannelId) ? channel.EpgChannelId : channel.StreamId.ToString(CultureInfo.InvariantCulture);

            sb.Append(CultureInfo.InvariantCulture, $"  <channel id=\"{EscapeXml(channelId)}\">\n");
            sb.Append(CultureInfo.InvariantCulture, $"    <display-name>{EscapeXml(cleanName)}</display-name>\n");
            if (!string.IsNullOrEmpty(channel.StreamIcon))
            {
                sb.Append(CultureInfo.InvariantCulture, $"    <icon src=\"{EscapeXml(channel.StreamIcon)}\" />\n");
            }

            sb.AppendLine("  </channel>");
        }

        // Fetch EPG data if enabled
        if (config.EnableEpg)
        {
            var connectionInfo = Plugin.Instance.Creds;
            var epgData = await FetchEpgDataAsync(channels, connectionInfo, config, cancellationToken).ConfigureAwait(false);

            foreach (var program in epgData.OrderBy(p => p.StartTimestamp))
            {
                var startStr = FormatXmltvTime(program.StartTimestamp);
                var stopStr = FormatXmltvTime(program.StopTimestamp);
                var channelId = !string.IsNullOrEmpty(program.ChannelId) ? program.ChannelId : program.EpgId;

                sb.Append(CultureInfo.InvariantCulture, $"  <programme start=\"{startStr}\" stop=\"{stopStr}\" channel=\"{EscapeXml(channelId)}\">\n");
                sb.Append(CultureInfo.InvariantCulture, $"    <title>{EscapeXml(program.Title)}</title>\n");
                if (!string.IsNullOrEmpty(program.Description))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"    <desc>{EscapeXml(program.Description)}</desc>\n");
                }

                sb.AppendLine("  </programme>");
            }
        }

        sb.AppendLine("</tv>");
        return sb.ToString();
    }

    private async Task<List<EpgProgram>> FetchEpgDataAsync(
        List<LiveStreamInfo> channels,
        ConnectionInfo connectionInfo,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var allPrograms = new List<EpgProgram>();
        using var semaphore = new SemaphoreSlim(config.EpgParallelism);

        // Calculate EPG time range
        var now = DateTimeOffset.UtcNow;
        var endTime = now.AddDays(config.EpgDaysToFetch);

        var tasks = channels.Select(async channel =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Use get_simple_data_table which returns more EPG data
                var epgListings = await _client.GetSimpleDataTableAsync(connectionInfo, channel.StreamId, cancellationToken).ConfigureAwait(false);

                if (epgListings?.Listings == null)
                {
                    return new List<EpgProgram>();
                }

                // Map channel ID
                var channelId = !string.IsNullOrEmpty(channel.EpgChannelId) ? channel.EpgChannelId : channel.StreamId.ToString(CultureInfo.InvariantCulture);

                foreach (var program in epgListings.Listings)
                {
                    if (string.IsNullOrEmpty(program.ChannelId))
                    {
                        program.ChannelId = channelId;
                    }
                }

                // Filter to our time range
                return epgListings.Listings
                    .Where(p => p.StopTimestamp > now.ToUnixTimeSeconds() && p.StartTimestamp < endTime.ToUnixTimeSeconds())
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to fetch EPG for channel {ChannelId}", channel.StreamId);
                return new List<EpgProgram>();
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var result in results)
        {
            allPrograms.AddRange(result);
        }

        _logger.LogInformation("Fetched {Count} EPG programs for {ChannelCount} channels", allPrograms.Count, channels.Count);
        return allPrograms;
    }

    private static string FormatXmltvTime(long unixTimestamp)
    {
        var dt = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).UtcDateTime;
        return dt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";
    }

    private static string EscapeXml(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private static string EscapeAttribute(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("&", "&amp;", StringComparison.Ordinal);
    }

    /// <summary>
    /// Dispose the service and release resources.
    /// </summary>
    /// <param name="disposing">True if disposing managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _m3uLock.Dispose();
                _epgLock.Dispose();
            }

            _disposed = true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
