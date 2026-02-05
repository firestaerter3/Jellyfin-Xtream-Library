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
using Jellyfin.Xtream.Library.Client.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Xtream.Library.Client;

/// <summary>
/// Converter for the episodes dictionary on SeriesStreamInfo.
/// Handles Xtream API quirks where episodes may be an empty array instead of
/// an empty object, or where a season contains a single episode object instead
/// of an array of episodes.
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

        if (reader.TokenType != JsonToken.StartObject)
        {
            // Empty array, null, or other non-object token — skip and return empty dict.
            if (reader.TokenType == JsonToken.StartArray)
            {
                JToken.Load(reader);
            }

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

    /// <inheritdoc/>
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        serializer.Serialize(writer, value);
    }
}
