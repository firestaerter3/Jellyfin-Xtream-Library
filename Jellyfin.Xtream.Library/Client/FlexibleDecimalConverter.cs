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
/// Converts JSON values that may be either numbers or strings to decimal.
/// Handles Xtream API inconsistencies where decimal fields like "rating"
/// are sometimes returned as strings (e.g. "7" instead of 7).
/// </summary>
public class FlexibleDecimalConverter : JsonConverter
{
    /// <inheritdoc />
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(decimal) || objectType == typeof(decimal?);
    }

    /// <inheritdoc />
    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return objectType == typeof(decimal?) ? null : (object)0m;
        }

        decimal result = reader.TokenType switch
        {
            JsonToken.Integer => Convert.ToDecimal(reader.Value, CultureInfo.InvariantCulture),
            JsonToken.Float => Convert.ToDecimal(reader.Value, CultureInfo.InvariantCulture),
            JsonToken.String when decimal.TryParse((string?)reader.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed) => parsed,
            _ => 0m,
        };

        return result;
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is decimal decimalValue)
        {
            writer.WriteValue(decimalValue);
        }
        else
        {
            writer.WriteNull();
        }
    }
}
