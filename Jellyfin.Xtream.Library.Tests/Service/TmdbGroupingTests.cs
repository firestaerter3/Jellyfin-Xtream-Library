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

    // Codex review finding: which stream ends up owning a folder depends on the order a run
    // processed them. A later run that worked the owner out again could pick a different one, hand
    // the plain file name to a second stream, and overwrite the first one's file.
    [Fact]
    public void TheRecordedOwnerIsHonoured_EvenWhenItIsNotTheLowestStreamId()
    {
        var snapshot = SnapshotWith(
            new MovieSnapshot { StreamId = 100, TmdbId = 42, TmdbIdSource = ItemIdSource.Provider, FolderName = "The Film 4K (2024) [tmdbid-42]", GroupOwnerStreamId = 200 },
            new MovieSnapshot { StreamId = 200, TmdbId = 42, TmdbIdSource = ItemIdSource.Provider, FolderName = "The Film 4K (2024) [tmdbid-42]", GroupOwnerStreamId = 200 });

        TmdbGrouping.BuildFolderMap(snapshot)[42]
            .Should().Be((200, "The Film 4K (2024) [tmdbid-42]"), "the files on disk were named by 200");
    }

    [Fact]
    public void WithNothingRecorded_TheLowestStreamIdStillDecides()
    {
        // Snapshots written before ownership was recorded fall back to the old rule.
        var map = TmdbGrouping.BuildFolderMap(SnapshotWith(
            Movie(200, 42, ItemIdSource.Provider, "The Film (2024) [tmdbid-42]"),
            Movie(100, 42, ItemIdSource.Provider, "The Film (2024) [tmdbid-42]")));

        map[42].OwnerStreamId.Should().Be(100);
    }

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
    public void TheFolderMapPinsTheChoiceForLaterSyncs()
    {
        var map = TmdbGrouping.BuildFolderMap(SnapshotWith(
            Movie(200, 42, ItemIdSource.Provider, "The Film 4K (2024) [tmdbid-42]"),
            Movie(100, 42, ItemIdSource.Provider, "The Film (2024) [tmdbid-42]"),
            Movie(300, 43, ItemIdSource.Lookup, "Guessed (2024) [tmdbid-43]")));

        map.Should().ContainKey(42).WhoseValue.Should().Be(
            (100, "The Film (2024) [tmdbid-42]"),
            "the lowest stream id owns the folder, and ownership is what decides who keeps the plain file name");
        map.Should().NotContainKey(43, "a guessed id must not steer new movies either");
    }

    [Fact]
    public void NoSnapshotMeansNoMap()
    {
        TmdbGrouping.BuildFolderMap(null).Should().BeEmpty();
    }
}
