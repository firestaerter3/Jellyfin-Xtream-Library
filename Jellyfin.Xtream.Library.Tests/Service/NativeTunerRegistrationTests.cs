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
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// GitHub #152: Jellyfin shows Live TV only once a tuner is listed under Tuner Devices, so the
/// Enable Native Tuner checkbox has to list one itself.
/// </summary>
public class NativeTunerRegistrationTests
{
    private readonly Mock<ITunerHostManager> _tunerHosts = new();
    private readonly Mock<ITaskManager> _tasks = new();
    private readonly Mock<IServerConfigurationManager> _serverConfig = new();
    private readonly LiveTvOptions _liveTv = new() { TunerHosts = [] };

    public NativeTunerRegistrationTests()
    {
        _serverConfig.Setup(c => c.GetConfiguration("livetv")).Returns(() => _liveTv);
        _tunerHosts.Setup(t => t.SaveTunerHost(It.IsAny<TunerHostInfo>(), It.IsAny<bool>()))
            .ReturnsAsync((TunerHostInfo info, bool _) => info);
    }

    [Fact]
    public async Task NativeTunerOn_NoTunerListed_AddsOne()
    {
        await SyncAsync(liveTv: true, nativeTuner: true);

        _tunerHosts.Verify(
            t => t.SaveTunerHost(
                It.Is<TunerHostInfo>(i => i.Type == XtreamTunerHost.TunerType && i.FriendlyName == NativeTunerRegistration.FriendlyName),
                It.IsAny<bool>()),
            Times.Once);
    }

    [Fact]
    public async Task NativeTunerOn_TunerAddedByHand_AddsNothing()
    {
        // Everyone who got the native tuner working before this fix has one of these.
        _liveTv.TunerHosts = [new TunerHostInfo { Id = "manual", Type = XtreamTunerHost.TunerType, FriendlyName = "Xtream Library" }];

        await SyncAsync(liveTv: true, nativeTuner: true);

        _tunerHosts.Verify(t => t.SaveTunerHost(It.IsAny<TunerHostInfo>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task NativeTunerOn_OnlyAnM3uTunerListed_StillAddsOurs()
    {
        _liveTv.TunerHosts = [new TunerHostInfo { Id = "m3u", Type = "m3u", FriendlyName = "Playlist" }];

        await SyncAsync(liveTv: true, nativeTuner: true);

        _tunerHosts.Verify(t => t.SaveTunerHost(It.Is<TunerHostInfo>(i => i.Type == XtreamTunerHost.TunerType), It.IsAny<bool>()), Times.Once);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task NativeTunerNotInUse_RemovesOnlyTheEntryThePluginAdded(bool liveTv, bool nativeTuner)
    {
        _liveTv.TunerHosts =
        [
            new TunerHostInfo { Id = "ours", Type = XtreamTunerHost.TunerType, FriendlyName = NativeTunerRegistration.FriendlyName },
            new TunerHostInfo { Id = "manual", Type = XtreamTunerHost.TunerType, FriendlyName = "Xtream Library" },
            new TunerHostInfo { Id = "m3u", Type = "m3u", FriendlyName = NativeTunerRegistration.FriendlyName },
        ];

        await SyncAsync(liveTv, nativeTuner);

        _tunerHosts.Verify(t => t.DeleteTunerHost("ours"), Times.Once);
        _tunerHosts.Verify(t => t.DeleteTunerHost("manual"), Times.Never);
        _tunerHosts.Verify(t => t.DeleteTunerHost("m3u"), Times.Never);
        _tunerHosts.Verify(t => t.SaveTunerHost(It.IsAny<TunerHostInfo>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task NativeTunerOff_NothingListed_DoesNothing()
    {
        await SyncAsync(liveTv: true, nativeTuner: false);

        _tunerHosts.Verify(t => t.DeleteTunerHost(It.IsAny<string>()), Times.Never);
        _tunerHosts.Verify(t => t.SaveTunerHost(It.IsAny<TunerHostInfo>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AddingAtStartup_DoesNotAskJellyfinToRefreshTheGuide(bool atStartup, bool expectedRefresh)
    {
        // During startup Jellyfin has not loaded its tasks, and SaveTunerHost throws while
        // queueing the refresh, after the entry is already saved. Measured on Jellyfin 12.
        var added = await SyncAsync(liveTv: true, nativeTuner: true, atStartup);

        added.Should().BeTrue();
        _tunerHosts.Verify(t => t.SaveTunerHost(It.IsAny<TunerHostInfo>(), expectedRefresh), Times.Once);
    }

    [Fact]
    public async Task GuideRefresh_IsQueuedOnceJellyfinHasLoadedTheTask()
    {
        var worker = new Mock<IScheduledTaskWorker>();
        var task = new Mock<IScheduledTask>();
        task.Setup(t => t.Key).Returns("RefreshGuide");
        worker.Setup(w => w.ScheduledTask).Returns(task.Object);
        var calls = 0;
        _tasks.Setup(t => t.ScheduledTasks).Returns(() => ++calls < 3 ? [] : [worker.Object]);

        using var registration = NewRegistration();
        var queued = await registration.QueueGuideRefreshWhenReadyAsync(TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(5));

        queued.Should().BeTrue();
        _tasks.Verify(t => t.QueueScheduledTask(task.Object, It.IsAny<TaskOptions>()), Times.Once);
    }

    [Fact]
    public async Task GuideRefresh_GivesUpWhenTheTaskNeverAppears()
    {
        _tasks.Setup(t => t.ScheduledTasks).Returns([]);

        using var registration = NewRegistration();
        var queued = await registration.QueueGuideRefreshWhenReadyAsync(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(20));

        queued.Should().BeFalse();
        _tasks.Verify(t => t.QueueScheduledTask(It.IsAny<IScheduledTask>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SwitchingTheNativeTunerWithAHandAddedEntry_RefreshesTheGuide(bool before, bool after)
    {
        // A settings save does not refresh the guide, and with an entry already there nothing is
        // added, so nothing else would.
        _liveTv.TunerHosts = [new TunerHostInfo { Id = "manual", Type = XtreamTunerHost.TunerType, FriendlyName = "Xtream Library" }];
        var refresh = SetUpRefreshGuideTask();

        using var registration = NewRegistration();
        await registration.SyncAsync(new PluginConfiguration { EnableLiveTv = true, EnableNativeTuner = before }, atStartup: true);
        await registration.SyncAsync(new PluginConfiguration { EnableLiveTv = true, EnableNativeTuner = after }, atStartup: false);

        _tasks.Verify(t => t.QueueScheduledTask(refresh, It.IsAny<TaskOptions>()), Times.Once);
    }

    [Fact]
    public async Task SavingWithoutAChange_DoesNotRefreshTheGuide()
    {
        _liveTv.TunerHosts = [new TunerHostInfo { Id = "manual", Type = XtreamTunerHost.TunerType, FriendlyName = "Xtream Library" }];
        SetUpRefreshGuideTask();

        using var registration = NewRegistration();
        await registration.SyncAsync(new PluginConfiguration { EnableLiveTv = true, EnableNativeTuner = true }, atStartup: true);
        await registration.SyncAsync(new PluginConfiguration { EnableLiveTv = true, EnableNativeTuner = true }, atStartup: false);

        _tasks.Verify(t => t.QueueScheduledTask(It.IsAny<IScheduledTask>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    private IScheduledTask SetUpRefreshGuideTask()
    {
        var worker = new Mock<IScheduledTaskWorker>();
        var task = new Mock<IScheduledTask>();
        task.Setup(t => t.Key).Returns("RefreshGuide");
        worker.Setup(w => w.ScheduledTask).Returns(task.Object);
        _tasks.Setup(t => t.ScheduledTasks).Returns([worker.Object]);
        return task.Object;
    }

    private NativeTunerRegistration NewRegistration()
        => new(_tunerHosts.Object, _tasks.Object, _serverConfig.Object, NullLogger<NativeTunerRegistration>.Instance);

    private async Task<bool> SyncAsync(bool liveTv, bool nativeTuner, bool atStartup = false)
    {
        using var registration = NewRegistration();
        return await registration.SyncAsync(new PluginConfiguration { EnableLiveTv = liveTv, EnableNativeTuner = nativeTuner }, atStartup);
    }
}
