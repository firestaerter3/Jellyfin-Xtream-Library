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
/// A plain movie file name a stream wanted but could not take during the loop: another stream
/// claimed it in this run, or the file on disk belongs to another stream (GitHub #142).
/// </summary>
/// <param name="PlainPath">The plain file path.</param>
/// <param name="StrmFileName">The plain file name.</param>
/// <param name="StreamUrl">The URL this stream would write.</param>
/// <param name="StreamId">The stream id the URL points at.</param>
/// <param name="DiskOwnerStreamId">Stream id in the existing file, when it is another stream's.</param>
internal sealed record ContestedStrm(string PlainPath, string StrmFileName, string StreamUrl, int StreamId, int? DiskOwnerStreamId);
