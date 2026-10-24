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
using Jellyfin.Xtream.Library.Client.Models;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Maps the stream ids this plugin knows a Live TV channel by onto Dispatcharr's own channel uuid,
/// so a playlist can be built without the provider password in it (GitHub #113).
/// </summary>
public static class DispatcharrChannelMap
{
    /// <summary>
    /// Builds the lookup from a Dispatcharr channel listing.
    /// <para>
    /// Two keys per channel, because which one matches depends on what the provider's Base URL
    /// points at, and the plugin has no way to ask.
    /// </para>
    /// <para>
    /// Pointed at Dispatcharr's own Xtream emulation, the stream id the plugin receives <em>is</em>
    /// Dispatcharr's channel id, so <c>id</c> is the key that matches. Pointed straight at an
    /// upstream panel, it is that panel's id, which Dispatcharr records against the channel as a
    /// stream. Writing both covers either arrangement.
    /// </para>
    /// <para>
    /// Where the two collide, the channel id wins: it is Dispatcharr's own identifier for this
    /// channel, whereas a stream id that happens to equal it belongs to some other channel and
    /// would otherwise shadow the correct answer.
    /// </para>
    /// </summary>
    /// <param name="channels">The listing, as returned with <c>include_streams=true</c>.</param>
    /// <returns>Stream or channel id to Dispatcharr uuid.</returns>
    public static IReadOnlyDictionary<int, string> Build(IEnumerable<DispatcharrChannel>? channels)
    {
        var map = new Dictionary<int, string>();
        if (channels == null)
        {
            return map;
        }

        foreach (var channel in channels)
        {
            // Only a real uuid. The value goes into the M3U playlist and the media source path as
            // is, so a line break in it would add a channel of Dispatcharr's choosing to the
            // playlist the anonymous endpoints serve.
            if (!IsUuid(channel?.Uuid))
            {
                continue;
            }

            if (channel!.Streams != null)
            {
                foreach (var stream in channel.Streams)
                {
                    // First wins. A later channel claiming the same upstream stream does not get to
                    // take it over.
                    if (stream?.StreamId is int streamId && !map.ContainsKey(streamId))
                    {
                        map[streamId] = channel.Uuid;
                    }
                }
            }

            map[channel.Id] = channel.Uuid;
        }

        return map;
    }

    /// <summary>
    /// Builds the lookup from the same listing to the stream stats Dispatcharr's proxy recorded,
    /// keyed the same two ways as <see cref="Build"/>. Dispatcharr's Xtream emulation never sends
    /// these, so without this every channel is probed by Jellyfin before it plays (homelab-ops #550).
    /// <para>
    /// Under the channel id, the stats of the channel's first stream, because that is the one
    /// Dispatcharr plays; a later stream's stats describe a failover that may never be used. Under
    /// an upstream stream id, that stream's own stats.
    /// </para>
    /// </summary>
    /// <param name="channels">The listing, as returned with <c>include_streams=true</c>.</param>
    /// <returns>Stream or channel id to stats. Ids without readable stats are left out.</returns>
    public static IReadOnlyDictionary<int, StreamStatsInfo> BuildStats(IEnumerable<DispatcharrChannel>? channels)
    {
        var byStream = new Dictionary<int, StreamStatsInfo>();
        var byChannel = new Dictionary<int, StreamStatsInfo>();
        if (channels == null)
        {
            return byChannel;
        }

        foreach (var channel in channels)
        {
            if (channel?.Streams == null || channel.Streams.Count == 0)
            {
                continue;
            }

            foreach (var stream in channel.Streams)
            {
                if (stream?.StreamId is int streamId && !byStream.ContainsKey(streamId) && stream.ReadStreamStats() is { } own)
                {
                    byStream[streamId] = own;
                }
            }

            if (channel.Streams[0]?.ReadStreamStats() is { } first)
            {
                byChannel[channel.Id] = first;
            }
        }

        // Channel ids win over stream ids, as in Build.
        foreach (var pair in byStream)
        {
            byChannel.TryAdd(pair.Key, pair.Value);
        }

        return byChannel;
    }

    /// <summary>
    /// Whether a value is a uuid, as Dispatcharr's channel and movie uuids are.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>True for a uuid.</returns>
    /// <remarks>The length check matters: the parser trims whitespace, so a uuid with a line break
    /// beside it would otherwise pass and the line break would reach the playlist.</remarks>
    public static bool IsUuid(string? value) => value?.Length == 36 && Guid.TryParseExact(value, "D", out _);

    /// <summary>
    /// The credential-free URL for one channel.
    /// </summary>
    /// <param name="dispatcharrBaseUrl">Dispatcharr's own base URL, which is not necessarily the
    /// Xtream one.</param>
    /// <param name="uuid">The channel uuid.</param>
    /// <returns>A playable URL carrying no credentials.</returns>
    public static string BuildProxyStreamUrl(string? dispatcharrBaseUrl, string uuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uuid);

        var trimmed = (dispatcharrBaseUrl ?? string.Empty).TrimEnd('/');
        return string.Create(CultureInfo.InvariantCulture, $"{trimmed}/proxy/ts/stream/{uuid}");
    }
}
