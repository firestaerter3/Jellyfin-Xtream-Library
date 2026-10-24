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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Tests.Helpers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// Only one sync runs at a time, whoever starts it. The Sync and Retry buttons and the
/// scheduled task all end up in SyncAsync, and the buttons' own "already running" check leaves a
/// window before the sync marks itself as running.
/// </summary>
[Collection("PluginSingletonTests")]
public class SingleSyncTests : IDisposable
{
    private readonly string _libraryPath = Path.Combine(Path.GetTempPath(), "xtream-singlesync-" + Guid.NewGuid().ToString("N"));

    public SingleSyncTests() => Directory.CreateDirectory(_libraryPath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_libraryPath, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SecondSyncWhileOneRuns_ReturnsAtOnceWithoutRunning()
    {
        var release = new TaskCompletionSource<List<Category>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var categoryCalls = 0;
        var client = new Mock<IXtreamClient>();
        client.Setup(c => c.GetVodCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref categoryCalls);
                entered.TrySetResult();
                return release.Task;
            });

        var service = CreateService(client.Object);

        var first = service.SyncAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.IsSyncActive.Should().BeTrue();

        var second = await service.SyncAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        second.Error.Should().Contain("already in progress");
        categoryCalls.Should().Be(1, "the second sync must not touch the provider");

        release.SetResult(new List<Category>());
        await first.WaitAsync(TimeSpan.FromSeconds(30));
        service.IsSyncActive.Should().BeFalse("the slot is free again once the sync ends");
    }

    private StrmSyncService CreateService(IXtreamClient client)
    {
        var appPaths = new Mock<IServerApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(_libraryPath);
        appPaths.Setup(p => p.DataPath).Returns(_libraryPath);
        appPaths.Setup(p => p.ProgramDataPath).Returns(_libraryPath);
        appPaths.Setup(p => p.CachePath).Returns(_libraryPath);
        appPaths.Setup(p => p.TempDirectory).Returns(_libraryPath);
        appPaths.Setup(p => p.PluginsPath).Returns(_libraryPath);

        var plugin = new Plugin(appPaths.Object, new RealXmlSerializer());
        plugin.Configuration.Providers = new List<ProviderConfig>
        {
            new()
            {
                Name = "test",
                BaseUrl = "http://provider.test",
                Username = "u",
                Password = "p",
                LibraryPath = _libraryPath,
                SyncMovies = true,
                SyncSeries = false,
                SyncParallelism = 1,
            },
        };
        plugin.Configuration.EnableLiveTv = false;
        plugin.Configuration.EnableMetadataLookup = false;

        var host = new Mock<IServerApplicationHost>();
        host.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");

        return new StrmSyncService(
            client,
            new Mock<IDispatcharrClient>().Object,
            new Mock<ILibraryManager>().Object,
            new Mock<IMetadataLookupService>().Object,
            new SnapshotService(appPaths.Object, NullLogger<SnapshotService>.Instance),
            new LiveTvService(client, new Mock<IDispatcharrClient>().Object, appPaths.Object, host.Object, NullLogger<LiveTvService>.Instance),
            appPaths.Object,
            NullLogger<StrmSyncService>.Instance);
    }
}
