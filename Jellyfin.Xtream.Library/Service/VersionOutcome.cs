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

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// What happened to a movie when a contested name was resolved (GitHub #142).
/// </summary>
internal enum VersionOutcome
{
    /// <summary>A file was written that did not exist before.</summary>
    Created,

    /// <summary>An existing file was rewritten.</summary>
    Updated,
}
