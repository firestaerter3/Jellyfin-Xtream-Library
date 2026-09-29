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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Xtream.Library.Client.Models;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Gathers, during one EPG build, what the guide said about the channels catch-up can browse
/// (GitHub #108).
/// <para>
/// Only channels with an archive, and only while catch-up browsing is switched on: nothing else
/// ever reads the recorded guide. Safe to fill from several threads, since the JSON fallback
/// fetches channels in parallel.
/// </para>
/// </summary>
internal sealed class CatchupHistoryCollector
{
    private readonly Dictionary<string, List<LiveStreamInfo>> _byEpgChannelId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int ProviderIndex, int StreamId), (string ProviderKey, int Days)> _channels = new();
    private readonly ConcurrentDictionary<(int ProviderIndex, int StreamId), ConcurrentQueue<CatchupHistoryEntry>> _entries = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CatchupHistoryCollector"/> class.
    /// </summary>
    /// <param name="channels">The channels of the EPG build.</param>
    /// <param name="config">The plugin configuration.</param>
    public CatchupHistoryCollector(IEnumerable<LiveStreamInfo> channels, PluginConfiguration config)
    {
        IsEnabled = config.EnableLiveTv && config.EnableCatchup && config.ShowCatchupInJellyfin;
        if (!IsEnabled)
        {
            return;
        }

        var providerKeys = LiveTvService.BuildProviderFingerprints(config);
        foreach (var channel in CatchupPlanner.CatchupChannels(channels))
        {
            int days = CatchupPlanner.DayCount(config.CatchupDays, channel.TvArchiveDuration);
            if (days <= 0
                || !providerKeys.TryGetValue(channel.ProviderIndex, out var providerKey)
                || !_channels.TryAdd((channel.ProviderIndex, channel.StreamId), (providerKey, days)))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(channel.EpgChannelId))
            {
                if (!_byEpgChannelId.TryGetValue(channel.EpgChannelId, out var list))
                {
                    list = new List<LiveStreamInfo>();
                    _byEpgChannelId[channel.EpgChannelId] = list;
                }

                list.Add(channel);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether anything is recorded at all.
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>
    /// Whether programmes for an upstream XMLTV channel id are recorded.
    /// </summary>
    /// <param name="epgChannelId">The channel attribute of an upstream programme.</param>
    /// <returns>True when a catch-up channel carries that EPG id.</returns>
    public bool Records(string? epgChannelId)
        => !string.IsNullOrEmpty(epgChannelId) && _byEpgChannelId.ContainsKey(epgChannelId);

    /// <summary>
    /// Adds a programme from the upstream XMLTV to every catch-up channel carrying its EPG id.
    /// </summary>
    /// <param name="epgChannelId">The channel attribute of the programme.</param>
    /// <param name="entry">The programme.</param>
    public void Add(string epgChannelId, CatchupHistoryEntry entry)
    {
        if (!_byEpgChannelId.TryGetValue(epgChannelId, out var channels))
        {
            return;
        }

        foreach (var channel in channels)
        {
            Queue(channel).Enqueue(entry);
        }
    }

    /// <summary>
    /// Adds one channel's programmes from the provider's JSON EPG.
    /// </summary>
    /// <param name="channel">The channel they were fetched for.</param>
    /// <param name="programmes">The programmes, title and description base64.</param>
    public void Add(LiveStreamInfo channel, IEnumerable<EpgProgram> programmes)
    {
        if (!_channels.ContainsKey((channel.ProviderIndex, channel.StreamId)))
        {
            return;
        }

        var queue = Queue(channel);
        foreach (var programme in programmes)
        {
            queue.Enqueue(CatchupHistoryStore.FromEpg(programme));
        }
    }

    /// <summary>
    /// One pull per catch-up channel, including those the guide said nothing about, so their
    /// stored history is still cut back to the archive horizon.
    /// </summary>
    /// <returns>The pulls.</returns>
    public IReadOnlyCollection<CatchupHistoryPull> ToPulls()
        => _channels
            .Select(pair => new CatchupHistoryPull(
                pair.Value.ProviderKey,
                pair.Key.StreamId,
                pair.Value.Days,
                _entries.TryGetValue(pair.Key, out var queue) ? queue.ToList() : new List<CatchupHistoryEntry>()))
            .ToList();

    private ConcurrentQueue<CatchupHistoryEntry> Queue(LiveStreamInfo channel)
        => _entries.GetOrAdd((channel.ProviderIndex, channel.StreamId), _ => new ConcurrentQueue<CatchupHistoryEntry>());
}
