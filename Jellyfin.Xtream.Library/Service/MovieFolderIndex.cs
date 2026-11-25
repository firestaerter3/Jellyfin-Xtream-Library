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
using System.IO;
using System.Linq;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Which movie folders already hold a file of each stream, read off the STRM URLs on disk.
/// <para>
/// Finding a stream's folder by its title alone breaks in two ways once a library has grouped
/// films: a guest is named after its own title but sits in the folder its group's owner named, and
/// two films that share a title share one name key. Either sent a full sync over an existing tree
/// off to write the stream a second time somewhere else. What the disk says is the one thing a
/// sync wrote itself and nothing has to remember.
/// </para>
/// </summary>
internal static class MovieFolderIndex
{
    /// <summary>
    /// Maps each stream of this provider to the movie folders under <paramref name="basePath"/> that
    /// hold a STRM file naming it.
    /// </summary>
    /// <param name="basePath">The movie root or a category subfolder.</param>
    /// <param name="owners">Tells this provider's streams from another provider's.</param>
    /// <returns>Stream id to folder names, each folder listed once.</returns>
    internal static Dictionary<int, List<string>> Build(string basePath, StrmOwnerResolver owners)
    {
        var index = new Dictionary<int, List<string>>();
        if (!Directory.Exists(basePath))
        {
            return index;
        }

        foreach (var folder in Directory.GetDirectories(basePath))
        {
            string folderName = Path.GetFileName(folder);
            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.strm");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be listed, or went away since the directory listing, is left
                // out: its streams fall back to the name lookup, as before the index. Failing here
                // would abort the whole movie sync (final review).
                continue;
            }

            foreach (var file in files)
            {
                if (owners.OwnerOf(StrmSyncService.ReadStrmUrl(file)) is not int streamId || streamId < 0)
                {
                    continue;
                }

                if (!index.TryGetValue(streamId, out var folders))
                {
                    folders = [];
                    index[streamId] = folders;
                }

                if (!folders.Contains(folderName, StringComparer.OrdinalIgnoreCase))
                {
                    folders.Add(folderName);
                }
            }
        }

        return index;
    }

    /// <summary>
    /// The folder a stream with files in several folders stays in: the one the last sync recorded,
    /// else the ordinal-first, so the choice does not depend on directory order.
    /// </summary>
    /// <param name="folders">Folders holding a file of the stream; not empty.</param>
    /// <param name="recordedFolder">Folder the snapshot recorded for the stream, may be null.</param>
    /// <returns>The folder to use.</returns>
    internal static string Pick(IReadOnlyList<string> folders, string? recordedFolder)
    {
        var recorded = folders.FirstOrDefault(f => string.Equals(f, recordedFolder, StringComparison.OrdinalIgnoreCase));
        return recorded ?? folders.Order(StringComparer.Ordinal).First();
    }
}
