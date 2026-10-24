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

#pragma warning disable CS1591
namespace Jellyfin.Xtream.Library.Client.Models;

/// <summary>
/// Raw response shape from Dispatcharr's GET /api/vod/movies/{id}/provider-info/.
/// Field names and shapes verified against a live Dispatcharr instance, not just its source.
/// </summary>
internal class DispatcharrMovieProviderInfoDto
{
    [JsonProperty("stream_id")]
    public string? StreamId { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("o_name")]
    public string? OName { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("plot")]
    public string? Plot { get; set; }

    [JsonProperty("genre")]
    public string? Genre { get; set; }

    [JsonProperty("director")]
    public string? Director { get; set; }

    [JsonProperty("actors")]
    public string? Actors { get; set; }

    [JsonProperty("country")]
    public string? Country { get; set; }

    [JsonProperty("release_date")]
    public string? ReleaseDate { get; set; }

    [JsonProperty("rating")]
    public string? Rating { get; set; }

    [JsonProperty("tmdb_id")]
    public string? TmdbId { get; set; }

    [JsonProperty("youtube_trailer")]
    public string? YoutubeTrailer { get; set; }

    // Confirmed live: this is already a JSON array, not a single string.
    [JsonProperty("backdrop_path")]
    public List<string>? BackdropPath { get; set; }

    [JsonProperty("duration_secs")]
    public int? DurationSecs { get; set; }

    [JsonProperty("bitrate")]
    public int? Bitrate { get; set; }

    [JsonProperty("video")]
    public VideoInfo? Video { get; set; }

    [JsonProperty("audio")]
    public AudioInfo? Audio { get; set; }

    [JsonProperty("container_extension")]
    public string? ContainerExtension { get; set; }
}
