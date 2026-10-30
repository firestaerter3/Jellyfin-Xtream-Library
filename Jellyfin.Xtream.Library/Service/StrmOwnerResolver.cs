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
/// Works out which stream a movie STRM file belongs to. Stream ids are only unique within one
/// provider, so when several providers write into the same library path the id alone would make
/// provider A's stream 100 look like provider B's stream 100.
/// </summary>
/// <param name="BaseUrl">Base URL of the provider doing the sync.</param>
/// <param name="ScopeToProvider">
/// Whether a URL that does not start with <paramref name="BaseUrl"/> belongs to another provider.
/// Only set when providers share a library path: with a single provider a different host just means
/// the provider moved, and its files are still its own.
/// </param>
/// <param name="AlternativeTag">
/// Tag that keeps this provider's alternative file names apart from the other providers' on a shared
/// library path ("p2", "p3", ...). Null for the first provider of the path and for paths nobody
/// shares, whose names carry the stream id alone.
/// </param>
/// <param name="Username">
/// Account of the provider doing the sync. Two providers can sit on the same host with different
/// accounts, and the account is the segment after <c>/movie/</c> in the URL, so a URL on this host
/// still belongs to another provider when it names another account. Null or empty skips the
/// comparison. The password is left out on purpose: changing it does not make the files foreign.
/// </param>
internal readonly record struct StrmOwnerResolver(string BaseUrl, bool ScopeToProvider, string? AlternativeTag = null, string? Username = null)
{
    /// <summary>
    /// The owner reported for a URL that belongs to a different provider. It is not a valid stream
    /// id, so it never equals the stream asking.
    /// </summary>
    internal const int OtherProvider = -1;

    /// <summary>
    /// Gets the stream a STRM URL belongs to.
    /// </summary>
    /// <param name="url">URL read from a STRM file, may be null.</param>
    /// <returns>
    /// The stream id, <see cref="OtherProvider"/> for another provider's file, or null when the URL
    /// names no stream (Dispatcharr and anything unreadable).
    /// </returns>
    internal int? OwnerOf(string? url)
    {
        int? id = StrmSyncService.ParseMovieStreamId(url);
        if (id is null || !ScopeToProvider)
        {
            return id;
        }

        // The movie writer puts BaseUrl in as configured, so a trailing slash there gives
        // ".../movie/" with a double slash. Both forms are this provider's (CodeRabbit).
        string prefix = BaseUrl.TrimEnd('/') + "/movie/";
        if (!url!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            prefix = BaseUrl + "/movie/";
            if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return OtherProvider;
            }
        }

        // ".../movie/<user>/<password>/<id>.<ext>": the account is the segment right after the
        // prefix. A URL with no further slash carries no account, so there is nothing to compare
        // and it stays with the host.
        if (string.IsNullOrEmpty(Username))
        {
            return id;
        }

        int accountEnd = url.IndexOf('/', prefix.Length);
        if (accountEnd < 0)
        {
            return id;
        }

        return url.AsSpan(prefix.Length, accountEnd - prefix.Length).Equals(Username, StringComparison.Ordinal) ? id : OtherProvider;
    }
}
