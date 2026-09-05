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
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Moves movie folders that hold the same film into one folder (GitHub #88).
/// <para>
/// This only ever runs when the user asks for it. A sync never moves anything already on disk,
/// because a rename costs watched status and artwork if the old folder is then cleaned up as an
/// orphan, and that is not something a scheduled task should decide.
/// </para>
/// </summary>
/// <param name="logger">Logger.</param>
public class TmdbRegroupService(ILogger<TmdbRegroupService> logger)
{
    private readonly ILogger<TmdbRegroupService> _logger = logger;

    /// <summary>
    /// Carries out a grouping plan, or works out what it would do.
    /// </summary>
    /// <param name="plan">Plan from <see cref="TmdbGrouping.Plan"/>.</param>
    /// <param name="moviesPath">Root of the movie library.</param>
    /// <param name="dryRun">True reports what would happen and touches nothing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was done, or would be done.</returns>
    public RegroupResult Apply(GroupingPlan plan, string moviesPath, bool dryRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var result = new RegroupResult
        {
            DryRun = dryRun,
            ItemsWithUnprovenId = plan.ItemsWithUnprovenId,
        };

        foreach (var move in plan.Moves)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string targetDirectory = Path.Combine(moviesPath, move.TargetFolder);
            if (!Directory.Exists(targetDirectory))
            {
                // The folder the plan wants to keep is gone, most likely renamed by hand. Merging
                // into a folder that does not exist would scatter files, so leave this group alone.
                _logger.LogWarning(
                    "Skipping TMDB group {TmdbId}: target folder {Folder} does not exist",
                    move.TmdbId,
                    move.TargetFolder);
                result.GroupsSkipped++;
                continue;
            }

            bool mergedAnything = false;
            foreach (var sourceFolder in move.SourceFolders)
            {
                string sourceDirectory = Path.Combine(moviesPath, sourceFolder);
                if (!Directory.Exists(sourceDirectory))
                {
                    continue;
                }

                MergeFolder(sourceDirectory, targetDirectory, dryRun, result);
                mergedAnything = true;
            }

            if (mergedAnything)
            {
                result.GroupsMerged++;
            }
        }

        _logger.LogInformation(
            "Regroup {Mode}: {Groups} groups, {Folders} folders, {Files} files moved, {Skipped} files left alone",
            dryRun ? "preview" : "applied",
            result.GroupsMerged,
            result.FoldersMerged,
            result.FilesMoved,
            result.FilesSkipped);

        return result;
    }

    private void MergeFolder(string sourceDirectory, string targetDirectory, bool dryRun, RegroupResult result)
    {
        foreach (string file in Directory.GetFiles(sourceDirectory))
        {
            string name = Path.GetFileName(file);
            string destination = Path.Combine(targetDirectory, name);

            if (!File.Exists(destination))
            {
                if (!dryRun)
                {
                    File.Move(file, destination);
                }

                result.FilesMoved++;
                continue;
            }

            // A name that is already taken means something different for a stream than for the
            // metadata around it. Two STRM files are two versions of the film and both have to
            // survive, so the incoming one is renamed. An NFO, poster or fanart is a second copy of
            // the same information, so the one already in the target folder is kept.
            if (!string.Equals(Path.GetExtension(name), ".strm", StringComparison.OrdinalIgnoreCase))
            {
                result.FilesSkipped++;
                continue;
            }

            string renamed = FindFreeName(targetDirectory, name);
            if (!dryRun)
            {
                File.Move(file, Path.Combine(targetDirectory, renamed));
            }

            result.FilesMoved++;
            result.FilesRenamed++;
        }

        result.FoldersMerged++;

        // Leave anything that is not empty, including a folder holding subdirectories: an empty
        // folder is safe to remove, a surprise is not.
        if (!dryRun
            && Directory.GetFiles(sourceDirectory).Length == 0
            && Directory.GetDirectories(sourceDirectory).Length == 0)
        {
            Directory.Delete(sourceDirectory);
            result.FoldersRemoved++;
        }
    }

    private static string FindFreeName(string directory, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        for (int suffix = 2; suffix < 1000; suffix++)
        {
            string candidate = string.Create(CultureInfo.InvariantCulture, $"{stem} - {suffix}{extension}");
            if (!File.Exists(Path.Combine(directory, candidate)))
            {
                return candidate;
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{stem} - {Guid.NewGuid():N}{extension}");
    }
}

/// <summary>
/// Outcome of a regroup run.
/// </summary>
public class RegroupResult
{
    /// <summary>Gets or sets a value indicating whether nothing was actually touched.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets the number of TMDB groups merged.</summary>
    public int GroupsMerged { get; set; }

    /// <summary>Gets or sets the number of groups left alone because their target folder was missing.</summary>
    public int GroupsSkipped { get; set; }

    /// <summary>Gets or sets the number of folders emptied into a target.</summary>
    public int FoldersMerged { get; set; }

    /// <summary>Gets or sets the number of folders removed once empty.</summary>
    public int FoldersRemoved { get; set; }

    /// <summary>Gets or sets the number of files moved.</summary>
    public int FilesMoved { get; set; }

    /// <summary>Gets or sets how many of those had to be renamed to avoid overwriting a file.</summary>
    public int FilesRenamed { get; set; }

    /// <summary>Gets or sets the number of files left behind because the target already had them.</summary>
    public int FilesSkipped { get; set; }

    /// <summary>
    /// Gets or sets the number of items whose id may not be grouped yet because it was guessed by a
    /// name lookup or read off a folder name. A full sync re-resolves those.
    /// </summary>
    public int ItemsWithUnprovenId { get; set; }
}
