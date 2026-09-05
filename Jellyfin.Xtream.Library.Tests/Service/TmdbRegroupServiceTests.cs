// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #88. The regroup action moves files, so it gets a real filesystem in its tests.

using System;
using System.IO;
using System.Threading;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class TmdbRegroupServiceTests : IDisposable
{
    private readonly string _moviesPath;
    private readonly TmdbRegroupService _service = new(NullLogger<TmdbRegroupService>.Instance);

    public TmdbRegroupServiceTests()
    {
        _moviesPath = Path.Combine(Path.GetTempPath(), "xtream-regroup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_moviesPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_moviesPath))
            {
                Directory.Delete(_moviesPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; a leftover temp directory must not fail the suite.
        }

        GC.SuppressFinalize(this);
    }

    private string Folder(string name, params (string File, string Content)[] files)
    {
        string path = Path.Combine(_moviesPath, name);
        Directory.CreateDirectory(path);
        foreach (var (file, content) in files)
        {
            File.WriteAllText(Path.Combine(path, file), content);
        }

        return path;
    }

    private static GroupingPlan PlanFor(string target, params string[] sources)
        => new([new GroupMove(42, target, sources)], 0);

    [Fact]
    public void BothVersionsSurvive_WhenTheirStrmFilesShareAName()
    {
        // The whole point of merging: two copies of one film. Overwriting either loses a stream.
        Folder("The Film (2024)", ("The Film (2024).strm", "http://host/100.mp4"));
        Folder("The Film 4K (2024)", ("The Film 4K (2024).strm", "http://host/200.mp4"));

        // The source file is named after its own folder, so it does not collide here.
        var result = _service.Apply(
            PlanFor("The Film (2024)", "The Film 4K (2024)"), _moviesPath, dryRun: false, CancellationToken.None);

        var target = Path.Combine(_moviesPath, "The Film (2024)");
        Directory.GetFiles(target, "*.strm").Should().HaveCount(2);
        result.FilesMoved.Should().Be(1);
        result.FoldersRemoved.Should().Be(1);
        Directory.Exists(Path.Combine(_moviesPath, "The Film 4K (2024)")).Should().BeFalse();
    }

    [Fact]
    public void AColldingStrmIsRenamed_NotOverwritten()
    {
        Folder("Target", ("Movie.strm", "http://host/100.mp4"));
        Folder("Source", ("Movie.strm", "http://host/200.mp4"));

        var result = _service.Apply(PlanFor("Target", "Source"), _moviesPath, dryRun: false, CancellationToken.None);

        var target = Path.Combine(_moviesPath, "Target");
        File.ReadAllText(Path.Combine(target, "Movie.strm")).Should().Be("http://host/100.mp4");
        File.ReadAllText(Path.Combine(target, "Movie - 2.strm")).Should().Be("http://host/200.mp4");
        result.FilesRenamed.Should().Be(1);
    }

    [Fact]
    public void DuplicateMetadataIsLeftBehind_RatherThanPilingUp()
    {
        // A second NFO or poster is the same information again, so the target's copy wins. Keeping
        // both would give Jellyfin two NFOs for one film.
        Folder("Target", ("Movie.nfo", "target"), ("poster.jpg", "target"));
        Folder("Source", ("Movie.nfo", "source"), ("poster.jpg", "source"), ("Movie.strm", "url"));

        var result = _service.Apply(PlanFor("Target", "Source"), _moviesPath, dryRun: false, CancellationToken.None);

        var target = Path.Combine(_moviesPath, "Target");
        File.ReadAllText(Path.Combine(target, "Movie.nfo")).Should().Be("target");
        File.ReadAllText(Path.Combine(target, "poster.jpg")).Should().Be("target");
        result.FilesSkipped.Should().Be(2);
        result.FilesMoved.Should().Be(1);

        // The source folder still holds the two skipped files, so it is not removed.
        Directory.Exists(Path.Combine(_moviesPath, "Source")).Should().BeTrue();
        result.FoldersRemoved.Should().Be(0);
    }

    [Fact]
    public void ADryRunReportsTheSameNumbersAndTouchesNothing()
    {
        Folder("Target", ("Movie.strm", "http://host/100.mp4"));
        Folder("Source", ("Other.strm", "http://host/200.mp4"));

        var preview = _service.Apply(PlanFor("Target", "Source"), _moviesPath, dryRun: true, CancellationToken.None);

        preview.DryRun.Should().BeTrue();
        preview.FilesMoved.Should().Be(1);
        File.Exists(Path.Combine(_moviesPath, "Source", "Other.strm")).Should().BeTrue("nothing may move on a preview");
        Directory.GetFiles(Path.Combine(_moviesPath, "Target")).Should().HaveCount(1);
    }

    [Fact]
    public void AMissingTargetFolder_SkipsTheGroupInsteadOfScatteringFiles()
    {
        Folder("Source", ("Movie.strm", "http://host/200.mp4"));

        var result = _service.Apply(
            PlanFor("Renamed By Hand", "Source"), _moviesPath, dryRun: false, CancellationToken.None);

        result.GroupsSkipped.Should().Be(1);
        result.FilesMoved.Should().Be(0);
        File.Exists(Path.Combine(_moviesPath, "Source", "Movie.strm")).Should().BeTrue();
    }

    [Fact]
    public void AnAlreadyMovedSourceIsNotAnError()
    {
        // Running the action twice must be safe.
        Folder("Target", ("Movie.strm", "http://host/100.mp4"));

        var result = _service.Apply(PlanFor("Target", "Gone"), _moviesPath, dryRun: false, CancellationToken.None);

        result.GroupsMerged.Should().Be(0);
        result.FilesMoved.Should().Be(0);
    }
}
