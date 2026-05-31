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
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Xtream.Library.Client.Models;

namespace Jellyfin.Xtream.Library.Service.Models;

/// <summary>
/// Point-in-time snapshot of all Live TV channels exposed to Jellyfin.
/// Used to compute add/update/remove deltas between sync runs.
/// </summary>
public class LiveChannelSnapshot
{
    /// <summary>
    /// Gets or sets the snapshot format version.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Gets or sets when this snapshot was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the channels indexed by StreamId.
    /// </summary>
    public Dictionary<int, LiveChannelSnapshotEntry> Channels { get; set; } = new();

    /// <summary>
    /// Builds a snapshot from the current channel list.
    /// </summary>
    /// <param name="channels">The current channels from the provider.</param>
    /// <returns>A new snapshot stamped with the current time.</returns>
    public static LiveChannelSnapshot FromChannels(IEnumerable<LiveStreamInfo> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);

        var snapshot = new LiveChannelSnapshot
        {
            CreatedAt = DateTime.UtcNow,
        };

        foreach (var channel in channels)
        {
            // Last write wins on StreamId collision - matches LiveTvService.GetFilteredChannelsAsync de-dup.
            snapshot.Channels[channel.StreamId] = LiveChannelSnapshotEntry.From(channel);
        }

        return snapshot;
    }

    /// <summary>
    /// Computes a delta between a previous snapshot and the current channel list.
    /// </summary>
    /// <param name="previous">The previous snapshot (null = first run, everything counts as added).</param>
    /// <param name="current">The current channels from the provider.</param>
    /// <returns>The delta describing added/updated/removed/unchanged channels.</returns>
    public static LiveChannelDelta ComputeDelta(LiveChannelSnapshot? previous, IEnumerable<LiveStreamInfo> current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var delta = new LiveChannelDelta();
        var previousChannels = previous?.Channels ?? new Dictionary<int, LiveChannelSnapshotEntry>();
        var seen = new HashSet<int>();

        foreach (var channel in current)
        {
            if (!seen.Add(channel.StreamId))
            {
                // Duplicate StreamId in the input list - already counted.
                continue;
            }

            var newChecksum = LiveChannelSnapshotEntry.ComputeChecksum(channel);

            if (!previousChannels.TryGetValue(channel.StreamId, out var existing))
            {
                delta.AddedStreamIds.Add(channel.StreamId);
            }
            else if (!string.Equals(existing.Checksum, newChecksum, StringComparison.Ordinal))
            {
                delta.UpdatedStreamIds.Add(channel.StreamId);
            }
            else
            {
                delta.UnchangedStreamIds.Add(channel.StreamId);
            }
        }

        foreach (var streamId in previousChannels.Keys)
        {
            if (!seen.Contains(streamId))
            {
                delta.RemovedStreamIds.Add(streamId);
            }
        }

        return delta;
    }
}

/// <summary>
/// Snapshot of a single Live TV channel. Only fields that affect user-visible behavior or guide
/// data are tracked - everything else is ignored to avoid spurious "updated" counts.
/// </summary>
public class LiveChannelSnapshotEntry
{
    /// <summary>
    /// Gets or sets the provider stream identifier.
    /// </summary>
    public int StreamId { get; set; }

    /// <summary>
    /// Gets or sets the channel name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the EPG channel identifier.
    /// </summary>
    public string EpgChannelId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel logo URL.
    /// </summary>
    public string StreamIcon { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel number from the provider.
    /// </summary>
    public int Num { get; set; }

    /// <summary>
    /// Gets or sets the MD5 checksum of the tracked fields (used for change detection).
    /// </summary>
    public string Checksum { get; set; } = string.Empty;

    /// <summary>
    /// Builds a snapshot entry from a live stream.
    /// </summary>
    /// <param name="channel">The live stream from the provider.</param>
    /// <returns>The snapshot entry.</returns>
    public static LiveChannelSnapshotEntry From(LiveStreamInfo channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return new LiveChannelSnapshotEntry
        {
            StreamId = channel.StreamId,
            Name = channel.Name ?? string.Empty,
            EpgChannelId = channel.EpgChannelId ?? string.Empty,
            StreamIcon = channel.StreamIcon ?? string.Empty,
            Num = channel.Num,
            Checksum = ComputeChecksum(channel),
        };
    }

    /// <summary>
    /// Computes the change-detection checksum for a channel. Covers user-visible fields
    /// (name, EPG id, logo, channel number) - other fields are intentionally excluded.
    /// </summary>
    /// <param name="channel">The live stream.</param>
    /// <returns>The MD5 checksum as a hex string.</returns>
#pragma warning disable CA5351 // MD5 used for change detection, not security.
    public static string ComputeChecksum(LiveStreamInfo channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var data = string.Join(
            "|",
            channel.Name ?? string.Empty,
            channel.EpgChannelId ?? string.Empty,
            channel.StreamIcon ?? string.Empty,
            channel.Num.ToString(CultureInfo.InvariantCulture));

        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(data));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
#pragma warning restore CA5351
}

/// <summary>
/// Result of comparing two channel snapshots: which channels were added, updated,
/// removed, or unchanged. StreamIds are used as identifiers throughout.
/// </summary>
public class LiveChannelDelta
{
    /// <summary>
    /// Gets the stream IDs of channels that did not exist in the previous snapshot.
    /// </summary>
    public List<int> AddedStreamIds { get; } = new();

    /// <summary>
    /// Gets the stream IDs of channels whose tracked fields changed.
    /// </summary>
    public List<int> UpdatedStreamIds { get; } = new();

    /// <summary>
    /// Gets the stream IDs of channels that were in the previous snapshot but are absent now.
    /// </summary>
    public List<int> RemovedStreamIds { get; } = new();

    /// <summary>
    /// Gets the stream IDs of channels present in both snapshots with no tracked changes.
    /// </summary>
    public List<int> UnchangedStreamIds { get; } = new();

    /// <summary>
    /// Gets the number of channels added since the previous snapshot.
    /// </summary>
    public int AddedCount => AddedStreamIds.Count;

    /// <summary>
    /// Gets the number of channels whose tracked fields changed.
    /// </summary>
    public int UpdatedCount => UpdatedStreamIds.Count;

    /// <summary>
    /// Gets the number of channels removed since the previous snapshot.
    /// </summary>
    public int RemovedCount => RemovedStreamIds.Count;

    /// <summary>
    /// Gets the number of channels present in both snapshots with no tracked changes.
    /// </summary>
    public int UnchangedCount => UnchangedStreamIds.Count;

    /// <summary>
    /// Gets the total number of channels in the current snapshot
    /// (added + updated + unchanged - mirrors SyncResult.TotalMovies style).
    /// </summary>
    public int TotalChannels => AddedCount + UpdatedCount + UnchangedCount;
}
