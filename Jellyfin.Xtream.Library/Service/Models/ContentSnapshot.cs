using System;
using System.Collections.Generic;

namespace Jellyfin.Xtream.Library.Service.Models;

/// <summary>
/// Represents a point-in-time snapshot of all content from an Xtream provider.
/// Used for incremental sync to detect changes between sync runs.
/// </summary>
public class ContentSnapshot
{
    /// <summary>
    /// Gets or sets the snapshot format version for future compatibility.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Gets or sets when this snapshot was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the provider URL this snapshot was created from.
    /// Used to detect provider changes that require full sync.
    /// </summary>
    public string ProviderUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets all movies indexed by StreamId.
    /// </summary>
    public Dictionary<int, MovieSnapshot> Movies { get; set; } = new();

    /// <summary>
    /// Gets or sets all series indexed by SeriesId.
    /// </summary>
    public Dictionary<int, SeriesSnapshot> Series { get; set; } = new();

    /// <summary>
    /// Gets or sets metadata about this snapshot.
    /// </summary>
    public SnapshotMetadata Metadata { get; set; } = new();
}

/// <summary>
/// Snapshot of a single movie's key identifying fields.
/// </summary>
public class MovieSnapshot
{
    /// <summary>
    /// Gets or sets the unique stream identifier.
    /// </summary>
    public int StreamId { get; set; }

    /// <summary>
    /// Gets or sets the movie name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the stream icon/poster URL.
    /// </summary>
    public string StreamIcon { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the container extension (e.g., "mkv", "mp4").
    /// </summary>
    public string? ContainerExtension { get; set; }

    /// <summary>
    /// Gets or sets the category ID this movie belongs to.
    /// </summary>
    public int CategoryId { get; set; }

    /// <summary>
    /// Gets or sets when the movie was added to the provider (Unix timestamp string from API).
    /// </summary>
    public string Added { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the MD5 checksum of key fields for change detection.
    /// </summary>
    public string Checksum { get; set; } = string.Empty;
}

/// <summary>
/// Snapshot of a single series's key identifying fields.
/// </summary>
public class SeriesSnapshot
{
    /// <summary>
    /// Gets or sets the unique series identifier.
    /// </summary>
    public int SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the series name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cover art URL.
    /// </summary>
    public string Cover { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the category ID this series belongs to.
    /// </summary>
    public int CategoryId { get; set; }

    /// <summary>
    /// Gets or sets the total number of episodes across all seasons.
    /// </summary>
    public int EpisodeCount { get; set; }

    /// <summary>
    /// Gets or sets when the series was last modified.
    /// </summary>
    public DateTime LastModified { get; set; }

    /// <summary>
    /// Gets or sets the MD5 checksum of key fields for change detection.
    /// </summary>
    public string Checksum { get; set; } = string.Empty;
}

/// <summary>
/// Metadata about a content snapshot.
/// </summary>
public class SnapshotMetadata
{
    /// <summary>
    /// Gets or sets the total number of movies in the snapshot.
    /// </summary>
    public int TotalMovies { get; set; }

    /// <summary>
    /// Gets or sets the total number of series in the snapshot.
    /// </summary>
    public int TotalSeries { get; set; }

    /// <summary>
    /// Gets or sets how long it took to create this snapshot.
    /// </summary>
    public TimeSpan SnapshotDuration { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the snapshot completed successfully.
    /// Incomplete snapshots should not be used for incremental sync.
    /// </summary>
    public bool IsComplete { get; set; } = true;
}

/// <summary>
/// Represents the delta between two content snapshots.
/// Contains lists of new, modified, and removed items.
/// </summary>
public class SyncDelta
{
    /// <summary>
    /// Gets or sets movies that are new since the last snapshot.
    /// </summary>
    public List<Client.Models.StreamInfo> NewMovies { get; set; } = new();

    /// <summary>
    /// Gets or sets movies whose metadata has changed since the last snapshot.
    /// </summary>
    public List<Client.Models.StreamInfo> ModifiedMovies { get; set; } = new();

    /// <summary>
    /// Gets or sets stream IDs of movies that have been removed from the provider.
    /// </summary>
    public List<int> RemovedMovieIds { get; set; } = new();

    /// <summary>
    /// Gets or sets series that are new since the last snapshot.
    /// </summary>
    public List<Client.Models.Series> NewSeries { get; set; } = new();

    /// <summary>
    /// Gets or sets series whose metadata has changed since the last snapshot.
    /// </summary>
    public List<Client.Models.Series> ModifiedSeries { get; set; } = new();

    /// <summary>
    /// Gets or sets series IDs of series that have been removed from the provider.
    /// </summary>
    public List<int> RemovedSeriesIds { get; set; } = new();

    /// <summary>
    /// Gets or sets statistics about the delta.
    /// </summary>
    public DeltaStatistics Stats { get; set; } = new();
}

/// <summary>
/// Statistical information about a sync delta.
/// </summary>
public class DeltaStatistics
{
    /// <summary>
    /// Gets or sets the total number of items in the current dataset.
    /// </summary>
    public int TotalItems { get; set; }

    /// <summary>
    /// Gets or sets the number of new items.
    /// </summary>
    public int NewItems { get; set; }

    /// <summary>
    /// Gets or sets the number of modified items.
    /// </summary>
    public int ModifiedItems { get; set; }

    /// <summary>
    /// Gets or sets the number of removed items.
    /// </summary>
    public int RemovedItems { get; set; }

    /// <summary>
    /// Gets or sets the number of unchanged items.
    /// </summary>
    public int UnchangedItems { get; set; }

    /// <summary>
    /// Gets the percentage of items that changed (new, modified, or removed)
    /// relative to the total universe of items (current + removed).
    /// </summary>
    public double ChangePercentage
    {
        get
        {
            var totalUniverse = TotalItems + RemovedItems;
            return totalUniverse > 0
                ? ((NewItems + ModifiedItems + RemovedItems) / (double)totalUniverse) * 100
                : 0;
        }
    }
}
