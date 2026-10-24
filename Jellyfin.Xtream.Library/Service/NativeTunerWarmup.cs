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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Loads the native tuner's channel list shortly after the server starts.
/// <para>
/// The tuner filled its list on first use, so the first channel anyone opened after a restart
/// waited for the whole provider channel list (and Dispatcharr's, in Dispatcharr mode) before
/// Jellyfin even started on the stream: about 9 seconds measured on a 700-channel setup
/// (homelab-ops #550). Loading it in the background moves that wait off the first click.
/// </para>
/// </summary>
public sealed class NativeTunerWarmup : IHostedService, IDisposable
{
    private readonly XtreamTunerHost? _tunerHost;
    private readonly ILogger<NativeTunerWarmup> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeSpan _delay;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeTunerWarmup"/> class.
    /// </summary>
    /// <param name="tunerHosts">The registered tuner hosts; the plugin's own is picked out.</param>
    /// <param name="logger">The logger.</param>
    public NativeTunerWarmup(IEnumerable<ITunerHost> tunerHosts, ILogger<NativeTunerWarmup> logger)
        : this(tunerHosts, logger, TimeSpan.FromSeconds(10))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeTunerWarmup"/> class.
    /// </summary>
    /// <param name="tunerHosts">The registered tuner hosts; the plugin's own is picked out.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="delay">How long after start to load, so it does not compete with startup.</param>
    internal NativeTunerWarmup(IEnumerable<ITunerHost> tunerHosts, ILogger<NativeTunerWarmup> logger, TimeSpan delay)
    {
        _tunerHost = (tunerHosts ?? throw new ArgumentNullException(nameof(tunerHosts))).OfType<XtreamTunerHost>().FirstOrDefault();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _delay = delay;
    }

    /// <summary>Gets the background load, for tests.</summary>
    internal Task? Running { get; private set; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_tunerHost is not null)
        {
            Running = Task.Run(() => WarmUpAsync(_tunerHost, _stopping.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => _stopping.CancelAsync();

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();

    private async Task WarmUpAsync(XtreamTunerHost tunerHost, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);

            var config = Plugin.Instance?.Configuration;
            if (config is null || !config.EnableLiveTv || !config.EnableNativeTuner)
            {
                return;
            }

            // true: a guide refresh queued at startup may already have loaded the list.
            var channels = await tunerHost.GetChannels(true, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Loaded {Count} channels for the native tuner ahead of first use", channels.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Server stopping.
        }
        catch (Exception ex)
        {
            // Not fatal: the tuner still loads the list on first use, as before.
            _logger.LogWarning(ex, "Could not load the native tuner's channels at startup; they load on first use instead");
        }
    }
}
