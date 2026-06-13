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
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service.Models;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Service for generating M3U playlists and XMLTV EPG files for Live TV.
/// </summary>
public class LiveTvService : IDisposable
{
    private readonly IXtreamClient _client;
    private readonly IServerApplicationPaths _appPaths;
    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<LiveTvService> _logger;
    private readonly SemaphoreSlim _m3uLock = new(1, 1);
    private readonly SemaphoreSlim _epgLock = new(1, 1);
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);

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
    /// <param name="appPaths">The Jellyfin application paths (used to locate the channel snapshot file).</param>
    /// <param name="appHost">The Jellyfin application host (used to resolve the server base URL for channel-logo proxy links).</param>
    /// <param name="logger">The logger instance.</param>
    public LiveTvService(IXtreamClient client, IServerApplicationPaths appPaths, IServerApplicationHost appHost, ILogger<LiveTvService> logger)
    {
        _client = client;
        _appPaths = appPaths;
        _appHost = appHost;
        _logger = logger;
    }

    /// <summary>
    /// Decision for which fetch path <see cref="GetFilteredChannelsAsync"/> takes when
    /// resolving Live TV channels for the active configuration.
    /// </summary>
    internal enum CategoryFetchStrategy
    {
        /// <summary>Fetch every channel the provider exposes.</summary>
        AllFromProvider,

        /// <summary>Don't fetch anything — produce an empty channel set.</summary>
        None,

        /// <summary>Fetch channels from the selected categories only.</summary>
        BySelectedCategories,
    }

    /// <summary>
    /// Resolves a channel logo value for output: local filesystem paths are rewritten to the
    /// ChannelLogo proxy endpoint; http(s) URLs pass through. See issue #53.
    /// </summary>
    /// <param name="streamIcon">The channel's logo value (may be null).</param>
    /// <param name="streamId">The channel stream ID.</param>
    /// <returns>The logo URL to expose to Jellyfin, or null if there is no logo.</returns>
    public string? ResolveChannelLogoUrl(string? streamIcon, int streamId)
        => ChannelLogoResolver.ResolveDisplayUrl(streamIcon, streamId, GetServerBaseUrl());

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
            var m3u = GenerateM3U(channels, config, catchupOnly: false, GetServerBaseUrl());

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
            var m3u = GenerateM3U(channels, config, catchupOnly: true, GetServerBaseUrl());

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
            var epgXml = await GenerateXmltvAsync(channels, config, GetServerBaseUrl(), cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Refreshes the Live TV channel set: fetches the current channels, computes a delta
    /// against the previously persisted snapshot, persists the new snapshot, and invalidates
    /// the M3U/EPG cache so the next tuner poll sees the fresh data.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The delta between the previous and current channel set.</returns>
    public async Task<LiveChannelDelta> RefreshChannelsAsync(CancellationToken cancellationToken)
    {
        var channels = await GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);

        await _snapshotLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await LoadChannelSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var delta = LiveChannelSnapshot.ComputeDelta(previous, channels);
            var next = LiveChannelSnapshot.FromChannels(channels);
            await SaveChannelSnapshotAsync(next, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Live TV refresh: {Total} channels ({Added} added, {Updated} updated, {Removed} removed, {Unchanged} unchanged)",
                delta.TotalChannels,
                delta.AddedCount,
                delta.UpdatedCount,
                delta.RemovedCount,
                delta.UnchangedCount);

            // Force the next tuner poll to pick up the fresh channel set.
            InvalidateCache();

            return delta;
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    /// <summary>
    /// Gets the server base URL used to build channel-logo proxy links. Channel images are
    /// fetched server-side, so the loopback/LAN URL is sufficient and is stable across requests
    /// (keeping the cached M3U/EPG coherent). Returns an empty string if it cannot be resolved.
    /// </summary>
    private string GetServerBaseUrl()
    {
        try
        {
            return _appHost.GetApiUrlForLocalAccess(System.Net.IPAddress.Loopback, false) ?? string.Empty;
        }
        catch (System.Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve server base URL for channel logo proxy");
            return string.Empty;
        }
    }

    private string GetChannelSnapshotPath() =>
        Path.Combine(_appPaths.DataPath, "xtream-library", "live-tv-channels.json");

    private async Task<LiveChannelSnapshot?> LoadChannelSnapshotAsync(CancellationToken cancellationToken)
    {
        var path = GetChannelSnapshotPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonConvert.DeserializeObject<LiveChannelSnapshot>(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Live TV channel snapshot at {Path} - treating as missing", path);
            return null;
        }
    }

    private async Task SaveChannelSnapshotAsync(LiveChannelSnapshot snapshot, CancellationToken cancellationToken)
    {
        var path = GetChannelSnapshotPath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonConvert.SerializeObject(snapshot, Newtonsoft.Json.Formatting.Indented);
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    internal async Task<List<LiveStreamInfo>> GetFilteredChannelsAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        var connectionInfo = Plugin.Instance.GetCreds(0);

        List<LiveStreamInfo> allChannels;

        var strategy = ChooseCategoryFetchStrategy(config.LiveChannelMode, config.SelectedLiveCategoryIds.Length);
        if (strategy == CategoryFetchStrategy.AllFromProvider)
        {
            allChannels = await _client.GetAllLiveStreamsAsync(connectionInfo, cancellationToken).ConfigureAwait(false);
        }
        else if (strategy == CategoryFetchStrategy.None)
        {
            // Custom mode + nothing selected = sync nothing. Headline fix vs pre-v1.35,
            // where this branch returned every channel from the provider.
            allChannels = new List<LiveStreamInfo>();
        }
        else
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

        // Filter adult channels
        if (!config.IncludeAdultChannels)
        {
            allChannels = allChannels.Where(c => !c.IsAdult).ToList();
        }

        // Apply per-channel exclusions only in Custom mode (IncludeAll deliberately ignores them).
        if (config.LiveChannelMode == LiveChannelSelectionMode.Custom)
        {
            allChannels = FilterExcludedChannels(allChannels, config.ExcludedLiveStreamIds);
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

    /// <summary>
    /// Picks the fetch strategy from selection mode + selected-category count.
    /// Internal for unit testing.
    /// </summary>
    /// <param name="mode">The configured <see cref="LiveChannelSelectionMode"/>.</param>
    /// <param name="selectedCategoryCount">Number of entries in <c>SelectedLiveCategoryIds</c>.</param>
    /// <returns>The strategy <see cref="GetFilteredChannelsAsync"/> should take.</returns>
    internal static CategoryFetchStrategy ChooseCategoryFetchStrategy(LiveChannelSelectionMode mode, int selectedCategoryCount)
    {
        if (mode == LiveChannelSelectionMode.IncludeAll)
        {
            return CategoryFetchStrategy.AllFromProvider;
        }

        return selectedCategoryCount == 0
            ? CategoryFetchStrategy.None
            : CategoryFetchStrategy.BySelectedCategories;
    }

    /// <summary>
    /// Removes channels whose stream IDs appear in <paramref name="excludedStreamIds"/>.
    /// Public for unit testing; safe to call with empty/null exclusion lists.
    /// </summary>
    /// <param name="channels">Source list of channels.</param>
    /// <param name="excludedStreamIds">Stream IDs to exclude. Null or empty returns the list unchanged.</param>
    /// <returns>Filtered list of channels.</returns>
    internal static List<LiveStreamInfo> FilterExcludedChannels(List<LiveStreamInfo> channels, int[]? excludedStreamIds)
    {
        if (excludedStreamIds == null || excludedStreamIds.Length == 0)
        {
            return channels;
        }

        var excluded = new HashSet<int>(excludedStreamIds);
        return channels.Where(c => !excluded.Contains(c.StreamId)).ToList();
    }

    private static string GenerateM3U(List<LiveStreamInfo> channels, PluginConfiguration config, bool catchupOnly, string baseUrl)
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

            var logoUrl = ChannelLogoResolver.ResolveDisplayUrl(channel.StreamIcon, channel.StreamId, baseUrl);
            if (!string.IsNullOrEmpty(logoUrl))
            {
                extinf.Append(CultureInfo.InvariantCulture, $" tvg-logo=\"{EscapeAttribute(logoUrl)}\"");
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
        var (baseUrl, username, password) = ResolveLiveTvProvider(config);
        var extension = string.Equals(config.LiveTvOutputFormat, "ts", StringComparison.OrdinalIgnoreCase) ? "ts" : "m3u8";
        return string.Create(CultureInfo.InvariantCulture, $"{baseUrl}/live/{username}/{password}/{channel.StreamId}.{extension}");
    }

    private static string BuildCatchupUrl(PluginConfiguration config, LiveStreamInfo channel)
    {
        // Xtream timeshift URL format
        // {utc} = unix timestamp of requested time
        // {start} = program start timestamp
        // {end} = program end timestamp
        // {duration} = duration in seconds
        var (baseUrl, username, password) = ResolveLiveTvProvider(config);
        return string.Create(CultureInfo.InvariantCulture, $"{baseUrl}/timeshift/{username}/{password}/{{duration}}/{{start}}/{channel.StreamId}.ts");
    }

    // Resolves credentials for the Live TV provider. Reads Providers[0] when populated
    // (the multi-provider data model since v1.32), falling back to the legacy single-provider
    // fields for any pre-migration config still in flight. See BUG-008 in BUGS.md.
    internal static (string BaseUrl, string Username, string Password) ResolveLiveTvProvider(PluginConfiguration config)
    {
        var p = config.Providers.FirstOrDefault();
        if (p != null && !string.IsNullOrEmpty(p.BaseUrl))
        {
            return (p.BaseUrl, p.Username, p.Password);
        }

        return (config.BaseUrl, config.Username, config.Password);
    }

    private async Task<string> GenerateXmltvAsync(List<LiveStreamInfo> channels, PluginConfiguration config, string baseUrl, CancellationToken cancellationToken)
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

            var channelId = XtreamTunerHost.ChannelIdPrefix + channel.StreamId.ToString(CultureInfo.InvariantCulture);

            sb.Append(CultureInfo.InvariantCulture, $"  <channel id=\"{EscapeXml(channelId)}\">\n");
            sb.Append(CultureInfo.InvariantCulture, $"    <display-name>{EscapeXml(cleanName)}</display-name>\n");
            var iconUrl = ChannelLogoResolver.ResolveDisplayUrl(channel.StreamIcon, channel.StreamId, baseUrl);
            if (!string.IsNullOrEmpty(iconUrl))
            {
                sb.Append(CultureInfo.InvariantCulture, $"    <icon src=\"{EscapeXml(iconUrl)}\" />\n");
            }

            sb.AppendLine("  </channel>");
        }

        // Fetch EPG data if enabled
        if (config.EnableEpg)
        {
            var connectionInfo = Plugin.Instance.GetCreds(0);

            // Build map: upstream epg_channel_id -> our xtream_{streamId} id
            var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in channels)
            {
                if (!string.IsNullOrEmpty(ch.EpgChannelId))
                {
                    idMap[ch.EpgChannelId] = XtreamTunerHost.ChannelIdPrefix + ch.StreamId.ToString(CultureInfo.InvariantCulture);
                }
            }

            // Prefer upstream XMLTV (preserves category, rating, credits, icon, etc.).
            // Fall back to JSON-based fetch only if the upstream file is unavailable.
            var passthroughCount = 0;
            if (idMap.Count > 0)
            {
                var upstreamXml = await _client.GetXmltvAsync(connectionInfo, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(upstreamXml))
                {
                    passthroughCount = AppendUpstreamProgrammes(sb, upstreamXml, idMap, config, cancellationToken);
                    _logger.LogInformation("Passed through {Count} programmes from upstream XMLTV", passthroughCount);
                }
            }

            if (passthroughCount == 0)
            {
                _logger.LogInformation("Upstream XMLTV unavailable or empty; falling back to per-channel JSON EPG");
                var epgData = await FetchEpgDataAsync(channels, connectionInfo, config, cancellationToken).ConfigureAwait(false);

                foreach (var program in epgData.OrderBy(p => p.StartTimestamp))
                {
                    var startStr = FormatXmltvTime(program.StartTimestamp);
                    var stopStr = FormatXmltvTime(program.StopTimestamp);
                    var channelId = !string.IsNullOrEmpty(program.ChannelId) ? program.ChannelId : program.EpgId;

                    sb.Append(CultureInfo.InvariantCulture, $"  <programme start=\"{startStr}\" stop=\"{stopStr}\" channel=\"{EscapeXml(channelId)}\">\n");
                    sb.Append(CultureInfo.InvariantCulture, $"    <title>{EscapeXml(DecodeBase64(program.Title))}</title>\n");
                    var desc = DecodeBase64(program.Description);
                    if (!string.IsNullOrEmpty(desc))
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"    <desc>{EscapeXml(desc)}</desc>\n");
                    }

                    sb.AppendLine("  </programme>");
                }
            }
        }

        sb.AppendLine("</tv>");
        return sb.ToString();
    }

    /// <summary>
    /// Streams the upstream XMLTV document and appends each &lt;programme&gt; whose channel
    /// is in <paramref name="idMap"/>, rewriting its channel attribute to our xtream_ id.
    /// All other programme child elements (category, rating, credits, icon, etc.) are
    /// preserved verbatim.
    /// </summary>
    /// <returns>Number of programmes written.</returns>
    private int AppendUpstreamProgrammes(
        StringBuilder sb,
        string upstreamXml,
        Dictionary<string, string> idMap,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var written = 0;
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Keep programs that ended up to 1 hour ago to be resilient to timezone/clock skews
        var pastGraceUnix = nowUnix - 3600;
        var endUnix = DateTimeOffset.UtcNow.AddDays(config.EpgDaysToFetch).ToUnixTimeSeconds();

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            IgnoreWhitespace = true,
            IgnoreComments = true,
        };

        try
        {
            using var stringReader = new StringReader(upstreamXml);
            using var reader = XmlReader.Create(stringReader, settings);

            reader.MoveToContent();
            while (!reader.EOF)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.NodeType != XmlNodeType.Element || reader.Name != "programme")
                {
                    reader.Read();
                    continue;
                }

                var upstreamCh = reader.GetAttribute("channel");
                if (string.IsNullOrEmpty(upstreamCh) || !idMap.TryGetValue(upstreamCh, out var ourId))
                {
                    reader.Skip();
                    continue;
                }

                // Optional time-window filter to keep the EPG file proportional to EpgDaysToFetch.
                var startAttr = reader.GetAttribute("start");
                var stopAttr = reader.GetAttribute("stop");
                if (TryParseXmltvTime(stopAttr, out var stopUnix) && stopUnix < pastGraceUnix)
                {
                    reader.Skip();
                    continue;
                }

                if (TryParseXmltvTime(startAttr, out var startUnix) && startUnix > endUnix)
                {
                    reader.Skip();
                    continue;
                }

                XElement element;
                try
                {
                    element = (XElement)XNode.ReadFrom(reader);
                }
                catch (XmlException ex)
                {
                    _logger.LogDebug(ex, "Skipping malformed <programme> in upstream XMLTV");
                    continue;
                }

                element.SetAttributeValue("channel", ourId);
                sb.Append("  ").Append(element.ToString(SaveOptions.DisableFormatting)).Append('\n');
                written++;
            }
        }
        catch (XmlException ex)
        {
            _logger.LogWarning(ex, "Failed to parse upstream XMLTV; falling back to JSON EPG");
            return 0;
        }

        return written;
    }

    internal static bool TryParseXmltvTime(string? value, out long unixSeconds)
    {
        unixSeconds = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // XMLTV format: "YYYYMMDDHHMMSS +ZZZZ" (offset optional)
        // Can also be "YYYYMMDDHHMMSS +ZZ:ZZ" or "YYYYMMDDHHMM" or "YYYYMMDD"
        var space = value.IndexOf(' ', StringComparison.Ordinal);
        var datePart = space >= 0 ? value.Substring(0, space) : value;
        var offsetPart = space >= 0 ? value.Substring(space + 1) : "+0000";

        string[] formats = { "yyyyMMddHHmmss", "yyyyMMddHHmm", "yyyyMMdd" };
        if (!DateTime.TryParseExact(datePart, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return false;
        }

        // Normalize offsetPart: handle +HH:MM by removing the colon
        if (offsetPart.Contains(':', StringComparison.Ordinal))
        {
            offsetPart = offsetPart.Replace(":", string.Empty, StringComparison.Ordinal);
        }

        var offset = TimeSpan.Zero;
        if (offsetPart.Length >= 5 && (offsetPart[0] == '+' || offsetPart[0] == '-')
            && int.TryParse(offsetPart.AsSpan(1, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            && int.TryParse(offsetPart.AsSpan(3, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            offset = new TimeSpan(hours, minutes, 0);
            if (offsetPart[0] == '-')
            {
                offset = -offset;
            }
        }

        var dto = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), offset);
        unixSeconds = dto.ToUnixTimeSeconds();
        return true;
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
        // Keep programs that ended up to 1 hour ago
        var pastGraceTime = now.AddHours(-1);
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

                // Map channel ID to match the native tuner's xtream_ prefix
                var channelId = XtreamTunerHost.ChannelIdPrefix + channel.StreamId.ToString(CultureInfo.InvariantCulture);

                foreach (var program in epgListings.Listings)
                {
                    program.ChannelId = channelId;
                }

                // Filter to our time range
                return epgListings.Listings
                    .Where(p => p.StopTimestamp > pastGraceTime.ToUnixTimeSeconds() && p.StartTimestamp < endTime.ToUnixTimeSeconds())
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

    private static string DecodeBase64(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            // Not base64-encoded, return as-is
            return value;
        }
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
                _snapshotLock.Dispose();
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
