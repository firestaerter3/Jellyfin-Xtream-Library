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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client.Models;

#pragma warning disable CS1591
namespace Jellyfin.Xtream.Library.Client;

public interface IDispatcharrClient
{
    /// <summary>
    /// Gets or sets the delay in milliseconds between API requests.
    /// </summary>
    int RequestDelayMs { get; set; }

    /// <summary>
    /// Configures the client with REST API credentials.
    /// </summary>
    /// <param name="username">Django admin username.</param>
    /// <param name="password">Django admin password.</param>
    void Configure(string username, string password);

    /// <summary>
    /// Gets movie detail including UUID.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Dispatcharr instance.</param>
    /// <param name="movieId">The Dispatcharr movie ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The movie detail, or null if not found.</returns>
    Task<DispatcharrMovieDetail?> GetMovieDetailAsync(string baseUrl, int movieId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets all stream providers for a movie.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Dispatcharr instance.</param>
    /// <param name="movieId">The Dispatcharr movie ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of stream providers for the movie.</returns>
    Task<List<DispatcharrMovieProvider>> GetMovieProvidersAsync(string baseUrl, int movieId, CancellationToken cancellationToken);

    /// <summary>
    /// Tests the REST API connection and JWT authentication.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Dispatcharr instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the connection and authentication succeeded.</returns>
    Task<bool> TestConnectionAsync(string baseUrl, CancellationToken cancellationToken);
}
