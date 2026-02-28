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

using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Interface for metadata lookup service.
/// </summary>
public interface IMetadataLookupService
{
    /// <summary>
    /// Looks up a TMDb ID for a movie.
    /// </summary>
    /// <param name="title">The movie title.</param>
    /// <param name="year">The release year.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="releaseDateYear">Alternative year from provider's release date metadata.</param>
    /// <param name="alternativeTitle">Alternative title (e.g. original name) to try if primary title fails.</param>
    /// <returns>The TMDb ID if found, null otherwise.</returns>
    Task<int?> LookupMovieTmdbIdAsync(string title, int? year, CancellationToken cancellationToken, int? releaseDateYear = null, string? alternativeTitle = null);

    /// <summary>
    /// Looks up a TVDb ID for a series.
    /// </summary>
    /// <param name="title">The series title.</param>
    /// <param name="year">The premiere year.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="alternativeTitle">Alternative title (e.g. original name) to try if primary title fails.</param>
    /// <returns>The TVDb ID if found, null otherwise.</returns>
    Task<int?> LookupSeriesTvdbIdAsync(string title, int? year, CancellationToken cancellationToken, string? alternativeTitle = null);

    /// <summary>
    /// Initializes the service and loads the cache.
    /// </summary>
    /// <returns>A task representing the async operation.</returns>
    Task InitializeAsync();

    /// <summary>
    /// Flushes the cache to disk.
    /// </summary>
    /// <returns>A task representing the async operation.</returns>
    Task FlushCacheAsync();

    /// <summary>
    /// Clears the metadata cache.
    /// </summary>
    /// <returns>A task representing the async operation.</returns>
    Task ClearCacheAsync();
}
