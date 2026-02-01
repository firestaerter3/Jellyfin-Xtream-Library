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
using Jellyfin.Xtream.Library.Client.Models;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Parses and applies channel overrides.
/// Format: StreamId=Name|Number|LogoUrl (fields after = are pipe-separated, optional).
/// </summary>
public static class ChannelOverrideParser
{
    private static readonly char[] LineSeparators = ['\n', '\r'];

    /// <summary>
    /// Parses channel overrides from a newline-separated string.
    /// Format: StreamId=Name|Number|LogoUrl
    /// Examples:
    ///   123=BBC One                     (just rename)
    ///   456=CNN|2                       (rename + channel number)
    ///   789=Sky News|5|http://logo.png  (all fields)
    ///   101=|10|                        (just channel number, keep original name).
    /// </summary>
    /// <param name="overridesText">Newline-separated list of overrides.</param>
    /// <returns>Dictionary mapping StreamId to ChannelOverride.</returns>
    public static Dictionary<int, ChannelOverride> Parse(string? overridesText)
    {
        var result = new Dictionary<int, ChannelOverride>();

        if (string.IsNullOrWhiteSpace(overridesText))
        {
            return result;
        }

        var lines = overridesText.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith('#'))
            {
                continue; // Skip empty lines and comments
            }

            var eqIndex = trimmedLine.IndexOf('=', StringComparison.Ordinal);
            if (eqIndex <= 0)
            {
                continue; // Invalid format
            }

            var streamIdStr = trimmedLine[..eqIndex].Trim();
            if (!int.TryParse(streamIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var streamId))
            {
                continue; // Invalid stream ID
            }

            var valueStr = trimmedLine[(eqIndex + 1)..];
            var parts = valueStr.Split('|');

            var channelOverride = new ChannelOverride { StreamId = streamId };

            // Parse name (first part, if not empty)
            if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                channelOverride.Name = parts[0].Trim();
            }

            // Parse number (second part, if valid integer)
            if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
            {
                if (int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    channelOverride.Number = number;
                }
            }

            // Parse logo URL (third part, if not empty)
            if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]))
            {
                channelOverride.LogoUrl = parts[2].Trim();
            }

            result[streamId] = channelOverride;
        }

        return result;
    }

    /// <summary>
    /// Applies a channel override to a LiveStreamInfo, modifying it in place.
    /// </summary>
    /// <param name="channel">The channel to modify.</param>
    /// <param name="channelOverride">The override to apply (null = no changes).</param>
    public static void ApplyOverride(LiveStreamInfo channel, ChannelOverride? channelOverride)
    {
        if (channel == null || channelOverride == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(channelOverride.Name))
        {
            channel.Name = channelOverride.Name;
        }

        if (channelOverride.Number.HasValue)
        {
            channel.Num = channelOverride.Number.Value;
        }

        if (!string.IsNullOrEmpty(channelOverride.LogoUrl))
        {
            channel.StreamIcon = channelOverride.LogoUrl;
        }
    }
}
