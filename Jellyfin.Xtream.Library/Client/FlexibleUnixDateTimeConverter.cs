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
/// Converts Unix timestamps that may be either numbers or strings to DateTime.
/// Handles Xtream API inconsistencies where last_modified is returned as
/// a string (e.g. "1772542351" instead of 1772542351).
/// Supports both DateTime and DateTime? properties.
/// </summary>
public class FlexibleUnixDateTimeConverter : JsonConverter
{
    /// <inheritdoc />
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(DateTime) || objectType == typeof(DateTime?);
    }

    /// <inheritdoc />
    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return objectType == typeof(DateTime?) ? null : (object)DateTime.MinValue;
        }

        long epoch = reader.TokenType switch
        {
            JsonToken.Integer => Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture),
            JsonToken.Float => Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture),
            JsonToken.String when long.TryParse((string?)reader.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) => result,
            _ => 0,
        };

        if (epoch > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
        }

        return objectType == typeof(DateTime?) ? null : (object)DateTime.MinValue;
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is DateTime dt && dt != DateTime.MinValue)
        {
            long epoch = new DateTimeOffset(dt).ToUnixTimeSeconds();
            writer.WriteValue(epoch);
        }
        else
        {
            writer.WriteNull();
        }
    }
}
