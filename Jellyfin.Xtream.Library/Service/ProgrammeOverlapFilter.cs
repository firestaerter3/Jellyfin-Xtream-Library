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

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Remembers which programmes went into the guide, per channel and title, so a second listing of
/// the same programme can be left out.
/// <para>
/// Some upstream guides merge two listings for one channel. Through Dispatcharr, Tivione's feed
/// carries NPO 1 twice, about 15 minutes apart, and Jellyfin then shows every programme twice,
/// overlapping. Only a programme that overlaps one already kept with the same title is dropped;
/// episodes of the same series that follow each other do not overlap and are all kept.
/// </para>
/// </summary>
internal sealed class ProgrammeOverlapFilter
{
    private readonly Dictionary<(string Channel, string Title), List<(long Start, long Stop)>> _kept = new();

    /// <summary>
    /// Records the programme and returns true, or returns false when it repeats a kept programme.
    /// </summary>
    /// <param name="channel">The channel id the programme belongs to.</param>
    /// <param name="title">The programme title; a programme without one is always kept.</param>
    /// <param name="start">Start, unix seconds.</param>
    /// <param name="stop">Stop, unix seconds.</param>
    /// <returns>True to keep the programme.</returns>
    public bool TryKeep(string channel, string? title, long start, long stop)
    {
        if (string.IsNullOrWhiteSpace(title) || stop <= start)
        {
            return true;
        }

        var key = (channel, title.Trim().ToUpperInvariant());
        if (!_kept.TryGetValue(key, out var spans))
        {
            _kept[key] = new List<(long, long)> { (start, stop) };
            return true;
        }

        foreach (var (keptStart, keptStop) in spans)
        {
            if (start < keptStop && stop > keptStart)
            {
                return false;
            }
        }

        spans.Add((start, stop));
        return true;
    }
}
