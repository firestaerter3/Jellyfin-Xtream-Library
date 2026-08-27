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
using System.Globalization;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Library.Client;

/// <summary>
/// Reads an int array from whichever shape a provider actually sends: a JSON array of numbers or
/// of numeric strings, a bare scalar, or a comma-separated string.
/// <para>
/// Xtream providers are inconsistent about collection fields in the same way they are about
/// <c>added</c> (see <see cref="Models.LiveStreamInfo.Added"/> and GitHub #41). A plain
/// <c>int[]</c> throws on any shape but the array form, and because these models are deserialized
/// on the shared fetch path, one such channel fails the entire request - taking down Live TV modes
/// that never read the field. Anything unrecognisable degrades to null or is skipped instead:
/// losing one channel's secondary category membership is survivable, failing the fetch is not.
/// </para>
/// </summary>
public class FlexibleIntArrayConverter : JsonConverter
{
    /// <inheritdoc />
    public override bool CanConvert(Type objectType) => objectType == typeof(int[]);

    /// <inheritdoc />
    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(reader);

        switch (reader.TokenType)
        {
            case JsonToken.Null:
            case JsonToken.Undefined:
                return null;

            case JsonToken.StartArray:
                return ReadArray(reader);

            case JsonToken.Integer:
                return TryReadScalar(reader, out var single) ? new[] { single } : Array.Empty<int>();

            case JsonToken.String:
                return ParseDelimited(reader.Value as string);

            default:
                // An object, a bool, anything else: consume it whole so the reader stays aligned
                // on the next property, and report "no membership known".
                reader.Skip();
                return null;
        }
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is not int[] values)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in values)
        {
            writer.WriteValue(item);
        }

        writer.WriteEndArray();
    }

    private static int[] ReadArray(JsonReader reader)
    {
        var values = new List<int>();

        while (reader.Read() && reader.TokenType != JsonToken.EndArray)
        {
            if (TryReadScalar(reader, out var parsed))
            {
                values.Add(parsed);
                continue;
            }

            // A nested array or object would otherwise leave the reader inside it.
            if (reader.TokenType is JsonToken.StartArray or JsonToken.StartObject)
            {
                reader.Skip();
            }
        }

        return values.ToArray();
    }

    private static int[] ParseDelimited(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<int>();
        }

        var values = new List<int>();
        foreach (var part in raw.Split(','))
        {
            if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                values.Add(parsed);
            }
        }

        return values.ToArray();
    }

    private static bool TryReadScalar(JsonReader reader, out int value)
    {
        value = 0;

        switch (reader.TokenType)
        {
            case JsonToken.Integer:
                // Via long: a provider sending something wider than int must not throw here.
                var wide = Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture);
                if (wide is < int.MinValue or > int.MaxValue)
                {
                    return false;
                }

                value = (int)wide;
                return true;

            case JsonToken.String:
                return int.TryParse(
                    reader.Value as string,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value);

            default:
                return false;
        }
    }
}
