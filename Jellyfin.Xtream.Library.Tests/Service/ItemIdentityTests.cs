// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #88 groundwork. Grouping items by TMDB id needs to know, per stream, which folder it went
// to and how trustworthy the id is. Only a provider-supplied id may be merged on: a name lookup can
// give a live-action film and its animated remake the same id, which is the one case that must not
// merge.

using System.Collections.Generic;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Service.Models;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class ItemIdentityTests
{
    private static readonly Dictionary<string, int> NoOverrides = [];

    [Theory]
    [InlineData("Some Movie (2024) [tmdbid-1234]", 1234)]
    [InlineData("Some Movie (2024) [TMDBID-99]", 99)]
    [InlineData("Weird [tmdbid-1] Name [tmdbid-2]", 1)]
    public void TmdbId_IsReadBackOutOfAFolderName(string folderName, int expected)
    {
        FolderIdentity.TryParseTmdbId(folderName, out int id).Should().BeTrue();
        id.Should().Be(expected);
    }

    [Theory]
    [InlineData("Some Movie (2024)")]
    [InlineData("Some Movie [tmdbid-]")]
    [InlineData("Some Movie [tvdbid-1234]")]
    [InlineData("")]
    [InlineData(null)]
    public void NoTmdbIdInTheName_IsNotAnId(string? folderName)
    {
        FolderIdentity.TryParseTmdbId(folderName, out int id).Should().BeFalse();
        id.Should().Be(0);
    }

    [Fact]
    public void TvdbId_IsReadBackToo_BecauseSeriesFoldersPreferIt()
    {
        FolderIdentity.TryParseTvdbId("Some Show [tvdbid-4321]", out int id).Should().BeTrue();
        id.Should().Be(4321);
    }

    [Fact]
    public void AnIdTooLargeForAnInt_IsRefusedRatherThanWrapped()
    {
        FolderIdentity.TryParseTmdbId("Some Movie [tmdbid-99999999999]", out int id).Should().BeFalse();
        id.Should().Be(0);
    }

    [Fact]
    public void AnOverrideOutranksEverything()
    {
        var identity = StrmSyncService.BuildMovieIdentity(
            "Movie (2024) [tmdbid-1]",
            "Movie (2024)",
            fromExistingFolder: false,
            new Dictionary<string, int> { ["Movie (2024)"] = 1 },
            providerTmdbId: 2,
            autoLookupTmdbId: 3);

        identity.TmdbId.Should().Be(1);
        identity.Source.Should().Be(ItemIdSource.Override);
    }

    [Fact]
    public void AProviderIdOutranksALookup()
    {
        var identity = StrmSyncService.BuildMovieIdentity(
            "Movie (2024) [tmdbid-2]", "Movie (2024)", fromExistingFolder: false, NoOverrides, 2, 3);

        identity.TmdbId.Should().Be(2);
        identity.Source.Should().Be(ItemIdSource.Provider);
    }

    [Fact]
    public void ALookupIsRecordedAsSuch_SoGroupingCanRefuseIt()
    {
        var identity = StrmSyncService.BuildMovieIdentity(
            "Movie (2024) [tmdbid-3]", "Movie (2024)", fromExistingFolder: false, NoOverrides, null, 3);

        identity.TmdbId.Should().Be(3);
        identity.Source.Should().Be(ItemIdSource.Lookup);
    }

    [Fact]
    public void NoIdAtAll_IsRecordedAsNone()
    {
        var identity = StrmSyncService.BuildMovieIdentity(
            "Movie (2024)", "Movie (2024)", fromExistingFolder: false, NoOverrides, null, null);

        identity.TmdbId.Should().BeNull();
        identity.Source.Should().Be(ItemIdSource.None);
    }

    [Fact]
    public void AnIdReadOffDisk_IsUnprovenEvenThoughItIsKnown()
    {
        // The folder is all that is left for a library synced before the snapshot carried this, and
        // it does not say whether the provider gave the id or a lookup guessed it.
        var identity = StrmSyncService.BuildMovieIdentity(
            "Movie (2024) [tmdbid-1234]", "Movie (2024)", fromExistingFolder: true, NoOverrides, null, null);

        identity.TmdbId.Should().Be(1234);
        identity.Source.Should().Be(ItemIdSource.Unknown);
        identity.FolderName.Should().Be("Movie (2024) [tmdbid-1234]");
    }

    [Fact]
    public void ASeriesKeepsItsTvdbIdSeparateFromTmdb()
    {
        var identity = StrmSyncService.BuildSeriesIdentity(
            "Show [tvdbid-77]", "Show", NoOverrides, providerTmdbId: null, autoLookupTvdbId: 77);

        identity.TvdbId.Should().Be(77);
        identity.TmdbId.Should().BeNull();
        identity.Source.Should().Be(ItemIdSource.Lookup);
    }

    // Codex review finding: an id the provider confirmed for an item already on disk is proven,
    // and treating it as Unknown left the regroup action with nothing it was allowed to merge.
    [Fact]
    public void AConfirmedProviderId_OutranksReadingItOffTheFolder()
    {
        var identity = StrmSyncService.BuildMovieIdentity(
            "Movie (2024) [tmdbid-1234]",
            "Movie (2024)",
            fromExistingFolder: true,
            NoOverrides,
            providerTmdbId: 1234,
            autoLookupTmdbId: null);

        identity.Source.Should().Be(ItemIdSource.Provider, "the provider answered for it this run");
        identity.TmdbId.Should().Be(1234);
    }

    // Codex review finding: an incremental run never reaches the loop for an unchanged movie, so
    // writing an empty identity would erase what an earlier run worked out. One scheduled sync
    // would then make regrouping impossible.
    [Fact]
    public void AnUntouchedMovie_KeepsWhatTheLastRunRecorded()
    {
        var previous = new ContentSnapshot();
        previous.Movies[100] = new MovieSnapshot
        {
            StreamId = 100,
            FolderName = "Movie (2024) [tmdbid-42]",
            TmdbId = 42,
            TmdbIdSource = ItemIdSource.Provider,
        };

        var result = StrmSyncService.EffectiveMovieIdentity(new(), previous, 100);

        result.FolderName.Should().Be("Movie (2024) [tmdbid-42]");
        result.TmdbId.Should().Be(42);
        result.Source.Should().Be(ItemIdSource.Provider);
    }

    [Fact]
    public void AMovieThisRunHandled_WinsOverTheOlderRecord()
    {
        // Including when it resolved to nothing: that is a fresh answer, not a missing one.
        var previous = new ContentSnapshot();
        previous.Movies[100] = new MovieSnapshot { StreamId = 100, FolderName = "Old", TmdbId = 42, TmdbIdSource = ItemIdSource.Provider };

        var identities = new System.Collections.Concurrent.ConcurrentDictionary<int, ItemIdentity>();
        identities[100] = new ItemIdentity("New", null, null, ItemIdSource.None);

        var result = StrmSyncService.EffectiveMovieIdentity(identities, previous, 100);

        result.FolderName.Should().Be("New");
        result.TmdbId.Should().BeNull();
        result.Source.Should().Be(ItemIdSource.None);
    }

    [Fact]
    public void AMovieNeitherRunKnows_IsSimplyEmpty()
    {
        var result = StrmSyncService.EffectiveMovieIdentity(new(), null, 999);

        result.FolderName.Should().BeEmpty();
        result.TmdbId.Should().BeNull();
        result.Source.Should().Be(ItemIdSource.None);
    }

    // Codex review finding: providers send 0 for "no id". Treating it as an id would put every
    // such film in one folder and then offer that folder for an irreversible merge.
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(1234, true)]
    public void OnlyAPositiveIdIdentifiesSomething(int id, bool usable)
    {
        StrmSyncService.IsUsableMetadataId(id).Should().Be(usable);
    }

    [Fact]
    public void TheNewFieldsSurviveTheSnapshotRoundTrip()
    {
        var snapshot = new ContentSnapshot();
        snapshot.Movies[100] = new MovieSnapshot
        {
            StreamId = 100,
            Name = "Movie",
            FolderName = "Movie (2024) [tmdbid-1234]",
            TmdbId = 1234,
            TmdbIdSource = ItemIdSource.Provider,
        };

        var restored = JsonConvert.DeserializeObject<ContentSnapshot>(JsonConvert.SerializeObject(snapshot))!;

        restored.Movies[100].FolderName.Should().Be("Movie (2024) [tmdbid-1234]");
        restored.Movies[100].TmdbId.Should().Be(1234);
        restored.Movies[100].TmdbIdSource.Should().Be(ItemIdSource.Provider);
    }

    [Fact]
    public void ASnapshotWrittenBeforeTheseFieldsExisted_StillLoads()
    {
        // Upgrade path: the fields are simply absent from an older snapshot file.
        const string Old = """
            {"Movies":{"100":{"StreamId":100,"Name":"Movie","Checksum":"abc"}},"Series":{}}
            """;

        var restored = JsonConvert.DeserializeObject<ContentSnapshot>(Old)!;

        restored.Movies[100].FolderName.Should().BeEmpty();
        restored.Movies[100].TmdbId.Should().BeNull();
        restored.Movies[100].TmdbIdSource.Should().Be(ItemIdSource.None);
    }
}
