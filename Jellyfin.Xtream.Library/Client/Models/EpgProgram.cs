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

using Newtonsoft.Json;

#pragma warning disable CS1591
namespace Jellyfin.Xtream.Library.Client.Models;

/// <summary>
/// Represents an EPG (Electronic Program Guide) program entry.
/// </summary>
public class EpgProgram
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    [JsonProperty("epg_id")]
    public string EpgId { get; set; } = string.Empty;

    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;

    [JsonProperty("lang")]
    public string Language { get; set; } = string.Empty;

    [JsonProperty("start")]
    public string Start { get; set; } = string.Empty;

    [JsonProperty("end")]
    public string End { get; set; } = string.Empty;

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;

    [JsonProperty("channel_id")]
    public string ChannelId { get; set; } = string.Empty;

    [JsonProperty("start_timestamp")]
    public long StartTimestamp { get; set; }

    [JsonProperty("stop_timestamp")]
    public long StopTimestamp { get; set; }

    [JsonProperty("has_archive")]
    [JsonConverter(typeof(StringBoolConverter))]
    public bool HasArchive { get; set; }
}
