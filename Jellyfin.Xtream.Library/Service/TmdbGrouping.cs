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
using System.Linq;
using Jellyfin.Xtream.Library.Service.Models;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Works out which movie folders hold the same film.
/// </summary>
public static class TmdbGrouping
{
    /// <summary>
    /// Whether an id from this source may be merged on.
    /// <para>
    /// Only what the provider itself returned, or what the user pinned by hand. An id a name
    /// lookup guessed is refused, because a lookup can give a film and its remake the same id and
    /// merging those is not undoable. An id read back off a folder name is refused for the same
    /// reason: the name does not record where it came from.
    /// </para>
    /// </summary>
    /// <param name="source">Where the id came from.</param>
    /// <returns>True when the id may be grouped on.</returns>
    public static bool IsGroupable(ItemIdSource source)
        => source is ItemIdSource.Provider or ItemIdSource.Override;

    /// <summary>
    /// The folder each groupable TMDB id should live in, according to what the snapshot recorded.
    /// <para>
    /// The lowest stream id in a group decides the name, so the choice does not depend on the
    /// order a sync happened to process things in, and does not move again on the next run.
    /// </para>
    /// </summary>
    /// <param name="snapshot">Snapshot to read, may be null.</param>
    /// <returns>TMDB id to the owning stream and its folder.</returns>
    public static Dictionary<int, (int OwnerStreamId, string FolderName)> BuildFolderMap(ContentSnapshot? snapshot)
    {
        var map = new Dictionary<int, (int OwnerStreamId, string FolderName)>();
        if (snapshot == null)
        {
            return map;
        }

        foreach (var group in GroupableMovies(snapshot).GroupBy(m => m.TmdbId!.Value))
        {
            var winner = group.OrderBy(m => m.StreamId).First();
            map[group.Key] = (winner.StreamId, winner.FolderName);
        }

        return map;
    }

    private static IEnumerable<MovieSnapshot> GroupableMovies(ContentSnapshot snapshot)
        => snapshot.Movies.Values.Where(m =>
            m.TmdbId.HasValue
            && IsGroupable(m.TmdbIdSource)
            && !string.IsNullOrEmpty(m.FolderName));
}
