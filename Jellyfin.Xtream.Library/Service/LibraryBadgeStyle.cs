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
/// How the library badge is drawn.
/// </summary>
/// <param name="Text">The badge text.</param>
/// <param name="Corner">The corner it is drawn in.</param>
/// <param name="Kinds">One letter per item type that has the badge switched on, for the image tag.</param>
internal readonly record struct LibraryBadgeStyle(string Text, LibraryBadgeCorner Corner, string Kinds);
