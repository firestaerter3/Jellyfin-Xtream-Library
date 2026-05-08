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
using System.Globalization;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Library.Client;

/// <summary>
/// Converts the Xtream API "added" field which may be a Unix timestamp (integer or numeric string)
/// or a formatted date string such as "dd/MM/yyyy HH:mm:ss".
/// </summary>
public class FlexibleUnixDateTimeConverter : JsonConverter
{
    // MM/dd must be tried before dd/MM: when day > 12 the wrong format fails fast via
    // TryParseExact, but for ambiguous dates (day ≤ 12) MM/dd wins — consistent with
    // how the Xtream API formats dates on providers that mix both conventions.
    private static readonly string[] DateFormats =
    [
        "MM/dd/yyyy HH:mm:ss",
        "dd/MM/yyyy HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd",
    ];

    /// <inheritdoc />
    public override bool CanConvert(Type objectType)
    {
        var underlying = Nullable.GetUnderlyingType(objectType) ?? objectType;
        return underlying == typeof(DateTime);
    }

    /// <inheritdoc />
    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        bool isNullable = Nullable.GetUnderlyingType(objectType) != null;

        if (reader.TokenType == JsonToken.Null)
        {
            return isNullable ? null : (object)default(DateTime);
        }

        if (reader.TokenType == JsonToken.Integer)
        {
            long unixSeconds = Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture);
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        }

        if (reader.TokenType == JsonToken.String)
        {
            string raw = (string)reader.Value!;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return isNullable ? null : (object)default(DateTime);
            }

            // Numeric string — treat as Unix timestamp
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixSeconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            }

            // Formatted date string
            if (DateTime.TryParseExact(raw, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
            {
                return parsed;
            }

            // Last-resort fallback
            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fallback))
            {
                return fallback;
            }

            return isNullable ? null : (object)default(DateTime);
        }

        throw new JsonSerializationException(
            $"Unexpected token type '{reader.TokenType}' when deserializing DateTime.");
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is DateTime dt)
        {
            writer.WriteValue(new DateTimeOffset(dt).ToUnixTimeSeconds());
        }
        else
        {
            writer.WriteNull();
        }
    }
}
