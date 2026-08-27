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

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Parses the Xtream "added" field into a <see cref="DateTime"/>.
/// Providers are inconsistent about the format of this field: most return a Unix
/// timestamp (e.g. "1622986585"), but some return an already-formatted date/time
/// string (e.g. "22/05/2024 10:00:00"). See GitHub #41 for the underlying provider
/// quirk (also noted next to <see cref="Client.Models.StreamInfo.Added"/> and
/// <see cref="Client.Models.LiveStreamInfo.Added"/>, which keep the raw string for
/// the same reason).
/// This parser never throws: any unparseable, missing, or out-of-range value simply
/// results in <see langword="null"/>, so a single misbehaving provider response can
/// never break a sync.
/// </summary>
public static class AddedDateParser
{
    // Known formatted-date variants seen in the wild across Xtream panels.
    private static readonly string[] KnownFormats =
    [
        "dd/MM/yyyy HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "MM/dd/yyyy HH:mm:ss",
        "dd-MM-yyyy HH:mm:ss",
        "yyyy-MM-dd",
        "dd/MM/yyyy",
    ];

    // Unix timestamps outside this range are almost certainly a provider glitch
    // (e.g. "0", or a value in milliseconds instead of seconds) rather than a
    // genuine "added" date, so they are treated as unparseable.
    private static readonly DateTime MinPlausibleDate = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Parses a raw "added" value into a <see cref="DateTime"/>, tolerating both
    /// Unix timestamps and formatted date strings, and never throwing.
    /// </summary>
    /// <param name="added">The raw "added" value from the Xtream API.</param>
    /// <returns>The parsed date, or <see langword="null"/> if it could not be parsed.</returns>
    public static DateTime? Parse(string? added)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(added))
            {
                return null;
            }

            var trimmed = added.Trim();

            // Purely numeric values are treated as Unix timestamps (seconds).
            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            {
                if (unixSeconds <= 0)
                {
                    return null;
                }

                var fromUnix = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
                return IsPlausible(fromUnix) ? fromUnix : null;
            }

            // Fall back to known formatted-date variants used by some providers.
            if (DateTime.TryParseExact(trimmed, KnownFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            {
                return IsPlausible(exact) ? exact : null;
            }

            // Last resort: a generic, culture-invariant parse for anything else.
            if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var generic))
            {
                return IsPlausible(generic) ? generic : null;
            }

            return null;
        }
        catch (Exception)
        {
            // Defensive: no malformed provider response may ever throw out of this parser.
            return null;
        }
    }

    private static bool IsPlausible(DateTime value)
    {
        return value >= MinPlausibleDate && value <= DateTime.UtcNow.AddDays(1);
    }
}
