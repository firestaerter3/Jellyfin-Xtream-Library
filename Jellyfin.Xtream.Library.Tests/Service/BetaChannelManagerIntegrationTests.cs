// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class BetaChannelManagerIntegrationTests
{
    /// <summary>
    /// In-memory fake of IServerConfigurationManager. Tracks how many times
    /// SaveConfiguration is called so we can assert the optimisation works.
    /// Only the surface BetaChannelManager actually touches is implemented;
    /// everything else throws NotSupportedException to keep accidental
    /// dependencies obvious.
    /// </summary>
    private sealed class FakeServerConfigurationManager : IServerConfigurationManager
    {
        public ServerConfiguration Configuration { get; } = new() { PluginRepositories = Array.Empty<RepositoryInfo>() };

        public int SaveCount { get; private set; }

        public bool ThrowOnNextSave { get; set; }

        public IServerApplicationPaths ApplicationPaths => throw new NotSupportedException();

        IApplicationPaths IConfigurationManager.CommonApplicationPaths => throw new NotSupportedException();

        public BaseApplicationConfiguration CommonConfiguration => throw new NotSupportedException();

        public event EventHandler<EventArgs>? ConfigurationUpdated;

        public event EventHandler<ConfigurationUpdateEventArgs>? NamedConfigurationUpdated;

        public event EventHandler<ConfigurationUpdateEventArgs>? NamedConfigurationUpdating;

        public void AddParts(System.Collections.Generic.IEnumerable<IConfigurationFactory> factories) => throw new NotSupportedException();

        public ConfigurationStore[] GetConfigurationStores() => throw new NotSupportedException();

        public object GetConfiguration(string key) => throw new NotSupportedException();

        public Type GetConfigurationType(string key) => throw new NotSupportedException();

        public void RegisterConfiguration<TFactory>()
            where TFactory : IConfigurationFactory => throw new NotSupportedException();

        public void ReplaceConfiguration(BaseApplicationConfiguration newConfiguration) => throw new NotSupportedException();

        public void SaveConfiguration()
        {
            if (ThrowOnNextSave)
            {
                ThrowOnNextSave = false;
                throw new IOException("Simulated disk error.");
            }

            SaveCount++;
        }

        public void SaveConfiguration(string key, object configuration) => throw new NotSupportedException();

        // No-op suppressions so the unused-event-warning doesn't fire under strict ruleset.
        public void RaiseConfigurationUpdated() => ConfigurationUpdated?.Invoke(this, EventArgs.Empty);

        public void RaiseNamedConfigurationUpdating(ConfigurationUpdateEventArgs e) => NamedConfigurationUpdating?.Invoke(this, e);

        public void RaiseNamedConfigurationUpdated(ConfigurationUpdateEventArgs e) => NamedConfigurationUpdated?.Invoke(this, e);
    }

    private static BetaChannelManager Build(FakeServerConfigurationManager fake) =>
        new(fake, NullLogger<BetaChannelManager>.Instance);

    private static PluginConfiguration ConfigWith(bool useBeta) =>
        new() { UseBetaChannel = useBeta };

    [Fact]
    public async Task StartAsync_UseBetaTrue_AppendsRepoAndSaves()
    {
        var fake = new FakeServerConfigurationManager();
        // Plugin singleton may not be present in test context — manager must
        // tolerate that and fall back gracefully. We seed config explicitly:
        var manager = Build(fake);
        manager.SetConfigurationForTesting(ConfigWith(useBeta: true));

        await manager.StartAsync(CancellationToken.None);

        fake.Configuration.PluginRepositories.Should().ContainSingle(r => r.Url == BetaChannelManager.BetaRepoUrl);
        fake.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_UseBetaFalse_NoRepoChangeNoSave()
    {
        var fake = new FakeServerConfigurationManager();
        var manager = Build(fake);
        manager.SetConfigurationForTesting(ConfigWith(useBeta: false));

        await manager.StartAsync(CancellationToken.None);

        fake.Configuration.PluginRepositories.Should().BeEmpty();
        fake.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task ConfigurationChanged_FlipsUseBeta_SyncsAndSaves()
    {
        var fake = new FakeServerConfigurationManager();
        var manager = Build(fake);
        manager.SetConfigurationForTesting(ConfigWith(useBeta: false));
        await manager.StartAsync(CancellationToken.None);

        manager.SetConfigurationForTesting(ConfigWith(useBeta: true));
        manager.OnConfigurationChangedForTesting();

        fake.Configuration.PluginRepositories.Should().ContainSingle(r => r.Url == BetaChannelManager.BetaRepoUrl);
        fake.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_SaveThrowsIOException_RollsBackInMemoryStateAndDoesNotThrow()
    {
        var fake = new FakeServerConfigurationManager { ThrowOnNextSave = true };
        var manager = Build(fake);
        manager.SetConfigurationForTesting(ConfigWith(useBeta: true));

        Func<Task> act = () => manager.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        // In-memory PluginRepositories must be rolled back to the pre-Sync state so a
        // subsequent Sync() detects the divergence and retries the save instead of
        // treating the failed-but-still-in-memory state as already persisted.
        fake.Configuration.PluginRepositories.Should().BeEmpty();
        fake.SaveCount.Should().Be(0);
    }

    // NOTE: the actual production-side unsubscription path
    // (`plugin.ConfigurationChanged -= _subscribedHandler`) requires a live
    // `Plugin.Instance` and cannot be verified in this unit-test context.
    // Task 5 (manual verification on a real Jellyfin server) covers the
    // end-to-end event lifecycle. Here we only assert that StopAsync runs
    // cleanly after StartAsync and does not itself trigger any extra saves.
    [Fact]
    public async Task StopAsync_AfterStart_CompletesCleanlyAndDoesNotSave()
    {
        var fake = new FakeServerConfigurationManager();
        var manager = Build(fake);
        manager.SetConfigurationForTesting(ConfigWith(useBeta: true));
        await manager.StartAsync(CancellationToken.None);
        var savesBeforeStop = fake.SaveCount;

        Func<Task> act = () => manager.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        fake.SaveCount.Should().Be(savesBeforeStop);
    }
}
