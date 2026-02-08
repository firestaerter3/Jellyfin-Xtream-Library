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
    public int M3uAccountId { get; set; }
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
