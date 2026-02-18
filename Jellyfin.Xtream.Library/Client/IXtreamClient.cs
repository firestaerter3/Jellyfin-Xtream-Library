// Copyright (C) 2022  Kevin Jilissen

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

#pragma warning disable CA1002

#pragma warning disable CS1591
namespace Jellyfin.Xtream.Library.Client;

public interface IXtreamClient
{
    /// <summary>
    /// Gets or sets the delay in milliseconds between API requests.
    /// </summary>
    int RequestDelayMs { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of retries for rate-limited requests.
    /// </summary>
    int MaxRetries { get; set; }

    /// <summary>
    /// Gets or sets the initial retry delay in milliseconds after a 429 response.
    /// </summary>
    int RetryDelayMs { get; set; }

    /// <summary>
    /// Updates the User-Agent header based on plugin configuration.
    /// </summary>
    /// <param name="customUserAgent">Optional custom user agent string.</param>
    void UpdateUserAgent(string? customUserAgent = null);

    Task<PlayerApi> GetUserAndServerInfoAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken);

    Task<List<Category>> GetVodCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken);

    Task<List<StreamInfo>> GetVodStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken);

    Task<List<Category>> GetSeriesCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken);

    Task<List<Series>> GetSeriesByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken);

    Task<SeriesStreamInfo> GetSeriesStreamsBySeriesAsync(ConnectionInfo connectionInfo, int seriesId, CancellationToken cancellationToken);

    Task<VodInfoResponse?> GetVodInfoAsync(ConnectionInfo connectionInfo, int vodId, CancellationToken cancellationToken);

    Task<List<Category>> GetLiveCategoryAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken);

    Task<List<LiveStreamInfo>> GetLiveStreamsByCategoryAsync(ConnectionInfo connectionInfo, int categoryId, CancellationToken cancellationToken);

    Task<List<LiveStreamInfo>> GetAllLiveStreamsAsync(ConnectionInfo connectionInfo, CancellationToken cancellationToken);

    Task<EpgListings?> GetSimpleDataTableAsync(ConnectionInfo connectionInfo, int streamId, CancellationToken cancellationToken);
}
