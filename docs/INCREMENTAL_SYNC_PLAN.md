# Incremental Sync Implementation Plan

## Overview
Implement delta-based syncing to only process changed content, reducing sync time from minutes to seconds for subsequent syncs.

## Goals
- **Performance**: 10-100x faster syncs after initial full sync
- **Efficiency**: Reduce API calls by 90%+ on incremental syncs
- **Reliability**: Automatic fallback to full sync on corruption
- **Compatibility**: Seamless migration for existing users

## Architecture

### Data Structures

#### ContentSnapshot (Storage Model)
```csharp
public class ContentSnapshot
{
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public string ProviderUrl { get; set; } = string.Empty;
    public Dictionary<int, MovieSnapshot> Movies { get; set; } = new();
    public Dictionary<int, SeriesSnapshot> Series { get; set; } = new();
    public SnapshotMetadata Metadata { get; set; } = new();
}

public class MovieSnapshot
{
    public int StreamId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? StreamIcon { get; set; }
    public string? ContainerExtension { get; set; }
    public int CategoryId { get; set; }
    public DateTime? Added { get; set; }
    public string Checksum { get; set; } = string.Empty; // MD5 of key fields
}

public class SeriesSnapshot
{
    public int SeriesId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Cover { get; set; }
    public int CategoryId { get; set; }
    public int EpisodeCount { get; set; } // Total episodes
    public DateTime? LastModified { get; set; }
    public string Checksum { get; set; } = string.Empty;
}

public class SnapshotMetadata
{
    public int TotalMovies { get; set; }
    public int TotalSeries { get; set; }
    public TimeSpan SnapshotDuration { get; set; }
    public bool IsComplete { get; set; } = true;
}
```

#### SyncDelta (Runtime Model)
```csharp
public class SyncDelta
{
    public List<VodStream> NewMovies { get; set; } = new();
    public List<VodStream> ModifiedMovies { get; set; } = new();
    public List<int> RemovedMovieIds { get; set; } = new();

    public List<SeriesStream> NewSeries { get; set; } = new();
    public List<SeriesStream> ModifiedSeries { get; set; } = new();
    public List<int> RemovedSeriesIds { get; set; } = new();

    public DeltaStatistics Stats { get; set; } = new();
}

public class DeltaStatistics
{
    public int TotalItems { get; set; }
    public int NewItems { get; set; }
    public int ModifiedItems { get; set; }
    public int RemovedItems { get; set; }
    public int UnchangedItems { get; set; }
    public double ChangePercentage => TotalItems > 0
        ? ((NewItems + ModifiedItems + RemovedItems) / (double)TotalItems) * 100
        : 0;
}
```

### Storage Location
```
/config/xtream-library/
├── .snapshots/
│   ├── snapshot_20260204_235959.json      # Latest snapshot
│   ├── snapshot_20260204_120000.json      # Previous (kept for rollback)
│   └── snapshot_20260203_235959.json      # Older (auto-cleanup after 7 days)
└── .snapshot-lock                          # Prevent concurrent writes
```

## Implementation Phases

### Phase 1: Snapshot Service (Core Infrastructure)
**Files**: `Service/SnapshotService.cs`, `Service/Models/Snapshot.cs`

#### SnapshotService.cs
```csharp
public class SnapshotService
{
    private readonly string _snapshotDirectory;
    private readonly ILogger<SnapshotService> _logger;
    private const string SnapshotFilePattern = "snapshot_*.json";
    private const int MaxSnapshotsToKeep = 3;

    public SnapshotService(ILogger<SnapshotService> logger)
    {
        _logger = logger;
        _snapshotDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "jellyfin", "data", "xtream-library", ".snapshots");
        Directory.CreateDirectory(_snapshotDirectory);
    }

    public async Task<ContentSnapshot?> LoadLatestSnapshotAsync(CancellationToken ct)
    {
        var latestFile = GetLatestSnapshotFile();
        if (latestFile == null)
        {
            _logger.LogInformation("No snapshot found - first sync will be full");
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(latestFile, ct);
            var snapshot = JsonConvert.DeserializeObject<ContentSnapshot>(json);

            if (snapshot?.Metadata?.IsComplete == true)
            {
                _logger.LogInformation(
                    "Loaded snapshot from {Date} ({Movies} movies, {Series} series)",
                    snapshot.CreatedAt,
                    snapshot.Metadata.TotalMovies,
                    snapshot.Metadata.TotalSeries);
                return snapshot;
            }

            _logger.LogWarning("Snapshot is incomplete or corrupted, triggering full sync");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load snapshot from {File}", latestFile);
            return null;
        }
    }

    public async Task SaveSnapshotAsync(
        ContentSnapshot snapshot,
        CancellationToken ct)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var filename = $"snapshot_{timestamp}.json";
        var filepath = Path.Combine(_snapshotDirectory, filename);
        var lockFile = Path.Combine(_snapshotDirectory, ".snapshot-lock");

        // Acquire lock to prevent concurrent writes
        await using var lockStream = new FileStream(
            lockFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        try
        {
            snapshot.CreatedAt = DateTime.UtcNow;
            var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
            await File.WriteAllTextAsync(filepath, json, ct);

            _logger.LogInformation(
                "Saved snapshot to {File} ({Size} KB)",
                filename,
                new FileInfo(filepath).Length / 1024);

            // Cleanup old snapshots
            await CleanupOldSnapshotsAsync();
        }
        finally
        {
            await lockStream.DisposeAsync();
            if (File.Exists(lockFile))
            {
                File.Delete(lockFile);
            }
        }
    }

    private string? GetLatestSnapshotFile()
    {
        return Directory.GetFiles(_snapshotDirectory, SnapshotFilePattern)
            .OrderByDescending(f => f)
            .FirstOrDefault();
    }

    private async Task CleanupOldSnapshotsAsync()
    {
        var snapshots = Directory.GetFiles(_snapshotDirectory, SnapshotFilePattern)
            .OrderByDescending(f => f)
            .Skip(MaxSnapshotsToKeep)
            .ToList();

        foreach (var oldSnapshot in snapshots)
        {
            try
            {
                File.Delete(oldSnapshot);
                _logger.LogDebug("Deleted old snapshot: {File}", Path.GetFileName(oldSnapshot));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete old snapshot: {File}", oldSnapshot);
            }
        }
    }

    public string CalculateChecksum(VodStream movie)
    {
        // Hash fields that indicate content change
        var data = $"{movie.Name}|{movie.StreamIcon}|{movie.ContainerExtension}|{movie.CategoryId}";
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
        return Convert.ToBase64String(hash);
    }

    public string CalculateChecksum(SeriesStream series, int episodeCount)
    {
        var data = $"{series.Name}|{series.Cover}|{series.CategoryId}|{episodeCount}";
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
        return Convert.ToBase64String(hash);
    }
}
```

**Tests**: Create `SnapshotServiceTests.cs` with tests for save/load/cleanup

---

### Phase 2: Delta Calculator
**Files**: `Service/DeltaCalculator.cs`

#### DeltaCalculator.cs
```csharp
public class DeltaCalculator
{
    private readonly SnapshotService _snapshotService;
    private readonly ILogger<DeltaCalculator> _logger;

    public DeltaCalculator(
        SnapshotService snapshotService,
        ILogger<DeltaCalculator> logger)
    {
        _snapshotService = snapshotService;
        _logger = logger;
    }

    public async Task<SyncDelta> CalculateMovieDeltaAsync(
        IEnumerable<VodStream> currentMovies,
        ContentSnapshot? previousSnapshot,
        CancellationToken ct)
    {
        var delta = new SyncDelta();

        if (previousSnapshot == null)
        {
            // First sync - everything is new
            delta.NewMovies = currentMovies.ToList();
            delta.Stats.NewItems = delta.NewMovies.Count;
            delta.Stats.TotalItems = delta.NewMovies.Count;
            _logger.LogInformation("First sync: all {Count} movies are new", delta.NewMovies.Count);
            return delta;
        }

        var currentDict = currentMovies.ToDictionary(m => m.StreamId);
        var previousDict = previousSnapshot.Movies;

        // Find new and modified movies
        foreach (var movie in currentMovies)
        {
            var checksum = _snapshotService.CalculateChecksum(movie);

            if (!previousDict.TryGetValue(movie.StreamId, out var previousMovie))
            {
                // New movie
                delta.NewMovies.Add(movie);
                delta.Stats.NewItems++;
            }
            else if (previousMovie.Checksum != checksum)
            {
                // Modified movie (metadata changed)
                delta.ModifiedMovies.Add(movie);
                delta.Stats.ModifiedItems++;
            }
            else
            {
                // Unchanged
                delta.Stats.UnchangedItems++;
            }
        }

        // Find removed movies (orphans)
        delta.RemovedMovieIds = previousDict.Keys
            .Where(id => !currentDict.ContainsKey(id))
            .ToList();
        delta.Stats.RemovedItems = delta.RemovedMovieIds.Count;
        delta.Stats.TotalItems = currentDict.Count + delta.RemovedMovieIds.Count;

        _logger.LogInformation(
            "Movie delta: {New} new, {Modified} modified, {Removed} removed, {Unchanged} unchanged ({Change:F1}% change)",
            delta.Stats.NewItems,
            delta.Stats.ModifiedItems,
            delta.Stats.RemovedItems,
            delta.Stats.UnchangedItems,
            delta.Stats.ChangePercentage);

        return delta;
    }

    public async Task<SyncDelta> CalculateSeriesDeltaAsync(
        IEnumerable<SeriesStream> currentSeries,
        Dictionary<int, SeriesInfo> seriesInfoDict,
        ContentSnapshot? previousSnapshot,
        CancellationToken ct)
    {
        var delta = new SyncDelta();

        if (previousSnapshot == null)
        {
            delta.NewSeries = currentSeries.ToList();
            delta.Stats.NewItems = delta.NewSeries.Count;
            delta.Stats.TotalItems = delta.NewSeries.Count;
            return delta;
        }

        var currentDict = currentSeries.ToDictionary(s => s.SeriesId);
        var previousDict = previousSnapshot.Series;

        foreach (var series in currentSeries)
        {
            // Get episode count for checksum
            var episodeCount = seriesInfoDict.TryGetValue(series.SeriesId, out var info)
                ? info.Episodes.Values.Sum(eps => eps.Count)
                : 0;

            var checksum = _snapshotService.CalculateChecksum(series, episodeCount);

            if (!previousDict.TryGetValue(series.SeriesId, out var previousSeries))
            {
                delta.NewSeries.Add(series);
                delta.Stats.NewItems++;
            }
            else if (previousSeries.Checksum != checksum || previousSeries.EpisodeCount != episodeCount)
            {
                // Modified (metadata changed or new episodes added)
                delta.ModifiedSeries.Add(series);
                delta.Stats.ModifiedItems++;
            }
            else
            {
                delta.Stats.UnchangedItems++;
            }
        }

        delta.RemovedSeriesIds = previousDict.Keys
            .Where(id => !currentDict.ContainsKey(id))
            .ToList();
        delta.Stats.RemovedItems = delta.RemovedSeriesIds.Count;
        delta.Stats.TotalItems = currentDict.Count + delta.RemovedSeriesIds.Count;

        _logger.LogInformation(
            "Series delta: {New} new, {Modified} modified, {Removed} removed, {Unchanged} unchanged ({Change:F1}% change)",
            delta.Stats.NewItems,
            delta.Stats.ModifiedItems,
            delta.Stats.RemovedItems,
            delta.Stats.UnchangedItems,
            delta.Stats.ChangePercentage);

        return delta;
    }
}
```

**Tests**: Create `DeltaCalculatorTests.cs` with comprehensive delta scenarios

---

### Phase 3: Configuration & Settings
**Files**: `PluginConfiguration.cs` (update), `Configuration/Web/config.html`, `Configuration/Web/config.js`

#### Add to PluginConfiguration.cs
```csharp
/// <summary>
/// Gets or sets a value indicating whether incremental sync is enabled.
/// When enabled, only changed content is synced after the first full sync.
/// </summary>
public bool EnableIncrementalSync { get; set; } = true;

/// <summary>
/// Gets or sets the interval in days before forcing a full sync.
/// This ensures data integrity even with incremental syncs enabled.
/// </summary>
public int FullSyncIntervalDays { get; set; } = 7;

/// <summary>
/// Gets or sets a value indicating whether to track metadata changes.
/// When enabled, movies/series with changed titles, icons, etc. will be re-synced.
/// </summary>
public bool TrackMetadataChanges { get; set; } = true;

/// <summary>
/// Gets or sets the change threshold percentage that triggers a full sync.
/// If more than this percentage of content changed, do a full sync instead.
/// </summary>
public double FullSyncChangeThreshold { get; set; } = 0.50; // 50%
```

#### Update config.html (Advanced Settings section)
```html
<div class="inputContainer">
    <label class="checkboxContainer">
        <input type="checkbox" id="chkEnableIncrementalSync" />
        <span>Enable Incremental Sync (faster syncs after first run)</span>
    </label>
    <div class="fieldDescription">
        Only sync changed content instead of re-syncing everything.
        Dramatically reduces sync time and API calls.
    </div>
</div>

<div class="inputContainer">
    <label class="inputLabel inputLabelUnfocused" for="txtFullSyncIntervalDays">
        Full Sync Interval (days)
    </label>
    <input type="number" id="txtFullSyncIntervalDays" min="1" max="30" />
    <div class="fieldDescription">
        Force a complete re-sync every N days for data integrity
    </div>
</div>

<div class="inputContainer">
    <label class="checkboxContainer">
        <input type="checkbox" id="chkTrackMetadataChanges" />
        <span>Track Metadata Changes</span>
    </label>
    <div class="fieldDescription">
        Re-sync content when titles, icons, or other metadata changes
    </div>
</div>
```

---

### Phase 4: Integration with StrmSyncService
**Files**: `Service/StrmSyncService.cs` (major updates)

#### Key Changes to SyncAsync Method

```csharp
public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
{
    var config = Plugin.Instance.Configuration;
    config.Validate();

    // ... existing setup code ...

    // NEW: Load previous snapshot
    ContentSnapshot? previousSnapshot = null;
    if (config.EnableIncrementalSync)
    {
        previousSnapshot = await _snapshotService.LoadLatestSnapshotAsync(linkedToken);

        // Check if full sync is needed
        if (ShouldForceFullSync(previousSnapshot, config))
        {
            _logger.LogInformation("Forcing full sync (last full sync: {Date})",
                previousSnapshot?.CreatedAt);
            previousSnapshot = null; // Treat as first sync
        }
    }

    bool isIncrementalSync = previousSnapshot != null;
    CurrentProgress.Phase = isIncrementalSync
        ? "Starting incremental sync"
        : "Starting full sync";

    // ... rest of sync logic with delta calculation ...
}

private bool ShouldForceFullSync(ContentSnapshot? snapshot, PluginConfiguration config)
{
    if (snapshot == null) return false;

    var daysSinceLastFull = (DateTime.UtcNow - snapshot.CreatedAt).TotalDays;
    if (daysSinceLastFull >= config.FullSyncIntervalDays)
    {
        _logger.LogInformation(
            "Full sync interval reached ({Days:F1} days since last full sync)",
            daysSinceLastFull);
        return true;
    }

    // Check if provider URL changed
    if (snapshot.ProviderUrl != config.BaseUrl)
    {
        _logger.LogInformation("Provider URL changed, forcing full sync");
        return true;
    }

    return false;
}
```

#### Update SyncMoviesAsync for Delta
```csharp
private async Task SyncMoviesAsync(
    ConnectionInfo connectionInfo,
    string moviesPath,
    ConcurrentDictionary<string, byte> syncedFiles,
    SyncResult result,
    ContentSnapshot? previousSnapshot, // NEW parameter
    CancellationToken cancellationToken)
{
    // ... existing category fetching ...

    // Fetch all movies
    var allMovies = await FetchAllMoviesAsync(/* ... */);

    // NEW: Calculate delta
    var delta = await _deltaCalculator.CalculateMovieDeltaAsync(
        allMovies,
        previousSnapshot,
        cancellationToken);

    // Update result with delta stats
    result.DeltaStats = delta.Stats;

    // Only process new + modified movies
    var moviesToProcess = delta.NewMovies.Concat(delta.ModifiedMovies).ToList();

    _logger.LogInformation(
        "Processing {Count} movies ({New} new, {Modified} modified, skipping {Unchanged} unchanged)",
        moviesToProcess.Count,
        delta.NewMovies.Count,
        delta.ModifiedMovies.Count,
        delta.Stats.UnchangedItems);

    CurrentProgress.TotalItems = moviesToProcess.Count;

    // Process only the delta
    await ProcessMoviesAsync(moviesToProcess, /* ... */);

    // Handle removed movies (orphans) if cleanup enabled
    if (config.CleanupOrphans && delta.RemovedMovieIds.Count > 0)
    {
        await HandleRemovedMoviesAsync(delta.RemovedMovieIds, moviesPath, result);
    }
}
```

---

### Phase 5: Snapshot Creation After Sync
**Files**: `Service/StrmSyncService.cs` (continued)

```csharp
public async Task<SyncResult> SyncAsync(CancellationToken cancellationToken)
{
    // ... sync logic ...

    try
    {
        // Existing sync operations
        await SyncMoviesAsync(/* ... previousSnapshot ... */);
        await SyncSeriesAsync(/* ... previousSnapshot ... */);

        // NEW: Create snapshot after successful sync
        if (config.EnableIncrementalSync && !cancellationToken.IsCancellationRequested)
        {
            CurrentProgress.Phase = "Creating snapshot";
            await CreateSnapshotAsync(allMovies, allSeries, seriesInfoDict, cancellationToken);
        }
    }
    catch (OperationCanceledException)
    {
        // Don't save snapshot if sync was cancelled
        throw;
    }

    // ... rest of method ...
}

private async Task CreateSnapshotAsync(
    IEnumerable<VodStream> allMovies,
    IEnumerable<SeriesStream> allSeries,
    Dictionary<int, SeriesInfo> seriesInfoDict,
    CancellationToken ct)
{
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var config = Plugin.Instance.Configuration;

    var snapshot = new ContentSnapshot
    {
        ProviderUrl = config.BaseUrl,
        Movies = allMovies.ToDictionary(
            m => m.StreamId,
            m => new MovieSnapshot
            {
                StreamId = m.StreamId,
                Name = m.Name,
                StreamIcon = m.StreamIcon,
                ContainerExtension = m.ContainerExtension,
                CategoryId = m.CategoryId,
                Added = m.Added,
                Checksum = _snapshotService.CalculateChecksum(m)
            }),
        Series = allSeries.ToDictionary(
            s => s.SeriesId,
            s =>
            {
                var episodeCount = seriesInfoDict.TryGetValue(s.SeriesId, out var info)
                    ? info.Episodes.Values.Sum(eps => eps.Count)
                    : 0;
                return new SeriesSnapshot
                {
                    SeriesId = s.SeriesId,
                    Name = s.Name,
                    Cover = s.Cover,
                    CategoryId = s.CategoryId,
                    EpisodeCount = episodeCount,
                    LastModified = s.LastModified,
                    Checksum = _snapshotService.CalculateChecksum(s, episodeCount)
                };
            }),
        Metadata = new SnapshotMetadata
        {
            TotalMovies = allMovies.Count(),
            TotalSeries = allSeries.Count(),
            IsComplete = true
        }
    };

    await _snapshotService.SaveSnapshotAsync(snapshot, ct);

    stopwatch.Stop();
    snapshot.Metadata.SnapshotDuration = stopwatch.Elapsed;

    _logger.LogInformation(
        "Snapshot created in {Duration:F2}s ({Movies} movies, {Series} series, {Size} KB)",
        stopwatch.Elapsed.TotalSeconds,
        snapshot.Movies.Count,
        snapshot.Series.Count,
        CalculateSnapshotSize(snapshot));
}

private long CalculateSnapshotSize(ContentSnapshot snapshot)
{
    var json = JsonConvert.SerializeObject(snapshot);
    return System.Text.Encoding.UTF8.GetByteCount(json) / 1024;
}
```

---

### Phase 6: UI Enhancements
**Files**: `Configuration/Web/config.js`, `Service/StrmSyncService.cs`

#### Update SyncResult to Include Delta Stats
```csharp
public class SyncResult
{
    // ... existing properties ...

    public DeltaStatistics? DeltaStats { get; set; } // NEW
    public bool WasIncrementalSync => DeltaStats != null;
    public DateTime? LastFullSyncDate { get; set; } // NEW
}
```

#### Update UI to Show Sync Type
```javascript
// In config.js, update the displaySyncResult function
displaySyncResult: function(result) {
    let html = '<div class="syncResultSummary">';

    // Show sync type
    if (result.WasIncrementalSync) {
        html += '<div class="syncType incremental">';
        html += '⚡ Incremental Sync';
        html += '</div>';

        // Show delta stats
        if (result.DeltaStats) {
            html += '<div class="deltaStats">';
            html += `<div>New: ${result.DeltaStats.NewItems}</div>`;
            html += `<div>Modified: ${result.DeltaStats.ModifiedItems}</div>`;
            html += `<div>Removed: ${result.DeltaStats.RemovedItems}</div>`;
            html += `<div>Unchanged: ${result.DeltaStats.UnchangedItems}</div>`;
            html += `<div class="changePercentage">${result.DeltaStats.ChangePercentage.toFixed(1)}% changed</div>`;
            html += '</div>';
        }

        if (result.LastFullSyncDate) {
            const daysSinceFull = Math.floor((Date.now() - new Date(result.LastFullSyncDate)) / (1000 * 60 * 60 * 24));
            html += `<div class="lastFullSync">Last full sync: ${daysSinceFull} days ago</div>`;
        }
    } else {
        html += '<div class="syncType full">';
        html += '🔄 Full Sync';
        html += '</div>';
    }

    // ... rest of result display ...
}
```

---

### Phase 7: Testing Strategy

#### Unit Tests
**File**: `Jellyfin.Xtream.Library.Tests/Service/IncrementalSyncTests.cs`

```csharp
public class IncrementalSyncTests
{
    [Fact]
    public async Task FirstSync_NoSnapshot_AllMoviesAreNew()
    {
        // Arrange
        var movies = CreateTestMovies(100);
        var calculator = CreateDeltaCalculator();

        // Act
        var delta = await calculator.CalculateMovieDeltaAsync(movies, null, CancellationToken.None);

        // Assert
        delta.NewMovies.Should().HaveCount(100);
        delta.ModifiedMovies.Should().BeEmpty();
        delta.RemovedMovieIds.Should().BeEmpty();
        delta.Stats.ChangePercentage.Should().Be(100.0);
    }

    [Fact]
    public async Task IncrementalSync_NoChanges_AllUnchanged()
    {
        // Arrange
        var movies = CreateTestMovies(100);
        var snapshot = CreateSnapshotFromMovies(movies);
        var calculator = CreateDeltaCalculator();

        // Act
        var delta = await calculator.CalculateMovieDeltaAsync(movies, snapshot, CancellationToken.None);

        // Assert
        delta.NewMovies.Should().BeEmpty();
        delta.ModifiedMovies.Should().BeEmpty();
        delta.RemovedMovieIds.Should().BeEmpty();
        delta.Stats.UnchangedItems.Should().Be(100);
        delta.Stats.ChangePercentage.Should().Be(0.0);
    }

    [Fact]
    public async Task IncrementalSync_NewMoviesAdded_DetectsNew()
    {
        // Arrange
        var oldMovies = CreateTestMovies(100);
        var snapshot = CreateSnapshotFromMovies(oldMovies);
        var newMovies = CreateTestMovies(110); // 10 new movies
        var calculator = CreateDeltaCalculator();

        // Act
        var delta = await calculator.CalculateMovieDeltaAsync(newMovies, snapshot, CancellationToken.None);

        // Assert
        delta.NewMovies.Should().HaveCount(10);
        delta.ModifiedMovies.Should().BeEmpty();
        delta.RemovedMovieIds.Should().BeEmpty();
        delta.Stats.UnchangedItems.Should().Be(100);
    }

    [Fact]
    public async Task IncrementalSync_MoviesRemoved_DetectsOrphans()
    {
        // Arrange
        var oldMovies = CreateTestMovies(100);
        var snapshot = CreateSnapshotFromMovies(oldMovies);
        var newMovies = oldMovies.Take(90).ToList(); // 10 removed
        var calculator = CreateDeltaCalculator();

        // Act
        var delta = await calculator.CalculateMovieDeltaAsync(newMovies, snapshot, CancellationToken.None);

        // Assert
        delta.RemovedMovieIds.Should().HaveCount(10);
        delta.NewMovies.Should().BeEmpty();
        delta.ModifiedMovies.Should().BeEmpty();
    }

    [Fact]
    public async Task IncrementalSync_MetadataChanged_DetectsModified()
    {
        // Arrange
        var oldMovies = CreateTestMovies(100);
        var snapshot = CreateSnapshotFromMovies(oldMovies);
        var newMovies = oldMovies.ToList();
        newMovies[0].Name = "Modified Title"; // Change metadata
        var calculator = CreateDeltaCalculator();

        // Act
        var delta = await calculator.CalculateMovieDeltaAsync(newMovies, snapshot, CancellationToken.None);

        // Assert
        delta.ModifiedMovies.Should().HaveCount(1);
        delta.ModifiedMovies[0].Name.Should().Be("Modified Title");
    }

    [Fact]
    public async Task SnapshotService_SaveAndLoad_RoundTrips()
    {
        // Arrange
        var service = CreateSnapshotService();
        var snapshot = CreateTestSnapshot(1000, 50);

        // Act
        await service.SaveSnapshotAsync(snapshot, CancellationToken.None);
        var loaded = await service.LoadLatestSnapshotAsync(CancellationToken.None);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.Movies.Should().HaveCount(1000);
        loaded.Series.Should().HaveCount(50);
        loaded.Metadata.IsComplete.Should().BeTrue();
    }
}
```

#### Integration Tests
```csharp
[Fact]
public async Task FullWorkflow_TwoSyncs_SecondIsIncremental()
{
    // First sync - full
    var result1 = await _syncService.SyncAsync(CancellationToken.None);
    result1.WasIncrementalSync.Should().BeFalse();

    // Second sync - incremental (no changes)
    var result2 = await _syncService.SyncAsync(CancellationToken.None);
    result2.WasIncrementalSync.Should().BeTrue();
    result2.DeltaStats.ChangePercentage.Should().Be(0.0);
}
```

---

## Migration & Compatibility

### Existing Users
- **Automatic**: No action required
- **First Sync**: Will be full sync (creates initial snapshot)
- **Subsequent Syncs**: Automatic incremental
- **Opt-Out**: Can disable via `EnableIncrementalSync` setting

### Backward Compatibility
- All existing settings and behavior preserved
- Snapshots stored separately, don't affect current operations
- Graceful degradation if snapshot corrupted

### Rollback Plan
If issues arise:
1. Disable incremental sync in config
2. Delete `.snapshots` directory
3. Next sync will be full sync

---

## Performance Targets

| Scenario | Current | Target | Improvement |
|----------|---------|--------|-------------|
| **First Sync (1000 movies)** | 5 min | 5 min | Baseline |
| **Second Sync (no changes)** | 5 min | 5 sec | **60x faster** |
| **Incremental (1% changed)** | 5 min | 10 sec | **30x faster** |
| **API Calls (no changes)** | 1000+ | ~10 | **100x reduction** |
| **Snapshot Size (1000 items)** | N/A | ~50 KB | Minimal |

---

## Rollout Plan

### Week 1: Core Infrastructure
- Phase 1: SnapshotService + tests
- Phase 2: DeltaCalculator + tests
- Phase 3: Configuration updates

### Week 2: Integration
- Phase 4: StrmSyncService integration
- Phase 5: Snapshot creation logic
- End-to-end testing

### Week 3: Polish & Release
- Phase 6: UI enhancements
- Phase 7: Comprehensive testing
- Documentation updates
- Beta testing
- Release as v1.29.0.0

---

## Success Metrics
- ✅ Sync time reduced by 50%+ on incremental syncs
- ✅ API calls reduced by 90%+ on no-change syncs
- ✅ Zero data loss or corruption
- ✅ Automatic recovery from snapshot corruption
- ✅ 100% test coverage for delta logic
- ✅ No performance regression on first sync

---

## Future Enhancements (Post-Release)
1. **Snapshot Compression**: gzip snapshots to reduce disk usage
2. **Remote Snapshots**: Sync snapshots across multiple Jellyfin instances
3. **Diff Viewer**: UI to show exactly what changed between syncs
4. **Smart Scheduling**: Auto-detect low activity periods for full syncs
5. **Snapshot Analytics**: Track provider stability over time
