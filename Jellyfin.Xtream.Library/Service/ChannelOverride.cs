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
/// Represents a channel override with optional name, number, and logo URL.
/// </summary>
public class ChannelOverride
{
    /// <summary>
    /// Gets or sets the stream ID this override applies to.
    /// </summary>
    public int StreamId { get; set; }

    /// <summary>
    /// Gets or sets the override name (null means keep original).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the override channel number (null means keep original).
    /// </summary>
    public int? Number { get; set; }

    /// <summary>
    /// Gets or sets the override logo URL (null means keep original).
    /// </summary>
    public string? LogoUrl { get; set; }
}
