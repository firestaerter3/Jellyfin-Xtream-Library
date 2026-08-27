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
using System.Security.Cryptography;
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
    private int _refreshInFlight;
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

            // Jellyfin gives its own fetch of this endpoint 100 seconds, a ceiling the plugin
            // cannot raise. A full upstream catalog fetch can exceed that on a large provider,
            // so answer from the stored snapshot and bring it up to date behind the request.
            var fromSnapshot = await TryRenderM3UFromSnapshotAsync(config, catchupOnly: false, cancellationToken)
                .ConfigureAwait(false);
            if (fromSnapshot != null)
            {
                _cachedM3U = fromSnapshot;
                _m3uCacheTime = DateTime.UtcNow;
                return fromSnapshot;
            }

            _logger.LogInformation("Generating M3U playlist");
            var channels = await GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
            var categoryNames = await GetCategoryNameMapAsync(cancellationToken).ConfigureAwait(false);
            var m3u = GenerateM3U(channels, config, catchupOnly: false, GetServerBaseUrl(), categoryNames);

            _cachedM3U = m3u;
            _m3uCacheTime = DateTime.UtcNow;

            // Seed the snapshot so the next cold start does not repeat this fetch.
            await PersistSnapshotAsync(channels, categoryNames, cancellationToken).ConfigureAwait(false);

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

            var fromSnapshot = await TryRenderM3UFromSnapshotAsync(config, catchupOnly: true, cancellationToken)
                .ConfigureAwait(false);
            if (fromSnapshot != null)
            {
                _cachedCatchupM3U = fromSnapshot;
                _catchupCacheTime = DateTime.UtcNow;
                return fromSnapshot;
            }

            _logger.LogInformation("Generating Catchup M3U playlist");
            var channels = await GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);
            var categoryNames = await GetCategoryNameMapAsync(cancellationToken).ConfigureAwait(false);
            var m3u = GenerateM3U(channels, config, catchupOnly: true, GetServerBaseUrl(), categoryNames);

            _cachedCatchupM3U = m3u;
            _catchupCacheTime = DateTime.UtcNow;

            await PersistSnapshotAsync(channels, categoryNames, cancellationToken).ConfigureAwait(false);

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
        ApplyClientTimeout(config);

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
    /// Renders the M3U from the stored channel snapshot, or returns null when there is nothing
    /// usable to render from (no snapshot yet, an empty one, or a pre-v2 file that predates the
    /// fields rendering needs). Callers fall back to fetching from the provider.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="catchupOnly">Whether to emit only catch-up capable channels.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rendered playlist, or null when the snapshot cannot serve this request.</returns>
    private async Task<string?> TryRenderM3UFromSnapshotAsync(
        PluginConfiguration config,
        bool catchupOnly,
        CancellationToken cancellationToken)
    {
        LiveChannelSnapshot? snapshot;
        try
        {
            snapshot = await LoadChannelSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the Live TV channel snapshot; falling back to a provider fetch");
            return null;
        }

        var channels = snapshot?.ToChannels();
        if (channels == null || channels.Count == 0)
        {
            return null;
        }

        // ProviderIndex is positional, so a reordered or replaced provider list would make the
        // stored indices resolve to the wrong credentials. Refuse and fetch fresh instead.
        if (!snapshot!.MatchesProviders(BuildProviderFingerprints(config)))
        {
            _logger.LogInformation(
                "Stored Live TV snapshot references providers that have changed; fetching from the provider instead");
            return null;
        }

        channels = ApplyRenderTimeFilters(channels, config);
        if (channels.Count == 0)
        {
            return null;
        }

        var refreshDue = ShouldRefreshSnapshot(snapshot.CreatedAt, DateTime.UtcNow, config.M3UCacheMinutes);
        var age = DateTime.UtcNow - snapshot.CreatedAt;
        _logger.LogInformation(
            "Serving M3U from the stored channel snapshot ({Count} channels, {AgeMinutes:F0} minutes old, refresh due: {RefreshDue})",
            channels.Count,
            age.TotalMinutes,
            refreshDue);

        // Only when the snapshot has actually aged out. Refreshing on every cold poll would
        // loop forever, because a successful refresh invalidates the cache that made the poll
        // cold in the first place.
        if (refreshDue)
        {
            StartBackgroundChannelRefresh();
        }

        return GenerateM3U(channels, config, catchupOnly, GetServerBaseUrl(), snapshot.Categories);
    }

    /// <summary>
    /// Builds an index to identity fingerprint map for the enabled Live TV providers. The
    /// fingerprint is a hash so the snapshot file carries no base URL or username: it is only
    /// used to detect that a stored provider index still means the same provider.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The provider index to fingerprint map.</returns>
    internal static Dictionary<int, string> BuildProviderFingerprints(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var map = new Dictionary<int, string>();
        foreach (var (index, provider) in ResolveLiveTvProviders(config))
        {
            var material = string.Create(CultureInfo.InvariantCulture, $"{provider.BaseUrl}|{provider.Username}");
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
            map[index] = Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }

        return map;
    }

    /// <summary>
    /// Re-applies the filters that the fetch path applies, so a snapshot taken before a filter
    /// was switched on does not keep serving what the filter now excludes. Matters most for the
    /// adult filter, where the staleness window is not acceptable.
    /// </summary>
    /// <param name="channels">The channels restored from a snapshot.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The filtered channel list.</returns>
    internal static List<LiveStreamInfo> ApplyRenderTimeFilters(List<LiveStreamInfo> channels, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(config);

        var result = channels;

        if (!config.IncludeAdultChannels)
        {
            result = result.Where(c => !c.IsAdult).ToList();
        }

        // Category filtering is normally decided at fetch time by choosing which categories to
        // request, so a snapshot cannot hold the wrong ones. ExcludeSelected breaks that: it fetches
        // everything and narrows afterwards, so a snapshot taken before a category was excluded
        // still contains it. Re-apply here for the same reason the adult filter is re-applied.
        if (config.LiveChannelMode == LiveChannelSelectionMode.ExcludeSelected)
        {
            result = FilterExcludedCategories(result, config.SelectedLiveCategoryIds);
        }

        // IncludeAll deliberately ignores per-channel exclusions, matching the fetch path.
        if (config.LiveChannelMode != LiveChannelSelectionMode.IncludeAll)
        {
            result = FilterExcludedChannels(result, config.ExcludedLiveStreamIds);
        }

        return result;
    }

    /// <summary>
    /// Decides whether a completed fetch may write its snapshot. The fetch happens outside the
    /// snapshot lock and more than one caller can be fetching (the background refresh and the
    /// scheduled sync), so a slow fetch finishing last must not move the stored data backwards.
    /// </summary>
    /// <param name="fetchStartedAtUtc">When this caller started fetching.</param>
    /// <param name="existingCreatedAtUtc">The stored snapshot's timestamp, null when there is none.</param>
    /// <returns>True when this caller's data is not older than what is already stored.</returns>
    internal static bool ShouldWriteSnapshot(DateTime fetchStartedAtUtc, DateTime? existingCreatedAtUtc)
        => existingCreatedAtUtc is null || existingCreatedAtUtc <= fetchStartedAtUtc;

    /// <summary>
    /// Decides whether a snapshot is due for a refresh. A successful refresh calls
    /// <see cref="InvalidateCache"/>, so the next tuner poll finds a cold cache again. Without
    /// this check the cold path would start a refresh on every poll and re-download the whole
    /// catalogue continuously, which is worse than the stall it was meant to fix.
    /// </summary>
    /// <param name="createdAtUtc">When the snapshot was written (default when unknown).</param>
    /// <param name="nowUtc">The current time.</param>
    /// <param name="cacheMinutes">The configured cache window in minutes.</param>
    /// <returns>True when a background refresh should be started.</returns>
    internal static bool ShouldRefreshSnapshot(DateTime createdAtUtc, DateTime nowUtc, int cacheMinutes)
    {
        if (createdAtUtc == default)
        {
            // No usable timestamp: refresh rather than pin the plugin to stale data forever.
            return true;
        }

        var age = nowUtc - createdAtUtc;
        if (age < TimeSpan.Zero)
        {
            // Stamped in the future (clock skew). Treat as fresh; reading it as infinitely
            // stale would reintroduce the refresh-per-poll loop.
            return false;
        }

        return age >= TimeSpan.FromMinutes(Math.Max(cacheMinutes, 1));
    }

    /// <summary>
    /// Claims the right to run a channel refresh. Returns false when one is already running, so
    /// every request arriving during a slow refresh does not kick off another one.
    /// </summary>
    /// <returns>True when the caller now owns the refresh and must call <see cref="EndChannelRefresh"/>.</returns>
    internal bool TryBeginChannelRefresh()
        => Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) == 0;

    /// <summary>
    /// Releases the claim taken by <see cref="TryBeginChannelRefresh"/>. Must run even when the
    /// refresh failed, otherwise no refresh is ever attempted again.
    /// </summary>
    internal void EndChannelRefresh()
        => Interlocked.Exchange(ref _refreshInFlight, 0);

    /// <summary>
    /// Refreshes the channel set behind the current request. Used when output was served from a
    /// stale snapshot: the caller gets an answer immediately and the fresh data lands in the
    /// snapshot for the next request.
    /// </summary>
    private void StartBackgroundChannelRefresh()
    {
        if (!TryBeginChannelRefresh())
        {
            _logger.LogDebug("Live TV channel refresh already running; not starting another");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshChannelsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Nothing awaits this task, so an escaping exception would be unobserved.
                // Serving the previous snapshot is the correct fallback here.
                _logger.LogError(ex, "Background Live TV channel refresh failed; continuing to serve the stored snapshot");
            }
            finally
            {
                EndChannelRefresh();
            }
        });
    }

    /// <summary>
    /// Persists the channel set without computing a delta or invalidating the cache. Used after a
    /// cold generation so the next restart has something to render from: the snapshot was
    /// previously only ever written by a library sync, which a Live-TV-only user never runs.
    /// </summary>
    /// <param name="channels">The channels just fetched.</param>
    /// <param name="categoryNames">The category names just fetched.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the snapshot has been written.</returns>
    private async Task PersistSnapshotAsync(
        List<LiveStreamInfo> channels,
        IReadOnlyDictionary<int, string> categoryNames,
        CancellationToken cancellationToken)
    {
        await _snapshotLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveChannelSnapshotAsync(
                LiveChannelSnapshot.FromChannels(
                    channels,
                    categoryNames,
                    BuildProviderFingerprints(Plugin.Instance.Configuration)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best effort: failing to cache must not fail the request that just succeeded.
            _logger.LogWarning(ex, "Could not persist the Live TV channel snapshot");
        }
        finally
        {
            _snapshotLock.Release();
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
        var fetchStartedAt = DateTime.UtcNow;
        var channels = await GetFilteredChannelsAsync(cancellationToken).ConfigureAwait(false);

        // Persisted alongside the channels so group titles survive a restart without a second
        // round trip to the provider.
        var categoryNames = await GetCategoryNameMapAsync(cancellationToken).ConfigureAwait(false);

        await _snapshotLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await LoadChannelSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var delta = LiveChannelSnapshot.ComputeDelta(previous, channels);

            if (ShouldWriteSnapshot(fetchStartedAt, previous?.CreatedAt))
            {
                var next = LiveChannelSnapshot.FromChannels(
                    channels,
                    categoryNames,
                    BuildProviderFingerprints(Plugin.Instance.Configuration));
                await SaveChannelSnapshotAsync(next, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // A fresher snapshot landed while this fetch was running. Keep it and report the
                // delta we computed rather than moving the stored data backwards.
                _logger.LogInformation(
                    "A newer Live TV snapshot was written while this refresh was fetching; keeping the newer one");
            }

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

    /// <summary>
    /// Builds a map of Live TV category id to category name for channel grouping
    /// (M3U <c>group-title</c> / native tuner <c>ChannelGroup</c>). Best-effort: any
    /// failure returns an empty map so channel output is never blocked.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A dictionary mapping category id to category name (empty on failure).</returns>
    internal async Task<Dictionary<int, string>> GetCategoryNameMapAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        ApplyClientTimeout(config);
        var liveTvProviders = ResolveLiveTvProviders(config).ToList();
        if (liveTvProviders.Count != 1)
        {
            return new Dictionary<int, string>();
        }

        try
        {
            var connectionInfo = Plugin.Instance.GetCreds(liveTvProviders[0].Index);
            var categories = await _client.GetLiveCategoryAsync(connectionInfo, cancellationToken).ConfigureAwait(false);
            var map = new Dictionary<int, string>(categories.Count);
            foreach (var category in categories)
            {
                // Last-wins on duplicate ids.
                map[category.CategoryId] = category.CategoryName;
            }

            return map;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Live TV categories for channel grouping; channels will be ungrouped");
            return new Dictionary<int, string>();
        }
    }

    internal async Task<List<LiveStreamInfo>> GetFilteredChannelsAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        ApplyClientTimeout(config);
        var liveTvProviders = ResolveLiveTvProviders(config).ToList();
        var allChannels = new List<LiveStreamInfo>();

        foreach (var provider in liveTvProviders)
        {
            var connectionInfo = Plugin.Instance.GetCreds(provider.Index);
            var providerChannels = await GetFilteredChannelsForProviderAsync(config, connectionInfo, provider.Index, cancellationToken).ConfigureAwait(false);
            allChannels.AddRange(providerChannels);
        }

        _logger.LogInformation("Fetched {Count} Live TV channels from {ProviderCount} provider(s)", allChannels.Count, liveTvProviders.Count);
        return allChannels;
    }

    private async Task<List<LiveStreamInfo>> GetFilteredChannelsForProviderAsync(
        PluginConfiguration config,
        ConnectionInfo connectionInfo,
        int providerIndex,
        CancellationToken cancellationToken)
    {
        List<LiveStreamInfo> providerChannels;

        var strategy = ChooseCategoryFetchStrategy(config.LiveChannelMode, config.SelectedLiveCategoryIds.Length);
        if (strategy == CategoryFetchStrategy.AllFromProvider)
        {
            providerChannels = await _client.GetAllLiveStreamsAsync(connectionInfo, cancellationToken).ConfigureAwait(false);
        }
        else if (strategy == CategoryFetchStrategy.None)
        {
            // Custom mode + nothing selected = sync nothing. Headline fix vs pre-v1.35,
            // where this branch returned every channel from the provider.
            providerChannels = new List<LiveStreamInfo>();
        }
        else
        {
            providerChannels = new List<LiveStreamInfo>();
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
                providerChannels.AddRange(result);
            }

            // Remove duplicates by StreamId within each provider while allowing the same id across providers.
            providerChannels = providerChannels.GroupBy(c => c.StreamId).Select(g => g.First()).ToList();
        }

        // Filter adult channels
        if (!config.IncludeAdultChannels)
        {
            providerChannels = providerChannels.Where(c => !c.IsAdult).ToList();
        }

        // ExcludeSelected fetched the whole catalogue above, so the category filter happens here
        // rather than by choosing what to request. No-op in the other two modes.
        if (config.LiveChannelMode == LiveChannelSelectionMode.ExcludeSelected)
        {
            providerChannels = FilterExcludedCategories(providerChannels, config.SelectedLiveCategoryIds);
        }

        // Apply per-channel exclusions in both selective modes (IncludeAll deliberately ignores
        // them: it is the "everything, no exceptions" mode).
        if (config.LiveChannelMode != LiveChannelSelectionMode.IncludeAll)
        {
            providerChannels = FilterExcludedChannels(providerChannels, config.ExcludedLiveStreamIds);
        }

        // Apply channel overrides
        var overrides = ChannelOverrideParser.Parse(config.ChannelOverrides);
        foreach (var channel in providerChannels)
        {
            channel.ProviderIndex = providerIndex;
            if (overrides.TryGetValue(channel.StreamId, out var channelOverride))
            {
                ChannelOverrideParser.ApplyOverride(channel, channelOverride);
            }
        }

        return providerChannels;
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

        // ExcludeSelected fetches the whole catalogue and drops the excluded categories afterwards,
        // so that categories the provider adds later are picked up on their own. It must not fall
        // through to the count check below: that branch fetches *by* SelectedLiveCategoryIds, which
        // in this mode would return exactly the categories the user asked to be rid of.
        if (mode == LiveChannelSelectionMode.ExcludeSelected)
        {
            return CategoryFetchStrategy.AllFromProvider;
        }

        return selectedCategoryCount == 0
            ? CategoryFetchStrategy.None
            : CategoryFetchStrategy.BySelectedCategories;
    }

    /// <summary>
    /// Removes channels belonging to any of <paramref name="excludedCategoryIds"/>. Used only in
    /// <see cref="LiveChannelSelectionMode.ExcludeSelected"/> mode, where the whole catalogue is
    /// fetched and narrowed here instead of by asking the provider for specific categories.
    /// </summary>
    /// <param name="channels">Source list of channels.</param>
    /// <param name="excludedCategoryIds">Category IDs to exclude. Null or empty returns the list unchanged,
    /// because excluding nothing excludes nothing (GitHub #76 semantics).</param>
    /// <returns>Filtered list of channels.</returns>
    internal static List<LiveStreamInfo> FilterExcludedCategories(List<LiveStreamInfo> channels, int[]? excludedCategoryIds)
    {
        if (excludedCategoryIds == null || excludedCategoryIds.Length == 0)
        {
            return channels;
        }

        var excluded = new HashSet<int>(excludedCategoryIds);
        return channels.Where(c => !IsInExcludedCategory(c, excluded)).ToList();
    }

    /// <summary>
    /// True when any category the channel belongs to is excluded.
    /// <para>
    /// Membership is the union of the scalar primary category and the <c>category_ids</c> array,
    /// because Include mode fetches per category and so pulls in a channel reachable through *any*
    /// of its categories. Matching that union keeps Exclude the exact complement of Include on
    /// providers that put one channel in several categories. A channel the provider gives no
    /// category at all is kept: it is not in the exclusion list.
    /// </para>
    /// </summary>
    private static bool IsInExcludedCategory(LiveStreamInfo channel, HashSet<int> excluded)
    {
        if (channel.CategoryId.HasValue && excluded.Contains(channel.CategoryId.Value))
        {
            return true;
        }

        if (channel.CategoryIds != null)
        {
            foreach (var categoryId in channel.CategoryIds)
            {
                if (excluded.Contains(categoryId))
                {
                    return true;
                }
            }
        }

        return false;
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

    internal static string GenerateM3U(List<LiveStreamInfo> channels, PluginConfiguration config, bool catchupOnly, string baseUrl, IReadOnlyDictionary<int, string> categoryNames)
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

            // Group channels by their Xtream category so Jellyfin can show category groups.
            if (channel.CategoryId is int catId
                && categoryNames.TryGetValue(catId, out var categoryName)
                && !string.IsNullOrEmpty(categoryName))
            {
                extinf.Append(CultureInfo.InvariantCulture, $" group-title=\"{EscapeAttribute(categoryName)}\"");
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
        var (baseUrl, username, password) = ResolveLiveTvProvider(config, channel.ProviderIndex);
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
        var (baseUrl, username, password) = ResolveLiveTvProvider(config, channel.ProviderIndex);
        return string.Create(CultureInfo.InvariantCulture, $"{baseUrl}/timeshift/{username}/{password}/{{duration}}/{{start}}/{channel.StreamId}.ts");
    }

    // Resolves credentials for the Live TV provider. Reads Providers[0] when populated
    // (the multi-provider data model since v1.32), falling back to the legacy single-provider
    // fields for any pre-migration config still in flight. See BUG-008 in BUGS.md.
    internal static (string BaseUrl, string Username, string Password) ResolveLiveTvProvider(PluginConfiguration config, int providerIndex = 0)
    {
        var p = config.Providers.ElementAtOrDefault(providerIndex) ?? config.Providers.FirstOrDefault();
        if (p != null && !string.IsNullOrEmpty(p.BaseUrl))
        {
            return (p.BaseUrl, p.Username, p.Password);
        }

        return (config.BaseUrl, config.Username, config.Password);
    }

    /// <summary>
    /// Resolves the per-request timeout to apply to the Xtream client for Live TV work.
    /// A single client serves every configured provider within one operation, so the largest
    /// configured value wins: a smaller one would cut off the slowest provider mid-fetch.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The timeout to apply, five minutes when no provider is usable.</returns>
    internal static TimeSpan ResolveClientTimeout(PluginConfiguration config)
    {
        var seconds = ResolveLiveTvProviders(config)
            .Select(p => p.Provider.TimeoutSeconds)
            .DefaultIfEmpty(300)
            .Max();

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Applies the configured timeout to the shared client. Live TV previously ran every call
    /// on the client defaults, so a provider timeout set in the UI had no effect on the channel
    /// fetch, which is exactly where large catalogs time out.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    private void ApplyClientTimeout(PluginConfiguration config)
    {
        _client.Timeout = ResolveClientTimeout(config);
    }

    internal static IEnumerable<(int Index, ProviderConfig Provider)> ResolveLiveTvProviders(PluginConfiguration config)
    {
        for (var i = 0; i < config.Providers.Count; i++)
        {
            var provider = config.Providers[i];
            if (provider.IsEnabled
                && !string.IsNullOrEmpty(provider.BaseUrl)
                && !string.IsNullOrEmpty(provider.Username))
            {
                yield return (i, provider);
            }
        }
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

            var channelId = XtreamTunerHost.BuildChannelId(channel.ProviderIndex, channel.StreamId);

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
            // Build map: upstream epg_channel_id -> our xtream_ id
            var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in channels)
            {
                if (!string.IsNullOrEmpty(ch.EpgChannelId))
                {
                    idMap[ch.EpgChannelId] = XtreamTunerHost.BuildChannelId(ch.ProviderIndex, ch.StreamId);
                }
            }

            // Prefer upstream XMLTV (preserves category, rating, credits, icon, etc.).
            // Fall back to JSON-based fetch only if the upstream file is unavailable.
            var passthroughCount = 0;
            if (idMap.Count > 0)
            {
                var upstreamXml = await GetMergedXmltvAsync(channels, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(upstreamXml))
                {
                    passthroughCount = AppendUpstreamProgrammes(sb, upstreamXml, idMap, config, cancellationToken);
                    _logger.LogInformation("Passed through {Count} programmes from upstream XMLTV", passthroughCount);
                }
            }

            if (passthroughCount == 0)
            {
                _logger.LogInformation("Upstream XMLTV unavailable or empty; falling back to per-channel JSON EPG");
                var epgData = await FetchEpgDataAsync(channels, config, cancellationToken).ConfigureAwait(false);

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

    private async Task<string?> GetMergedXmltvAsync(List<LiveStreamInfo> channels, CancellationToken cancellationToken)
    {
        var fragments = new List<string>();
        foreach (var providerIndex in channels.Select(c => c.ProviderIndex).Distinct())
        {
            var connectionInfo = Plugin.Instance.GetCreds(providerIndex);
            var xml = await _client.GetXmltvAsync(connectionInfo, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(xml))
            {
                fragments.Add(xml);
            }
        }

        return fragments.Count == 0 ? null : string.Join('\n', fragments);
    }

    private async Task<List<EpgProgram>> FetchEpgDataAsync(
        List<LiveStreamInfo> channels,
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
                var connectionInfo = Plugin.Instance.GetCreds(channel.ProviderIndex);
                var epgListings = await _client.GetSimpleDataTableAsync(connectionInfo, channel.StreamId, cancellationToken).ConfigureAwait(false);

                if (epgListings?.Listings == null)
                {
                    return new List<EpgProgram>();
                }

                // Map channel ID to match the native tuner's xtream_ prefix
                var channelId = XtreamTunerHost.BuildChannelId(channel.ProviderIndex, channel.StreamId);

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
