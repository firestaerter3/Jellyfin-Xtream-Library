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
using System.Collections.Concurrent;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Stops artwork downloads from a host that keeps failing, for the rest of one sync
/// (GitHub #154).
/// <para>
/// Dead artwork is nearly always a whole host that is gone, not one missing file, and every
/// image from such a host used to wait for the full timeout. After
/// <see cref="FailureThreshold"/> failures in a row from one host, the rest of its images are
/// skipped. A new instance is made per sync, so a host that comes back is tried again next time.
/// A missing image (404) counts as an answer, not a failure: the host is up.
/// </para>
/// </summary>
internal sealed class ArtworkHostBreaker
{
    /// <summary>Failures in a row after which a host is skipped.</summary>
    internal const int FailureThreshold = 5;

    private readonly ConcurrentDictionary<string, int> _failuresInARow = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether downloads from this host are skipped for the rest of the sync.
    /// </summary>
    /// <param name="host">The host, with port if one was given.</param>
    /// <returns>True when the host is skipped.</returns>
    public bool IsSkipped(string host)
        => _failuresInARow.TryGetValue(host, out var failures) && failures >= FailureThreshold;

    /// <summary>
    /// Records that the host answered.
    /// </summary>
    /// <param name="host">The host.</param>
    public void RecordAnswer(string host)
    {
        // A host that already tripped stays skipped: a download that started before the trip
        // and happens to finish must not reopen it.
        _failuresInARow.AddOrUpdate(host, 0, (_, failures) => failures >= FailureThreshold ? failures : 0);
    }

    /// <summary>
    /// Records that the host did not answer (connection refused, timeout, server error).
    /// </summary>
    /// <param name="host">The host.</param>
    /// <returns>True for the failure that made the host skipped, so it is logged once.</returns>
    public bool RecordFailure(string host)
        => _failuresInARow.AddOrUpdate(host, 1, (_, failures) => failures + 1) == FailureThreshold;
}
