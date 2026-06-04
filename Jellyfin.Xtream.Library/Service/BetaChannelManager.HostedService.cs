// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Hosted-service half of <see cref="BetaChannelManager"/>. Wires the pure
/// decision logic from <c>BetaChannelManager.cs</c> into the live
/// <see cref="IServerConfigurationManager"/>, subscribing to the plugin's
/// configuration-changed event in <see cref="StartAsync"/> and unsubscribing
/// in <see cref="StopAsync"/>.
/// </summary>
public sealed partial class BetaChannelManager : IHostedService
{
    private readonly IServerConfigurationManager _serverConfig;
    private readonly ILogger<BetaChannelManager> _logger;

    /// <summary>Test seam — overrides Plugin.Instance.Configuration when set.</summary>
    private PluginConfiguration? _configurationOverride;

    private EventHandler<BasePluginConfiguration>? _subscribedHandler;

    /// <summary>
    /// Initializes a new instance of the <see cref="BetaChannelManager"/> class.
    /// </summary>
    /// <param name="serverConfig">The Jellyfin server configuration manager.</param>
    /// <param name="logger">The logger.</param>
    public BetaChannelManager(
        IServerConfigurationManager serverConfig,
        ILogger<BetaChannelManager> logger)
    {
        _serverConfig = serverConfig ?? throw new ArgumentNullException(nameof(serverConfig));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Sync();

        var plugin = TryGetPluginInstance();
        if (plugin is not null)
        {
            _subscribedHandler = (_, _) => OnConfigurationChanged();
            plugin.ConfigurationChanged += _subscribedHandler;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscribedHandler is not null)
        {
            var plugin = TryGetPluginInstance();
            if (plugin is not null)
            {
                plugin.ConfigurationChanged -= _subscribedHandler;
            }

            _subscribedHandler = null;
        }

        return Task.CompletedTask;
    }

    private void OnConfigurationChanged()
    {
        try
        {
            Sync();
        }
        catch (Exception ex)
        {
            // A sync failure must never abort the user's config save.
            _logger.LogWarning(ex, "BetaChannelManager sync failed during ConfigurationChanged handler.");
        }
    }

    private void Sync()
    {
        var config = ResolveConfiguration();
        if (config is null)
        {
            _logger.LogInformation("BetaChannelManager: plugin instance not yet available; skipping sync. Will reconcile on next config save.");
            return;
        }

        var current = _serverConfig.Configuration.PluginRepositories ?? Array.Empty<RepositoryInfo>();
        var next = ComputeNextRepositories(config.UseBetaChannel, current);

        if (StructurallyEqual(next, current))
        {
            return;
        }

        try
        {
            _serverConfig.Configuration.PluginRepositories = next;
            _serverConfig.SaveConfiguration();
            _logger.LogInformation(
                "BetaChannelManager: {Action} beta repository entry (UseBetaChannel={UseBetaChannel}).",
                config.UseBetaChannel ? "added" : "removed",
                config.UseBetaChannel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Roll back the in-memory mutation so a subsequent Sync() will detect
            // the divergence and retry instead of treating the in-memory state as
            // already-persisted.
            _serverConfig.Configuration.PluginRepositories = current;
            _logger.LogWarning(ex, "BetaChannelManager: could not persist updated plugin repository list. Will retry on next config save.");
        }
    }

    private PluginConfiguration? ResolveConfiguration()
    {
        if (_configurationOverride is not null)
        {
            return _configurationOverride;
        }

        return TryGetPluginInstance()?.Configuration;
    }

    private static Plugin? TryGetPluginInstance()
    {
        try
        {
            return Plugin.Instance;
        }
        catch (InvalidOperationException)
        {
            // Plugin singleton not assigned yet (startup race). Caller treats null as "skip".
            return null;
        }
    }

    // ---- Test seams ----

    /// <summary>
    /// Sets a configuration override for testing, bypassing <c>Plugin.Instance</c>.
    /// </summary>
    /// <param name="config">The configuration to use during tests.</param>
    internal void SetConfigurationForTesting(PluginConfiguration config) => _configurationOverride = config;

    /// <summary>
    /// Simulates the plugin's ConfigurationChanged event firing, for use in tests.
    /// Invokes the same handler the production code subscribes from Plugin.Instance.ConfigurationChanged.
    /// </summary>
    internal void OnConfigurationChangedForTesting() => OnConfigurationChanged();
}
