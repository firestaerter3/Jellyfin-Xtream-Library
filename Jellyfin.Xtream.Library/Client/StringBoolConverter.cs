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
/// Class StringBoolConverter converts "1"/"0" strings to booleans.
/// </summary>
public class StringBoolConverter : JsonConverter
{
    /// <inheritdoc />
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(string);
    }

    /// <inheritdoc />
    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.Value == null)
        {
            return false;
        }

        return reader.TokenType switch
        {
            JsonToken.String => "1".Equals((string)reader.Value!, StringComparison.Ordinal),
            JsonToken.Integer => Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture) == 1,
            JsonToken.Boolean => (bool)reader.Value!,
            _ => false,
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

        string result = (bool)value ? "1" : "0";
        writer.WriteValue(result);
    }
}
