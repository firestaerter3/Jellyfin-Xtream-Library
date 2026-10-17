// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #108. The IChannel implementation itself cannot be unit tested without a Jellyfin server,
// so every decision it makes lives in CatchupPlanner and is asserted here instead.

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class CatchupPlannerTests
{
    // Amsterdam: UTC+1, UTC+2 over summer. Picked because it makes a UTC-versus-local mistake
    // visible rather than a no-op, which UTC itself would not.
    private static readonly TimeZoneInfo Amsterdam = ResolveZone("Europe/Amsterdam", "W. Europe Standard Time");

    private static TimeZoneInfo ResolveZone(string iana, string windows)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(iana);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windows);
        }
    }

    private static LiveStreamInfo Channel(int streamId, bool archive, int duration) =>
        new() { StreamId = streamId, Name = $"Channel {streamId}", TvArchive = archive, TvArchiveDuration = duration };

    private static EpgProgram Programme(long start, long stop) =>
        new() { StartTimestamp = start, StopTimestamp = stop, Title = "x" };

    private static long Unix(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds();

    // ---- CatchupChannels ----

    [Fact]
    public void OnlyChannelsTheProviderKeepsAnArchiveForAreOffered()
    {
        var channels = new[]
        {
            Channel(1, archive: true, duration: 7),
            Channel(2, archive: false, duration: 7),   // no archive at all
            Channel(3, archive: true, duration: 0),    // claims archive, keeps nothing
        };

        CatchupPlanner.CatchupChannels(channels).Select(c => c.StreamId).Should().Equal(1);
    }

    [Fact]
    public void FilteringDoesNotMutateTheInput()
    {
        var channels = new List<LiveStreamInfo> { Channel(1, true, 7), Channel(2, false, 7) };
        CatchupPlanner.CatchupChannels(channels);
        channels.Should().HaveCount(2);
    }

    [Fact]
    public void ANullChannelListIsEmpty()
    {
        CatchupPlanner.CatchupChannels(null).Should().BeEmpty();
    }

    // ---- DayCount ----

    [Theory]
    [InlineData(7, 14, 7)]    // setting is the tighter limit
    [InlineData(14, 7, 7)]    // provider is the tighter limit
    [InlineData(7, 7, 7)]
    [InlineData(7, 0, 0)]     // provider keeps nothing
    [InlineData(0, 7, 0)]
    [InlineData(-3, 7, 0)]    // never negative
    public void DayCountTakesTheLowerOfTheTwoLimits(int configured, int archiveDuration, int expected)
    {
        // Offering a day the provider has already dropped gives a folder where every item fails.
        CatchupPlanner.DayCount(configured, archiveDuration).Should().Be(expected);
    }

    // ---- DayWindowUtc ----

    [Fact]
    public void ADayIsTheViewersCalendarDay_NotAUtcOne()
    {
        // 00:30 local on 15 July is 22:30 UTC on the 14th. Getting this wrong files the whole
        // evening under the wrong day for anyone east of Greenwich.
        var now = DateTimeOffset.Parse("2026-07-15T00:30:00+02:00", System.Globalization.CultureInfo.InvariantCulture);
        var (from, to) = CatchupPlanner.DayWindowUtc(0, Amsterdam, now);

        from.ToUniversalTime().Should().Be(DateTimeOffset.Parse("2026-07-14T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        to.ToUniversalTime().Should().Be(DateTimeOffset.Parse("2026-07-15T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void DaysAgoWalksBackWholeCalendarDays()
    {
        var now = DateTimeOffset.Parse("2026-07-15T12:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture);
        var (from, _) = CatchupPlanner.DayWindowUtc(3, Amsterdam, now);

        from.ToUniversalTime().Should().Be(DateTimeOffset.Parse("2026-07-11T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TheShortDayAtASpringForwardIsStillContiguous()
    {
        // Clocks go forward on 29 March 2026 in Amsterdam, making that day 23 hours long. The
        // window must still start where the previous one ended, or an hour of archive vanishes.
        var now = DateTimeOffset.Parse("2026-03-30T12:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture);
        var shortDay = CatchupPlanner.DayWindowUtc(1, Amsterdam, now);
        var dayBefore = CatchupPlanner.DayWindowUtc(2, Amsterdam, now);

        dayBefore.ToUtc.Should().Be(shortDay.FromUtc);
        (shortDay.ToUtc - shortDay.FromUtc).Should().Be(TimeSpan.FromHours(23));
    }

    [Fact]
    public void TheLongDayAtAnAutumnFallBackIsStillContiguous()
    {
        var now = DateTimeOffset.Parse("2026-10-26T12:00:00+01:00", System.Globalization.CultureInfo.InvariantCulture);
        var longDay = CatchupPlanner.DayWindowUtc(1, Amsterdam, now);

        (longDay.ToUtc - longDay.FromUtc).Should().Be(TimeSpan.FromHours(25));
    }

    // ---- ProgrammesInWindow ----

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-15T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset Horizon = DateTimeOffset.Parse("2026-07-08T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset From = DateTimeOffset.Parse("2026-07-14T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset To = DateTimeOffset.Parse("2026-07-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void AProgrammeInsideTheWindowIsListed()
    {
        var p = Programme(Unix("2026-07-14T20:00:00Z"), Unix("2026-07-14T21:00:00Z"));
        CatchupPlanner.ProgrammesInWindow([p], From, To, Now, Horizon).Should().ContainSingle();
    }

    [Fact]
    public void AProgrammeCrossingMidnightShowsUnderBothDays()
    {
        // 23:30 to 00:30 is a real thing to want to watch. Containment would drop it entirely.
        var p = Programme(Unix("2026-07-14T23:30:00Z"), Unix("2026-07-15T00:30:00Z"));

        CatchupPlanner.ProgrammesInWindow([p], From, To, Now, Horizon).Should().ContainSingle();

        var nextDay = CatchupPlanner.ProgrammesInWindow(
            [p],
            To,
            To.AddDays(1),
            Now,
            Horizon);
        nextDay.Should().ContainSingle();
    }

    [Fact]
    public void AProgrammeStillAiringIsNotCatchUp()
    {
        // It is live, and Jellyfin already plays live.
        var p = Programme(Unix("2026-07-15T11:30:00Z"), Unix("2026-07-15T12:30:00Z"));
        CatchupPlanner.ProgrammesInWindow([p], To, To.AddDays(1), Now, Horizon).Should().BeEmpty();
    }

    [Fact]
    public void AProgrammeInTheFutureIsNotListed()
    {
        var p = Programme(Unix("2026-07-15T20:00:00Z"), Unix("2026-07-15T21:00:00Z"));
        CatchupPlanner.ProgrammesInWindow([p], To, To.AddDays(1), Now, Horizon).Should().BeEmpty();
    }

    [Fact]
    public void AProgrammeOlderThanTheArchiveHorizonIsNotListed()
    {
        // The provider has dropped it; listing it only produces an item that fails when clicked.
        var p = Programme(Unix("2026-07-01T20:00:00Z"), Unix("2026-07-01T21:00:00Z"));
        var window = DateTimeOffset.Parse("2026-07-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        CatchupPlanner.ProgrammesInWindow([p], window, window.AddDays(1), Now, Horizon).Should().BeEmpty();
    }

    [Fact]
    public void AnEntryWithNoDurationIsDropped()
    {
        var p = Programme(Unix("2026-07-14T20:00:00Z"), Unix("2026-07-14T20:00:00Z"));
        CatchupPlanner.ProgrammesInWindow([p], From, To, Now, Horizon).Should().BeEmpty();
    }

    [Fact]
    public void ResultsComeBackOldestFirst()
    {
        var late = Programme(Unix("2026-07-14T21:00:00Z"), Unix("2026-07-14T22:00:00Z"));
        var early = Programme(Unix("2026-07-14T19:00:00Z"), Unix("2026-07-14T20:00:00Z"));

        CatchupPlanner.ProgrammesInWindow([late, early], From, To, Now, Horizon)
            .Select(p => p.StartTimestamp)
            .Should().BeInAscendingOrder();
    }

    [Fact]
    public void ANullProgrammeListIsEmpty()
    {
        CatchupPlanner.ProgrammesInWindow(null, From, To, Now, Horizon).Should().BeEmpty();
    }

    // ---- TimeBlocks ----

    [Fact]
    public void ADayWithNoGuideIsOfferedInBlocks()
    {
        // Measured against a real provider: the archive plays, but no endpoint says what aired.
        var blocks = CatchupPlanner.TimeBlocks(From, To, Now, Horizon, 30);
        blocks.Should().HaveCount(48);
        blocks[0].FromUtc.Should().Be(From);
        (blocks[0].ToUtc - blocks[0].FromUtc).Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void OnlyBlocksThatHaveFinishedAreOffered()
    {
        // Today, half past noon. A block covering this afternoon is not in the archive yet.
        var today = DateTimeOffset.Parse("2026-07-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var blocks = CatchupPlanner.TimeBlocks(today, today.AddDays(1), Now, Horizon, 60);

        blocks.Should().HaveCount(12);
        blocks[^1].ToUtc.Should().BeOnOrBefore(Now);
    }

    [Fact]
    public void BlocksBeforeTheArchiveHorizonAreNotOffered()
    {
        var old = DateTimeOffset.Parse("2026-07-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        CatchupPlanner.TimeBlocks(old, old.AddDays(1), Now, Horizon, 30).Should().BeEmpty();
    }

    [Fact]
    public void APartialBlockAtTheEndOfTheWindowIsNotOffered()
    {
        // 50 does not divide 1440, so the last block would run past midnight into a day whose
        // folder covers it already.
        var blocks = CatchupPlanner.TimeBlocks(From, To, Now, Horizon, 50);
        blocks[^1].ToUtc.Should().BeOnOrBefore(To);
    }

    [Fact]
    public void BlocksAreContiguousAndOldestFirst()
    {
        var blocks = CatchupPlanner.TimeBlocks(From, To, Now, Horizon, 60);
        for (int i = 1; i < blocks.Count; i++)
        {
            blocks[i].FromUtc.Should().Be(blocks[i - 1].ToUtc);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void BlocksCanBeSwitchedOff(int blockMinutes)
    {
        CatchupPlanner.TimeBlocks(From, To, Now, Horizon, blockMinutes).Should().BeEmpty();
    }

    // ---- GapBlocks ----

    private static EpgProgram At(string fromIso, string toIso) => Programme(Unix(fromIso), Unix(toIso));

    [Fact]
    public void WithoutAnyGuideTheGapsAreTheWholeDay()
    {
        CatchupPlanner.GapBlocks(null, From, To, Now, Horizon, 30)
            .Should().Equal(CatchupPlanner.TimeBlocks(From, To, Now, Horizon, 30));
    }

    [Fact]
    public void AFullyCoveredDayGetsNoBlocks()
    {
        var programmes = Enumerable.Range(0, 24)
            .Select(h => Programme(From.AddHours(h).ToUnixTimeSeconds(), From.AddHours(h + 1).ToUnixTimeSeconds()));

        CatchupPlanner.GapBlocks(programmes, From, To, Now, Horizon, 30).Should().BeEmpty();
    }

    [Fact]
    public void RecordingThatStartedMidDayLeavesTheMorningInBlocks()
    {
        // Installed at 14:10: titles from then on, nothing before.
        var programmes = new[] { At("2026-07-14T14:10:00Z", "2026-07-15T00:00:00Z") };

        var blocks = CatchupPlanner.GapBlocks(programmes, From, To, Now, Horizon, 30);

        blocks.Should().HaveCount(29, "28 whole blocks up to 14:00, then 14:00-14:10");
        blocks[^1].Should().Be((From.AddHours(14), From.AddHours(14).AddMinutes(10)));
    }

    [Fact]
    public void ABlockIsCutBackToTheUncoveredPart()
    {
        // 20:05-20:20 is a hole in the middle of the 20:00-20:30 block.
        var programmes = new[]
        {
            At("2026-07-14T00:00:00Z", "2026-07-14T20:05:00Z"),
            At("2026-07-14T20:20:00Z", "2026-07-15T00:00:00Z"),
        };

        CatchupPlanner.GapBlocks(programmes, From, To, Now, Horizon, 30)
            .Should().Equal((From.AddHours(20).AddMinutes(5), From.AddHours(20).AddMinutes(20)));
    }

    [Fact]
    public void SliversBetweenProgrammesAreNotOffered()
    {
        // Guides leave a minute or two between one stop and the next start all the time.
        var programmes = new[]
        {
            At("2026-07-14T00:00:00Z", "2026-07-14T20:00:00Z"),
            At("2026-07-14T20:02:00Z", "2026-07-15T00:00:00Z"),
        };

        CatchupPlanner.GapBlocks(programmes, From, To, Now, Horizon, 30).Should().BeEmpty();
    }

    [Fact]
    public void OverlappingProgrammesCountAsOneCoveredStretch()
    {
        var programmes = new[]
        {
            At("2026-07-14T00:00:00Z", "2026-07-14T20:30:00Z"),
            At("2026-07-14T20:00:00Z", "2026-07-14T21:00:00Z"),
            At("2026-07-14T20:10:00Z", "2026-07-14T20:40:00Z"),
            At("2026-07-14T21:00:00Z", "2026-07-15T00:00:00Z"),
        };

        CatchupPlanner.GapBlocks(programmes, From, To, Now, Horizon, 30).Should().BeEmpty();
    }

    // The browser passes what ProgrammesInWindow lists, not the whole guide. These two run the same
    // pair of calls, because a programme that isn't listed must not hide the time it covers.
    private static IReadOnlyList<(DateTimeOffset FromUtc, DateTimeOffset ToUtc)> BrowsedGaps(
        EpgProgram[] guide, DateTimeOffset from, DateTimeOffset to)
        => CatchupPlanner.GapBlocks(CatchupPlanner.ProgrammesInWindow(guide, from, to, Now, Horizon), from, to, Now, Horizon, 30);

    [Fact]
    public void AProgrammeStillAiringLeavesItsFinishedPartAsBlocks()
    {
        // 11:00 to 13:00, and it is 12:00. The programme isn't listed yet, so 11:00 to 12:00 has
        // to be reachable as blocks. Some providers send one entry covering the whole day.
        var today = DateTimeOffset.Parse("2026-07-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var guide = new[] { At("2026-07-15T00:00:00Z", "2026-07-15T11:00:00Z"), At("2026-07-15T11:00:00Z", "2026-07-15T13:00:00Z") };

        var blocks = BrowsedGaps(guide, today, today.AddDays(1));

        blocks.Should().Equal((today.AddHours(11), today.AddHours(11.5)), (today.AddHours(11.5), today.AddHours(12)));
    }

    [Fact]
    public void AProgrammeFromBeforeTheHorizonLeavesTheStartOfTheOldestDayAsBlocks()
    {
        // Runs from before the horizon into the oldest day. It isn't listed, since it began before
        // what the provider still serves, so the first hour must still come as blocks.
        var guide = new[] { At("2026-07-07T23:00:00Z", "2026-07-08T01:00:00Z"), At("2026-07-08T01:00:00Z", "2026-07-09T00:00:00Z") };

        var blocks = BrowsedGaps(guide, Horizon, Horizon.AddDays(1));

        blocks.Should().Equal((Horizon, Horizon.AddMinutes(30)), (Horizon.AddMinutes(30), Horizon.AddHours(1)));
    }

    [Fact]
    public void GapsFollowTheSameRulesAsBlocks()
    {
        // Nothing unfinished, nothing before the horizon, nothing when blocks are switched off.
        var today = DateTimeOffset.Parse("2026-07-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        CatchupPlanner.GapBlocks(null, today, today.AddDays(1), Now, Horizon, 60).Should().HaveCount(12);

        var old = DateTimeOffset.Parse("2026-07-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        CatchupPlanner.GapBlocks(null, old, old.AddDays(1), Now, Horizon, 30).Should().BeEmpty();

        CatchupPlanner.GapBlocks(null, From, To, Now, Horizon, 0).Should().BeEmpty();
    }

    // ---- DurationMinutes ----

    [Theory]
    [InlineData(0, 3600, 60)]
    [InlineData(0, 1800, 30)]
    [InlineData(0, 1, 1)]        // rounds up, never zero
    [InlineData(0, 61, 2)]       // partial minute rounds up so the tail is not cut off
    [InlineData(0, 0, 1)]        // a bad EPG entry still yields a sendable request
    [InlineData(100, 50, 1)]
    public void DurationIsWholeMinutesRoundedUp(long start, long stop, int expected)
    {
        CatchupPlanner.DurationMinutes(start, stop).Should().Be(expected);
    }
}
