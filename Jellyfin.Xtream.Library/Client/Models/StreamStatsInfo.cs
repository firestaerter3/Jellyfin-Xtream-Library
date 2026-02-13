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
/// Stream statistics from Dispatcharr/StreamFlow probing.
/// </summary>
public class StreamStatsInfo
{
    [JsonProperty("resolution")]
    public string? Resolution { get; set; }

    [JsonProperty("video_codec")]
    public string? VideoCodec { get; set; }

    [JsonProperty("audio_codec")]
    public string? AudioCodec { get; set; }

    [JsonProperty("source_fps")]
    public double? SourceFps { get; set; }

    [JsonProperty("ffmpeg_output_bitrate")]
    public int? Bitrate { get; set; }
}
