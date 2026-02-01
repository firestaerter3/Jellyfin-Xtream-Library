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

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Cache entry for metadata lookup results.
/// </summary>
public class MetadataCacheEntry
{
    /// <summary>
    /// Gets or sets the TMDb ID if found.
    /// </summary>
    public int? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the TVDb ID if found.
    /// </summary>
    public int? TvdbId { get; set; }

    /// <summary>
    /// Gets or sets the confidence level of the match (0-100).
    /// </summary>
    public int Confidence { get; set; }

    /// <summary>
    /// Gets or sets when this lookup was performed.
    /// </summary>
    public DateTime LastLookup { get; set; }
}
