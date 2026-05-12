using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service.Models;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Service for persisting and loading content snapshots for incremental sync.
/// </summary>
public class SnapshotService : IDisposable
{
    private const int MaxSnapshotsToKeep = 3;
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILogger<SnapshotService> _logger;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotService"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths service.</param>
    /// <param name="logger">The logger.</param>
    public SnapshotService(
        IServerApplicationPaths appPaths,
        ILogger<SnapshotService> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>
    /// Gets the snapshot directory for the given provider key.
    /// Provider key format: "{providerIndex}-{urlHashPrefix}" (e.g. "0-a1b2c3d4").
    /// Use "0-legacy" for snapshots created before multi-provider support.
    /// </summary>
    /// <param name="providerKey">The provider-specific key.</param>
    /// <returns>The snapshot directory path for this provider.</returns>
    private string GetSnapshotDirectory(string providerKey) =>
        Path.Combine(_appPaths.DataPath, "xtream-library", ".snapshots", $"provider-{providerKey}");

    /// <summary>
    /// Calculates an MD5 checksum for a movie's key fields.
    /// Note: StreamIcon (poster URL) is intentionally excluded - URL changes should not trigger re-sync.
    /// </summary>
    /// <param name="movie">The movie to checksum.</param>
    /// <returns>The MD5 checksum as a hex string.</returns>
#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms - MD5 used for checksums only, not security
    public static string CalculateChecksum(StreamInfo movie)
    {
        var data = string.Join(
            "|",
            movie.Name,
            movie.ContainerExtension ?? string.Empty,
            (movie.CategoryId ?? 0).ToString(CultureInfo.InvariantCulture));

        return ComputeMd5(data);
    }

    /// <summary>
    /// Calculates an MD5 checksum for a series's key fields.
    /// Note: Cover (poster URL) is intentionally excluded - URL changes should not trigger re-sync.
    /// LastModified is included to detect provider-signaled metadata updates.
    /// </summary>
    /// <param name="series">The series to checksum.</param>
    /// <param name="episodeCount">The total episode count.</param>
    /// <returns>The MD5 checksum as a hex string.</returns>
    public static string CalculateChecksum(Series series, int episodeCount)
    {
        var data = string.Join(
            "|",
            series.Name,
            (series.CategoryId ?? 0).ToString(CultureInfo.InvariantCulture),
            episodeCount.ToString(CultureInfo.InvariantCulture),
            series.LastModified.GetValueOrDefault().ToString("O", CultureInfo.InvariantCulture));

        return ComputeMd5(data);
    }

    /// <summary>
    /// Computes a fingerprint of per-provider configuration settings that affect folder structure.
    /// Changes to these settings require a full sync to reprocess all items.
    /// </summary>
    /// <param name="provider">The provider configuration.</param>
    /// <param name="enableMetadataLookup">Whether global metadata lookup is enabled (from <see cref="PluginConfiguration.EnableMetadataLookup"/>).</param>
    /// <returns>An MD5 fingerprint of the relevant settings.</returns>
    public static string CalculateConfigFingerprint(ProviderConfig provider, bool enableMetadataLookup = true)
    {
        var data = string.Join(
            "|",
            provider.MovieFolderMode,
            provider.SeriesFolderMode,
            provider.MovieFolderMappings ?? string.Empty,
            provider.SeriesFolderMappings ?? string.Empty,
            string.Join(",", provider.SelectedVodCategoryIds?.OrderBy(id => id) ?? Enumerable.Empty<int>()),
            string.Join(",", provider.SelectedSeriesCategoryIds?.OrderBy(id => id) ?? Enumerable.Empty<int>()),
            enableMetadataLookup.ToString(CultureInfo.InvariantCulture),
            provider.TmdbFolderIdOverrides ?? string.Empty,
            provider.TvdbFolderIdOverrides ?? string.Empty);

        return ComputeMd5(data);
    }

#pragma warning restore CA5351

    /// <summary>
    /// Loads the most recent valid snapshot for the given provider.
    /// </summary>
    /// <param name="providerKey">Provider-specific directory key (format: "{index}-{urlHash8}"). Defaults to "0-legacy".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The latest snapshot, or null if none exists or if corrupted.</returns>
    public async Task<ContentSnapshot?> LoadLatestSnapshotAsync(string providerKey = "0-legacy", CancellationToken cancellationToken = default)
    {
        var snapshotDirectory = GetSnapshotDirectory(providerKey);

        try
        {
            if (!Directory.Exists(snapshotDirectory))
            {
                _logger.LogDebug("Snapshot directory does not exist");
                return null;
            }

            var snapshotFiles = Directory.GetFiles(snapshotDirectory, "snapshot_*.json")
                .OrderByDescending(f => f)
                .ToList();

            if (snapshotFiles.Count == 0)
            {
                _logger.LogDebug("No snapshot files found");
                return null;
            }

            foreach (var file in snapshotFiles)
            {
                try
                {
                    await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                        var snapshot = JsonConvert.DeserializeObject<ContentSnapshot>(json);

                        if (snapshot == null)
                        {
                            _logger.LogWarning("Failed to deserialize snapshot: {File}", file);
                            continue;
                        }

                        if (!snapshot.Metadata.IsComplete)
                        {
                            _logger.LogWarning("Snapshot is incomplete: {File}", file);
                            continue;
                        }

                        _logger.LogInformation(
                            "Loaded snapshot from {Date} ({Movies} movies, {Series} series)",
                            snapshot.CreatedAt,
                            snapshot.Movies.Count,
                            snapshot.Series.Count);

                        return snapshot;
                    }
                    finally
                    {
                        _fileLock.Release();
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Corrupted snapshot file: {File}", file);
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error loading snapshot: {File}", file);
                    continue;
                }
            }

            _logger.LogWarning("No valid snapshot files found");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error loading latest snapshot");
            return null;
        }
    }

    /// <summary>
    /// Saves a snapshot atomically and cleans up old snapshots.
    /// Writes to a temporary file first, then renames to prevent corruption from process crashes.
    /// </summary>
    /// <param name="snapshot">The snapshot to save.</param>
    /// <param name="providerKey">Provider-specific directory key (format: "{index}-{urlHash8}"). Defaults to "0-legacy".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SaveSnapshotAsync(ContentSnapshot snapshot, string providerKey = "0-legacy", CancellationToken cancellationToken = default)
    {
        var snapshotDirectory = GetSnapshotDirectory(providerKey);

        try
        {
            Directory.CreateDirectory(snapshotDirectory);

            snapshot.CreatedAt = DateTime.UtcNow;

            var fileName = $"snapshot_{snapshot.CreatedAt:yyyyMMdd_HHmmss_fff}.json";
            var filePath = Path.Combine(snapshotDirectory, fileName);
            var tmpPath = filePath + ".tmp";

            await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);

                // Atomic write: write to .tmp file, then rename
                await File.WriteAllTextAsync(tmpPath, json, cancellationToken).ConfigureAwait(false);
                File.Move(tmpPath, filePath, overwrite: true);

                _logger.LogInformation(
                    "Saved snapshot to {File} ({Movies} movies, {Series} series)",
                    fileName,
                    snapshot.Movies.Count,
                    snapshot.Series.Count);
            }
            catch
            {
                // Clean up temp file on failure
                if (File.Exists(tmpPath))
                {
                    try
                    {
                        File.Delete(tmpPath);
                    }
                    catch
                    {
                        // Best effort cleanup
                    }
                }

                throw;
            }
            finally
            {
                _fileLock.Release();
            }

            // Cleanup old snapshots
            await CleanupOldSnapshotsAsync(providerKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error saving snapshot");
            throw;
        }
    }

    /// <summary>
    /// Disposes the resources used by this service.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the resources used by this service.
    /// </summary>
    /// <param name="disposing">Whether to dispose managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _fileLock.Dispose();
        }

        _disposed = true;
    }

    /// <summary>
    /// Deletes all snapshot files for a provider. Used when cleaning libraries to force a full sync.
    /// </summary>
    /// <param name="providerKey">Provider-specific directory key. Defaults to "0-legacy".</param>
    public void ClearAllSnapshots(string providerKey = "0-legacy")
    {
        var snapshotDirectory = GetSnapshotDirectory(providerKey);

        if (!Directory.Exists(snapshotDirectory))
        {
            return;
        }

        _fileLock.Wait();
        try
        {
            foreach (var file in Directory.GetFiles(snapshotDirectory, "snapshot_*.json"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete snapshot: {File}", file);
                }
            }

            _logger.LogInformation("Cleared all snapshots");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Deletes old snapshot files, keeping only the most recent ones.
    /// </summary>
    /// <param name="providerKey">Provider-specific directory key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task CleanupOldSnapshotsAsync(string providerKey, CancellationToken cancellationToken)
    {
        var snapshotDirectory = GetSnapshotDirectory(providerKey);

        try
        {
            if (!Directory.Exists(snapshotDirectory))
            {
                return;
            }

            var snapshotFiles = Directory.GetFiles(snapshotDirectory, "snapshot_*.json")
                .OrderByDescending(f => f)
                .Skip(MaxSnapshotsToKeep)
                .ToList();

            if (snapshotFiles.Count == 0)
            {
                return;
            }

            await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var file in snapshotFiles)
                {
                    try
                    {
                        File.Delete(file);
                        _logger.LogDebug("Deleted old snapshot: {File}", Path.GetFileName(file));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete old snapshot: {File}", file);
                    }
                }

                _logger.LogInformation("Cleaned up {Count} old snapshot(s)", snapshotFiles.Count);
            }
            finally
            {
                _fileLock.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error cleaning up old snapshots");
        }
    }

    /// <summary>
    /// Computes MD5 hash of a string.
    /// </summary>
    /// <param name="input">The input string.</param>
    /// <returns>The MD5 hash as a hex string.</returns>
#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms - MD5 used for checksums only, not security
    private static string ComputeMd5(string input)
    {
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = MD5.HashData(inputBytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
#pragma warning restore CA5351
}
