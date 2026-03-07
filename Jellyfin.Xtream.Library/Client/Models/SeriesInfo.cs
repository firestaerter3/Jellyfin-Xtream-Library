// Copyright (C) 2022  Kevin Jilissen

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
using Newtonsoft.Json;

#pragma warning disable CS1591
namespace Jellyfin.Xtream.Library.Client.Models;

public class SeriesInfo
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("cover")]
    public string Cover { get; set; } = string.Empty;

    [JsonProperty("plot")]
    public string Plot { get; set; } = string.Empty;

    [JsonProperty("cast")]
    public string Cast { get; set; } = string.Empty;

    [JsonProperty("director")]
    public string Director { get; set; } = string.Empty;

    [JsonProperty("genre")]
    public string Genre { get; set; } = string.Empty;

    [JsonConverter(typeof(FlexibleUnixDateTimeConverter))]
    [JsonProperty("last_modified")]
    public DateTime LastModified { get; set; }

    [JsonConverter(typeof(FlexibleDecimalConverter))]
    [JsonProperty("rating")]
    public decimal Rating { get; set; }

    [JsonConverter(typeof(FlexibleDecimalConverter))]
    [JsonProperty("rating_5based")]
    public decimal Rating5Based { get; set; }

    [JsonConverter(typeof(SingularToListConverter<string>))]
    [JsonProperty("backdrop_path")]
    #pragma warning disable CA2227
    public ICollection<string> BackdropPaths { get; set; } = new List<string>();
    #pragma warning restore CA2227

    [JsonProperty("youtube_trailer")]
    public string YoutubeTrailer { get; set; } = string.Empty;

    [JsonConverter(typeof(FlexibleIntConverter))]
    [JsonProperty("episode_run_time")]
    public int EpisodeRunTime { get; set; }

    [JsonConverter(typeof(FlexibleIntConverter))]
    [JsonProperty("category_id")]
    public int CategoryId { get; set; }

    [JsonProperty("tmdb")]
    public string? Tmdb { get; set; }

    [JsonProperty("imdb")]
    public string? Imdb { get; set; }
}
