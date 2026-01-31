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

using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;

namespace Jellyfin.Xtream.Library.Tests.Helpers;

/// <summary>
/// Factory methods for creating test data objects.
/// </summary>
public static class TestDataBuilder
{
    /// <summary>
    /// Creates a test ConnectionInfo with default or custom values.
    /// </summary>
    public static ConnectionInfo CreateConnectionInfo(
        string baseUrl = "http://test.example.com",
        string username = "testuser",
        string password = "testpass")
    {
        return new ConnectionInfo(baseUrl, username, password);
    }

    /// <summary>
    /// Creates a test Category with default or custom values.
    /// </summary>
    public static Category CreateCategory(
        int categoryId = 1,
        string categoryName = "Test Category",
        int parentId = 0)
    {
        return new Category
        {
            CategoryId = categoryId,
            CategoryName = categoryName,
            ParentId = parentId,
        };
    }

    /// <summary>
    /// Creates a test StreamInfo (VOD) with default or custom values.
    /// </summary>
    public static StreamInfo CreateStreamInfo(
        int streamId = 1,
        string name = "Test Movie",
        string containerExtension = "mp4",
        int? categoryId = 1)
    {
        return new StreamInfo
        {
            StreamId = streamId,
            Name = name,
            ContainerExtension = containerExtension,
            CategoryId = categoryId,
            StreamType = "movie",
        };
    }

    /// <summary>
    /// Creates a test Series with default or custom values.
    /// </summary>
    public static Series CreateSeries(
        int seriesId = 1,
        string name = "Test Series",
        int categoryId = 1)
    {
        return new Series
        {
            SeriesId = seriesId,
            Name = name,
            CategoryId = categoryId,
        };
    }

    /// <summary>
    /// Creates a test Episode with default or custom values.
    /// </summary>
    public static Episode CreateEpisode(
        int episodeId = 1,
        int episodeNum = 1,
        string title = "Test Episode",
        string containerExtension = "mkv",
        int season = 1)
    {
        return new Episode
        {
            EpisodeId = episodeId,
            EpisodeNum = episodeNum,
            Title = title,
            ContainerExtension = containerExtension,
            Season = season,
        };
    }

    /// <summary>
    /// Creates a test SeriesStreamInfo with episodes.
    /// </summary>
    public static SeriesStreamInfo CreateSeriesStreamInfo(
        Dictionary<int, ICollection<Episode>>? episodes = null)
    {
        return new SeriesStreamInfo
        {
            Episodes = episodes ?? new Dictionary<int, ICollection<Episode>>(),
        };
    }

    /// <summary>
    /// Creates a test PlayerApi response.
    /// </summary>
    public static PlayerApi CreatePlayerApi(
        string username = "testuser",
        string status = "Active",
        int maxConnections = 1,
        int activeConnections = 0)
    {
        return new PlayerApi
        {
            UserInfo = new UserInfo
            {
                Username = username,
                Status = status,
                MaxConnections = maxConnections,
                ActiveCons = activeConnections,
            },
        };
    }
}
