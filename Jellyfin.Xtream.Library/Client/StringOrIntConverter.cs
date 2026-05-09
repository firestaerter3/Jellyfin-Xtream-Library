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
using System.Globalization;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Library.Client;

/// <summary>
/// Class StringOrIntConverter deserializes integer fields that some Xtream providers
/// return as non-numeric strings (e.g. "N/A"), falling back to 0.
/// </summary>
public class StringOrIntConverter : JsonConverter
{
    /// <inheritdoc />
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(int);
    }

    /// <inheritdoc />
    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.Value == null)
        {
            return 0;
        }

        return reader.TokenType switch
        {
            JsonToken.Integer => Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture),
            JsonToken.String => int.TryParse((string)reader.Value!, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0,
            _ => 0,
        };
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value == null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteValue((int)value);
    }
}
