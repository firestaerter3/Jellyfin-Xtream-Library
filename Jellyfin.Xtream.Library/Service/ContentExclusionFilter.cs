// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System.Collections.Generic;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Helpers for filtering individual VOD/Series items out of a sync run based on
/// the per-provider exclusion lists (<see cref="Jellyfin.Xtream.Library.ProviderConfig.ExcludedVodStreamIds"/>
/// and <see cref="Jellyfin.Xtream.Library.ProviderConfig.ExcludedSeriesIds"/>).
/// </summary>
internal static class ContentExclusionFilter
{
    /// <summary>
    /// Builds a lookup set from an exclusion ID array. Null or empty input yields an empty set.
    /// </summary>
    /// <param name="excludedIds">The configured exclusion IDs, may be null.</param>
    /// <returns>A hash set of the excluded IDs.</returns>
    public static HashSet<int> BuildSet(int[]? excludedIds)
    {
        if (excludedIds == null || excludedIds.Length == 0)
        {
            return new HashSet<int>();
        }

        return new HashSet<int>(excludedIds);
    }

    /// <summary>
    /// Returns true if the given item ID is present in the exclusion set.
    /// An empty set always returns false (nothing excluded).
    /// </summary>
    /// <param name="excludedSet">The exclusion set from <see cref="BuildSet"/>.</param>
    /// <param name="itemId">The stream or series ID to test.</param>
    /// <returns>True if the item should be excluded.</returns>
    public static bool IsExcluded(HashSet<int> excludedSet, int itemId)
        => excludedSet.Count != 0 && excludedSet.Contains(itemId);
}
