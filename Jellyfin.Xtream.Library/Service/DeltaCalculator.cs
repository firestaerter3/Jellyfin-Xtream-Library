using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Calculates deltas between content snapshots to identify new, modified, and removed items.
/// </summary>
public class DeltaCalculator
{
    private readonly ILogger<DeltaCalculator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeltaCalculator"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public DeltaCalculator(ILogger<DeltaCalculator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Calculates the delta between current movies and a previous snapshot.
    /// </summary>
    /// <param name="currentMovies">The current movies from the provider.</param>
    /// <param name="previousSnapshot">The previous snapshot to compare against.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A sync delta containing new, modified, and removed movies.</returns>
    public Task<SyncDelta> CalculateMovieDeltaAsync(
        IEnumerable<StreamInfo> currentMovies,
        ContentSnapshot previousSnapshot,
        CancellationToken cancellationToken = default)
    {
        var delta = new SyncDelta();
        var currentList = currentMovies.ToList();
        var processedIds = new HashSet<int>();
        var stats = new DeltaStatistics
        {
            TotalItems = currentList.Count
        };

        foreach (var movie in currentList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip duplicate StreamIds (Xtream APIs can return duplicates across categories)
            if (!processedIds.Add(movie.StreamId))
            {
                stats.TotalItems--;
                continue;
            }

            var currentChecksum = SnapshotService.CalculateChecksum(movie);

            if (!previousSnapshot.Movies.TryGetValue(movie.StreamId, out var previousMovie))
            {
                // New movie - not in previous snapshot
                delta.NewMovies.Add(movie);
                stats.NewItems++;
                _logger.LogDebug("New movie detected: {StreamId} - {Name}", movie.StreamId, movie.Name);
            }
            else if (previousMovie.Checksum != currentChecksum)
            {
                // Modified movie - checksum changed
                delta.ModifiedMovies.Add(movie);
                stats.ModifiedItems++;
                _logger.LogDebug(
                    "Modified movie detected: {StreamId} - {Name} (checksum: {Old} -> {New})",
                    movie.StreamId,
                    movie.Name,
                    previousMovie.Checksum,
                    currentChecksum);
            }
            else
            {
                // Unchanged movie
                stats.UnchangedItems++;
            }
        }

        // Find removed movies (in snapshot but not in current)
        foreach (var snapshotMovie in previousSnapshot.Movies.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!processedIds.Contains(snapshotMovie.StreamId))
            {
                delta.RemovedMovieIds.Add(snapshotMovie.StreamId);
                stats.RemovedItems++;
                _logger.LogDebug("Removed movie detected: {StreamId} - {Name}", snapshotMovie.StreamId, snapshotMovie.Name);
            }
        }

        delta.Stats = stats;

        _logger.LogInformation(
            "Movie delta: {Total} total, {New} new, {Modified} modified, {Removed} removed, {Unchanged} unchanged ({ChangePercent:F1}% changed)",
            stats.TotalItems,
            stats.NewItems,
            stats.ModifiedItems,
            stats.RemovedItems,
            stats.UnchangedItems,
            stats.ChangePercentage);

        return Task.FromResult(delta);
    }

    /// <summary>
    /// Calculates the delta between current series and a previous snapshot.
    /// </summary>
    /// <param name="currentSeries">The current series from the provider.</param>
    /// <param name="seriesInfoDict">Dictionary mapping series IDs to their episode information.</param>
    /// <param name="previousSnapshot">The previous snapshot to compare against.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A sync delta containing new, modified, and removed series.</returns>
    public Task<SyncDelta> CalculateSeriesDeltaAsync(
        IEnumerable<Series> currentSeries,
        Dictionary<int, SeriesStreamInfo> seriesInfoDict,
        ContentSnapshot previousSnapshot,
        CancellationToken cancellationToken = default)
    {
        var delta = new SyncDelta();
        var currentList = currentSeries.ToList();
        var processedIds = new HashSet<int>();
        var stats = new DeltaStatistics
        {
            TotalItems = currentList.Count
        };

        foreach (var series in currentList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip duplicate SeriesIds (Xtream APIs can return duplicates across categories)
            if (!processedIds.Add(series.SeriesId))
            {
                stats.TotalItems--;
                continue;
            }

            // Calculate episode count for checksum
            var episodeCount = 0;
            if (seriesInfoDict.TryGetValue(series.SeriesId, out var info) && info.Episodes != null)
            {
                episodeCount = info.Episodes.Values.Sum(eps => eps.Count);
            }

            var currentChecksum = SnapshotService.CalculateChecksum(series, episodeCount);

            if (!previousSnapshot.Series.TryGetValue(series.SeriesId, out var previousSeries))
            {
                // New series - not in previous snapshot
                delta.NewSeries.Add(series);
                stats.NewItems++;
                _logger.LogDebug("New series detected: {SeriesId} - {Name}", series.SeriesId, series.Name);
            }
            else if (previousSeries.Checksum != currentChecksum)
            {
                // Modified series - checksum changed (could be episode count or metadata)
                delta.ModifiedSeries.Add(series);
                stats.ModifiedItems++;
                _logger.LogDebug(
                    "Modified series detected: {SeriesId} - {Name} (episodes: {OldCount} -> {NewCount}, checksum: {Old} -> {New})",
                    series.SeriesId,
                    series.Name,
                    previousSeries.EpisodeCount,
                    episodeCount,
                    previousSeries.Checksum,
                    currentChecksum);
            }
            else
            {
                // Unchanged series
                stats.UnchangedItems++;
            }
        }

        // Find removed series (in snapshot but not in current)
        foreach (var snapshotSeries in previousSnapshot.Series.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!processedIds.Contains(snapshotSeries.SeriesId))
            {
                delta.RemovedSeriesIds.Add(snapshotSeries.SeriesId);
                stats.RemovedItems++;
                _logger.LogDebug("Removed series detected: {SeriesId} - {Name}", snapshotSeries.SeriesId, snapshotSeries.Name);
            }
        }

        delta.Stats = stats;

        _logger.LogInformation(
            "Series delta: {Total} total, {New} new, {Modified} modified, {Removed} removed, {Unchanged} unchanged ({ChangePercent:F1}% changed)",
            stats.TotalItems,
            stats.NewItems,
            stats.ModifiedItems,
            stats.RemovedItems,
            stats.UnchangedItems,
            stats.ChangePercentage);

        return Task.FromResult(delta);
    }

    /// <summary>
    /// Merges movie and series deltas into a combined delta with unified statistics.
    /// </summary>
    /// <param name="movieDelta">The movie delta.</param>
    /// <param name="seriesDelta">The series delta.</param>
    /// <returns>A merged sync delta with combined statistics.</returns>
    public static SyncDelta MergeDeltas(SyncDelta movieDelta, SyncDelta seriesDelta)
    {
        return new SyncDelta
        {
            NewMovies = new List<StreamInfo>(movieDelta.NewMovies),
            ModifiedMovies = new List<StreamInfo>(movieDelta.ModifiedMovies),
            RemovedMovieIds = new List<int>(movieDelta.RemovedMovieIds),
            NewSeries = new List<Series>(seriesDelta.NewSeries),
            ModifiedSeries = new List<Series>(seriesDelta.ModifiedSeries),
            RemovedSeriesIds = new List<int>(seriesDelta.RemovedSeriesIds),
            Stats = new DeltaStatistics
            {
                TotalItems = movieDelta.Stats.TotalItems + seriesDelta.Stats.TotalItems,
                NewItems = movieDelta.Stats.NewItems + seriesDelta.Stats.NewItems,
                ModifiedItems = movieDelta.Stats.ModifiedItems + seriesDelta.Stats.ModifiedItems,
                RemovedItems = movieDelta.Stats.RemovedItems + seriesDelta.Stats.RemovedItems,
                UnchangedItems = movieDelta.Stats.UnchangedItems + seriesDelta.Stats.UnchangedItems
            }
        };
    }
}
