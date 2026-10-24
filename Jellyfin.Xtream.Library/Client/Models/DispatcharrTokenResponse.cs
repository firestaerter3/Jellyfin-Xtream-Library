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

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#pragma warning disable CS1591
namespace Jellyfin.Xtream.Library.Client.Models;

public class DispatcharrTokenResponse
{
    [JsonProperty("access")]
    public string Access { get; set; } = string.Empty;

    [JsonProperty("refresh")]
    public string Refresh { get; set; } = string.Empty;
}

public class DispatcharrMovieProvider
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("stream_id")]
    public int StreamId { get; set; }

    [JsonProperty("m3u_account")]
    public DispatcharrM3uAccount? M3uAccount { get; set; }
}

public class DispatcharrM3uAccount
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}

public class DispatcharrMovieDetail
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("uuid")]
    public string Uuid { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}

public class DispatcharrChannel
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("uuid")]
    public string Uuid { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    // Present only when the listing was asked for with include_streams=true. Each entry carries the
    // stream_id of the upstream provider stream behind this channel (GitHub #113).
    [JsonProperty("streams")]
    public List<DispatcharrChannelSource>? Streams { get; set; }
}

public class DispatcharrChannelSource
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("stream_id")]
    public int? StreamId { get; set; }

    // Kept as raw JSON and read through ReadStreamStats, so a field of an unexpected type only
    // loses this one stream's stats. Deserializing it typed would throw for the whole listing, and
    // the listing is also what keeps the password out of the Live TV URLs (GitHub #113).
    [JsonProperty("stream_stats")]
    public JToken? StreamStatsJson { get; set; }

    /// <summary>
    /// The stats Dispatcharr's proxy recorded for this stream, or null when there are none or
    /// they cannot be read.
    /// </summary>
    /// <returns>The stats, or null.</returns>
    public StreamStatsInfo? ReadStreamStats()
    {
        if (StreamStatsJson is not JObject obj)
        {
            return null;
        }

        try
        {
            var stats = obj.ToObject<StreamStatsInfo>();
            return string.IsNullOrWhiteSpace(stats?.VideoCodec) ? null : stats;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (System.FormatException)
        {
            return null;
        }
        catch (System.InvalidCastException)
        {
            return null;
        }
        catch (System.OverflowException)
        {
            return null;
        }
    }
}
