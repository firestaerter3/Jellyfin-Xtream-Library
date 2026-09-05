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

using System.Collections.Generic;
using Jellyfin.Xtream.Library.Client.Models;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Builds the channel number Jellyfin sorts and tunes by (GitHub #86).
/// <para>
/// Providers number channels from 1 within each category, so the raw number is not unique across a
/// multi-category selection. Two things follow. The guide interleaves categories, because Jellyfin
/// sorts on the number rather than on the group. And the number is the key
/// <see cref="XtreamTunerHost"/> resolves <c>hdhr_&lt;number&gt;</c> back to a stream with, so a
/// duplicate silently makes one of the two channels unreachable.
/// </para>
/// <para>
/// Prefixing the category id fixes both. The category id is used rather than a running index so the
/// numbers stay stable when the provider adds a category later.
/// </para>
/// </summary>
internal static class ChannelNumbering
{
    /// <summary>
    /// Smallest room reserved per category. Widened when a provider numbers channels beyond it.
    /// </summary>
    internal const int MinimumStride = 1000;

    /// <summary>
    /// Ceiling for the stride. A stride beyond this leaves no room for the category id inside an
    /// int, and channel numbers are an int all the way through Jellyfin.
    /// </summary>
    internal const int MaximumStride = 1000000;

    /// <summary>
    /// Picks the multiplier for the category id: the smallest power of ten that still fits every
    /// channel number in the list, so no category can spill into the next one's range.
    /// </summary>
    /// <param name="channels">Channels about to be numbered.</param>
    /// <returns>The stride to pass to <see cref="Resolve"/>.</returns>
    internal static int ComputeStride(IEnumerable<LiveStreamInfo> channels)
    {
        int highest = 0;
        foreach (var channel in channels)
        {
            if (channel.Num > highest)
            {
                highest = channel.Num;
            }
        }

        int stride = MinimumStride;
        while (stride <= highest && stride < MaximumStride)
        {
            stride *= 10;
        }

        return stride;
    }

    /// <summary>
    /// Returns the number to publish for a channel.
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="stride">Stride from <see cref="ComputeStride"/>.</param>
    /// <param name="byCategory">False returns the provider's own number unchanged.</param>
    /// <returns>The channel number.</returns>
    internal static int Resolve(LiveStreamInfo channel, int stride, bool byCategory)
    {
        if (!byCategory || channel.CategoryId is not int categoryId || categoryId < 0)
        {
            // A channel the provider files under no category has nothing to group by, so it keeps
            // its own number and sorts among the low numbers.
            return channel.Num;
        }

        long composite = ((long)categoryId * stride) + channel.Num;

        // Providers have been seen with category ids in the thousands; combined with a widened
        // stride that can leave the int range. Falling back to the raw number keeps such a channel
        // working, at the cost of it sorting outside its category.
        return composite > int.MaxValue ? channel.Num : (int)composite;
    }
}
