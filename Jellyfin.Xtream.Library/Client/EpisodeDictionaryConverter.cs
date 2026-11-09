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
using System.Linq;
using Jellyfin.Xtream.Library.Client.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Xtream.Library.Client;

/// <summary>
/// Converter for the episodes dictionary on SeriesStreamInfo.
/// Handles Xtream API quirks where episodes may be a list instead of an object
/// (empty, a list of seasons, or a flat list of episodes), or where a season
/// contains a single episode object instead of an array of episodes.
/// </summary>
public class EpisodeDictionaryConverter : JsonConverter
{
    /// <inheritdoc/>
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(Dictionary<int, ICollection<Episode>>);
    }

    /// <inheritdoc/>
    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var result = new Dictionary<int, ICollection<Episode>>();

        if (reader.TokenType == JsonToken.StartArray)
        {
            ReadList(JArray.Load(reader), serializer, result);
            return result;
        }

        if (reader.TokenType != JsonToken.StartObject)
        {
            // Null or another non-object token: no episodes.
            return result;
        }

        JObject obj = JObject.Load(reader);
        foreach (var property in obj.Properties())
        {
            if (!int.TryParse(property.Name, out int seasonNumber))
            {
                continue;
            }

            if (property.Value.Type == JTokenType.Array)
            {
                var episodes = property.Value.ToObject<List<Episode>>(serializer) ?? new List<Episode>();
                result[seasonNumber] = episodes;
            }
            else if (property.Value.Type == JTokenType.Object)
            {
                // Single episode object instead of array — wrap in list.
                var episode = property.Value.ToObject<Episode>(serializer);
                result[seasonNumber] = episode is not null ? new List<Episode> { episode } : new List<Episode>();
            }
            else
            {
                result[seasonNumber] = new List<Episode>();
            }
        }

        return result;
    }

    /// <summary>
    /// Reads <c>episodes</c> sent as a JSON list instead of an object (GitHub #193).
    /// PHP's <c>json_encode</c> writes an array whose keys are exactly 0..n-1 as a list, so a series
    /// with a season 0 and no gaps after it arrives as a list of seasons, where the position is the
    /// season number. A list holding only episode objects is grouped by each episode's own season.
    /// An empty list means no episodes.
    /// </summary>
    private static void ReadList(JArray list, JsonSerializer serializer, Dictionary<int, ICollection<Episode>> result)
    {
        bool isListOfSeasons = list.Any(item => item.Type == JTokenType.Array);

        if (isListOfSeasons)
        {
            for (int seasonNumber = 0; seasonNumber < list.Count; seasonNumber++)
            {
                var slot = list[seasonNumber];
                if (slot.Type == JTokenType.Array)
                {
                    result[seasonNumber] = slot.ToObject<List<Episode>>(serializer) ?? new List<Episode>();
                }
                else if (slot.Type == JTokenType.Object)
                {
                    var episode = slot.ToObject<Episode>(serializer);
                    result[seasonNumber] = episode is not null ? new List<Episode> { episode } : new List<Episode>();
                }
            }

            return;
        }

        foreach (var item in list)
        {
            if (item.Type != JTokenType.Object)
            {
                continue;
            }

            var episode = item.ToObject<Episode>(serializer);
            if (episode is null)
            {
                continue;
            }

            if (!result.TryGetValue(episode.Season, out var seasonEpisodes))
            {
                seasonEpisodes = new List<Episode>();
                result[episode.Season] = seasonEpisodes;
            }

            seasonEpisodes.Add(episode);
        }
    }

    /// <inheritdoc/>
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value);
    }
}
