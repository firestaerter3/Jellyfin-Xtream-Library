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
/// Response from Xtream get_vod_info endpoint.
/// </summary>
public class VodInfoResponse
{
    [JsonProperty("info")]
    public VodInfoDetails? Info { get; set; }

    [JsonProperty("movie_data")]
    public VodMovieData? MovieData { get; set; }
}

/// <summary>
/// Detailed VOD info including media streams.
/// </summary>
public class VodInfoDetails
{
    [JsonProperty("movie_image")]
    public string? MovieImage { get; set; }

    [JsonProperty("tmdb_id")]
    public string? TmdbId { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("o_name")]
    public string? OriginalName { get; set; }

    [JsonProperty("plot")]
    public string? Plot { get; set; }

    [JsonProperty("cast")]
    public string? Cast { get; set; }

    [JsonProperty("director")]
    public string? Director { get; set; }

    [JsonProperty("genre")]
    public string? Genre { get; set; }

    [JsonProperty("releasedate")]
    public string? ReleaseDate { get; set; }

    [JsonProperty("rating")]
    public string? Rating { get; set; }

    [JsonProperty("duration")]
    public string? Duration { get; set; }

    [JsonProperty("duration_secs")]
    public int? DurationSecs { get; set; }

    [JsonProperty("bitrate")]
    public int? Bitrate { get; set; }

    [JsonProperty("video")]
    [JsonConverter(typeof(OnlyObjectConverter<VideoInfo>))]
    public VideoInfo? Video { get; set; }

    [JsonProperty("audio")]
    [JsonConverter(typeof(OnlyObjectConverter<AudioInfo>))]
    public AudioInfo? Audio { get; set; }
}

/// <summary>
/// Movie data from VOD info response.
/// </summary>
public class VodMovieData
{
    [JsonProperty("stream_id")]
    public int StreamId { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("container_extension")]
    public string ContainerExtension { get; set; } = string.Empty;
}
