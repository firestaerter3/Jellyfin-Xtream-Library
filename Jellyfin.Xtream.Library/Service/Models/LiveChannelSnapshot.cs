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
    /// Format version of a snapshot that carries everything M3U rendering needs.
    /// Version 1 tracked only change-detection fields.
    /// </summary>
    public const int RenderableVersion = 2;

    /// <summary>
    /// Gets or sets the snapshot format version.
    /// </summary>
    public int Version { get; set; } = RenderableVersion;

    /// <summary>
    /// Gets or sets when this snapshot was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the channels indexed by <see cref="ChannelKey(int, int)"/>. Keyed on provider *and*
    /// stream id: <c>GetFilteredChannelsAsync</c> de-dupes by stream id within a provider but
    /// deliberately allows the same id across providers, so a stream-id-only key silently drops
    /// one of them.
    /// </summary>
    public Dictionary<string, LiveChannelSnapshotEntry> Channels { get; set; } = new();

    /// <summary>
    /// Gets or sets the category id to category name map, so group titles survive a restart
    /// without re-fetching the category list.
    /// </summary>
    public Dictionary<int, string> Categories { get; set; } = new();

    /// <summary>
    /// Gets or sets the provider index to identity fingerprint map. <c>ProviderIndex</c> is
    /// positional, so without this a reordered or replaced provider list would resolve a stored
    /// index against a different provider and build stream URLs with the wrong credentials.
    /// </summary>
    public Dictionary<int, string> Providers { get; set; } = new();

    /// <summary>
    /// Builds a snapshot from the current channel list.
    /// </summary>
    /// <param name="channels">The current channels from the provider.</param>
    /// <param name="categoryNames">Optional category id to name map to persist alongside the channels.</param>
    /// <param name="providerFingerprints">Optional provider index to identity fingerprint map.</param>
    /// <returns>A new snapshot stamped with the current time.</returns>
    public static LiveChannelSnapshot FromChannels(
        IEnumerable<LiveStreamInfo> channels,
        IReadOnlyDictionary<int, string>? categoryNames = null,
        IReadOnlyDictionary<int, string>? providerFingerprints = null)
    {
        ArgumentNullException.ThrowIfNull(channels);

        var snapshot = new LiveChannelSnapshot
        {
            CreatedAt = DateTime.UtcNow,
        };

        foreach (var channel in channels)
        {
            // Last write wins within a provider, matching GetFilteredChannelsAsync's per-provider
            // de-dup. Across providers the ids stay distinct because the key includes the index.
            snapshot.Channels[ChannelKey(channel)] = LiveChannelSnapshotEntry.From(channel);
        }

        if (categoryNames != null)
        {
            foreach (var (id, name) in categoryNames)
            {
                snapshot.Categories[id] = name;
            }
        }

        if (providerFingerprints != null)
        {
            foreach (var (index, fingerprint) in providerFingerprints)
            {
                snapshot.Providers[index] = fingerprint;
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Checks that every provider index the stored channels point at still identifies the same
    /// provider. Returns false when a referenced index is missing, changed, or was never
    /// recorded, in which case the snapshot must not be rendered from.
    /// </summary>
    /// <param name="currentFingerprints">The current provider index to fingerprint map.</param>
    /// <returns>True when the snapshot's provider indices are still valid.</returns>
    public bool MatchesProviders(IReadOnlyDictionary<int, string> currentFingerprints)
    {
        ArgumentNullException.ThrowIfNull(currentFingerprints);

        foreach (var index in Channels.Values.Select(c => c.ProviderIndex).Distinct())
        {
            if (!Providers.TryGetValue(index, out var stored)
                || !currentFingerprints.TryGetValue(index, out var current)
                || !string.Equals(stored, current, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Rebuilds the channel list from this snapshot so output can be produced without calling
    /// the provider. Returns null for a pre-<see cref="RenderableVersion"/> snapshot: those files
    /// predate the fields rendering needs, and defaulting them would invent data (a defaulted
    /// ProviderIndex builds stream URLs with the wrong provider's credentials).
    /// </summary>
    /// <returns>The channels, or null when this snapshot cannot be rendered from.</returns>
    public List<LiveStreamInfo>? ToChannels()
    {
        if (Version < RenderableVersion)
        {
            return null;
        }

        return Channels.Values
            .Select(entry => new LiveStreamInfo
            {
                StreamId = entry.StreamId,
                Name = entry.Name,
                EpgChannelId = entry.EpgChannelId,
                StreamIcon = entry.StreamIcon,
                Num = entry.Num,
                Tags = entry.Tags,
                CategoryId = entry.CategoryId,
                CategoryIds = entry.CategoryIds,
                TvArchive = entry.TvArchive,
                TvArchiveDuration = entry.TvArchiveDuration,
                ProviderIndex = entry.ProviderIndex,
                IsAdult = entry.IsAdult,
            })
            .ToList();
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
        var previousChannels = previous?.Channels ?? new Dictionary<string, LiveChannelSnapshotEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var channel in current)
        {
            if (!seen.Add(ChannelKey(channel)))
            {
                // Same provider and stream id seen twice in the input list - already counted.
                continue;
            }

            var newChecksum = LiveChannelSnapshotEntry.ComputeChecksum(channel);

            if (!previousChannels.TryGetValue(ChannelKey(channel), out var existing))
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

        foreach (var (key, entry) in previousChannels)
        {
            if (!seen.Contains(key))
            {
                delta.RemovedStreamIds.Add(entry.StreamId);
            }
        }

        return delta;
    }

    /// <summary>
    /// Builds the snapshot key for a channel. Provider index is part of the key because the same
    /// stream id can legitimately come from two different providers.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The snapshot key.</returns>
    public static string ChannelKey(LiveStreamInfo channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return ChannelKey(channel.ProviderIndex, channel.StreamId);
    }

    /// <summary>
    /// Builds the snapshot key from a provider index and stream id.
    /// </summary>
    /// <param name="providerIndex">The provider index.</param>
    /// <param name="streamId">The stream id.</param>
    /// <returns>The snapshot key.</returns>
    public static string ChannelKey(int providerIndex, int streamId)
        => string.Create(CultureInfo.InvariantCulture, $"{providerIndex}:{streamId}");
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
    /// Gets or sets the channel tags from overrides.
    /// </summary>
    public string[]? Tags { get; set; }

    /// <summary>
    /// Gets or sets the MD5 checksum of the tracked fields (used for change detection).
    /// </summary>
    public string Checksum { get; set; } = string.Empty;

    // The fields below exist so output can be rebuilt from the snapshot without calling the
    // provider. They are deliberately outside ComputeChecksum: feeding them into it would make
    // the first refresh after upgrading report every channel as updated.

    /// <summary>
    /// Gets or sets the provider category id, used for the M3U group title.
    /// </summary>
    public int? CategoryId { get; set; }

    /// <summary>
    /// Gets or sets the channel's full category membership, on providers that report more than the
    /// primary one. Persisted so the exclude-categories filter re-applied when rendering from a
    /// snapshot sees the same membership the fetch-time filter did; without it a channel excluded
    /// through a secondary category would survive until the next refresh. Older snapshots simply
    /// have no value here, which reads as "primary category only". See GitHub #79.
    /// </summary>
    public int[]? CategoryIds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the channel supports catch-up.
    /// </summary>
    public bool TvArchive { get; set; }

    /// <summary>
    /// Gets or sets the number of catch-up days the channel offers.
    /// </summary>
    public int TvArchiveDuration { get; set; }

    /// <summary>
    /// Gets or sets the index of the provider this channel came from. Required to build stream
    /// URLs with the right credentials on multi-provider setups.
    /// </summary>
    public int ProviderIndex { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the provider flags this channel as adult.
    /// Persisted so the adult filter can be re-applied when rendering from a snapshot, instead
    /// of relying on whatever the filter happened to be when the snapshot was taken.
    /// </summary>
    public bool IsAdult { get; set; }

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
            Tags = channel.Tags,
            Checksum = ComputeChecksum(channel),
            CategoryId = channel.CategoryId,
            CategoryIds = channel.CategoryIds,
            TvArchive = channel.TvArchive,
            TvArchiveDuration = channel.TvArchiveDuration,
            ProviderIndex = channel.ProviderIndex,
            IsAdult = channel.IsAdult,
        };
    }

    /// <summary>
    /// Computes the change-detection checksum for a channel. Covers user-visible fields
    /// (name, EPG id, logo, channel number, tags) - other fields are intentionally excluded.
    /// </summary>
    /// <param name="channel">The live stream.</param>
    /// <returns>The MD5 checksum as a hex string.</returns>
#pragma warning disable CA5351 // MD5 used for change detection, not security.
    public static string ComputeChecksum(LiveStreamInfo channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var tagsStr = channel.Tags != null ? string.Join(",", channel.Tags) : string.Empty;
        var data = string.Join(
            "|",
            channel.Name ?? string.Empty,
            channel.EpgChannelId ?? string.Empty,
            channel.StreamIcon ?? string.Empty,
            channel.Num.ToString(CultureInfo.InvariantCulture),
            tagsStr);

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
