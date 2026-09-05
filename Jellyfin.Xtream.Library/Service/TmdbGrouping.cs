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
    /// <returns>TMDB id to folder name.</returns>
    public static Dictionary<int, string> BuildFolderMap(ContentSnapshot? snapshot)
    {
        var map = new Dictionary<int, string>();
        if (snapshot == null)
        {
            return map;
        }

        foreach (var group in GroupableMovies(snapshot).GroupBy(m => m.TmdbId!.Value))
        {
            var winner = group.OrderBy(m => m.StreamId).First();
            map[group.Key] = winner.FolderName;
        }

        return map;
    }

    /// <summary>
    /// Works out which folders have to be merged for the snapshot's items to sit together.
    /// </summary>
    /// <param name="snapshot">Snapshot to read, may be null.</param>
    /// <returns>The plan. Empty when there is nothing to merge.</returns>
    public static GroupingPlan Plan(ContentSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            return new GroupingPlan([], 0);
        }

        int unproven = snapshot.Movies.Values.Count(m => m.TmdbId.HasValue && !IsGroupable(m.TmdbIdSource));

        var moves = new List<GroupMove>();
        foreach (var group in GroupableMovies(snapshot).GroupBy(m => m.TmdbId!.Value).OrderBy(g => g.Key))
        {
            string target = group.OrderBy(m => m.StreamId).First().FolderName;

            var sources = group
                .Select(m => m.FolderName)
                .Where(f => !string.Equals(f, target, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            if (sources.Count > 0)
            {
                moves.Add(new GroupMove(group.Key, target, sources));
            }
        }

        return new GroupingPlan(moves, unproven);
    }

    private static IEnumerable<MovieSnapshot> GroupableMovies(ContentSnapshot snapshot)
        => snapshot.Movies.Values.Where(m =>
            m.TmdbId.HasValue
            && IsGroupable(m.TmdbIdSource)
            && !string.IsNullOrEmpty(m.FolderName));
}

/// <summary>
/// One folder that has to absorb the others holding the same film (GitHub #88).
/// </summary>
/// <param name="TmdbId">The TMDB id the folders share.</param>
/// <param name="TargetFolder">Folder that keeps its name.</param>
/// <param name="SourceFolders">Folders whose contents move into the target.</param>
public sealed record GroupMove(int TmdbId, string TargetFolder, IReadOnlyList<string> SourceFolders);

/// <summary>
/// What regrouping would do, worked out before anything on disk is touched.
/// </summary>
/// <param name="Moves">Folder merges to perform.</param>
/// <param name="ItemsWithUnprovenId">
/// Items carrying an id that may not be grouped because it was guessed by a name lookup or read
/// back off a folder name. A full sync re-resolves those.
/// </param>
public sealed record GroupingPlan(IReadOnlyList<GroupMove> Moves, int ItemsWithUnprovenId);
