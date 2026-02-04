using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Service.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// Tests for <see cref="DeltaCalculator"/>.
/// </summary>
public class DeltaCalculatorTests
{
    private readonly DeltaCalculator _calculator;

    public DeltaCalculatorTests()
    {
        _calculator = new DeltaCalculator(NullLogger<DeltaCalculator>.Instance);
    }

    [Fact]
    public async Task FirstSync_NoSnapshot_AllMoviesAreNew()
    {
        // Arrange
        var movies = CreateTestMovies(10);
        var emptySnapshot = new ContentSnapshot();

        // Act
        var delta = await _calculator.CalculateMovieDeltaAsync(movies, emptySnapshot, CancellationToken.None);

        // Assert
        Assert.Equal(10, delta.NewMovies.Count);
        Assert.Empty(delta.ModifiedMovies);
        Assert.Empty(delta.RemovedMovieIds);
        Assert.Equal(10, delta.Stats.NewItems);
        Assert.Equal(0, delta.Stats.ModifiedItems);
        Assert.Equal(0, delta.Stats.RemovedItems);
        Assert.Equal(0, delta.Stats.UnchangedItems);
        Assert.Equal(100.0, delta.Stats.ChangePercentage);
    }

    [Fact]
    public async Task IncrementalSync_NoChanges_AllUnchanged()
    {
        // Arrange
        var movies = CreateTestMovies(10);
        var snapshot = CreateSnapshotFromMovies(movies);

        // Act
        var delta = await _calculator.CalculateMovieDeltaAsync(movies, snapshot, CancellationToken.None);

        // Assert
        Assert.Empty(delta.NewMovies);
        Assert.Empty(delta.ModifiedMovies);
        Assert.Empty(delta.RemovedMovieIds);
        Assert.Equal(0, delta.Stats.NewItems);
        Assert.Equal(0, delta.Stats.ModifiedItems);
        Assert.Equal(0, delta.Stats.RemovedItems);
        Assert.Equal(10, delta.Stats.UnchangedItems);
        Assert.Equal(0.0, delta.Stats.ChangePercentage);
    }

    [Fact]
    public async Task IncrementalSync_NewMoviesAdded_DetectsNew()
    {
        // Arrange
        var originalMovies = CreateTestMovies(10);
        var snapshot = CreateSnapshotFromMovies(originalMovies);

        var currentMovies = CreateTestMovies(10);
        currentMovies.AddRange(CreateTestMovies(3, startId: 11)); // Add 3 new movies

        // Act
        var delta = await _calculator.CalculateMovieDeltaAsync(currentMovies, snapshot, CancellationToken.None);

        // Assert
        Assert.Equal(3, delta.NewMovies.Count);
        Assert.Equal(11, delta.NewMovies[0].StreamId);
        Assert.Equal(12, delta.NewMovies[1].StreamId);
        Assert.Equal(13, delta.NewMovies[2].StreamId);
        Assert.Empty(delta.ModifiedMovies);
        Assert.Empty(delta.RemovedMovieIds);
        Assert.Equal(3, delta.Stats.NewItems);
        Assert.Equal(10, delta.Stats.UnchangedItems);
        Assert.Equal(13, delta.Stats.TotalItems);
    }

    [Fact]
    public async Task IncrementalSync_MoviesRemoved_DetectsOrphans()
    {
        // Arrange
        var originalMovies = CreateTestMovies(10);
        var snapshot = CreateSnapshotFromMovies(originalMovies);

        var currentMovies = CreateTestMovies(7); // Removed movies 8, 9, 10

        // Act
        var delta = await _calculator.CalculateMovieDeltaAsync(currentMovies, snapshot, CancellationToken.None);

        // Assert
        Assert.Empty(delta.NewMovies);
        Assert.Empty(delta.ModifiedMovies);
        Assert.Equal(3, delta.RemovedMovieIds.Count);
        Assert.Contains(8, delta.RemovedMovieIds);
        Assert.Contains(9, delta.RemovedMovieIds);
        Assert.Contains(10, delta.RemovedMovieIds);
        Assert.Equal(3, delta.Stats.RemovedItems);
        Assert.Equal(7, delta.Stats.UnchangedItems);
        Assert.Equal(7, delta.Stats.TotalItems);
    }

    [Fact]
    public async Task IncrementalSync_MetadataChanged_DetectsModified()
    {
        // Arrange
        var originalMovies = CreateTestMovies(10);
        var snapshot = CreateSnapshotFromMovies(originalMovies);

        var currentMovies = CreateTestMovies(10);
        currentMovies[0].Name = "Modified Movie 1"; // Change name
        currentMovies[1].ContainerExtension = "mp4"; // Change container
        currentMovies[2].CategoryId = 99; // Change category

        // Act
        var delta = await _calculator.CalculateMovieDeltaAsync(currentMovies, snapshot, CancellationToken.None);

        // Assert
        Assert.Empty(delta.NewMovies);
        Assert.Equal(3, delta.ModifiedMovies.Count);
        Assert.Contains(delta.ModifiedMovies, m => m.StreamId == 1);
        Assert.Contains(delta.ModifiedMovies, m => m.StreamId == 2);
        Assert.Contains(delta.ModifiedMovies, m => m.StreamId == 3);
        Assert.Empty(delta.RemovedMovieIds);
        Assert.Equal(3, delta.Stats.ModifiedItems);
        Assert.Equal(7, delta.Stats.UnchangedItems);
    }

    [Fact]
    public async Task IncrementalSync_CategoryChanged_DetectsModified()
    {
        // Arrange
        var originalMovies = CreateTestMovies(5);
        var snapshot = CreateSnapshotFromMovies(originalMovies);

        var currentMovies = CreateTestMovies(5);
        currentMovies[2].CategoryId = 999; // Change category for movie 3

        // Act
        var delta = await _calculator.CalculateMovieDeltaAsync(currentMovies, snapshot, CancellationToken.None);

        // Assert
        var modified = Assert.Single(delta.ModifiedMovies);
        Assert.Equal(3, modified.StreamId);
        Assert.Equal(999, modified.CategoryId);
        Assert.Equal(1, delta.Stats.ModifiedItems);
        Assert.Equal(4, delta.Stats.UnchangedItems);
    }

    [Fact]
    public async Task CalculateDelta_LargeDataset_Performs()
    {
        // Arrange
        var movies = CreateTestMovies(1000);
        var snapshot = CreateSnapshotFromMovies(movies);

        // Modify 10% of movies
        for (int i = 0; i < 100; i++)
        {
            movies[i].Name = $"Modified Movie {i}";
        }

        // Act
        var startTime = DateTime.UtcNow;
        var delta = await _calculator.CalculateMovieDeltaAsync(movies, snapshot, CancellationToken.None);
        var duration = DateTime.UtcNow - startTime;

        // Assert
        Assert.Equal(100, delta.ModifiedMovies.Count);
        Assert.Equal(900, delta.Stats.UnchangedItems);
        Assert.True(duration.TotalSeconds < 1, $"Delta calculation took {duration.TotalSeconds}s, expected < 1s");
    }

    [Fact]
    public async Task CalculateDelta_MixedChanges_CorrectStatistics()
    {
        // Arrange
        var originalMovies = CreateTestMovies(20);
        var snapshot = CreateSnapshotFromMovies(originalMovies);

        var currentMovies = CreateTestMovies(15); // Remove 5 (16-20)
        currentMovies.AddRange(CreateTestMovies(3, startId: 21)); // Add 3 new (21-23)
        currentMovies[0].Name = "Modified Movie 1"; // Modify 1
        currentMovies[1].Name = "Modified Movie 2"; // Modify 2

        // Act
        var delta = await _calculator.CalculateMovieDeltaAsync(currentMovies, snapshot, CancellationToken.None);

        // Assert
        Assert.Equal(3, delta.NewMovies.Count);
        Assert.Equal(2, delta.ModifiedMovies.Count);
        Assert.Equal(5, delta.RemovedMovieIds.Count);
        Assert.Equal(13, delta.Stats.UnchangedItems); // 18 total - 3 new - 2 modified
        Assert.Equal(18, delta.Stats.TotalItems);
        // ChangePercentage denominator = TotalItems + RemovedItems = 18 + 5 = 23
        // Changed = NewItems + ModifiedItems + RemovedItems = 3 + 2 + 5 = 10
        Assert.Equal(10.0 / 23.0 * 100, delta.Stats.ChangePercentage, 0.01);
    }

    [Fact]
    public async Task Series_NewEpisodesAdded_DetectsModified()
    {
        // Arrange
        var series = CreateTestSeries(5);
        var seriesInfoDict = new Dictionary<int, SeriesStreamInfo>
        {
            [1] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } },
            [2] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } },
            [3] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } },
            [4] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } },
            [5] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } }
        };

        var snapshot = CreateSnapshotFromSeries(series, seriesInfoDict);

        // Add episode to series 3
        seriesInfoDict[3] = new SeriesStreamInfo
        {
            Episodes = new Dictionary<int, ICollection<Episode>>
            {
                [1] = new List<Episode> { new Episode(), new Episode() }
            }
        };

        // Act
        var delta = await _calculator.CalculateSeriesDeltaAsync(series, seriesInfoDict, snapshot, CancellationToken.None);

        // Assert
        var modified = Assert.Single(delta.ModifiedSeries);
        Assert.Equal(3, modified.SeriesId);
        Assert.Equal(1, delta.Stats.ModifiedItems);
        Assert.Equal(4, delta.Stats.UnchangedItems);
    }

    [Fact]
    public async Task Series_EpisodeCountChanged_UpdatesChecksum()
    {
        // Arrange
        var series = CreateTestSeries(3);
        var originalSeriesInfo = new Dictionary<int, SeriesStreamInfo>
        {
            [1] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode(), new Episode() } } },
            [2] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } },
            [3] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } }
        };

        var snapshot = CreateSnapshotFromSeries(series, originalSeriesInfo);

        var newSeriesInfo = new Dictionary<int, SeriesStreamInfo>
        {
            [1] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } }, // Reduced from 2 to 1
            [2] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } },
            [3] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } }
        };

        // Act
        var delta = await _calculator.CalculateSeriesDeltaAsync(series, newSeriesInfo, snapshot, CancellationToken.None);

        // Assert
        var modified = Assert.Single(delta.ModifiedSeries);
        Assert.Equal(1, modified.SeriesId);
    }

    [Fact]
    public void MergeDeltas_CombinesStatistics()
    {
        // Arrange
        var movieDelta = new SyncDelta
        {
            NewMovies = CreateTestMovies(5),
            ModifiedMovies = CreateTestMovies(2),
            RemovedMovieIds = new List<int> { 1, 2 },
            Stats = new DeltaStatistics
            {
                TotalItems = 100,
                NewItems = 5,
                ModifiedItems = 2,
                RemovedItems = 2,
                UnchangedItems = 93
            }
        };

        var seriesDelta = new SyncDelta
        {
            NewSeries = CreateTestSeries(3),
            ModifiedSeries = CreateTestSeries(1),
            RemovedSeriesIds = new List<int> { 10 },
            Stats = new DeltaStatistics
            {
                TotalItems = 50,
                NewItems = 3,
                ModifiedItems = 1,
                RemovedItems = 1,
                UnchangedItems = 46
            }
        };

        // Act
        var merged = DeltaCalculator.MergeDeltas(movieDelta, seriesDelta);

        // Assert
        Assert.Equal(5, merged.NewMovies.Count);
        Assert.Equal(2, merged.ModifiedMovies.Count);
        Assert.Equal(2, merged.RemovedMovieIds.Count);
        Assert.Equal(3, merged.NewSeries.Count);
        Assert.Single(merged.ModifiedSeries);
        Assert.Single(merged.RemovedSeriesIds);
        Assert.Equal(150, merged.Stats.TotalItems);
        Assert.Equal(8, merged.Stats.NewItems);
        Assert.Equal(3, merged.Stats.ModifiedItems);
        Assert.Equal(3, merged.Stats.RemovedItems);
        Assert.Equal(139, merged.Stats.UnchangedItems);
    }

    [Fact]
    public async Task Series_Removed_DetectsOrphans()
    {
        // Arrange - use the same series objects so LastModified matches exactly
        var allSeries = CreateTestSeries(5);
        var seriesInfoDict = new Dictionary<int, SeriesStreamInfo>
        {
            [1] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } },
            [2] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } },
            [3] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } },
            [4] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } },
            [5] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } }
        };

        var snapshot = CreateSnapshotFromSeries(allSeries, seriesInfoDict);

        // Remove series 4 and 5 - reuse the same series objects so checksums match
        var currentSeries = allSeries.Take(3).ToList();
        var currentSeriesInfo = new Dictionary<int, SeriesStreamInfo>
        {
            [1] = seriesInfoDict[1],
            [2] = seriesInfoDict[2],
            [3] = seriesInfoDict[3]
        };

        // Act
        var delta = await _calculator.CalculateSeriesDeltaAsync(currentSeries, currentSeriesInfo, snapshot, CancellationToken.None);

        // Assert
        Assert.Empty(delta.NewSeries);
        Assert.Empty(delta.ModifiedSeries);
        Assert.Equal(2, delta.RemovedSeriesIds.Count);
        Assert.Contains(4, delta.RemovedSeriesIds);
        Assert.Contains(5, delta.RemovedSeriesIds);
        Assert.Equal(2, delta.Stats.RemovedItems);
        Assert.Equal(3, delta.Stats.UnchangedItems);
    }

    [Fact]
    public void SeriesChecksum_SameContent_SameHash()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var series1 = new Series
        {
            SeriesId = 1,
            Name = "Test Series",
            Cover = "http://example.com/cover1.jpg",
            CategoryId = 5,
            LastModified = now
        };

        var series2 = new Series
        {
            SeriesId = 2, // Different ID
            Name = "Test Series",
            Cover = "http://example.com/cover2.jpg", // Different cover URL
            CategoryId = 5,
            LastModified = now
        };

        // Act
        var checksum1 = SnapshotService.CalculateChecksum(series1, 10);
        var checksum2 = SnapshotService.CalculateChecksum(series2, 10);

        // Assert
        Assert.Equal(checksum1, checksum2); // Same content = same hash (cover URL ignored)
    }

    [Fact]
    public void SeriesChecksum_DifferentEpisodeCount_DifferentHash()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var series = new Series
        {
            SeriesId = 1,
            Name = "Test Series",
            Cover = "http://example.com/cover.jpg",
            CategoryId = 5,
            LastModified = now
        };

        // Act
        var checksum1 = SnapshotService.CalculateChecksum(series, 10);
        var checksum2 = SnapshotService.CalculateChecksum(series, 11);

        // Assert
        Assert.NotEqual(checksum1, checksum2);
    }

    [Fact]
    public async Task PosterUrlChange_DoesNotTriggerModification()
    {
        // Arrange
        var originalMovies = CreateTestMovies(5);
        var snapshot = CreateSnapshotFromMovies(originalMovies);

        // Change only poster URLs
        var currentMovies = CreateTestMovies(5);
        currentMovies[0].StreamIcon = "http://newcdn.example.com/movie1.jpg";
        currentMovies[1].StreamIcon = "http://newcdn.example.com/movie2.jpg";

        // Act
        var delta = await _calculator.CalculateMovieDeltaAsync(currentMovies, snapshot, CancellationToken.None);

        // Assert
        Assert.Empty(delta.NewMovies);
        Assert.Empty(delta.ModifiedMovies);
        Assert.Empty(delta.RemovedMovieIds);
        Assert.Equal(5, delta.Stats.UnchangedItems);
    }

    [Fact]
    public async Task DuplicateStreamIds_AreDeduped()
    {
        // Arrange
        var movies = CreateTestMovies(3);
        // Add duplicate with same StreamId but different category (simulating Xtream API returning same movie in multiple categories)
        movies.Add(new StreamInfo
        {
            StreamId = 1, // Duplicate of first movie
            Name = "Movie 1",
            StreamIcon = "http://example.com/movie1.jpg",
            ContainerExtension = "mkv",
            CategoryId = 99
        });

        var emptySnapshot = new ContentSnapshot();

        // Act
        var delta = await _calculator.CalculateMovieDeltaAsync(movies, emptySnapshot, CancellationToken.None);

        // Assert - should be 3 unique, not 4
        Assert.Equal(3, delta.NewMovies.Count);
        Assert.Equal(3, delta.Stats.TotalItems);
    }

    [Fact]
    public async Task DuplicateSeriesIds_AreDeduped()
    {
        // Arrange
        var series = CreateTestSeries(3);
        // Add duplicate
        series.Add(new Series
        {
            SeriesId = 1,
            Name = "Series 1",
            Cover = "http://example.com/series1.jpg",
            CategoryId = 99,
            LastModified = DateTime.UtcNow
        });

        var seriesInfoDict = new Dictionary<int, SeriesStreamInfo>
        {
            [1] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } },
            [2] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } },
            [3] = new SeriesStreamInfo { Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode> { new Episode() } } }
        };

        var emptySnapshot = new ContentSnapshot();

        // Act
        var delta = await _calculator.CalculateSeriesDeltaAsync(series, seriesInfoDict, emptySnapshot, CancellationToken.None);

        // Assert - should be 3 unique, not 4
        Assert.Equal(3, delta.NewSeries.Count);
        Assert.Equal(3, delta.Stats.TotalItems);
    }

    [Fact]
    public async Task CancellationToken_MovieDelta_ThrowsOnCancellation()
    {
        // Arrange
        var movies = CreateTestMovies(100);
        var emptySnapshot = new ContentSnapshot();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _calculator.CalculateMovieDeltaAsync(movies, emptySnapshot, cts.Token));
    }

    [Fact]
    public async Task CancellationToken_SeriesDelta_ThrowsOnCancellation()
    {
        // Arrange
        var series = CreateTestSeries(100);
        var seriesInfoDict = new Dictionary<int, SeriesStreamInfo>();
        var emptySnapshot = new ContentSnapshot();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _calculator.CalculateSeriesDeltaAsync(series, seriesInfoDict, emptySnapshot, cts.Token));
    }

    private static List<StreamInfo> CreateTestMovies(int count, int startId = 1)
    {
        var movies = new List<StreamInfo>();
        for (int i = 0; i < count; i++)
        {
            movies.Add(new StreamInfo
            {
                StreamId = startId + i,
                Name = $"Movie {startId + i}",
                StreamIcon = $"http://example.com/movie{startId + i}.jpg",
                ContainerExtension = "mkv",
                CategoryId = (startId + i) % 10
            });
        }

        return movies;
    }

    private static List<Series> CreateTestSeries(int count, int startId = 1)
    {
        var series = new List<Series>();
        for (int i = 0; i < count; i++)
        {
            series.Add(new Series
            {
                SeriesId = startId + i,
                Name = $"Series {startId + i}",
                Cover = $"http://example.com/series{startId + i}.jpg",
                CategoryId = (startId + i) % 5,
                LastModified = DateTime.UtcNow.AddDays(-i)
            });
        }

        return series;
    }

    private static ContentSnapshot CreateSnapshotFromMovies(List<StreamInfo> movies)
    {
        var snapshot = new ContentSnapshot();
        foreach (var movie in movies)
        {
            snapshot.Movies[movie.StreamId] = new MovieSnapshot
            {
                StreamId = movie.StreamId,
                Name = movie.Name,
                StreamIcon = movie.StreamIcon,
                ContainerExtension = movie.ContainerExtension,
                CategoryId = movie.CategoryId ?? 0,
                Checksum = SnapshotService.CalculateChecksum(movie)
            };
        }

        return snapshot;
    }

    private static ContentSnapshot CreateSnapshotFromSeries(List<Series> series, Dictionary<int, SeriesStreamInfo> seriesInfoDict)
    {
        var snapshot = new ContentSnapshot();
        foreach (var s in series)
        {
            var episodeCount = seriesInfoDict.TryGetValue(s.SeriesId, out var info) && info.Episodes != null
                ? info.Episodes.Values.Sum(eps => eps.Count)
                : 0;

            snapshot.Series[s.SeriesId] = new SeriesSnapshot
            {
                SeriesId = s.SeriesId,
                Name = s.Name,
                Cover = s.Cover,
                CategoryId = s.CategoryId,
                EpisodeCount = episodeCount,
                LastModified = s.LastModified,
                Checksum = SnapshotService.CalculateChecksum(s, episodeCount)
            };
        }

        return snapshot;
    }
}
