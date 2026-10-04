// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #108: the recorded catch-up guide. Merge rules, the files behind them, what an EPG build
// collects, and that recording leaves the XMLTV output itself exactly as it was.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Tests.Helpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class CatchupHistoryMergeTests
{
    private const long Horizon = 1_000_000;

    private static CatchupHistoryEntry Entry(long start, long stop, string title = "x") => new(start, stop, title, string.Empty);

    [Fact]
    public void StoredProgrammeEndingAtTheFirstNewStartIsKept()
    {
        var stored = new[] { Entry(Horizon + 100, Horizon + 200, "old") };
        var incoming = new[] { Entry(Horizon + 200, Horizon + 300, "new") };

        var merged = CatchupHistoryStore.Merge(stored, incoming, Horizon);

        merged.Select(e => e.Title).Should().Equal("old", "new");
    }

    [Fact]
    public void StoredProgrammeRunningIntoTheNewDataIsReplaced()
    {
        // The schedule moved: the stored 20:00-21:00 slot now reads 20:30-21:30 in the pull.
        var stored = new[] { Entry(Horizon + 100, Horizon + 200, "kept"), Entry(Horizon + 200, Horizon + 400, "moved") };
        var incoming = new[] { Entry(Horizon + 300, Horizon + 500, "fresh") };

        var merged = CatchupHistoryStore.Merge(stored, incoming, Horizon);

        merged.Select(e => e.Title).Should().Equal("kept", "fresh");
    }

    [Fact]
    public void StoredProgrammesAfterAShorterPullAreReplaced()
    {
        var stored = new[] { Entry(Horizon + 100, Horizon + 200), Entry(Horizon + 900, Horizon + 1000, "stale future") };
        var incoming = new[] { Entry(Horizon + 200, Horizon + 300, "new") };

        var merged = CatchupHistoryStore.Merge(stored, incoming, Horizon);

        merged.Select(e => e.Title).Should().NotContain("stale future");
    }

    [Fact]
    public void AnEmptyPullKeepsTheStoredGuide()
    {
        var stored = new[] { Entry(Horizon + 100, Horizon + 200, "a"), Entry(Horizon + 200, Horizon + 300, "b") };

        var merged = CatchupHistoryStore.Merge(stored, Array.Empty<CatchupHistoryEntry>(), Horizon);

        merged.Select(e => e.Title).Should().Equal("a", "b");
    }

    [Fact]
    public void ProgrammesThatEndedBeforeTheHorizonAreDropped()
    {
        var stored = new[] { Entry(Horizon - 200, Horizon - 100, "gone"), Entry(Horizon + 100, Horizon + 200, "kept") };
        var incoming = new[] { Entry(Horizon - 400, Horizon - 300, "old in feed"), Entry(Horizon + 200, Horizon + 300, "new") };

        var merged = CatchupHistoryStore.Merge(stored, incoming, Horizon);

        merged.Select(e => e.Title).Should().Equal("kept", "new");
    }

    [Fact]
    public void AnEarlyTimestampInThePullDoesNotDeleteRetainedHistory()
    {
        // One programme in the pull claims to span the horizon. Its start must not become the
        // point from which everything stored gets replaced.
        var stored = new[] { Entry(Horizon + 100, Horizon + 200, "retained") };
        var incoming = new[] { Entry(Horizon - 5000, Horizon + 50, "odd"), Entry(Horizon + 300, Horizon + 400, "new") };

        var merged = CatchupHistoryStore.Merge(stored, incoming, Horizon);

        merged.Select(e => e.Title).Should().Contain("retained");
    }

    [Fact]
    public void AnInvertedIntervalCannotMoveTheFirstStart()
    {
        var stored = new[] { Entry(Horizon + 100, Horizon + 200, "retained") };
        var incoming = new[] { Entry(Horizon + 150, Horizon + 10, "inverted"), Entry(Horizon + 300, Horizon + 400, "new") };

        var merged = CatchupHistoryStore.Merge(stored, incoming, Horizon);

        merged.Select(e => e.Title).Should().Equal("retained", "new");
    }

    [Fact]
    public void AProgrammeSpanningTheHorizonIsNotListedTwice()
    {
        var spanning = Entry(Horizon - 100, Horizon + 100, "spanning");
        var stored = new[] { spanning };
        var incoming = new[] { spanning, Entry(Horizon + 100, Horizon + 200, "next") };

        var merged = CatchupHistoryStore.Merge(stored, incoming, Horizon);

        merged.Select(e => e.Title).Should().Equal("spanning", "next");
    }

    [Fact]
    public void ThePullWinsOverAStoredProgrammeWithTheSameStart()
    {
        var stored = new[] { Entry(Horizon + 100, Horizon + 200, "old title") };
        var incoming = new[] { Entry(Horizon + 100, Horizon + 200, "new title") };

        CatchupHistoryStore.Merge(stored, incoming, Horizon).Single().Title.Should().Be("new title");
    }
}

public sealed class CatchupHistoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "catchup-history-" + Guid.NewGuid().ToString("N"));
    private readonly CatchupHistoryStore _store;

    public CatchupHistoryStoreTests()
    {
        _store = new CatchupHistoryStore(_root, NullLogger.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private const string Key = "0123456789abcdef";

    private static CatchupHistoryPull Pull(int streamId, params CatchupHistoryEntry[] programmes) => new(Key, streamId, 3, programmes);

    private static CatchupHistoryEntry HoursAgo(double startHours, double stopHours, string title)
    {
        var now = DateTimeOffset.UtcNow;
        return new CatchupHistoryEntry(
            now.AddHours(-startHours).ToUnixTimeSeconds(),
            now.AddHours(-stopHours).ToUnixTimeSeconds(),
            title,
            "desc " + title);
    }

    [Fact]
    public async Task AProgrammeThatLeftTheFeedIsStillThereNextTime()
    {
        var yesterday = HoursAgo(26, 25, "yesterday");
        var today = HoursAgo(1, -1, "today");

        await _store.RecordAsync(new[] { Pull(7, yesterday, today) }, DateTimeOffset.UtcNow, false, CancellationToken.None);
        var after = await _store.RecordAndLoadAsync(Pull(7, today), DateTimeOffset.UtcNow, CancellationToken.None);

        after.Select(e => e.Title).Should().Equal("yesterday", "today");
        after[0].Description.Should().Be("desc yesterday");
    }

    [Fact]
    public async Task ChannelsAreKeptApart()
    {
        await _store.RecordAsync(new[] { Pull(1, HoursAgo(3, 2, "one")), Pull(2, HoursAgo(3, 2, "two")) }, DateTimeOffset.UtcNow, false, CancellationToken.None);

        (await _store.LoadAsync(Key, 1, CancellationToken.None)).Single().Title.Should().Be("one");
        (await _store.LoadAsync(Key, 2, CancellationToken.None)).Single().Title.Should().Be("two");
        (await _store.LoadAsync("fedcba9876543210", 1, CancellationToken.None)).Should().BeEmpty("another provider has its own files");
    }

    [Fact]
    public async Task AnUnchangedGuideIsNotRewritten()
    {
        var pull = Pull(7, HoursAgo(3, 2, "a"));
        await _store.RecordAsync(new[] { pull }, DateTimeOffset.UtcNow, false, CancellationToken.None);
        var path = Path.Combine(_root, CatchupHistoryStore.FileName(Key, 7));
        var old = DateTime.UtcNow.AddHours(-5);
        File.SetLastWriteTimeUtc(path, old);

        await _store.RecordAsync(new[] { pull }, DateTimeOffset.UtcNow, false, CancellationToken.None);

        File.GetLastWriteTimeUtc(path).Should().Be(old);
    }

    [Fact]
    public async Task ADamagedFileIsIgnoredAndReplaced()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, CatchupHistoryStore.FileName(Key, 7));
        await File.WriteAllTextAsync(path, "{ not json");

        (await _store.LoadAsync(Key, 7, CancellationToken.None)).Should().BeEmpty();

        var after = await _store.RecordAndLoadAsync(Pull(7, HoursAgo(3, 2, "a")), DateTimeOffset.UtcNow, CancellationToken.None);
        after.Single().Title.Should().Be("a");
    }

    [Fact]
    public async Task AFileOfAnotherVersionIsIgnored()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, CatchupHistoryStore.FileName(Key, 7));
        await File.WriteAllTextAsync(path, "{\"Version\":99,\"Programmes\":[{\"Start\":1,\"Stop\":2,\"Title\":\"t\",\"Description\":\"\"}]}");

        (await _store.LoadAsync(Key, 7, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task AGuideThatAgedOutRemovesItsFile()
    {
        var old = new CatchupHistoryEntry(
            DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.AddDays(-10).AddHours(1).ToUnixTimeSeconds(),
            "old",
            string.Empty);
        await _store.RecordAsync(new[] { Pull(7, old) }, DateTimeOffset.UtcNow.AddDays(-10), false, CancellationToken.None);
        var path = Path.Combine(_root, CatchupHistoryStore.FileName(Key, 7));
        File.Exists(path).Should().BeTrue();

        await _store.RecordAsync(new[] { Pull(7) }, DateTimeOffset.UtcNow, false, CancellationToken.None);

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task StaleFilesAreSweptOnlyWhenAsked()
    {
        await _store.RecordAsync(new[] { Pull(9, HoursAgo(3, 2, "a")) }, DateTimeOffset.UtcNow, false, CancellationToken.None);
        var path = Path.Combine(_root, CatchupHistoryStore.FileName(Key, 9));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - CatchupHistoryStore.StaleFileAge - TimeSpan.FromDays(1));

        await _store.RecordAsync(Array.Empty<CatchupHistoryPull>(), DateTimeOffset.UtcNow, false, CancellationToken.None);
        File.Exists(path).Should().BeTrue();

        await _store.RecordAsync(Array.Empty<CatchupHistoryPull>(), DateTimeOffset.UtcNow, true, CancellationToken.None);
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public async Task AChannelThatCannotBeWrittenDoesNotStopTheRest()
    {
        // A directory where channel 7's file should go makes its move fail, the way a file held
        // open by a reader does on Windows.
        Directory.CreateDirectory(Path.Combine(_root, CatchupHistoryStore.FileName(Key, 7)));

        await _store.RecordAsync(
            new[] { Pull(7, HoursAgo(3, 2, "blocked")), Pull(8, HoursAgo(3, 2, "after")) },
            DateTimeOffset.UtcNow,
            false,
            CancellationToken.None);

        (await _store.LoadAsync(Key, 8, CancellationToken.None)).Single().Title.Should().Be("after");
        Directory.EnumerateFiles(_root, "*.tmp").Should().BeEmpty("a failed write takes its temporary file with it");
    }

    [Fact]
    public async Task AChannelThatFailsInAnUnexpectedWayDoesNotStopTheRest()
    {
        // A NUL in the key is rejected as an argument error when the file is written, which is
        // neither of the I/O failures RecordOneAsync handles itself. The batch must still reach
        // the channel after it.
        var broken = new CatchupHistoryPull("bad\0key", 7, 3, new[] { HoursAgo(3, 2, "broken") });

        await _store.RecordAsync(
            new[] { broken, Pull(8, HoursAgo(3, 2, "after")) },
            DateTimeOffset.UtcNow,
            false,
            CancellationToken.None);

        (await _store.LoadAsync(Key, 8, CancellationToken.None)).Single().Title.Should().Be("after");
    }

    [Fact]
    public async Task ABrowseStillSeesTheFreshPullWhenItCannotBeWritten()
    {
        Directory.CreateDirectory(Path.Combine(_root, CatchupHistoryStore.FileName(Key, 7)));

        var shown = await _store.RecordAndLoadAsync(Pull(7, HoursAgo(3, 2, "fresh")), DateTimeOffset.UtcNow, CancellationToken.None);

        shown.Single().Title.Should().Be("fresh");
    }

    [Fact]
    public async Task ATemporaryFileLeftBehindIsSwept()
    {
        await _store.RecordAsync(new[] { Pull(9, HoursAgo(3, 2, "a")) }, DateTimeOffset.UtcNow, false, CancellationToken.None);
        var tmp = Path.Combine(_root, CatchupHistoryStore.FileName(Key, 10) + ".tmp");
        await File.WriteAllTextAsync(tmp, "{");
        File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow.AddHours(-2));

        await _store.RecordAsync(Array.Empty<CatchupHistoryPull>(), DateTimeOffset.UtcNow, true, CancellationToken.None);

        File.Exists(tmp).Should().BeFalse();
        File.Exists(Path.Combine(_root, CatchupHistoryStore.FileName(Key, 9))).Should().BeTrue("a recent history file is not stale");
    }

    [Theory]
    [InlineData("Test")]
    [InlineData("News")]
    [InlineData("Journaal & weer: 20:00")]
    [InlineData("")]
    public void TitlesSurviveTheTripToTheBrowserUnchanged(string title)
    {
        // "Test" and "News" are valid base64. Handed over as plain text they would be decoded
        // into garbage by the browser, which reads titles the way the provider sends them.
        var programme = CatchupHistoryStore.ToEpg(new CatchupHistoryEntry(1, 2, title, title));

        LiveTvService.DecodeBase64(programme.Title).Should().Be(title);
        CatchupHistoryStore.FromEpg(programme).Should().Be(new CatchupHistoryEntry(1, 2, title, title));
    }
}

public class CatchupHistoryCollectorTests
{
    private static PluginConfiguration Config(bool showCatchup = true, string baseUrl = "http://test.example.com")
    {
        var config = new PluginConfiguration
        {
            EnableLiveTv = true,
            EnableCatchup = true,
            ShowCatchupInJellyfin = showCatchup,
            CatchupDays = 7,
        };
        config.Providers.Add(TestDataBuilder.CreateProviderConfig(baseUrl: baseUrl));
        return config;
    }

    private static LiveStreamInfo Channel(int streamId, string epgId, bool archive = true, int duration = 3) => new()
    {
        StreamId = streamId,
        EpgChannelId = epgId,
        TvArchive = archive,
        TvArchiveDuration = duration,
    };

    [Fact]
    public void NothingIsRecordedWhileCatchupBrowsingIsOff()
    {
        var collector = new CatchupHistoryCollector(new[] { Channel(1, "npo1") }, Config(showCatchup: false));

        collector.IsEnabled.Should().BeFalse();
        collector.Records(0, "npo1").Should().BeFalse();
        collector.ToPulls().Should().BeEmpty();
    }

    [Fact]
    public void OnlyChannelsWithAnArchiveAreRecorded()
    {
        var collector = new CatchupHistoryCollector(
            new[] { Channel(1, "npo1"), Channel(2, "npo2", archive: false), Channel(3, "npo3", duration: 0) },
            Config());

        collector.Records(0, "npo1").Should().BeTrue();
        collector.Records(0, "NPO1").Should().BeTrue("upstream ids are matched the way the EPG output matches them");
        collector.Records(0, "npo2").Should().BeFalse();
        collector.Records(0, "npo3").Should().BeFalse();
        collector.ToPulls().Select(p => p.StreamId).Should().Equal(1);
    }

    [Fact]
    public void ChannelsSharingAnEpgIdOnTheSameProviderEachGetTheProgramme()
    {
        var collector = new CatchupHistoryCollector(new[] { Channel(1, "npo1"), Channel(2, "npo1") }, Config());

        collector.Add(0, "npo1", new CatchupHistoryEntry(1, 2, "t", string.Empty));

        collector.ToPulls().Should().OnlyContain(p => p.Programmes.Count == 1);
    }

    [Fact]
    public void ChannelsOnDifferentProvidersSharingAnEpgIdDoNotCrossContaminate()
    {
        var channelA = Channel(1, "npo1");
        channelA.ProviderIndex = 0;
        var channelB = Channel(2, "npo1");
        channelB.ProviderIndex = 1;

        var config = Config();
        config.Providers.Add(TestDataBuilder.CreateProviderConfig(baseUrl: "http://other.example.com"));
        var collector = new CatchupHistoryCollector(new[] { channelA, channelB }, config);

        collector.Add(0, "npo1", new CatchupHistoryEntry(1, 2, "from provider 0", string.Empty));

        var pulls = collector.ToPulls();
        pulls.Single(p => p.StreamId == 1).Programmes.Should().ContainSingle();
        pulls.Single(p => p.StreamId == 2).Programmes.Should()
            .BeEmpty("provider 1's channel must not receive provider 0's programme just because the upstream EPG id collides");
    }

    [Fact]
    public void ArchiveDaysAreCappedByTheCatchupSetting()
    {
        var config = Config();
        config.CatchupDays = 2;
        var collector = new CatchupHistoryCollector(new[] { Channel(1, "npo1", duration: 7) }, config);

        collector.ToPulls().Single().ArchiveDays.Should().Be(2);
    }

    [Fact]
    public void AChannelTheGuideSaidNothingAboutStillGetsAPull()
    {
        // An empty pull keeps the stored guide but still cuts it back to the horizon.
        var collector = new CatchupHistoryCollector(new[] { Channel(1, "npo1"), Channel(2, string.Empty) }, Config());

        collector.ToPulls().Should().HaveCount(2).And.OnlyContain(p => p.Programmes.Count == 0);
    }

    [Fact]
    public void AReplacedProviderDoesNotInheritTheOldProvidersFiles()
    {
        // Same position in the list, same stream id, different provider: stream ids overlap
        // between providers, so the history must not follow the position.
        var before = new CatchupHistoryCollector(new[] { Channel(1, "npo1") }, Config()).ToPulls().Single();
        var after = new CatchupHistoryCollector(new[] { Channel(1, "npo1") }, Config(baseUrl: "http://other.example.com")).ToPulls().Single();

        CatchupHistoryStore.FileName(after.ProviderKey, after.StreamId)
            .Should().NotBe(CatchupHistoryStore.FileName(before.ProviderKey, before.StreamId));
    }

    [Fact]
    public void AChannelOfAProviderThatIsNotConfiguredIsNotRecorded()
    {
        var orphan = Channel(1, "npo1");
        orphan.ProviderIndex = 3;

        new CatchupHistoryCollector(new[] { orphan }, Config()).ToPulls().Should().BeEmpty();
    }

    [Fact]
    public void JsonEpgProgrammesAreDecoded()
    {
        var channel = Channel(1, "npo1");
        var collector = new CatchupHistoryCollector(new[] { channel }, Config());

        collector.Add(channel, new[] { new EpgProgram { StartTimestamp = 1, StopTimestamp = 2, Title = "VGVzdA==", Description = string.Empty } });

        collector.ToPulls().Single().Programmes.Single().Title.Should().Be("Test");
    }
}

[Collection("PluginSingletonTests")]
public sealed class CatchupHistoryXmltvTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), "test-catchup-history-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IXtreamClient> _client = new();
    private readonly LiveTvService _liveTvService;

    public CatchupHistoryXmltvTests()
    {
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(_tempPath);
        appPaths.Setup(p => p.DataPath).Returns(_tempPath);
        appPaths.Setup(p => p.ProgramDataPath).Returns(_tempPath);
        appPaths.Setup(p => p.CachePath).Returns(_tempPath);
        appPaths.Setup(p => p.LogDirectoryPath).Returns(_tempPath);
        appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(_tempPath);
        appPaths.Setup(p => p.TempDirectory).Returns(_tempPath);
        appPaths.Setup(p => p.PluginsPath).Returns(_tempPath);
        appPaths.Setup(p => p.WebPath).Returns(_tempPath);
        appPaths.Setup(p => p.ProgramSystemPath).Returns(_tempPath);
        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer
            .Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(new PluginConfiguration());
        _ = new Plugin(appPaths.Object, xmlSerializer.Object);

        var config = Plugin.Instance.Configuration;
        config.Providers.Add(TestDataBuilder.CreateProviderConfig());
        config.EnableLiveTv = true;
        config.EnableEpg = true;
        config.EnableCatchup = true;
        config.ShowCatchupInJellyfin = true;
        config.CatchupDays = 7;
        config.EnableChannelNameCleaning = false;

        var serverAppPaths = new Mock<IServerApplicationPaths>();
        serverAppPaths.Setup(p => p.DataPath).Returns(_tempPath);
        var appHost = new Mock<IServerApplicationHost>();
        appHost.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");
        _liveTvService = new LiveTvService(_client.Object, new Mock<IDispatcharrClient>().Object, serverAppPaths.Object, appHost.Object, NullLogger<LiveTvService>.Instance);
    }

    public void Dispose()
    {
        _liveTvService.Dispose();
        if (Directory.Exists(_tempPath))
        {
            Directory.Delete(_tempPath, recursive: true);
        }
    }

    private static string ProviderKey => LiveTvService.BuildProviderFingerprints(Plugin.Instance.Configuration)[0];

    private static string XmltvTime(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + " +0000";

    private static string Programme(string channel, DateTimeOffset start, DateTimeOffset stop, string title)
        => $"<programme start=\"{XmltvTime(start)}\" stop=\"{XmltvTime(stop)}\" channel=\"{channel}\"><title>{title}</title><desc>about {title}</desc></programme>";

    [Fact]
    public async Task PastProgrammesAreRecordedButNotWrittenToTheEpg()
    {
        var now = DateTimeOffset.UtcNow;
        _client
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveStreamInfo>
            {
                new() { StreamId = 1, Num = 1, Name = "Archive", EpgChannelId = "arch", TvArchive = true, TvArchiveDuration = 3 },
                new() { StreamId = 2, Num = 2, Name = "Plain", EpgChannelId = "plain" },
            });
        _client
            .Setup(c => c.GetXmltvAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<tv>"
                + Programme("arch", now.AddHours(-20), now.AddHours(-19), "Last night")
                + Programme("arch", now.AddMinutes(-30), now.AddMinutes(30), "Now on")
                + Programme("plain", now.AddHours(-20), now.AddHours(-19), "Plain last night")
                + Programme("plain", now.AddMinutes(-30), now.AddMinutes(30), "Plain now")
                + "</tv>");

        var xml = await _liveTvService.GetXmltvEpgAsync(CancellationToken.None);

        xml.Should().Contain("Now on").And.Contain("Plain now");
        xml.Should().NotContain("Last night", "the EPG window is unchanged by recording");

        var recorded = await _liveTvService.CatchupHistory.LoadAsync(ProviderKey, 1, CancellationToken.None);
        recorded.Select(e => e.Title).Should().Equal("Last night", "Now on");
        recorded[0].Description.Should().Be("about Last night");
        (await _liveTvService.CatchupHistory.LoadAsync(ProviderKey, 2, CancellationToken.None)).Should().BeEmpty("the channel keeps no archive");
    }

    [Fact]
    public async Task AnXmltvWithOnlyPastProgrammesStillRecordsHistoryInsteadOfFallingBack()
    {
        // A fully-read upstream XMLTV whose only programmes fall outside the EPG window writes
        // nothing to the visible guide. That must not look like a parse failure: falling back
        // to the JSON fetch here would rebuild the collector from scratch and discard the
        // history this build already recorded.
        var now = DateTimeOffset.UtcNow;
        _client
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveStreamInfo>
            {
                new() { StreamId = 1, Num = 1, Name = "Archive", EpgChannelId = "arch", TvArchive = true, TvArchiveDuration = 3 },
            });
        _client
            .Setup(c => c.GetXmltvAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<tv>" + Programme("arch", now.AddHours(-20), now.AddHours(-19), "Last night") + "</tv>");

        await _liveTvService.GetXmltvEpgAsync(CancellationToken.None);

        var recorded = await _liveTvService.CatchupHistory.LoadAsync(ProviderKey, 1, CancellationToken.None);
        recorded.Select(e => e.Title).Should().Equal("Last night");
        _client.Verify(
            c => c.GetSimpleDataTableAsync(It.IsAny<ConnectionInfo>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a valid upstream XMLTV must not trigger the JSON fallback just because every programme was in the past");
    }

    [Fact]
    public async Task AProviderWithRecordedHistoryOnlyDoesNotMaskAnotherProvidersNeedForFallback()
    {
        // Provider A's XMLTV has only past programmes: written=0 but recorded>0. That is fine for
        // provider A alone, but must not be read as "fine" for provider B too. Provider B's XMLTV
        // breaks outright and must still get the JSON fallback, while provider A's already
        // recorded history is left untouched.
        var now = DateTimeOffset.UtcNow;
        var providerA = Plugin.Instance.Configuration.Providers[0];
        var providerB = TestDataBuilder.CreateProviderConfig(baseUrl: "http://other.example.com");
        Plugin.Instance.Configuration.Providers.Add(providerB);
        var baseUrlA = providerA.BaseUrl;
        var baseUrlB = providerB.BaseUrl;
        var providerKeyB = LiveTvService.BuildProviderFingerprints(Plugin.Instance.Configuration)[1];

        _client
            .Setup(c => c.GetAllLiveStreamsAsync(It.Is<ConnectionInfo>(conn => conn.BaseUrl == baseUrlA), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveStreamInfo> { new() { StreamId = 1, Num = 1, Name = "Archive A", EpgChannelId = "a", TvArchive = true, TvArchiveDuration = 3 } });
        _client
            .Setup(c => c.GetAllLiveStreamsAsync(It.Is<ConnectionInfo>(conn => conn.BaseUrl == baseUrlB), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveStreamInfo> { new() { StreamId = 1, Num = 1, Name = "Archive B", EpgChannelId = "b", TvArchive = true, TvArchiveDuration = 3 } });

        _client
            .Setup(c => c.GetXmltvAsync(It.Is<ConnectionInfo>(conn => conn.BaseUrl == baseUrlA), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<tv>" + Programme("a", now.AddHours(-20), now.AddHours(-19), "A history") + "</tv>");
        _client
            .Setup(c => c.GetXmltvAsync(It.Is<ConnectionInfo>(conn => conn.BaseUrl == baseUrlB), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<tv>" + Programme("b", now.AddHours(-20), now.AddHours(-19), "Never reached") + "<programme start=");
        _client
            .Setup(c => c.GetSimpleDataTableAsync(It.Is<ConnectionInfo>(conn => conn.BaseUrl == baseUrlB), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpgListings
            {
                Listings = new List<EpgProgram>
                {
                    new()
                    {
                        StartTimestamp = now.AddHours(-3).ToUnixTimeSeconds(),
                        StopTimestamp = now.AddHours(-2).ToUnixTimeSeconds(),
                        Title = "RnJvbSBKU09O",
                        Description = string.Empty,
                    },
                },
            });

        await _liveTvService.GetXmltvEpgAsync(CancellationToken.None);

        var recordedA = await _liveTvService.CatchupHistory.LoadAsync(ProviderKey, 1, CancellationToken.None);
        recordedA.Select(e => e.Title).Should().Equal(
            new[] { "A history" },
            "provider A's own recorded history must survive even though provider B needed the fallback");

        var recordedB = await _liveTvService.CatchupHistory.LoadAsync(providerKeyB, 1, CancellationToken.None);
        recordedB.Select(e => e.Title).Should().Equal(
            new[] { "From JSON" },
            "provider B must still fall back to its JSON EPG even though provider A's recorded history made the aggregate non-zero");
    }

    [Fact]
    public async Task AnXmltvThatFailsHalfWayRecordsOnlyTheFallbackGuide()
    {
        var now = DateTimeOffset.UtcNow;
        _client
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveStreamInfo>
            {
                new() { StreamId = 1, Num = 1, Name = "Archive", EpgChannelId = "arch", TvArchive = true, TvArchiveDuration = 3 },
            });
        _client
            .Setup(c => c.GetXmltvAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<tv>"
                + Programme("arch", now.AddHours(-20), now.AddHours(-19), "Half a guide")
                + Programme("arch", now.AddHours(-19), now.AddHours(-18), "Never reached")
                + "<programme start=");
        _client
            .Setup(c => c.GetSimpleDataTableAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EpgListings
            {
                Listings = new List<EpgProgram>
                {
                    new()
                    {
                        StartTimestamp = now.AddHours(-3).ToUnixTimeSeconds(),
                        StopTimestamp = now.AddHours(-2).ToUnixTimeSeconds(),
                        Title = "RnJvbSBKU09O",
                        Description = string.Empty,
                    },
                },
            });

        await _liveTvService.GetXmltvEpgAsync(CancellationToken.None);

        var recorded = await _liveTvService.CatchupHistory.LoadAsync(ProviderKey, 1, CancellationToken.None);
        recorded.Select(e => e.Title).Should().Equal("From JSON");
    }

    [Fact]
    public async Task NothingIsRecordedWhenCatchupBrowsingIsOff()
    {
        Plugin.Instance.Configuration.ShowCatchupInJellyfin = false;
        var now = DateTimeOffset.UtcNow;
        _client
            .Setup(c => c.GetAllLiveStreamsAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<LiveStreamInfo>
            {
                new() { StreamId = 1, Num = 1, Name = "Archive", EpgChannelId = "arch", TvArchive = true, TvArchiveDuration = 3 },
            });
        _client
            .Setup(c => c.GetXmltvAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("<tv>" + Programme("arch", now.AddHours(-20), now.AddHours(-19), "Last night") + "</tv>");

        await _liveTvService.GetXmltvEpgAsync(CancellationToken.None);

        Directory.Exists(Path.Combine(_tempPath, "xtream-library", "catchup-history")).Should().BeFalse();
    }
}
