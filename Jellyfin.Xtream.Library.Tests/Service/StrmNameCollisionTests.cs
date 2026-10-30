// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

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
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

/// <summary>
/// Two provider streams that share a title and a recognised quality tag produce the same STRM
/// file name, because the name is built from the folder name and the version label alone. Before
/// the guard, the second stream silently overwrote the first: the File.Exists branch compares the
/// file contents against a stream URL that embeds the stream id, so it can never match across two
/// different streams and always fell through to the overwrite. Nothing was logged and no counter
/// moved, and with SyncParallelism above 1 the surviving stream flipped between runs (GitHub #74).
/// </summary>
[Collection("PluginSingletonTests")]
public class StrmNameCollisionTests : IDisposable
{
    private readonly string _libraryPath;
    private readonly Mock<IXtreamClient> _client = new();
    private readonly List<string> _log = [];
    private readonly Mock<IMetadataLookupService> _lookup = new();
    private bool _metadataLookup;
    private Action<ProviderConfig>? _configure;
    private StrmSyncService? _lastService;

    private sealed class ListLogger(List<string> sink) : Microsoft.Extensions.Logging.ILogger<StrmSyncService>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => sink.Add($"[{logLevel}] {formatter(state, exception)}");
    }

    public StrmNameCollisionTests()
    {
        _libraryPath = Path.Combine(Path.GetTempPath(), "xtream-collision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_libraryPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_libraryPath))
            {
                Directory.Delete(_libraryPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; a leftover temp directory must not fail the suite.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TwoStreamsSharingTitleAndQualityTag_BothAreKeptTheLowestIdAsPrimary()
    {
        // Both resolve to "Duplicate Movie (2024)" with version label "FHD", so both want
        // "Duplicate Movie (2024) - FHD.strm". The lowest id keeps that name, the other keeps its own file
        // next to it, tagged with its stream id, so neither version is lost.
        var result = await RunMovieSyncAsync(
            new StreamInfo { StreamId = 100, Name = "Duplicate Movie (2024) - [FHD]", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "Duplicate Movie (2024) - [FHD]", ContainerExtension = "mp4" })
            .ConfigureAwait(true);

        var movieFolder = Path.Combine(_libraryPath, "Movies", "Duplicate Movie (2024)");
        var written = Directory.GetFiles(movieFolder, "*.strm");
        written.Should().HaveCount(2);
        result.MovieNameCollisions.Should().Be(0, "the second stream is an alternative, not a collision");
        result.MoviesCreated.Should().Be(2);
        result.Errors.Should().Be(0);

        var primary = Path.Combine(movieFolder, "Duplicate Movie (2024) - FHD.strm");
        (await StreamIdOfAsync(primary).ConfigureAwait(true)).Should().Be(100, "the lowest stream id gets the plain name");
        var alternative = Path.Combine(movieFolder, "Duplicate Movie (2024) - FHD - 200.strm");
        File.Exists(alternative).Should().BeTrue();
        (await StreamIdOfAsync(alternative).ConfigureAwait(true)).Should().Be(200);
    }

    private static async Task<int?> StreamIdOfAsync(string strmPath)
        => StrmSyncService.ParseMovieStreamId(await File.ReadAllTextAsync(strmPath).ConfigureAwait(true));

    // Same-title streams without an id: the primary is named like the folder, the alternative
    // carries its stream id, and who is which does not change from one run to the next.
    [Fact]
    public async Task SameTitleStreams_KeepTheirRolesOnEveryRun()
    {
        StreamInfo[] Streams() =>
        [
            new StreamInfo { StreamId = 100, Name = "Twin Movie (2024)", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "Twin Movie (2024)", ContainerExtension = "mp4" },
        ];

        await RunMovieSyncAsync(Streams(), useShippedDefaults: false).ConfigureAwait(true);
        var folder = Path.Combine(_libraryPath, "Movies", "Twin Movie (2024)");
        var primary = Path.Combine(folder, "Twin Movie (2024).strm");
        var firstPrimary = await StreamIdOfAsync(primary).ConfigureAwait(true);

        foreach (bool shipped in new[] { false, true, false })
        {
            var again = await RunMovieSyncAsync(Streams(), useShippedDefaults: shipped, cleanupOrphans: true).ConfigureAwait(true);
            again.MovieNameCollisions.Should().Be(0);
            (await StreamIdOfAsync(primary).ConfigureAwait(true)).Should().Be(firstPrimary);
            Directory.GetFiles(folder, "*.strm").Should().HaveCount(2);
        }
    }

    // The primary leaves the provider: its file is cleaned up as an orphan and the alternative takes
    // the folder's name, in a full sync and in an incremental one that skips the unchanged stream.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WhenThePrimaryLeaves_TheAlternativeBecomesThePrimary(bool incremental)
    {
        StreamInfo Stream(int id) => new() { StreamId = id, Name = "Twin Movie (2024)", ContainerExtension = "mp4" };

        await RunMovieSyncAsync([Stream(100), Stream(200)], useShippedDefaults: incremental).ConfigureAwait(true);
        var folder = Path.Combine(_libraryPath, "Movies", "Twin Movie (2024)");
        var primary = Path.Combine(folder, "Twin Movie (2024).strm");
        int leaving = (await StreamIdOfAsync(primary).ConfigureAwait(true))!.Value;
        int staying = leaving == 100 ? 200 : 100;

        var result = await RunMovieSyncAsync([Stream(staying)], useShippedDefaults: incremental, cleanupOrphans: true).ConfigureAwait(true);

        Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).Should().Equal("Twin Movie (2024).strm");
        (await StreamIdOfAsync(primary).ConfigureAwait(true)).Should().Be(staying);
        result.MovieNameCollisions.Should().Be(0);

        // The next run recognises the promoted file as the stream's own instead of adding a second one.
        await RunMovieSyncAsync([Stream(staying)], useShippedDefaults: incremental, cleanupOrphans: true).ConfigureAwait(true);
        Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).Should().Equal("Twin Movie (2024).strm");
    }

    [Fact]
    public async Task WhenAnAlternativeLeaves_ItsFileIsCleanedUpAndThePrimaryStays()
    {
        StreamInfo Stream(int id) => new() { StreamId = id, Name = "Twin Movie (2024)", ContainerExtension = "mp4" };

        await RunMovieSyncAsync([Stream(100), Stream(200)], useShippedDefaults: true).ConfigureAwait(true);
        var folder = Path.Combine(_libraryPath, "Movies", "Twin Movie (2024)");
        var primary = Path.Combine(folder, "Twin Movie (2024).strm");
        int staying = (await StreamIdOfAsync(primary).ConfigureAwait(true))!.Value;

        await RunMovieSyncAsync([Stream(staying)], useShippedDefaults: true, cleanupOrphans: true).ConfigureAwait(true);

        Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).Should().Equal("Twin Movie (2024).strm");
        (await StreamIdOfAsync(primary).ConfigureAwait(true)).Should().Be(staying);
    }

    // Cleanup is off, so the departed primary's file is deliberately kept; nothing may overwrite it.
    [Fact]
    public async Task WithCleanupOff_AStalePrimaryIsNotReplacedByAnAlternative()
    {
        StreamInfo Stream(int id) => new() { StreamId = id, Name = "Twin Movie (2024)", ContainerExtension = "mp4" };

        await RunMovieSyncAsync([Stream(100), Stream(200)], useShippedDefaults: false).ConfigureAwait(true);
        var folder = Path.Combine(_libraryPath, "Movies", "Twin Movie (2024)");
        var primary = Path.Combine(folder, "Twin Movie (2024).strm");
        int leaving = (await StreamIdOfAsync(primary).ConfigureAwait(true))!.Value;
        int staying = leaving == 100 ? 200 : 100;

        await RunMovieSyncAsync([Stream(staying)], useShippedDefaults: false, cleanupOrphans: false).ConfigureAwait(true);

        (await StreamIdOfAsync(primary).ConfigureAwait(true)).Should().Be(leaving);
        File.Exists(Path.Combine(folder, $"Twin Movie (2024) - {staying}.strm")).Should().BeTrue();
    }

    // The same rule for a TMDB group: when the owner leaves, the guest takes the folder's name and
    // is the owner on the next run, so it is not written a second time as a guest.
    [Fact]
    public async Task WhenAGroupOwnerLeaves_TheGuestBecomesTheOwnerOnEveryLaterRun()
    {
        VodInfoWithTmdbId(100, "42");
        VodInfoWithTmdbId(200, "42");
        await RunMovieSyncAsync(
            [
                new StreamInfo { StreamId = 100, Name = "Alpha Movie (2024)", ContainerExtension = "mp4" },
                new StreamInfo { StreamId = 200, Name = "Beta Movie (2024)", ContainerExtension = "mp4" },
            ],
            useShippedDefaults: false,
            groupByTmdbId: true).ConfigureAwait(true);

        var folder = Path.Combine(_libraryPath, "Movies", "Alpha Movie (2024) [tmdbid-42]");
        var primary = Path.Combine(folder, "Alpha Movie (2024) [tmdbid-42].strm");

        for (int run = 0; run < 3; run++)
        {
            await RunMovieSyncAsync(
                [new StreamInfo { StreamId = 200, Name = "Beta Movie (2024)", ContainerExtension = "mp4" }],
                useShippedDefaults: run == 1,
                groupByTmdbId: true,
                cleanupOrphans: true).ConfigureAwait(true);

            Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).Should().Equal("Alpha Movie (2024) [tmdbid-42].strm");
            (await StreamIdOfAsync(primary).ConfigureAwait(true)).Should().Be(200);
        }
    }

    [Theory]
    [InlineData("http://h/movie/u/p/123.mp4", 123)]
    [InlineData("http://h/movie/u/p/123", 123)]
    [InlineData("http://h:8080/movie/u/p/45.mkv?token=1", 45)]
    [InlineData("http://d/proxy/vod/movie/0d4f1c2a?stream_id=5", null)]
    [InlineData("http://h/live/u/p/9.ts", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseMovieStreamId_ReadsOnlyXtreamMovieUrls(string? url, int? expected)
        => StrmSyncService.ParseMovieStreamId(url).Should().Be(expected);

    [Fact]
    public void AlternativeFileName_PutsTheStreamIdBeforeTheExtension()
        => StrmSyncService.AlternativeFileName("Movie (2024) - 4K.strm", 77).Should().Be("Movie (2024) - 4K - 77.strm");

    private static MovieRoleLedger NewLedger()
        => new(
            path => File.Exists(path) ? StrmSyncService.ParseMovieStreamId(File.ReadAllText(path)) : null,
            (folder, plainName, streamId) => Path.Combine(folder, StrmSyncService.AlternativeFileName(plainName, streamId)));

    private static System.Collections.Concurrent.ConcurrentDictionary<string, byte> FileSet(params string[] files)
    {
        var set = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            set[f] = 0;
        }

        return set;
    }

    private static string WriteMainStrm(string path, int streamId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"http://provider0.test/movie/u/p/{streamId}.mp4");
        return path;
    }

    [Fact]
    public void Promote_OnlyReplacesAPrimaryCleanupDeletedWhoseStreamIsGone()
    {
        var folder = Path.Combine(_libraryPath, "Twin (2024)");
        var primary = Path.Combine(folder, "Twin (2024).strm");
        var alt9 = WriteMainStrm(Path.Combine(folder, "Twin (2024) - 9.strm"), 9);
        var alt3 = WriteMainStrm(Path.Combine(folder, "Twin (2024) - 3.strm"), 3);
        var deleted = new Dictionary<string, int?> { [primary] = 1 };

        MovieRoleLedger Ledger(int[] listed)
        {
            var ledger = NewLedger();
            ledger.SetCatalogue(listed, complete: true);
            ledger.RegisterAlternative(primary, 9, alt9, 1, false, true);
            ledger.RegisterAlternative(primary, 3, alt3, 1, false, true);
            return ledger;
        }

        // The primary's stream is still listed, or nothing was deleted: nothing moves.
        Ledger([1, 3, 9]).Promote(deleted, FileSet(alt9, alt3), FileSet(alt9, alt3)).Should().BeEmpty();
        Ledger([3, 9]).Promote(new Dictionary<string, int?>(), FileSet(alt9, alt3), FileSet(alt9, alt3)).Should().BeEmpty();
        Ledger([3, 9]).Promote(new Dictionary<string, int?> { [primary] = null }, FileSet(alt9, alt3), FileSet(alt9, alt3)).Should().BeEmpty();
        File.Exists(primary).Should().BeFalse();

        var synced = FileSet(alt9, alt3);
        var promoted = Ledger([3, 9]).Promote(deleted, synced, FileSet(alt9, alt3));

        promoted.Should().ContainSingle().Which.StreamId.Should().Be(3, "the lowest stream id takes the name");
        File.Exists(primary).Should().BeTrue();
        File.Exists(alt3).Should().BeFalse();
        File.Exists(alt9).Should().BeTrue();
        synced.ContainsKey(primary).Should().BeTrue();
        synced.ContainsKey(alt3).Should().BeFalse();
    }

    [Fact]
    public void Promote_WaitsOnTheOwnersRealPrimaryNotOnTheGuestsOwnName()
    {
        var folder = Path.Combine(_libraryPath, "Alpha (2024) [tmdbid-42]");
        var ownerPrimary = WriteMainStrm(Path.Combine(folder, "Alpha (2024) [tmdbid-42] - FHD.strm"), 100);
        var guestPlain = Path.Combine(folder, "Alpha (2024) [tmdbid-42].strm");
        var guestAlt = WriteMainStrm(Path.Combine(folder, "Alpha (2024) [tmdbid-42] - 101.strm"), 101);
        var ledger = NewLedger();
        ledger.SetCatalogue([100, 101], complete: true);
        ledger.RegisterAlternative(guestPlain, 101, guestAlt, 100, true, true);

        ledger.SettleNewStreams(FileSet(ownerPrimary, guestAlt), FileSet(ownerPrimary, guestAlt)).Should().BeEmpty();
        ledger.Promote(new Dictionary<string, int?>(), FileSet(ownerPrimary, guestAlt), FileSet(ownerPrimary, guestAlt)).Should().BeEmpty();
        File.Exists(guestPlain).Should().BeFalse("the owner is listed, so the guest never moves to a name of its own");

        File.Delete(ownerPrimary);
        var later = NewLedger();
        later.SetCatalogue([101], complete: true);
        later.RegisterAlternative(guestPlain, 101, guestAlt, 100, true, true);
        later.Promote(new Dictionary<string, int?> { [ownerPrimary] = 100 }, FileSet(guestAlt), FileSet(guestAlt)).Should().ContainSingle();
        File.Exists(guestPlain).Should().BeTrue("the guest takes its own primary name, so the next sync has nothing to rename");
        File.Exists(ownerPrimary).Should().BeFalse();
        File.Exists(guestAlt).Should().BeFalse();
    }

    [Fact]
    public void Promote_DoesNotCountAFileOfAnotherProviderAsGone()
    {
        var folder = Path.Combine(_libraryPath, "Shared (2024)");
        var primary = Path.Combine(folder, "Shared (2024).strm");
        var alt = WriteMainStrm(Path.Combine(folder, "Shared (2024) - 100 - p2.strm"), 100);
        var ledger = NewLedger();
        ledger.SetCatalogue([100], complete: true);
        ledger.RegisterAlternative(primary, 100, alt, null, false, true);

        ledger.Promote(new Dictionary<string, int?> { [primary] = StrmOwnerResolver.OtherProvider }, FileSet(alt), FileSet(alt)).Should().BeEmpty();

        File.Exists(primary).Should().BeFalse();
        File.Exists(alt).Should().BeTrue();
    }

    [Fact]
    public void Settle_DecidesNothingFromAnIncompleteCatalogue()
    {
        var folder = Path.Combine(_libraryPath, "Gamma (2024)");
        var primary = WriteMainStrm(Path.Combine(folder, "Gamma (2024).strm"), 5);
        var alt = WriteMainStrm(Path.Combine(folder, "Gamma (2024) - 500.strm"), 500);
        var ledger = NewLedger();
        ledger.SetCatalogue([500], complete: false);
        ledger.NoteCreated(alt);
        ledger.RegisterAlternative(primary, 500, alt, 5, false, true);

        ledger.SettleNewStreams(FileSet(alt), FileSet(alt)).Should().BeEmpty();
        ledger.Promote(new Dictionary<string, int?> { [primary] = 5 }, FileSet(alt), FileSet(alt)).Should().BeEmpty();
        StrmSyncService.ParseMovieStreamId(File.ReadAllText(primary)).Should().Be(5);
    }

    [Fact]
    public void Settle_TakesAGonePrimaryOverInPlaceOnlyForAStreamThatIsNew()
    {
        var folder = Path.Combine(_libraryPath, "Gamma (2024)");
        var primary = WriteMainStrm(Path.Combine(folder, "Gamma (2024).strm"), 5);
        var established = WriteMainStrm(Path.Combine(folder, "Gamma (2024) - 500.strm"), 500);
        var ledger = NewLedger();
        ledger.SetCatalogue([500], complete: true);
        ledger.RegisterAlternative(primary, 500, established, 5, false, true);
        ledger.SettleNewStreams(FileSet(established), FileSet(established)).Should().BeEmpty("an alternative from an earlier run waits for cleanup instead");

        var added = WriteMainStrm(Path.Combine(folder, "Gamma (2024) - 600.strm"), 600);
        var fresh = NewLedger();
        fresh.SetCatalogue([500, 600], complete: true);
        fresh.NoteCreated(added);
        fresh.RegisterAlternative(primary, 600, added, 5, false, true);
        var synced = FileSet(established, added);
        fresh.SettleNewStreams(synced, FileSet(established, added)).Should().ContainSingle().Which.StreamId.Should().Be(600);

        StrmSyncService.ParseMovieStreamId(File.ReadAllText(primary)).Should().Be(600);
        File.Exists(added).Should().BeFalse();
        synced.ContainsKey(primary).Should().BeTrue();
    }

    [Fact]
    public void Settle_GivesAFolderWrittenThisRunToItsLowestStreamId_ButNotAnEstablishedPrimary()
    {
        var folder = Path.Combine(_libraryPath, "Twin (2024)");
        var primary = WriteMainStrm(Path.Combine(folder, "Twin (2024).strm"), 200);
        var alt = WriteMainStrm(Path.Combine(folder, "Twin (2024) - 100.strm"), 100);
        var kept = NewLedger();
        kept.SetCatalogue([100, 200], complete: true);
        kept.NoteCreated(alt);
        kept.RegisterPrimary(primary, 200, established: true);
        kept.RegisterAlternative(primary, 100, alt, 200, false, true);
        kept.SettleNewStreams(FileSet(primary, alt), FileSet(primary, alt)).Should().BeEmpty();

        var ledger = NewLedger();
        ledger.SetCatalogue([100, 200], complete: true);
        ledger.NoteCreated(primary);
        ledger.NoteCreated(alt);
        ledger.RegisterPrimary(primary, 200, established: false);
        ledger.RegisterAlternative(primary, 100, alt, 200, false, true);

        ledger.SettleNewStreams(FileSet(primary, alt), FileSet(primary, alt)).Should().ContainSingle();

        StrmSyncService.ParseMovieStreamId(File.ReadAllText(primary)).Should().Be(100);
        StrmSyncService.ParseMovieStreamId(File.ReadAllText(Path.Combine(folder, "Twin (2024) - 200.strm"))).Should().Be(200);
        File.Exists(alt).Should().BeFalse();
    }

    // The maintainer's first case. The guest of a TMDB group carries another label than the owner, so
    // the file named like the folder never existed. Waiting on that name moved the guest onto it while
    // the owner was still listed, on the first sync after the upgrade and, with cleanup on, on every
    // full sync after it.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AGroupWithDifferentLabels_IsLeftAloneWhileItsOwnerIsListed(bool upgradeFromMain, bool cleanup)
    {
        VodInfoWithTmdbId(100, "42");
        VodInfoWithTmdbId(101, "42");
        var folder = Path.Combine(_libraryPath, "Movies", "Alpha (2024) [tmdbid-42]");
        var ownerFile = Path.Combine(folder, "Alpha (2024) [tmdbid-42] - FHD.strm");
        var guestFile = Path.Combine(folder, "Alpha (2024) [tmdbid-42] - 101.strm");
        if (upgradeFromMain)
        {
            WriteMainStrm(ownerFile, 100);
            WriteMainStrm(guestFile, 101);
        }

        StreamInfo[] Streams() =>
        [
            new StreamInfo { StreamId = 100, Name = "Alpha (2024) - [FHD]", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 101, Name = "Beta (2024)", ContainerExtension = "mp4" },
        ];

        foreach (bool shipped in new[] { false, true, false, true })
        {
            var result = await RunMovieSyncAsync(Streams(), useShippedDefaults: shipped, groupByTmdbId: true, cleanupOrphans: cleanup).ConfigureAwait(true);

            Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                .Should().Equal(Path.GetFileName(guestFile), Path.GetFileName(ownerFile));
            (await StreamIdOfAsync(ownerFile).ConfigureAwait(true)).Should().Be(100);
            (await StreamIdOfAsync(guestFile).ConfigureAwait(true)).Should().Be(101);
            result.MoviesDeleted.Should().Be(0);
        }
    }

    // The same group once the owner is really gone: with cleanup on its file goes and the guest takes
    // that file's place; with cleanup off nothing moves.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AGroupWithDifferentLabels_WhenTheOwnerLeaves_OnlyCleanupHandsOverItsFile(bool cleanup)
    {
        VodInfoWithTmdbId(100, "42");
        VodInfoWithTmdbId(101, "42");
        var folder = Path.Combine(_libraryPath, "Movies", "Alpha (2024) [tmdbid-42]");
        await RunMovieSyncAsync(
            [
                new StreamInfo { StreamId = 100, Name = "Alpha (2024) - [FHD]", ContainerExtension = "mp4" },
                new StreamInfo { StreamId = 101, Name = "Beta (2024)", ContainerExtension = "mp4" },
            ],
            useShippedDefaults: false,
            groupByTmdbId: true).ConfigureAwait(true);

        foreach (bool shipped in new[] { false, true, false })
        {
            await RunMovieSyncAsync(
                [new StreamInfo { StreamId = 101, Name = "Beta (2024)", ContainerExtension = "mp4" }],
                useShippedDefaults: shipped,
                groupByTmdbId: true,
                cleanupOrphans: cleanup).ConfigureAwait(true);

            var files = Directory.GetFiles(folder, "*.strm");
            if (cleanup)
            {
                // One rename, straight to the guest's own primary name, and the same file on every later run.
                files.Select(Path.GetFileName).Should().Equal("Alpha (2024) [tmdbid-42].strm");
                (await StreamIdOfAsync(files[0]).ConfigureAwait(true)).Should().Be(101);
            }
            else
            {
                files.Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
                    .Should().Equal("Alpha (2024) [tmdbid-42] - 101.strm", "Alpha (2024) [tmdbid-42] - FHD.strm");
            }
        }
    }

    // A guest that joins the group in the same sync its owner leaves takes over at once. It goes
    // straight to its own primary name, not into the departed owner's labelled file, so the next
    // sync does not rename it a second time (each rename is a new item in Jellyfin).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewGuestWhoseOwnerLeavesTheSameSync_TakesItsOwnNameOnce(bool cleanup)
    {
        VodInfoWithTmdbId(100, "42");
        VodInfoWithTmdbId(101, "42");
        var folder = Path.Combine(_libraryPath, "Movies", "Alpha (2024) [tmdbid-42]");
        await RunMovieSyncAsync(
            [new StreamInfo { StreamId = 100, Name = "Alpha (2024) - [FHD]", ContainerExtension = "mp4" }],
            useShippedDefaults: false,
            groupByTmdbId: true).ConfigureAwait(true);
        Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).Should().Equal("Alpha (2024) [tmdbid-42] - FHD.strm");

        foreach (bool shipped in new[] { false, true, false })
        {
            await RunMovieSyncAsync(
                [new StreamInfo { StreamId = 101, Name = "Beta (2024)", ContainerExtension = "mp4" }],
                useShippedDefaults: shipped,
                groupByTmdbId: true,
                cleanupOrphans: cleanup).ConfigureAwait(true);

            var files = Directory.GetFiles(folder, "*.strm");
            files.Select(Path.GetFileName).Should().Equal("Alpha (2024) [tmdbid-42].strm");
            (await StreamIdOfAsync(files[0]).ConfigureAwait(true)).Should().Be(101);
        }
    }

    // The maintainer's second case. A provider that renumbers every id leaves each primary naming a
    // stream that no longer exists. Each new stream rewrites it in place, as before alternatives, so
    // no movie gets a second file and cleanup has nothing to block on.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProviderRenumberingItsIds_RewritesEveryPrimaryInPlace(bool cleanup)
    {
        const int Count = 12;
        for (int i = 1; i <= Count; i++)
        {
            WriteMainStrm(Path.Combine(_libraryPath, "Movies", $"Movie {i:00} (2024)", $"Movie {i:00} (2024).strm"), i);
        }

        StreamInfo[] Renumbered() => Enumerable.Range(1, Count)
            .Select(i => new StreamInfo { StreamId = 1000 + i, Name = $"Movie {i:00} (2024)", ContainerExtension = "mp4" })
            .ToArray();

        foreach (bool shipped in new[] { false, true, false })
        {
            var result = await RunMovieSyncAsync(Renumbered(), useShippedDefaults: shipped, cleanupOrphans: cleanup).ConfigureAwait(true);

            for (int i = 1; i <= Count; i++)
            {
                var folder = Path.Combine(_libraryPath, "Movies", $"Movie {i:00} (2024)");
                var files = Directory.GetFiles(folder, "*.strm");
                files.Select(Path.GetFileName).Should().Equal($"Movie {i:00} (2024).strm");
                (await StreamIdOfAsync(files[0]).ConfigureAwait(true)).Should().Be(1000 + i);
            }

            result.OrphanCleanupBlockedByThreshold.Should().BeFalse();
            result.MoviesDeleted.Should().Be(0);
        }
    }

    // Movies arrive in category batches. The stream that owns the primary on disk is only in the second
    // batch, so when the first batch's stream meets that file it cannot yet tell the owner is listed.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APrimaryOnDisk_StaysWithItsStream_WhenThatStreamIsListedInALaterBatch(bool cleanup)
    {
        var folder = Path.Combine(_libraryPath, "Movies", "Gamma (2024)");
        WriteMainStrm(Path.Combine(folder, "Gamma (2024).strm"), 5);
        SetTwoCategories(
            new StreamInfo { StreamId = 500, Name = "Gamma (2024)", ContainerExtension = "mp4", CategoryId = 1 },
            new StreamInfo { StreamId = 5, Name = "Gamma (2024)", ContainerExtension = "mp4", CategoryId = 2 });
        _configure = p => p.CategoryBatchSize = 1;

        foreach (bool shipped in new[] { false, true, false })
        {
            await RunSyncAsync(syncMovies: true, syncSeries: false, shipped, providerCount: 1, proactiveMediaInfo: false, groupByTmdbId: false, cleanup).ConfigureAwait(true);

            (await StreamIdOfAsync(Path.Combine(folder, "Gamma (2024).strm")).ConfigureAwait(true)).Should().Be(5);
            (await StreamIdOfAsync(Path.Combine(folder, "Gamma (2024) - 500.strm")).ConfigureAwait(true)).Should().Be(500);
            Directory.GetFiles(folder, "*.strm").Should().HaveCount(2);
        }
    }

    // A new folder gives the plain name to the lowest stream id, not to whichever stream the sync reached
    // first, also when the two come in different category batches.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheLowestStreamIdGetsThePlainName_EvenWhenItsCategoryComesLater(bool cleanup)
    {
        var folder = Path.Combine(_libraryPath, "Movies", "Twin Movie (2024)");
        SetTwoCategories(
            new StreamInfo { StreamId = 200, Name = "Twin Movie (2024)", ContainerExtension = "mp4", CategoryId = 1 },
            new StreamInfo { StreamId = 100, Name = "Twin Movie (2024)", ContainerExtension = "mp4", CategoryId = 2 });
        _configure = p => p.CategoryBatchSize = 1;

        foreach (bool shipped in new[] { false, true, false })
        {
            await RunSyncAsync(syncMovies: true, syncSeries: false, shipped, providerCount: 1, proactiveMediaInfo: false, groupByTmdbId: false, cleanup).ConfigureAwait(true);

            (await StreamIdOfAsync(Path.Combine(folder, "Twin Movie (2024).strm")).ConfigureAwait(true)).Should().Be(100);
            (await StreamIdOfAsync(Path.Combine(folder, "Twin Movie (2024) - 200.strm")).ConfigureAwait(true)).Should().Be(200);
            Directory.GetFiles(folder, "*.strm").Should().HaveCount(2);
        }
    }

    private void SetTwoCategories(StreamInfo first, StreamInfo second)
    {
        _client.Setup(c => c.GetVodCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "One" }, new() { CategoryId = 2, CategoryName = "Two" } });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo> { first });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo> { second });
    }

    [Fact]
    public async Task TwoStreamsWithDifferentQualityTags_BothWriteAndNothingCollides()
    {
        // The regression guard for #74's actual feature: distinct tags still group into one
        // folder as separate versions, which is what the version dropdown is built on.
        var result = await RunMovieSyncAsync(
            new StreamInfo { StreamId = 100, Name = "Variant Movie (2024) - [FHD]", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "Variant Movie (2024) - [4K]", ContainerExtension = "mp4" })
            .ConfigureAwait(true);

        var movieFolder = Path.Combine(_libraryPath, "Movies", "Variant Movie (2024)");
        Directory.GetFiles(movieFolder, "*.strm").Should().HaveCount(2);
        result.MovieNameCollisions.Should().Be(0);
        result.MoviesCreated.Should().Be(2);
    }

    [Fact]
    public async Task TwoStreamsSharingAVersionTag_AreKeptTheSameWayAsAQualityTag()
    {
        // V1/V2 became version labels in #75, so they share a name exactly like FHD does.
        var result = await RunMovieSyncAsync(
            new StreamInfo { StreamId = 100, Name = "Tagged Movie (2024) - [V1]", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "Tagged Movie (2024) - [V1]", ContainerExtension = "mp4" })
            .ConfigureAwait(true);

        result.MovieNameCollisions.Should().Be(0);
        Directory.GetFiles(Path.Combine(_libraryPath, "Movies", "Tagged Movie (2024)"), "*.strm")
            .Should().HaveCount(2);
    }

    // syncedFiles doubles as the orphan-protection set: the incremental and smart-skip paths bulk-add
    // existing .strm files to it so cleanup leaves them alone. Both default to on, and the first
    // version of this guard reused that dictionary as its collision detector, which would have made
    // "this file exists" indistinguishable from "another stream wrote this". A stale URL would then
    // never be refreshed. These runs use the shipped defaults, unlike the tests above.
    [Fact]
    public async Task DefaultConfigWithIncrementalAndSmartSkip_ReportsNoCollisionsForDistinctMovies()
    {
        StreamInfo[] Streams() =>
        [
            new StreamInfo { StreamId = 100, Name = "Alpha Movie (2024)", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "Beta Movie (2024)", ContainerExtension = "mp4" },
        ];

        var first = await RunMovieSyncAsync(Streams(), useShippedDefaults: true).ConfigureAwait(true);
        first.MovieNameCollisions.Should().Be(0);
        first.MoviesCreated.Should().Be(2);

        // Second run over the same catalogue: the files now exist, so the skip and orphan-protection
        // paths are the ones doing the work. Nothing here is a collision.
        var second = await RunMovieSyncAsync(Streams(), useShippedDefaults: true).ConfigureAwait(true);
        second.MovieNameCollisions.Should().Be(0, "an existing file is not another stream's claim");
        second.MoviesCreated.Should().Be(0);

        Directory.GetFiles(Path.Combine(_libraryPath, "Movies"), "*.strm", SearchOption.AllDirectories)
            .Should().HaveCount(2);
    }

    // The guard sits in the write loop, immediately before the branch that rewrites a STRM whose
    // URL no longer matches. If it treated "this file exists" as a collision, that rewrite would
    // never happen and a provider URL change would leave the library pointing at a dead host.
    [Fact]
    public async Task DefaultConfig_ChangedProviderUrlIsStillRewritten()
    {
        var first = await RunMovieSyncAsync(
            [new StreamInfo { StreamId = 100, Name = "Refresh Movie (2024)", ContainerExtension = "mp4" }],
            useShippedDefaults: true).ConfigureAwait(true);
        first.MoviesCreated.Should().Be(1);

        var strm = Directory.GetFiles(Path.Combine(_libraryPath, "Movies"), "*.strm", SearchOption.AllDirectories).Single();
        (await File.ReadAllTextAsync(strm).ConfigureAwait(true)).Should().EndWith("/100.mp4");

        // Same movie, same name, different container. The URL changes, so the stream counts as
        // modified and reaches the write loop, where the file already exists.
        var second = await RunMovieSyncAsync(
            [new StreamInfo { StreamId = 100, Name = "Refresh Movie (2024)", ContainerExtension = "mkv" }],
            useShippedDefaults: true).ConfigureAwait(true);

        second.MovieNameCollisions.Should().Be(0, "an existing file is not another stream's claim");
        second.MoviesUpdated.Should().Be(1, "a changed provider URL still has to be rewritten");
        (await File.ReadAllTextAsync(strm).ConfigureAwait(true)).Should().EndWith("/100.mkv");
    }

    // Episodes collide on series name plus season and episode number. The wiring is the same two
    // hops as the movie counter (local -> provider result -> global result), and the movie one was
    // missing its second hop while the guard itself worked, so this path needs its own run.
    [Fact]
    public async Task TwoEpisodesSharingSeasonAndEpisodeNumber_WriteOneFileAndCountTheCollision()
    {
        // The episode title is part of the file name, so a collision needs that to match too.
        // Providers routinely send no title at all, which collapses both to "<series> - S01E01.strm".
        var result = await RunSeriesSyncAsync(
            new Episode { EpisodeId = 11, EpisodeNum = 1, Season = 1, Title = string.Empty, ContainerExtension = "mkv" },
            new Episode { EpisodeId = 22, EpisodeNum = 1, Season = 1, Title = string.Empty, ContainerExtension = "mkv" })
            .ConfigureAwait(true);

        result.EpisodeNameCollisions.Should().Be(1);
        result.Errors.Should().Be(0);
        Directory.GetFiles(Path.Combine(_libraryPath, "Series"), "*.strm", SearchOption.AllDirectories)
            .Should().HaveCount(1);
        _log.Should().Contain(l => l.StartsWith("[Warning] STRM name collision for series", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EpisodesWithDistinctNumbers_BothWriteAndNothingCollides()
    {
        var result = await RunSeriesSyncAsync(
            new Episode { EpisodeId = 11, EpisodeNum = 1, Season = 1, Title = "First", ContainerExtension = "mkv" },
            new Episode { EpisodeId = 22, EpisodeNum = 2, Season = 1, Title = "Second", ContainerExtension = "mkv" })
            .ConfigureAwait(true);

        result.EpisodeNameCollisions.Should().Be(0);
        result.EpisodesCreated.Should().Be(2);
        Directory.GetFiles(Path.Combine(_libraryPath, "Series"), "*.strm", SearchOption.AllDirectories)
            .Should().HaveCount(2);
    }

    private async Task<SyncResult> RunSeriesSyncAsync(params Episode[] episodes)
    {
        _client.Setup(c => c.GetSeriesCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Shows" } });
        _client.Setup(c => c.GetSeriesByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Series> { new() { SeriesId = 7, Name = "Collide Show (2024)" } });
        _client.Setup(c => c.GetSeriesStreamsBySeriesAsync(It.IsAny<ConnectionInfo>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesStreamInfo
            {
                Seasons = new List<Season> { new() { SeasonNumber = 1 } },
                Episodes = new Dictionary<int, ICollection<Episode>> { [1] = new List<Episode>(episodes) },
            });

        return await RunSyncAsync(syncMovies: false, syncSeries: true, useShippedDefaults: false).ConfigureAwait(true);
    }

    // Neither comparer is safe on both platforms: case-sensitively "The Matrix" and "THE MATRIX"
    // are two real paths and folding them refuses a legitimate write, case-insensitively they are
    // one file and not folding them lets the second silently overwrite the first. The guard follows
    // the filesystem, so this test asserts what the host actually does rather than one platform's
    // answer, and fails if the two ever disagree.
    [Fact]
    public async Task TitlesDifferingOnlyInCase_BothSurviveWhateverTheFilesystem()
    {
        // Case-sensitively these are two folders; case-insensitively one folder, where the
        // second stream becomes an alternative. Either way nothing is lost or overwritten.
        var result = await RunMovieSyncAsync(
            new StreamInfo { StreamId = 100, Name = "Case Movie (2024)", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "CASE MOVIE (2024)", ContainerExtension = "mp4" })
            .ConfigureAwait(true);

        var written = Directory.GetFiles(Path.Combine(_libraryPath, "Movies"), "*.strm", SearchOption.AllDirectories);
        result.Errors.Should().Be(0);
        result.MovieNameCollisions.Should().Be(0);
        written.Should().HaveCount(2);
    }

    private static bool IsCaseSensitive(string dir)
    {
        var probe = Path.Combine(dir, "CaseProbe.tmp");
        File.WriteAllText(probe, string.Empty);
        try
        {
            return !File.Exists(Path.Combine(dir, "caseprobe.tmp"));
        }
        finally
        {
            File.Delete(probe);
        }
    }

    // Providers are allowed to share a LibraryPath: PluginConfiguration records
    // HasDuplicateLibraryPaths but nothing refuses the config. A per-provider claim set would let
    // the second provider overwrite the first one's file with nothing counted.
    [Fact]
    public async Task TwoProvidersSharingALibraryPath_NeverOverwriteEachOther()
    {
        _client.Setup(c => c.GetVodCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Movies" } });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo>
            {
                new() { StreamId = 100, Name = "Shared Movie (2024)", ContainerExtension = "mp4" },
            });

        var result = await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: false, providerCount: 2)
            .ConfigureAwait(true);

        // The second provider's copy is an alternative next to the first one's file.
        result.MovieNameCollisions.Should().Be(0);
        Directory.GetFiles(Path.Combine(_libraryPath, "Movies", "Shared Movie (2024)"), "*.strm")
            .Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
            .Should().Equal("Shared Movie (2024) - 100 - p2.strm", "Shared Movie (2024).strm");
    }

    // Stream ids are only unique within a provider. Provider 0 and provider 1 both list a stream 100
    // for the same title; whichever leaves, the other one's file must neither be kept as if it were
    // its own nor left without a primary. Each provider only cleans up what its own URLs wrote.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task WhenOneProvidersStreamLeaves_TheOtherProvidersSameNumberedStreamIsNotTakenForIt(int leaving)
    {
        int staying = 1 - leaving;
        StreamInfo SharedMovie() => new() { StreamId = 100, Name = "Shared Movie (2024)", ContainerExtension = "mp4" };
        SetProviderCatalogue(0, SharedMovie());
        SetProviderCatalogue(1, SharedMovie());

        await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: true, providerCount: 2).ConfigureAwait(true);

        var folder = Path.Combine(_libraryPath, "Movies", "Shared Movie (2024)");
        Directory.GetFiles(folder, "*.strm").Should().HaveCount(2, "the second provider's copy is an alternative");

        SetProviderCatalogue(leaving, new StreamInfo { StreamId = 999, Name = "Other Movie (2023)", ContainerExtension = "mp4" });
        SetProviderCatalogue(staying, SharedMovie());
        _configure = p => p.CleanupOrphans = true;

        // The leaving provider removes its own file; the staying one never touches it, because its
        // catalogue cannot say that stream is gone, and the next full sync names its own file like the folder.
        await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: true, providerCount: 2).ConfigureAwait(true);
        await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: false, providerCount: 2).ConfigureAwait(true);

        var remaining = Directory.GetFiles(folder, "*.strm");
        remaining.Select(Path.GetFileName).Should().Equal("Shared Movie (2024).strm");
        (await File.ReadAllTextAsync(remaining[0]).ConfigureAwait(true))
            .Should().StartWith($"http://provider{staying}.test/movie/", "the leaving provider's file is gone and the other one is the primary");
    }

    // Both providers keep listing the same stream and both clean up. Neither provider's file is gone, so
    // after the first sync settles nothing is created or deleted any more, as on main.
    [Fact]
    public async Task TwoProvidersSharingAPath_WithCleanupOn_LeaveEachOthersFilesAloneOverSeveralSyncs()
    {
        StreamInfo SharedMovie() => new() { StreamId = 100, Name = "Shared Movie (2024)", ContainerExtension = "mp4" };
        SetProviderCatalogue(0, SharedMovie());
        SetProviderCatalogue(1, SharedMovie());
        _configure = p => p.CleanupOrphans = true;
        var folder = Path.Combine(_libraryPath, "Movies", "Shared Movie (2024)");

        await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: false, providerCount: 2).ConfigureAwait(true);
        var settled = Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToArray();

        for (int i = 0; i < 3; i++)
        {
            var result = await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: false, providerCount: 2).ConfigureAwait(true);

            result.MoviesCreated.Should().Be(0);
            result.MoviesDeleted.Should().Be(0);
            Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).Should().Equal(settled);
        }
    }

    // Both providers list a stream 100 and the first one also lists a second stream with the same
    // title. Everything is kept: the primary, the first provider's alternative with its bare id,
    // and the second provider's stream 100 under its own tag instead of a refused collision.
    [Fact]
    public async Task SameStreamIdOnTwoProviders_BothAlternativesAreWritten()
    {
        StreamInfo Movie(int id) => new() { StreamId = id, Name = "Shared Movie (2024)", ContainerExtension = "mp4" };
        SetProviderCatalogue(0, Movie(100), Movie(200));
        SetProviderCatalogue(1, Movie(100));

        var result = await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: false, providerCount: 2).ConfigureAwait(true);

        var files = Directory.GetFiles(Path.Combine(_libraryPath, "Movies", "Shared Movie (2024)"), "*.strm")
            .Select(Path.GetFileName).ToArray();
        result.MovieNameCollisions.Should().Be(0);
        files.Should().HaveCount(3);
        files.Should().Contain("Shared Movie (2024) - 100 - p2.strm");
        files.Should().Contain("Shared Movie (2024).strm");
    }

    // A file the second provider wrote before tags existed keeps its name: renaming it would make
    // Jellyfin see a new item and lose its watched state.
    [Fact]
    public async Task AnUntaggedFileOfTheSecondProvider_KeepsItsName()
    {
        var folder = Path.Combine(_libraryPath, "Movies", "Shared Movie (2024)");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "Shared Movie (2024).strm"), "http://provider0.test/movie/u/p/100.mp4").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(folder, "Shared Movie (2024) - 100.strm"), "http://provider1.test/movie/u/p/100.mp4").ConfigureAwait(true);
        StreamInfo Movie() => new() { StreamId = 100, Name = "Shared Movie (2024)", ContainerExtension = "mp4" };
        SetProviderCatalogue(0, Movie());
        SetProviderCatalogue(1, Movie());

        await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: false, providerCount: 2).ConfigureAwait(true);

        Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
            .Should().Equal("Shared Movie (2024) - 100.strm", "Shared Movie (2024).strm");
    }

    // The same file name belongs to the first provider when the URL says so, so it is not taken for
    // the second provider's legacy file.
    [Fact]
    public async Task AnUntaggedFileOfTheFirstProvider_IsNotTakenForTheSecondProvidersOwn()
    {
        var folder = Path.Combine(_libraryPath, "Movies", "Shared Movie (2024)");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "Shared Movie (2024).strm"), "http://provider0.test/movie/u/p/200.mp4").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(folder, "Shared Movie (2024) - 100.strm"), "http://provider0.test/movie/u/p/100.mp4").ConfigureAwait(true);
        StreamInfo Movie(int id) => new() { StreamId = id, Name = "Shared Movie (2024)", ContainerExtension = "mp4" };
        SetProviderCatalogue(0, Movie(200), Movie(100));
        SetProviderCatalogue(1, Movie(100));

        await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: false, providerCount: 2).ConfigureAwait(true);

        var files = Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).ToArray();
        files.Should().HaveCount(3);
        files.Should().Contain("Shared Movie (2024) - 100 - p2.strm");
        (await File.ReadAllTextAsync(Path.Combine(folder, "Shared Movie (2024) - 100.strm")).ConfigureAwait(true))
            .Should().StartWith("http://provider0.test/");
    }

    [Theory]
    [InlineData(null, "Movie - 100.strm")]
    [InlineData("p2", "Movie - 100 - p2.strm")]
    public void AlternativeFileName_AddsTheProviderTagOnlyWhenThereIsOne(string? tag, string expected)
        => StrmSyncService.AlternativeFileName("Movie.strm", 100, tag).Should().Be(expected);

    [Fact]
    public void AlternativeTag_TagsEveryProviderOfASharedPathButTheFirst()
    {
        var first = new ProviderConfig { BaseUrl = "http://a", Username = "u", LibraryPath = "/media" };
        var other = new ProviderConfig { BaseUrl = "http://o", Username = "u", LibraryPath = "/other" };
        var second = new ProviderConfig { BaseUrl = "http://b", Username = "u", LibraryPath = "/media/" };
        var third = new ProviderConfig { BaseUrl = "http://c", Username = "u", LibraryPath = " /MEDIA " };
        var providers = new[] { first, other, second, third };

        StrmSyncService.AlternativeTag(providers, first).Should().BeNull();
        StrmSyncService.AlternativeTag(providers, other).Should().BeNull("it is alone on its path");
        StrmSyncService.AlternativeTag(providers, second).Should().Be("p2");
        StrmSyncService.AlternativeTag(providers, third).Should().Be("p3");

        second.IsEnabled = false;
        StrmSyncService.AlternativeTag(providers, third).Should().Be("p3", "switching a provider off renames nothing");
    }

    [Fact]
    public void AlternativeTag_FindsAProviderThatIsACopyOfAConfiguredOne()
    {
        var first = new ProviderConfig { BaseUrl = "http://a", Username = "u", LibraryPath = "/media" };
        var second = new ProviderConfig { BaseUrl = "http://b", Username = "u", LibraryPath = "/media" };
        var copy = new ProviderConfig { BaseUrl = "http://b", Username = "u", LibraryPath = "/media" };

        StrmSyncService.AlternativeTag(new[] { first, second }, copy).Should().Be("p2");
    }

    [Theory]
    [InlineData("http://a.test/movie/u/p/100.mp4", "http://a.test", true, 100)]
    [InlineData("http://a.test/movie/u/p/100.mp4", "http://a.test/", true, 100)]
    [InlineData("HTTP://A.TEST/movie/u/p/100.mp4", "http://a.test", true, 100)]
    [InlineData("http://b.test/movie/u/p/100.mp4", "http://a.test", true, StrmOwnerResolver.OtherProvider)]
    [InlineData("http://a.test:8080/movie/u/p/100.mp4", "http://a.test", true, StrmOwnerResolver.OtherProvider)]
    [InlineData("http://b.test/movie/u/p/100.mp4", "http://a.test", false, 100)]
    [InlineData("http://d.test/proxy/vod/movie/abc?stream_id=5", "http://a.test", true, null)]
    [InlineData(null, "http://a.test", true, null)]
    public void StrmOwnerResolver_NamesTheProviderOnlyWhenPathsAreShared(string? url, string baseUrl, bool scoped, int? expected)
        => new StrmOwnerResolver(baseUrl, scoped).OwnerOf(url).Should().Be(expected);

    // A BaseUrl configured with a trailing slash ends up in the STRM as ".../movie/" after a double
    // slash. That file is still this provider's (CodeRabbit).
    [Theory]
    [InlineData("http://a.test//movie/u/p/100.mp4", 100)]
    [InlineData("http://a.test/movie/u/p/100.mp4", 100)]
    [InlineData("http://b.test//movie/u/p/100.mp4", StrmOwnerResolver.OtherProvider)]
    [InlineData("http://a.test//movie/v/p/100.mp4", StrmOwnerResolver.OtherProvider)]
    public void StrmOwnerResolver_AcceptsABaseUrlWithATrailingSlash(string url, int expected)
        => new StrmOwnerResolver("http://a.test/", ScopeToProvider: true, Username: "u").OwnerOf(url).Should().Be(expected);

    // Two accounts on one server share the host and differ only in the segment after "/movie/".
    // The password is not compared: changing it must not turn the provider's own files foreign.
    [Theory]
    [InlineData("http://a.test/movie/u/p/100.mp4", "u", 100)]
    [InlineData("http://a.test/movie/u/other-password/100.mp4", "u", 100)]
    [InlineData("http://a.test/movie/v/p/100.mp4", "u", StrmOwnerResolver.OtherProvider)]
    [InlineData("http://a.test/movie/U/p/100.mp4", "u", StrmOwnerResolver.OtherProvider)]
    [InlineData("http://b.test/movie/u/p/100.mp4", "u", StrmOwnerResolver.OtherProvider)]
    [InlineData("http://a.test/movie/100.mp4", "u", 100)]
    [InlineData("http://a.test/movie/v/p/100.mp4", null, 100)]
    [InlineData("http://a.test/movie/v/p/100.mp4", "", 100)]
    [InlineData("http://d.test/proxy/vod/movie/abc?stream_id=5", "u", null)]
    public void StrmOwnerResolver_TellsTwoAccountsOnOneHostApart(string url, string? username, int? expected)
        => new StrmOwnerResolver("http://a.test", true, null, username).OwnerOf(url).Should().Be(expected);

    [Fact]
    public void StrmOwnerResolver_IgnoresTheAccountWhenPathsAreNotShared()
        => new StrmOwnerResolver("http://a.test", false, null, "u").OwnerOf("http://a.test/movie/v/p/100.mp4").Should().Be(100);

    [Theory]
    [InlineData("Movie - 100 - p2.strm", 100, null, "Movie.strm")]
    [InlineData("Movie - 100 - p3.strm", 100, "p2", "Movie.strm")]
    [InlineData("Movie (2024) - [V2] - 100 - p12.strm", 100, "p2", "Movie (2024) - [V2].strm")]
    [InlineData("Movie - 100 - p2.strm", 100, "p2", null)]
    [InlineData("Movie - 100 - P2.strm", 100, "p2", null)]
    [InlineData("Movie - 200 - p2.strm", 100, null, null)]
    [InlineData("Movie - 1100 - p2.strm", 100, null, null)]
    [InlineData("Movie - 100.strm", 100, null, null)]
    [InlineData("Movie - 100 - px.strm", 100, null, null)]
    [InlineData("Movie - 100 - p.strm", 100, null, null)]
    [InlineData(" - 100 - p2.strm", 100, null, null)]
    [InlineData("Movie - 100 - p2.mp4", 100, null, null)]
    public void ObsoleteTaggedPrimaryName_NamesThePrimaryOnlyForAnotherTagOfTheSameStream(string fileName, int streamId, string? currentTag, string? expected)
        => StrmSyncService.ObsoleteTaggedPrimaryName(fileName, streamId, currentTag).Should().Be(expected);

    // Two accounts on one server: the URLs differ only after "/movie/", so the host alone cannot
    // say whose file the primary is. Account v lists the stream the primary file names, and the
    // file is account u's: it stays, and v's copy goes next to it under v's tag.
    [Fact]
    public async Task TwoAccountsOnOneHost_DoNotTakeEachOthersPrimaryForTheirOwn()
    {
        var folder = Path.Combine(_libraryPath, "Movies", "Shared Movie (2024)");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "Shared Movie (2024).strm"), "http://shared.test/movie/u/p/100.mp4").ConfigureAwait(true);
        SetAccountCatalogue("http://shared.test", "u", new StreamInfo { StreamId = 999, Name = "Other Movie (2023)", ContainerExtension = "mp4" });
        SetAccountCatalogue("http://shared.test", "v", new StreamInfo { StreamId = 100, Name = "Shared Movie (2024)", ContainerExtension = "mp4" });
        _configure = p =>
        {
            p.BaseUrl = "http://shared.test";
            p.Username = p.Name == "test0" ? "u" : "v";
        };

        await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: false, providerCount: 2).ConfigureAwait(true);

        (await File.ReadAllTextAsync(Path.Combine(folder, "Shared Movie (2024).strm")).ConfigureAwait(true))
            .Should().Be("http://shared.test/movie/u/p/100.mp4", "the primary is the other account's file");
        (await File.ReadAllTextAsync(Path.Combine(folder, "Shared Movie (2024) - 100 - p2.strm")).ConfigureAwait(true))
            .Should().Be("http://shared.test/movie/v/p/100.mp4");
    }

    // A tag can pass from one provider to the next when a provider ahead of them is removed. The
    // file under the inherited tag is still the departed provider's, and the existing check one
    // line below the tagged path (the untagged one) must apply to it as well.
    [Fact]
    public async Task ATaggedAlternativeOfAnotherProvider_IsNotOverwritten()
    {
        var folder = Path.Combine(_libraryPath, "Movies", "Shared Movie (2024)");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "Shared Movie (2024).strm"), "http://provider0.test/movie/u/p/100.mp4").ConfigureAwait(true);
        var tagged = Path.Combine(folder, "Shared Movie (2024) - 100 - p2.strm");
        await File.WriteAllTextAsync(tagged, "http://gone.test/movie/u/p/100.mp4").ConfigureAwait(true);
        StreamInfo Movie() => new() { StreamId = 100, Name = "Shared Movie (2024)", ContainerExtension = "mp4" };
        SetProviderCatalogue(0, Movie());
        SetProviderCatalogue(1, Movie());

        var result = await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: false, providerCount: 2).ConfigureAwait(true);

        (await File.ReadAllTextAsync(tagged).ConfigureAwait(true)).Should().Be("http://gone.test/movie/u/p/100.mp4");
        result.MovieNameCollisions.Should().Be(1, "the refused overwrite is counted");
        result.FailedItems.Should().ContainSingle(f => f.ItemType == "Movie" && f.ItemId == 100, "the snapshot must leave it out so the next sync retries it");
        Directory.GetFiles(folder, "*.strm").Should().HaveCount(2);
    }

    // The provider that used to be second is first now, so its tag is gone. Its file still says
    // "p2": it is reused, and no second file is written beside it.
    [Fact]
    public async Task AnAlternativeUnderAnObsoleteTag_IsReusedInsteadOfWrittenTwice()
    {
        var folder = Path.Combine(_libraryPath, "Movies", "Shared Movie (2024)");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "Shared Movie (2024).strm"), "http://provider0.test/movie/u/p/200.mp4").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(folder, "Shared Movie (2024) - 100 - p2.strm"), "http://provider0.test/movie/u/p/100.mp4").ConfigureAwait(true);
        StreamInfo Movie(int id) => new() { StreamId = id, Name = "Shared Movie (2024)", ContainerExtension = "mp4" };
        SetProviderCatalogue(0, Movie(200), Movie(100));
        SetProviderCatalogue(1, new StreamInfo { StreamId = 999, Name = "Other Movie (2023)", ContainerExtension = "mp4" });

        await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: false, providerCount: 2).ConfigureAwait(true);

        Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
            .Should().Equal("Shared Movie (2024) - 100 - p2.strm", "Shared Movie (2024).strm");
    }

    // Once the file under the current name exists, the obsolete one is a leftover. The movies are
    // unchanged on the second run, so this goes through the protection of unchanged movies and not
    // through the sync loop.
    [Fact]
    public async Task AnObsoleteTaggedFile_IsCleanedOnceTheCurrentOneExists()
    {
        // Stream 200 holds the primary from the start, so the order the streams are reached in
        // does not decide which one becomes the alternative.
        var folder = Path.Combine(_libraryPath, "Movies", "Shared Movie (2024)");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "Shared Movie (2024).strm"), "http://provider0.test/movie/u/p/200.mp4").ConfigureAwait(true);
        StreamInfo Movie(int id) => new() { StreamId = id, Name = "Shared Movie (2024)", ContainerExtension = "mp4" };
        SetProviderCatalogue(0, Movie(200), Movie(100));
        SetProviderCatalogue(1, new StreamInfo { StreamId = 999, Name = "Other Movie (2023)", ContainerExtension = "mp4" });
        await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: true, providerCount: 2).ConfigureAwait(true);

        Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
            .Should().Equal("Shared Movie (2024) - 100.strm", "Shared Movie (2024).strm");
        await File.WriteAllTextAsync(Path.Combine(folder, "Shared Movie (2024) - 100 - p2.strm"), "http://provider0.test/movie/u/p/100.mp4").ConfigureAwait(true);

        _configure = p => p.CleanupOrphans = p.Name == "test0";
        await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: true, providerCount: 2).ConfigureAwait(true);

        Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
            .Should().Equal("Shared Movie (2024) - 100.strm", "Shared Movie (2024).strm");
    }

    // The same stream's obsolete file is kept while the current name is free, also for an
    // unchanged movie, instead of being protected without ever being registered.
    [Fact]
    public async Task AnObsoleteTaggedFile_IsKeptAsTheAlternativeWhileTheCurrentNameIsFree()
    {
        var folder = Path.Combine(_libraryPath, "Movies", "Shared Movie (2024)");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "Shared Movie (2024).strm"), "http://provider0.test/movie/u/p/200.mp4").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(folder, "Shared Movie (2024) - 100 - p2.strm"), "http://provider0.test/movie/u/p/100.mp4").ConfigureAwait(true);
        StreamInfo Movie(int id) => new() { StreamId = id, Name = "Shared Movie (2024)", ContainerExtension = "mp4" };
        SetProviderCatalogue(0, Movie(200), Movie(100));
        SetProviderCatalogue(1, new StreamInfo { StreamId = 999, Name = "Other Movie (2023)", ContainerExtension = "mp4" });
        await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: true, providerCount: 2).ConfigureAwait(true);

        _configure = p => p.CleanupOrphans = p.Name == "test0";
        await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: true, providerCount: 2).ConfigureAwait(true);

        Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)
            .Should().Equal("Shared Movie (2024) - 100 - p2.strm", "Shared Movie (2024).strm");
    }

    // GitHub #90. Every version of a title (V1/V2/V3) resolves to one folder and therefore to one
    // "<folder>.nfo", while the STRM files keep their version label and stay distinct. So the STRM
    // claim never fires here and the NFO write was unguarded: whichever version finished last won,
    // including a version whose metadata fetch had failed and had nothing to write.

    private const string RaceNfo = "Race Movie (2024).nfo";

    private static StreamInfo RaceVersion(int streamId, string label)
        => new() { StreamId = streamId, Name = $"Race Movie (2024) - [{label}]", ContainerExtension = "mp4" };

    private string RaceNfoPath => Path.Combine(_libraryPath, "Movies", "Race Movie (2024)", RaceNfo);

    private void VodInfoReturns(int streamId, string plot)
        => _client.Setup(c => c.GetVodInfoAsync(It.IsAny<ConnectionInfo>(), streamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VodInfoResponse { Info = new VodInfoDetails { Plot = plot } });

    private void VodInfoFails(int streamId)
        => _client.Setup(c => c.GetVodInfoAsync(It.IsAny<ConnectionInfo>(), streamId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider refused the info call"));

    [Fact]
    public async Task AVersionAddedLaterWhoseFetchFails_DoesNotStripTheExistingNfo()
    {
        VodInfoReturns(100, "The good plot");
        VodInfoFails(200);

        await RunMovieSyncAsync([RaceVersion(100, "V1")], useShippedDefaults: false, proactiveMediaInfo: true)
            .ConfigureAwait(true);
        (await File.ReadAllTextAsync(RaceNfoPath).ConfigureAwait(true))
            .Should().Contain("The good plot", "the first run had working metadata");

        // The provider adds V2 later and its info call fails. V2 is the only stream created this
        // run, so it is the one that reaches the NFO write, with nothing to put in it.
        await RunMovieSyncAsync([RaceVersion(100, "V1"), RaceVersion(200, "V2")], useShippedDefaults: false, proactiveMediaInfo: true)
            .ConfigureAwait(true);

        (await File.ReadAllTextAsync(RaceNfoPath).ConfigureAwait(true))
            .Should().Contain("The good plot", "a failed fetch must not replace an NFO that already had metadata");
        _log.Should().Contain(l => l.StartsWith("[Warning] Keeping the existing NFO", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFailingVersionRunningFirst_StillLeavesTheRichNfo()
    {
        // A plain first-writer-wins claim would lock in the empty NFO here, because the version
        // that cannot fetch anything is the one that takes the path.
        VodInfoFails(100);
        VodInfoReturns(200, "The good plot");

        await RunMovieSyncAsync([RaceVersion(100, "V1"), RaceVersion(200, "V2")], useShippedDefaults: false, proactiveMediaInfo: true)
            .ConfigureAwait(true);

        (await File.ReadAllTextAsync(RaceNfoPath).ConfigureAwait(true))
            .Should().Contain("The good plot", "the version with real metadata has to win regardless of order");
    }

    [Fact]
    public async Task AFailedFetchWithNothingElseKnown_WritesNoNfoAndSaysWhy()
    {
        VodInfoFails(100);

        var result = await RunMovieSyncAsync([RaceVersion(100, "V1")], useShippedDefaults: false, proactiveMediaInfo: true)
            .ConfigureAwait(true);

        // NfoWriter already refuses to write a file with no TMDB id, no media info and no metadata,
        // so the outcome here is no NFO rather than an empty one. What changed is that the reason is
        // now visible instead of being logged at Debug.
        File.Exists(RaceNfoPath).Should().BeFalse("there was nothing to put in it");
        result.Errors.Should().Be(0, "a provider that will not answer the info call is not a sync failure");
        _log.Should().Contain(l => l.StartsWith("[Warning] Failed to fetch VOD info for NFO", StringComparison.Ordinal));
    }
    // GitHub #88. Two differently titled streams the provider reports under one TMDB id have to
    // end up as two versions of one film, not two entries. Metadata lookup is off in this harness,
    // which is the case that matters: grouping keys on the provider's own id, so it has to work
    // without the name-based lookup being enabled.
    [Fact]
    public async Task MoviesSharingAProviderTmdbId_LandInOneFolder()
    {
        VodInfoWithTmdbId(100, "42");
        VodInfoWithTmdbId(200, "42");

        var result = await RunMovieSyncAsync(
            [
                new StreamInfo { StreamId = 100, Name = "Alpha Movie (2024)", ContainerExtension = "mp4" },
                new StreamInfo { StreamId = 200, Name = "Beta Movie (2024)", ContainerExtension = "mp4" },
            ],
            useShippedDefaults: false,
            groupByTmdbId: true).ConfigureAwait(true);

        var grouped = Path.Combine(_libraryPath, "Movies", "Alpha Movie (2024) [tmdbid-42]");
        var written = Directory.GetFiles(grouped, "*.strm").Select(Path.GetFileName).OrderBy(f => f).ToList();

        written.Should().Equal(
            "Alpha Movie (2024) [tmdbid-42] - 200.strm",
            "Alpha Movie (2024) [tmdbid-42].strm");
        Directory.Exists(Path.Combine(_libraryPath, "Movies", "Beta Movie (2024)")).Should().BeFalse();
        result.MovieNameCollisions.Should().Be(0, "sharing a folder is the feature, not a collision");
        result.Errors.Should().Be(0);
    }

    [Fact]
    public async Task WithGroupingOff_TheSameTwoMoviesStayApart()
    {
        VodInfoWithTmdbId(100, "42");
        VodInfoWithTmdbId(200, "42");

        await RunMovieSyncAsync(
            [
                new StreamInfo { StreamId = 100, Name = "Alpha Movie (2024)", ContainerExtension = "mp4" },
                new StreamInfo { StreamId = 200, Name = "Beta Movie (2024)", ContainerExtension = "mp4" },
            ],
            useShippedDefaults: false).ConfigureAwait(true);

        Directory.GetDirectories(Path.Combine(_libraryPath, "Movies")).Should().HaveCount(2);
    }

    // Codex review finding: providers send 0 for "no id". Treating it as an id would collapse
    // every such film into one folder, and then offer that folder for an irreversible merge.
    [Fact]
    public async Task MoviesReportingTmdbIdZero_AreNotGroupedTogether()
    {
        VodInfoWithTmdbId(100, "0");
        VodInfoWithTmdbId(200, "0");

        await RunMovieSyncAsync(
            [
                new StreamInfo { StreamId = 100, Name = "Alpha Movie (2024)", ContainerExtension = "mp4" },
                new StreamInfo { StreamId = 200, Name = "Beta Movie (2024)", ContainerExtension = "mp4" },
            ],
            useShippedDefaults: false,
            groupByTmdbId: true).ConfigureAwait(true);

        Directory.GetDirectories(Path.Combine(_libraryPath, "Movies"))
            .Should().HaveCount(2, "0 means the provider has no id, not that these are the same film");
        Directory.Exists(Path.Combine(_libraryPath, "Movies", "Alpha Movie (2024) [tmdbid-0]"))
            .Should().BeFalse("a folder named after a non-id helps nobody");
    }

    // Codex review finding: a grouped guest lives in the folder its owner named, so the orphan
    // protection that looks up the guest's own base name cannot find it. When the owner leaves the
    // provider's catalogue, cleanup would delete a file the provider still lists.
    [Fact]
    public async Task AGroupedGuestSurvivesCleanup_WhenItsOwnerLeavesTheCatalogue()
    {
        VodInfoWithTmdbId(100, "42");
        VodInfoWithTmdbId(200, "42");

        await RunMovieSyncAsync(
            [
                new StreamInfo { StreamId = 100, Name = "Alpha Movie (2024)", ContainerExtension = "mp4" },
                new StreamInfo { StreamId = 200, Name = "Beta Movie (2024)", ContainerExtension = "mp4" },
            ],
            useShippedDefaults: false,
            groupByTmdbId: true).ConfigureAwait(true);

        var guestFile = Path.Combine(
            _libraryPath, "Movies", "Alpha Movie (2024) [tmdbid-42]", "Alpha Movie (2024) [tmdbid-42] - 200.strm");
        File.Exists(guestFile).Should().BeTrue("the first run has to have produced the grouped file");

        // The provider drops Alpha. Beta is unchanged, so an incremental run skips it entirely and
        // only the orphan protection stands between its file and cleanup.
        await RunMovieSyncAsync(
            [new StreamInfo { StreamId = 200, Name = "Beta Movie (2024)", ContainerExtension = "mp4" }],
            useShippedDefaults: true,
            groupByTmdbId: true,
            cleanupOrphans: true).ConfigureAwait(true);

        // The owner is gone, so the guest takes the folder's name: its file is kept, as the
        // primary, and the departed owner's file is not left behind.
        var folder = Path.Combine(_libraryPath, "Movies", "Alpha Movie (2024) [tmdbid-42]");
        Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).Should().Equal("Alpha Movie (2024) [tmdbid-42].strm");
        (await File.ReadAllTextAsync(Path.Combine(folder, "Alpha Movie (2024) [tmdbid-42].strm")).ConfigureAwait(true))
            .Should().EndWith("/200.mp4", "the provider still lists this stream");
        File.Exists(guestFile).Should().BeFalse("it was renamed to the primary's name");
    }

    // Codex review finding: two streams can share a title and a year. Deciding who owns the folder
    // by comparing names made neither of them a guest, so both claimed the same file name and one
    // was refused, while its identity was still recorded and an incremental run never retried it.
    [Fact]
    public async Task TwoStreamsSharingATitleAndAnId_BothSurviveWhenGrouped()
    {
        VodInfoWithTmdbId(100, "42");
        VodInfoWithTmdbId(200, "42");

        var result = await RunMovieSyncAsync(
            [
                new StreamInfo { StreamId = 100, Name = "Same Movie (2024)", ContainerExtension = "mp4" },
                new StreamInfo { StreamId = 200, Name = "Same Movie (2024)", ContainerExtension = "mp4" },
            ],
            useShippedDefaults: false,
            groupByTmdbId: true).ConfigureAwait(true);

        var folder = Path.Combine(_libraryPath, "Movies", "Same Movie (2024) [tmdbid-42]");
        Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).OrderBy(f => f).Should().Equal(
            "Same Movie (2024) [tmdbid-42] - 200.strm",
            "Same Movie (2024) [tmdbid-42].strm");
        result.MovieNameCollisions.Should().Be(0, "the provider offering the film twice is what grouping is for");
        result.MoviesCreated.Should().Be(2);
    }

    // Codex review finding: on a later run both same-title streams find the shared folder, and
    // without restoring who owns it neither counted as a guest, so both built the plain file name
    // and one was refused as a collision.
    [Fact]
    public async Task TwoStreamsSharingATitle_KeepTheirOwnFilesOnEveryRun()
    {
        VodInfoWithTmdbId(100, "42");
        VodInfoWithTmdbId(200, "42");

        StreamInfo[] Streams() =>
        [
            new StreamInfo { StreamId = 100, Name = "Same Movie (2024)", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "Same Movie (2024)", ContainerExtension = "mp4" },
        ];

        await RunMovieSyncAsync(Streams(), useShippedDefaults: false, groupByTmdbId: true).ConfigureAwait(true);
        var second = await RunMovieSyncAsync(Streams(), useShippedDefaults: false, groupByTmdbId: true).ConfigureAwait(true);

        var folder = Path.Combine(_libraryPath, "Movies", "Same Movie (2024) [tmdbid-42]");
        Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).OrderBy(f => f).Should().Equal(
            "Same Movie (2024) [tmdbid-42] - 200.strm",
            "Same Movie (2024) [tmdbid-42].strm");
        second.MovieNameCollisions.Should().Be(0, "the second run must not refuse a file it wrote itself");
    }

    // Codex review finding: the provider failing to answer for a moment must not dissolve a group
    // that already exists, or the guest goes back to the plain name, collides with its owner and
    // has its record downgraded to unproven.
    [Fact]
    public async Task AnEstablishedGroupSurvivesTheProviderNotAnswering()
    {
        VodInfoWithTmdbId(100, "42");
        VodInfoWithTmdbId(200, "42");

        StreamInfo[] Streams() =>
        [
            new StreamInfo { StreamId = 100, Name = "Same Movie (2024)", ContainerExtension = "mp4" },
            new StreamInfo { StreamId = 200, Name = "Same Movie (2024)", ContainerExtension = "mp4" },
        ];

        await RunMovieSyncAsync(Streams(), useShippedDefaults: false, groupByTmdbId: true).ConfigureAwait(true);

        // The info call starts failing for both streams.
        _client.Setup(c => c.GetVodInfoAsync(It.IsAny<ConnectionInfo>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider unavailable"));

        var second = await RunMovieSyncAsync(Streams(), useShippedDefaults: false, groupByTmdbId: true).ConfigureAwait(true);

        var folder = Path.Combine(_libraryPath, "Movies", "Same Movie (2024) [tmdbid-42]");
        Directory.GetFiles(folder, "*.strm").Select(Path.GetFileName).OrderBy(f => f).Should().Equal(
            "Same Movie (2024) [tmdbid-42] - 200.strm",
            "Same Movie (2024) [tmdbid-42].strm");
        second.MovieNameCollisions.Should().Be(0, "the group the provider confirmed earlier still stands");
    }

    // GitHub #88, the reporter's own two streams. The provider does not answer for the second one,
    // the title search finds the id the first one's provider confirmed, and writing it anyway gave
    // it a folder of its own that no later sync would move.
    [Fact]
    public async Task AStreamTheProviderDidNotAnswerFor_WaitsAndJoinsItsGroupOnTheNextSync()
    {
        VodInfoWithTmdbId(83302, "10882");
        VodInfoNotAnswered(114677);
        TitleSearchFinds(10882);

        var first = await RunTwoCategorySleepingBeautySyncAsync(groupByTmdbId: true).ConfigureAwait(true);

        MovieFolders().Should().Equal("La bella addormentata nel bosco (1959) [tmdbid-10882]");
        first.MoviesDeferredForGrouping.Should().Be(1);
        first.FailedItems.Should().ContainSingle(f => f.ItemId == 114677);
        _log.Should().Contain(l => l.StartsWith("[Warning] 1 movies were not written", StringComparison.Ordinal));

        VodInfoWithTmdbId(114677, "10882");
        var second = await RunTwoCategorySleepingBeautySyncAsync(groupByTmdbId: true).ConfigureAwait(true);

        MovieFolders().Should().Equal("La bella addormentata nel bosco (1959) [tmdbid-10882]");
        Directory.GetFiles(Path.Combine(_libraryPath, "Movies", "La bella addormentata nel bosco (1959) [tmdbid-10882]"), "*.strm")
            .Should().HaveCount(2);
        second.MoviesDeferredForGrouping.Should().Be(0);
    }

    // An answer without an id is an answer: asking again next sync would get the same one, so the
    // film is written on its own as it always was.
    [Fact]
    public async Task AProviderAnswerWithoutAnId_IsWrittenUngroupedAsBefore()
    {
        VodInfoWithTmdbId(83302, "10882");
        _client.Setup(c => c.GetVodInfoAsync(It.IsAny<ConnectionInfo>(), 114677, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VodInfoResponse { Info = new VodInfoDetails() });
        TitleSearchFinds(10882);

        var result = await RunTwoCategorySleepingBeautySyncAsync(groupByTmdbId: true).ConfigureAwait(true);

        MovieFolders().Should().HaveCount(2);
        result.MoviesDeferredForGrouping.Should().Be(0);
    }

    [Fact]
    public async Task WithGroupingOff_AStreamTheProviderDidNotAnswerFor_IsStillWritten()
    {
        VodInfoWithTmdbId(83302, "10882");
        VodInfoNotAnswered(114677);
        TitleSearchFinds(10882);

        var result = await RunTwoCategorySleepingBeautySyncAsync(groupByTmdbId: false).ConfigureAwait(true);

        MovieFolders().Should().HaveCount(2);
        result.MoviesDeferredForGrouping.Should().Be(0);
    }

    // A guest's folder carries its owner's name, so a full sync treats it as new. Deferring it
    // there would leave its file unprotected and cleanup would take it.
    [Fact]
    public async Task AnEstablishedGuestTheProviderStopsAnsweringFor_KeepsItsFileThroughCleanup()
    {
        VodInfoWithTmdbId(83302, "10882");
        VodInfoWithTmdbId(114677, "10882");
        TitleSearchFinds(10882);
        await RunTwoCategorySleepingBeautySyncAsync(groupByTmdbId: true, cleanupOrphans: true, incremental: false).ConfigureAwait(true);

        VodInfoNotAnswered(114677);
        var second = await RunTwoCategorySleepingBeautySyncAsync(groupByTmdbId: true, cleanupOrphans: true, incremental: false).ConfigureAwait(true);

        MovieFolders().Should().Equal("La bella addormentata nel bosco (1959) [tmdbid-10882]");
        Directory.GetFiles(Path.Combine(_libraryPath, "Movies", "La bella addormentata nel bosco (1959) [tmdbid-10882]"), "*.strm")
            .Should().HaveCount(2);
        second.MoviesDeferredForGrouping.Should().Be(0);
    }

    [Fact]
    public async Task RetryFailed_LeavesADeferredMovieForTheNextSync()
    {
        VodInfoWithTmdbId(83302, "10882");
        VodInfoNotAnswered(114677);
        TitleSearchFinds(10882);
        await RunTwoCategorySleepingBeautySyncAsync(groupByTmdbId: true).ConfigureAwait(true);

        var retry = await _lastService!.RetryFailedAsync(CancellationToken.None).ConfigureAwait(true);

        retry.MoviesCreated.Should().Be(0);
        MovieFolders().Should().Equal("La bella addormentata nel bosco (1959) [tmdbid-10882]");
    }

    [Fact]
    public async Task TitleSearch_IsGivenTheProvidersReleaseYearAndOriginalTitle()
    {
        _client.Setup(c => c.GetVodInfoAsync(It.IsAny<ConnectionInfo>(), 501, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VodInfoResponse { Info = new VodInfoDetails { ReleaseDate = "2001-10-26", OriginalName = "Thirteen Ghosts" } });
        TitleSearchFinds(9378);

        await RunMovieSyncAsync(new StreamInfo { StreamId = 501, Name = "13 Geister (2025)", ContainerExtension = "mp4" }).ConfigureAwait(true);

        _lookup.Verify(l => l.LookupMovieTmdbIdAsync("13 Geister", 2025, 2001, "Thirteen Ghosts", It.IsAny<CancellationToken>()), Times.Once());
    }

    // A movie that failed to match before is on disk without an id. A match found now must not
    // move it to a new folder: Jellyfin would treat it as a new item and drop its watched state.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AMovieAlreadyOnDiskWithoutAnId_KeepsItsFolderWhenTheLookupNowMatches(bool useShippedDefaults)
    {
        var existing = Path.Combine(_libraryPath, "Movies", "13 Geister (2025)");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "13 Geister (2025).strm"), "http://old");
        _client.Setup(c => c.GetVodInfoAsync(It.IsAny<ConnectionInfo>(), 501, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VodInfoResponse { Info = new VodInfoDetails { ReleaseDate = "2001-10-26", OriginalName = "Thirteen Ghosts" } });
        TitleSearchFinds(9378);

        await RunMovieSyncAsync(
            new[] { new StreamInfo { StreamId = 501, Name = "13 Geister (2025)", ContainerExtension = "mp4" } },
            useShippedDefaults).ConfigureAwait(true);

        MovieFolders().Select(Path.GetFileName).Should().Equal("13 Geister (2025)");
    }

    [Fact]
    public async Task ANewMovie_GetsTheIdTheLookupFound()
    {
        _client.Setup(c => c.GetVodInfoAsync(It.IsAny<ConnectionInfo>(), 501, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VodInfoResponse { Info = new VodInfoDetails { ReleaseDate = "2001-10-26" } });
        TitleSearchFinds(9378);

        await RunMovieSyncAsync(new StreamInfo { StreamId = 501, Name = "13 Geister (2025)", ContainerExtension = "mp4" }).ConfigureAwait(true);

        MovieFolders().Select(Path.GetFileName).Should().ContainSingle().Which.Should().Contain("tmdbid-9378");
    }

    // === Folder layout change (the idea from andreadegiovine's feat-db branch) ===

    private const string KidsAndOther = "Kids=1\nOther=2";

    private void MovieCatalogue(IEnumerable<int> kids, IEnumerable<int> other)
    {
        static List<StreamInfo> Films(IEnumerable<int> ids, int category) => ids
            .Select(i => new StreamInfo { StreamId = 700 + i, Name = $"Film {i} (2001)", ContainerExtension = "mp4", CategoryId = category })
            .ToList();
        _client.Setup(c => c.GetVodCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Kids" }, new() { CategoryId = 2, CategoryName = "Other" } });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>())).ReturnsAsync(Films(kids, 1));
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 2, It.IsAny<CancellationToken>())).ReturnsAsync(Films(other, 2));
    }

    private Task<SyncResult> RunLayoutSyncAsync(string mode, string mappings, bool syncMovies = true, bool syncSeries = false, string? seriesMode = null, string? seriesMappings = null)
    {
        _configure = p =>
        {
            p.MovieFolderMode = mode;
            p.MovieFolderMappings = mappings;
            p.SeriesFolderMode = seriesMode ?? mode;
            p.SeriesFolderMappings = seriesMappings ?? mappings;
        };
        return RunSyncAsync(syncMovies, syncSeries, useShippedDefaults: true, providerCount: 1, proactiveMediaInfo: false, groupByTmdbId: false, cleanupOrphans: true);
    }

    private string[] StrmFiles(string root)
        => Directory.GetFiles(Path.Combine(_libraryPath, root), "*.strm", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Path.Combine(_libraryPath, root), f))
            .ToArray();

    [Fact]
    public async Task SwitchingMultipleToSingle_RemovesTheOldCopiesInsteadOfDuplicatingTheLibrary()
    {
        MovieCatalogue(Enumerable.Range(1, 20), Enumerable.Range(21, 10));
        await RunLayoutSyncAsync("Multiple", KidsAndOther).ConfigureAwait(true);
        StrmFiles("Movies").Should().HaveCount(30).And.OnlyContain(f => f.StartsWith("Kids", StringComparison.Ordinal) || f.StartsWith("Other", StringComparison.Ordinal));

        var result = await RunLayoutSyncAsync("Single", string.Empty).ConfigureAwait(true);

        StrmFiles("Movies").Should().HaveCount(30).And.NotContain(f => f.StartsWith("Kids", StringComparison.Ordinal) || f.StartsWith("Other", StringComparison.Ordinal));
        result.OrphanCleanupBlockedByThreshold.Should().BeFalse();
        Directory.Exists(Path.Combine(_libraryPath, "Movies", "Kids")).Should().BeFalse();
    }

    [Fact]
    public async Task ALayoutChangeDoesNotLetRealLossesPastTheThreshold()
    {
        MovieCatalogue(Enumerable.Range(1, 20), Enumerable.Range(21, 10));
        await RunLayoutSyncAsync("Multiple", KidsAndOther).ConfigureAwait(true);

        // Same switch, but the provider also dropped ten films: 10 of 30 is over the 20% limit.
        MovieCatalogue(Enumerable.Range(1, 20), Enumerable.Empty<int>());
        var result = await RunLayoutSyncAsync("Single", string.Empty).ConfigureAwait(true);

        var files = StrmFiles("Movies");
        files.Where(f => f.StartsWith("Kids", StringComparison.Ordinal)).Should().BeEmpty("the twenty moved films are copies");
        files.Where(f => f.StartsWith("Other", StringComparison.Ordinal)).Should().HaveCount(10, "the ten lost films stay behind the threshold");
        result.OrphanCleanupBlockedByThreshold.Should().BeTrue();
        result.MovieOrphansSkipped.Should().Be(10);
    }

    [Fact]
    public async Task AProviderRenamingEveryTitle_IsStillHeldBackByTheThreshold()
    {
        // Review of #128: moves were matched on the stream URL alone, so a provider renaming its
        // titles made every original a "copy" and the whole library was deleted past the limit.
        MovieCatalogue(Enumerable.Range(1, 30), Enumerable.Empty<int>());
        await RunLayoutSyncAsync("Single", string.Empty).ConfigureAwait(true);

        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, 30)
                .Select(i => new StreamInfo { StreamId = 700 + i, Name = $"NEW Film {i} (2001)", ContainerExtension = "mp4", CategoryId = 1 })
                .ToList());
        var result = await RunLayoutSyncAsync("Single", string.Empty).ConfigureAwait(true);

        StrmFiles("Movies").Where(f => f.StartsWith("Film ", StringComparison.Ordinal)).Should().HaveCount(30, "the originals keep their watched status");
        result.OrphanCleanupBlockedByThreshold.Should().BeTrue();
        result.OrphansRelocated.Should().Be(0);
    }

    [Fact]
    public async Task AnEmptySeriesSelection_DoesNotStopMovieCopiesBeingRemoved()
    {
        // Review of #128: the empty-selection guard covered both types at once.
        MovieCatalogue(Enumerable.Range(1, 20), Enumerable.Range(21, 10));
        SeriesCatalogue(12);
        await RunLayoutSyncAsync("Multiple", KidsAndOther, syncSeries: true, seriesMode: "Multiple", seriesMappings: "Kids=1").ConfigureAwait(true);

        // Movies switch to Single; the series mapping is cleared, so series select nothing.
        var result = await RunLayoutSyncAsync("Single", string.Empty, syncSeries: true, seriesMode: "Multiple", seriesMappings: string.Empty).ConfigureAwait(true);

        StrmFiles("Movies").Should().HaveCount(30).And.NotContain(f => f.StartsWith("Kids", StringComparison.Ordinal) || f.StartsWith("Other", StringComparison.Ordinal));
        StrmFiles("Series").Should().HaveCount(12, "an empty selection still protects the series");
        result.OrphansRelocated.Should().Be(30);
    }

    private void SeriesCatalogue(int episodeCount)
    {
        var episodes = Enumerable.Range(1, episodeCount).Select(i => new Episode { EpisodeId = 900 + i, EpisodeNum = i, Title = $"Ep {i}", ContainerExtension = "mkv" }).ToList();
        _client.Setup(c => c.GetSeriesCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Kids" } });
        _client.Setup(c => c.GetSeriesByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Series> { new() { SeriesId = 7, Name = "Moving Show (2024)", CategoryId = 1 } });
        _client.Setup(c => c.GetSeriesStreamsBySeriesAsync(It.IsAny<ConnectionInfo>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesStreamInfo
            {
                Seasons = new List<Season> { new() { SeasonNumber = 1 } },
                Episodes = new Dictionary<int, ICollection<Episode>> { [1] = episodes },
            });
    }

    [Fact]
    public async Task SwitchingSeriesLayout_RemovesTheOldEpisodeCopies()
    {
        var episodes = Enumerable.Range(1, 12).Select(i => new Episode { EpisodeId = 900 + i, EpisodeNum = i, Title = $"Ep {i}", ContainerExtension = "mkv" }).ToList();
        _client.Setup(c => c.GetSeriesCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Kids" } });
        _client.Setup(c => c.GetSeriesByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Series> { new() { SeriesId = 7, Name = "Moving Show (2024)", CategoryId = 1 } });
        _client.Setup(c => c.GetSeriesStreamsBySeriesAsync(It.IsAny<ConnectionInfo>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeriesStreamInfo
            {
                Seasons = new List<Season> { new() { SeasonNumber = 1 } },
                Episodes = new Dictionary<int, ICollection<Episode>> { [1] = episodes },
            });

        await RunLayoutSyncAsync("Multiple", "Kids=1", syncMovies: false, syncSeries: true).ConfigureAwait(true);
        StrmFiles("Series").Should().HaveCount(12).And.OnlyContain(f => f.StartsWith("Kids", StringComparison.Ordinal));

        var result = await RunLayoutSyncAsync("Single", string.Empty, syncMovies: false, syncSeries: true).ConfigureAwait(true);

        StrmFiles("Series").Should().HaveCount(12).And.NotContain(f => f.StartsWith("Kids", StringComparison.Ordinal));
        result.OrphanCleanupBlockedByThreshold.Should().BeFalse();
    }

    private void SetProviderCatalogue(int providerIndex, params StreamInfo[] streams)
    {
        string baseUrl = $"http://provider{providerIndex}.test";
        _client.Setup(c => c.GetVodCategoryAsync(It.Is<ConnectionInfo>(i => i.BaseUrl == baseUrl), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Movies" } });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.Is<ConnectionInfo>(i => i.BaseUrl == baseUrl), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo>(streams));
    }

    private void SetAccountCatalogue(string baseUrl, string userName, params StreamInfo[] streams)
    {
        _client.Setup(c => c.GetVodCategoryAsync(It.Is<ConnectionInfo>(i => i.BaseUrl == baseUrl && i.UserName == userName), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Movies" } });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.Is<ConnectionInfo>(i => i.BaseUrl == baseUrl && i.UserName == userName), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo>(streams));
    }

    private string[] MovieFolders()
        => Directory.GetDirectories(Path.Combine(_libraryPath, "Movies")).Select(d => Path.GetFileName(d)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private void VodInfoNotAnswered(int streamId)
        => _client.Setup(c => c.GetVodInfoAsync(It.IsAny<ConnectionInfo>(), streamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((VodInfoResponse?)null);

    private void TitleSearchFinds(int tmdbId)
    {
        _metadataLookup = true;
        _lookup.Setup(l => l.LookupMovieTmdbIdAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tmdbId);
    }

    private Task<SyncResult> RunTwoCategorySleepingBeautySyncAsync(bool groupByTmdbId, bool cleanupOrphans = false, bool incremental = true)
    {
        _client.Setup(c => c.GetVodCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 105, CategoryName = "Animazione" }, new() { CategoryId = 296, CategoryName = "Disney" } });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 105, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo> { new() { StreamId = 83302, Name = "La bella addormentata nel bosco (1959)", ContainerExtension = "mkv", CategoryId = 105 } });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 296, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo> { new() { StreamId = 114677, Name = "La Bella Addormentata Nel Bosco - ", ContainerExtension = "mkv", CategoryId = 296 } });

        return RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults: incremental, providerCount: 1, proactiveMediaInfo: false, groupByTmdbId, cleanupOrphans);
    }

    private void VodInfoWithTmdbId(int streamId, string tmdbId)
        => _client.Setup(c => c.GetVodInfoAsync(It.IsAny<ConnectionInfo>(), streamId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VodInfoResponse { Info = new VodInfoDetails { TmdbId = tmdbId } });

    private Task<SyncResult> RunMovieSyncAsync(params StreamInfo[] streams)
        => RunMovieSyncAsync(streams, useShippedDefaults: false);

    private async Task<SyncResult> RunMovieSyncAsync(StreamInfo[] streams, bool useShippedDefaults, bool proactiveMediaInfo = false, bool groupByTmdbId = false, bool cleanupOrphans = false)
    {
        _client.Setup(c => c.GetVodCategoryAsync(It.IsAny<ConnectionInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Category> { new() { CategoryId = 1, CategoryName = "Movies" } });
        _client.Setup(c => c.GetVodStreamsByCategoryAsync(It.IsAny<ConnectionInfo>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<StreamInfo>(streams));

        return await RunSyncAsync(syncMovies: true, syncSeries: false, useShippedDefaults, providerCount: 1, proactiveMediaInfo, groupByTmdbId, cleanupOrphans).ConfigureAwait(true);
    }

    private Task<SyncResult> RunSyncAsync(bool syncMovies, bool syncSeries, bool useShippedDefaults)
        => RunSyncAsync(syncMovies, syncSeries, useShippedDefaults, providerCount: 1);

    private Task<SyncResult> RunSyncAsync(bool syncMovies, bool syncSeries, bool useShippedDefaults, int providerCount)
        => RunSyncAsync(syncMovies, syncSeries, useShippedDefaults, providerCount, proactiveMediaInfo: false, groupByTmdbId: false, cleanupOrphans: false);

    private async Task<SyncResult> RunSyncAsync(bool syncMovies, bool syncSeries, bool useShippedDefaults, int providerCount, bool proactiveMediaInfo, bool groupByTmdbId, bool cleanupOrphans)
    {
        var appPaths = new Mock<IServerApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(_libraryPath);
        appPaths.Setup(p => p.DataPath).Returns(_libraryPath);
        appPaths.Setup(p => p.ProgramDataPath).Returns(_libraryPath);
        appPaths.Setup(p => p.CachePath).Returns(_libraryPath);
        appPaths.Setup(p => p.TempDirectory).Returns(_libraryPath);
        appPaths.Setup(p => p.PluginsPath).Returns(_libraryPath);

        // Constructing the plugin publishes Plugin.Instance, which SyncAsync reads.
        var plugin = new Plugin(appPaths.Object, new RealXmlSerializer());
        plugin.Configuration.Providers = Enumerable.Range(0, providerCount).Select(i => new ProviderConfig
        {
            Name = $"test{i}",
            BaseUrl = $"http://provider{i}.test",
            Username = "u",
            Password = "p",
            LibraryPath = _libraryPath,
            SyncMovies = syncMovies,
            SyncSeries = syncSeries,
            CleanupOrphans = cleanupOrphans,
            EnableIncrementalSync = useShippedDefaults,
            SmartSkipExisting = useShippedDefaults,
            DownloadArtworkForUnmatched = false,
            EnableProactiveMediaInfo = proactiveMediaInfo,
            GroupMoviesByTmdbId = groupByTmdbId,
            SyncParallelism = 1,
        }).ToList();
        foreach (var p in plugin.Configuration.Providers)
        {
            _configure?.Invoke(p);
        }

        plugin.Configuration.EnableLiveTv = false;
        plugin.Configuration.EnableMetadataLookup = _metadataLookup;

        var service = new StrmSyncService(
            _client.Object,
            new Mock<IDispatcharrClient>().Object,
            new Mock<ILibraryManager>().Object,
            _lookup.Object,
            new SnapshotService(appPaths.Object, NullLogger<SnapshotService>.Instance),
            new LiveTvService(_client.Object, new Mock<IDispatcharrClient>().Object, appPaths.Object, MockAppHost(), NullLogger<LiveTvService>.Instance),
            appPaths.Object,
            new ListLogger(_log));

        _lastService = service;
        return await service.SyncAsync(CancellationToken.None).ConfigureAwait(true);
    }

    private static IServerApplicationHost MockAppHost()
    {
        var host = new Mock<IServerApplicationHost>();
        host.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
            .Returns("http://127.0.0.1:8096");
        return host.Object;
    }
}
