// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #88. Which folders hold the same film, worked out before anything is touched.

using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Service.Models;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class TmdbGroupingTests
{
    private static ContentSnapshot SnapshotWith(params MovieSnapshot[] movies)
    {
        var snapshot = new ContentSnapshot();
        foreach (var movie in movies)
        {
            snapshot.Movies[movie.StreamId] = movie;
        }

        return snapshot;
    }

    private static MovieSnapshot Movie(int streamId, int? tmdbId, ItemIdSource source, string folder) =>
        new() { StreamId = streamId, TmdbId = tmdbId, TmdbIdSource = source, FolderName = folder };

    [Theory]
    [InlineData(ItemIdSource.Provider, true)]
    [InlineData(ItemIdSource.Override, true)]
    [InlineData(ItemIdSource.Lookup, false)]
    [InlineData(ItemIdSource.Unknown, false)]
    [InlineData(ItemIdSource.None, false)]
    public void OnlyAnIdTheProviderGaveOrTheUserPinned_MayBeGroupedOn(ItemIdSource source, bool groupable)
    {
        TmdbGrouping.IsGroupable(source).Should().Be(groupable);
    }

    [Fact]
    public void TwoFoldersSharingAProviderId_AreMerged()
    {
        var plan = TmdbGrouping.Plan(SnapshotWith(
            Movie(100, 42, ItemIdSource.Provider, "The Film (2024) [tmdbid-42]"),
            Movie(200, 42, ItemIdSource.Provider, "The Film 4K (2024) [tmdbid-42]")));

        plan.Moves.Should().HaveCount(1);
        plan.Moves[0].TmdbId.Should().Be(42);
        plan.Moves[0].TargetFolder.Should().Be("The Film (2024) [tmdbid-42]", "the lowest stream id names the folder");
        plan.Moves[0].SourceFolders.Should().Equal("The Film 4K (2024) [tmdbid-42]");
    }

    [Fact]
    public void TheTargetDoesNotDependOnDictionaryOrder()
    {
        // Same two films, inserted the other way round.
        var plan = TmdbGrouping.Plan(SnapshotWith(
            Movie(200, 42, ItemIdSource.Provider, "The Film 4K (2024) [tmdbid-42]"),
            Movie(100, 42, ItemIdSource.Provider, "The Film (2024) [tmdbid-42]")));

        plan.Moves[0].TargetFolder.Should().Be("The Film (2024) [tmdbid-42]");
    }

    [Fact]
    public void ItemsAlreadySharingAFolder_NeedNoMove()
    {
        var plan = TmdbGrouping.Plan(SnapshotWith(
            Movie(100, 42, ItemIdSource.Provider, "The Film (2024) [tmdbid-42]"),
            Movie(200, 42, ItemIdSource.Provider, "The Film (2024) [tmdbid-42]")));

        plan.Moves.Should().BeEmpty();
    }

    [Fact]
    public void AGuessedIdIsNeverMerged_AndIsReportedInstead()
    {
        // The case that must not merge: a search can give a film and its remake one id.
        var plan = TmdbGrouping.Plan(SnapshotWith(
            Movie(100, 42, ItemIdSource.Lookup, "The Lion King (1994) [tmdbid-42]"),
            Movie(200, 42, ItemIdSource.Lookup, "The Lion King (2019) [tmdbid-42]")));

        plan.Moves.Should().BeEmpty();
        plan.ItemsWithUnprovenId.Should().Be(2, "the user has to be told why nothing happened");
    }

    [Fact]
    public void AnIdReadOffDiskCountsAsUnproven_UntilASyncConfirmsIt()
    {
        var plan = TmdbGrouping.Plan(SnapshotWith(
            Movie(100, 42, ItemIdSource.Unknown, "The Film (2024) [tmdbid-42]"),
            Movie(200, 42, ItemIdSource.Provider, "The Film 4K (2024) [tmdbid-42]")));

        plan.Moves.Should().BeEmpty("one confirmed item is not two");
        plan.ItemsWithUnprovenId.Should().Be(1);
    }

    [Fact]
    public void DifferentIdsStayApart()
    {
        var plan = TmdbGrouping.Plan(SnapshotWith(
            Movie(100, 42, ItemIdSource.Provider, "The Film (2024) [tmdbid-42]"),
            Movie(200, 43, ItemIdSource.Provider, "Another Film (2024) [tmdbid-43]")));

        plan.Moves.Should().BeEmpty();
    }

    [Fact]
    public void AnItemWithNoFolderRecorded_IsIgnored()
    {
        var plan = TmdbGrouping.Plan(SnapshotWith(
            Movie(100, 42, ItemIdSource.Provider, "The Film (2024) [tmdbid-42]"),
            Movie(200, 42, ItemIdSource.Provider, string.Empty)));

        plan.Moves.Should().BeEmpty();
    }

    [Fact]
    public void TheFolderMapPinsTheChoiceForLaterSyncs()
    {
        var map = TmdbGrouping.BuildFolderMap(SnapshotWith(
            Movie(200, 42, ItemIdSource.Provider, "The Film 4K (2024) [tmdbid-42]"),
            Movie(100, 42, ItemIdSource.Provider, "The Film (2024) [tmdbid-42]"),
            Movie(300, 43, ItemIdSource.Lookup, "Guessed (2024) [tmdbid-43]")));

        map.Should().ContainKey(42).WhoseValue.Should().Be("The Film (2024) [tmdbid-42]");
        map.Should().NotContainKey(43, "a guessed id must not steer new movies either");
    }

    [Fact]
    public void NoSnapshotMeansNoPlan()
    {
        TmdbGrouping.Plan(null).Moves.Should().BeEmpty();
        TmdbGrouping.BuildFolderMap(null).Should().BeEmpty();
    }
}
