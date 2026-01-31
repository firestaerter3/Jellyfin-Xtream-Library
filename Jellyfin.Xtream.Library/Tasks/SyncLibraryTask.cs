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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Tasks;

/// <summary>
/// Scheduled task for syncing Xtream content to STRM files.
/// </summary>
public class SyncLibraryTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly StrmSyncService _syncService;
    private readonly ILogger<SyncLibraryTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncLibraryTask"/> class.
    /// </summary>
    /// <param name="syncService">The STRM sync service.</param>
    /// <param name="logger">The logger instance.</param>
    public SyncLibraryTask(StrmSyncService syncService, ILogger<SyncLibraryTask> logger)
    {
        _syncService = syncService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync Xtream Library";

    /// <inheritdoc />
    public string Key => "XtreamLibrarySync";

    /// <inheritdoc />
    public string Description => "Syncs VOD and Series content from Xtream provider to STRM files for native Jellyfin library integration.";

    /// <inheritdoc />
    public string Category => "Xtream Library";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Xtream Library sync task");
        progress.Report(0);

        try
        {
            var result = await _syncService.SyncAsync(cancellationToken).ConfigureAwait(false);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Xtream Library sync completed successfully. Movies: {MoviesCreated} created, {MoviesSkipped} skipped. Episodes: {EpisodesCreated} created, {EpisodesSkipped} skipped. Orphans deleted: {Deleted}",
                    result.MoviesCreated,
                    result.MoviesSkipped,
                    result.EpisodesCreated,
                    result.EpisodesSkipped,
                    result.FilesDeleted);
            }
            else
            {
                _logger.LogWarning("Xtream Library sync completed with errors: {Error}", result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Xtream Library sync was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Xtream Library sync failed with exception");
            throw;
        }

        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var config = Plugin.Instance.Configuration;
        int intervalMinutes = config.SyncIntervalMinutes > 0 ? config.SyncIntervalMinutes : 60;

        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(intervalMinutes).Ticks,
            },
        };
    }
}
