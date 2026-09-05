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
