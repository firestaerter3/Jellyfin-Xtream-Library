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

namespace Jellyfin.Xtream.Library.Service.Models;

/// <summary>
/// Where the metadata id recorded for an item came from. The distinction matters for grouping
/// (GitHub #88): only an id the provider itself supplied is safe to merge two items on. An id a
/// name lookup guessed can be wrong in exactly the way that hurts, for example a live-action film
/// and its animated remake sharing a title.
/// </summary>
public enum ItemIdSource
{
    /// <summary>No id was resolved.</summary>
    None = 0,

    /// <summary>The provider returned the id in its own metadata.</summary>
    Provider = 1,

    /// <summary>A name-based lookup guessed the id.</summary>
    Lookup = 2,

    /// <summary>The user pinned the id in the folder overrides.</summary>
    Override = 3,

    /// <summary>
    /// The id was read back off an existing folder name. Where it originally came from is not
    /// recoverable from disk, so it is treated as unproven until a sync resolves the item again.
    /// </summary>
    Unknown = 4,
}

/// <summary>
/// What one provider stream resolved to on disk during a sync.
/// </summary>
/// <param name="FolderName">Folder the item was written to, without the library path.</param>
/// <param name="TmdbId">Resolved TMDB id, if any.</param>
/// <param name="TvdbId">Resolved TVDB id, if any. Series only.</param>
/// <param name="Source">Where <paramref name="TmdbId"/> or <paramref name="TvdbId"/> came from.</param>
/// <param name="GroupOwnerStreamId">Stream whose title named the shared folder, when grouped.</param>
public sealed record ItemIdentity(string FolderName, int? TmdbId, int? TvdbId, ItemIdSource Source, int? GroupOwnerStreamId = null);
