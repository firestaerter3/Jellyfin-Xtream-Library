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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Remembers, for one provider's movie sync, which stream holds the primary STRM of each movie
/// folder and which streams sit next to it as alternatives, and settles the roles that cannot be
/// known while the movies are still arriving in category batches.
/// </summary>
/// <remarks>
/// Three decisions are made here, all from facts the catalogue only has once the last batch is in:
/// <list type="number">
/// <item>A stream that finds another stream's primary on disk writes an alternative at first. Once
/// the catalogue is complete, if that other stream is no longer listed, the new stream takes the
/// primary file over in place, which is what a sync did before alternatives existed.</item>
/// <item>Two streams that wrote a new folder in the same run: the lowest stream id gets the plain
/// name, whichever of them got there first.</item>
/// <item>After orphan cleanup: a primary that cleanup deleted, whose stream is no longer listed, is
/// replaced by the alternative waiting on it.</item>
/// </list>
/// Nothing here renames a file whose stream is still listed.
/// </remarks>
internal sealed class MovieRoleLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _created = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, int?> _ownerOfFile;
    private readonly Func<string, string, int, string?> _alternativePathFor;
    private HashSet<int>? _catalogue;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieRoleLedger"/> class.
    /// </summary>
    /// <param name="ownerOfFile">
    /// Reads a STRM file and names the stream its URL belongs to: the stream id, <see cref="StrmOwnerResolver.OtherProvider"/>
    /// for another provider's file, or null when the URL names no stream.
    /// </param>
    /// <param name="alternativePathFor">
    /// Where a stream's alternative file goes, given the folder, the plain file name and the stream id. Null when
    /// the name is held by another stream's file.
    /// </param>
    internal MovieRoleLedger(Func<string, int?> ownerOfFile, Func<string, string, int, string?> alternativePathFor)
    {
        _ownerOfFile = ownerOfFile;
        _alternativePathFor = alternativePathFor;
    }

    /// <summary>
    /// Whether a stream id is known to be listed. False until the catalogue is complete, so nothing
    /// is decided from a partial one.
    /// </summary>
    /// <param name="streamId">The stream id.</param>
    /// <returns>True when the catalogue is complete and lists the stream.</returns>
    internal bool IsListed(int streamId)
    {
        lock (_gate)
        {
            return _catalogue is not null && _catalogue.Contains(streamId);
        }
    }

    /// <summary>
    /// Tells the ledger which streams the provider lists. A catalogue that is not complete (a
    /// category could not be fetched) leaves every decision that depends on a stream being gone
    /// undone: a stream missing from it may simply be in the category that failed.
    /// </summary>
    /// <param name="listedStreamIds">Every stream id this run saw.</param>
    /// <param name="complete">Whether every category was fetched.</param>
    internal void SetCatalogue(IEnumerable<int> listedStreamIds, bool complete)
    {
        lock (_gate)
        {
            _catalogue = complete ? new HashSet<int>(listedStreamIds) : null;
        }
    }

    /// <summary>
    /// Records that this run created a file, as opposed to finding it on disk.
    /// </summary>
    /// <param name="path">The new file.</param>
    internal void NoteCreated(string path)
    {
        lock (_gate)
        {
            _created.Add(path);
        }
    }

    /// <summary>
    /// Records that a stream holds a movie's primary file this run.
    /// </summary>
    /// <param name="primaryPath">The primary file.</param>
    /// <param name="streamId">The stream writing it.</param>
    /// <param name="established">
    /// Whether the file was already on disk and already named this stream, so an earlier run
    /// settled the role and nothing now may change it.
    /// </param>
    internal void RegisterPrimary(string primaryPath, int streamId, bool established)
    {
        lock (_gate)
        {
            var slot = SlotFor(primaryPath);
            slot.Holder = streamId;
            slot.HolderEstablished = established;
        }
    }

    /// <summary>
    /// Records an alternative file waiting on a primary.
    /// </summary>
    /// <param name="primaryPath">
    /// The name the stream's primary would have: the file it would take if the holder went away.
    /// </param>
    /// <param name="streamId">The stream the alternative belongs to.</param>
    /// <param name="alternativePath">The alternative file.</param>
    /// <param name="waitsOn">
    /// The stream whose primary file is in the way: the one named by the file on disk, or the
    /// owner of the TMDB group the stream joined. Null when the holder is a stream of this run.
    /// </param>
    /// <param name="groupGuest">Whether the stream is a guest in a TMDB group's folder.</param>
    /// <param name="processed">Whether the stream was written this run, as opposed to carried over from disk.</param>
    internal void RegisterAlternative(string primaryPath, int streamId, string alternativePath, int? waitsOn, bool groupGuest, bool processed)
    {
        lock (_gate)
        {
            var slot = SlotFor(primaryPath);
            if (slot.Alternatives.TryGetValue(streamId, out var known))
            {
                known.Path = alternativePath;
                known.Processed |= processed;
                known.WaitsOn ??= waitsOn;
                known.GroupGuest |= groupGuest;
                return;
            }

            slot.Alternatives[streamId] = new Alternative(streamId, alternativePath, waitsOn, groupGuest, processed);
        }
    }

    /// <summary>
    /// Settles what only the complete catalogue can: streams that found a primary of a stream that
    /// is gone take it over (in place, or under its own name for a TMDB group guest whose owner had
    /// another label), and the lowest stream id of a folder written this run gets
    /// the plain name. Runs once, after the last batch and before orphan cleanup.
    /// </summary>
    /// <param name="syncedFiles">The run's set of files that must survive cleanup; updated.</param>
    /// <param name="claimedPaths">Paths written this run; updated.</param>
    /// <returns>One entry per change.</returns>
    internal List<RoleChange> SettleNewStreams(
        ConcurrentDictionary<string, byte> syncedFiles,
        ConcurrentDictionary<string, byte> claimedPaths)
    {
        var changes = new List<RoleChange>();
        lock (_gate)
        {
            if (_catalogue is null)
            {
                return changes;
            }

            foreach (var slot in _slots.Values.OrderBy(s => s.PrimaryPath, StringComparer.Ordinal).ToList())
            {
                TakeOver(slot, syncedFiles, claimedPaths, changes);
            }

            foreach (var slot in _slots.Values.OrderBy(s => s.PrimaryPath, StringComparer.Ordinal).ToList())
            {
                SwapToLowestStreamId(slot, syncedFiles, claimedPaths, changes);
            }
        }

        return changes;
    }

    /// <summary>
    /// Gives a primary file to an alternative when cleanup deleted the primary it waited on. Only a
    /// primary this run's cleanup deleted counts, and only when the stream it belonged to is no longer
    /// listed in this provider's catalogue, so a primary that is still wanted, belongs to another
    /// provider, or never existed is left alone. The alternative takes its own primary name.
    /// </summary>
    /// <param name="deletedPrimaries">
    /// The movie files orphan cleanup deleted this run, with the stream each URL named before it
    /// was deleted (null when it named none).
    /// </param>
    /// <param name="syncedFiles">The run's set of files that must survive cleanup; updated.</param>
    /// <param name="claimedPaths">Paths written this run; updated.</param>
    /// <returns>One entry per promotion.</returns>
    internal List<RoleChange> Promote(
        IReadOnlyDictionary<string, int?> deletedPrimaries,
        ConcurrentDictionary<string, byte> syncedFiles,
        ConcurrentDictionary<string, byte> claimedPaths)
    {
        var changes = new List<RoleChange>();
        lock (_gate)
        {
            if (_catalogue is null)
            {
                return changes;
            }

            // Slots by folder, so each deleted path looks only at its own folder instead of every slot.
            var slotsByFolder = _slots.Values
                .GroupBy(s => Path.GetDirectoryName(s.PrimaryPath)!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var deleted in deletedPrimaries.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                string path = deleted.Key;

                // Null: the URL named no stream, so nothing says it is gone. Negative: the file belonged to
                // another provider (StrmOwnerResolver.OtherProvider), which this provider's catalogue says
                // nothing about, so it is not gone either.
                if (deleted.Value is not int gone
                    || gone < 0
                    || _catalogue.Contains(gone)
                    || IsAlternativeNameOf(Path.GetFileName(path), gone)
                    || !slotsByFolder.TryGetValue(Path.GetDirectoryName(path)!, out var folderSlots))
                {
                    continue;
                }

                var candidate = folderSlots
                    .SelectMany(s => s.Alternatives.Values.Select(a => (Slot: s, Alternative: a)))
                    .Where(c => (string.Equals(c.Slot.PrimaryPath, path, StringComparison.OrdinalIgnoreCase)
                                 || (c.Alternative.GroupGuest && c.Alternative.WaitsOn == gone))
                                && File.Exists(c.Alternative.Path)
                                && syncedFiles.ContainsKey(c.Alternative.Path))
                    .OrderBy(c => c.Alternative.StreamId)
                    .Select(c => new { c.Slot, c.Alternative })
                    .FirstOrDefault();
                if (candidate is null)
                {
                    continue;
                }

                // The promoted stream takes the name it would have as a primary: the deleted file's own name
                // for a stream that waited on it, or its own label's name for a guest of a TMDB group whose
                // owner carried another label. One rename now, and none on the next sync.
                string target = candidate.Slot.PrimaryPath;
                var moved = candidate.Alternative;
                if (syncedFiles.ContainsKey(target) || File.Exists(target) || !TryMove(moved.Path, target))
                {
                    continue;
                }

                syncedFiles.TryRemove(moved.Path, out _);
                syncedFiles.TryAdd(target, 0);
                claimedPaths.TryRemove(moved.Path, out _);
                claimedPaths.TryAdd(target, 0);
                candidate.Slot.Alternatives.Remove(moved.StreamId);
                changes.Add(new RoleChange("promotion", moved.StreamId, target, moved.Path));
            }
        }

        return changes;
    }

    /// <summary>
    /// Whether a file name is the alternative of a stream: the plain name with " - stream id" before
    /// the extension, optionally followed by a provider tag (" - p2").
    /// </summary>
    /// <param name="fileName">A STRM file name.</param>
    /// <param name="streamId">The stream.</param>
    /// <returns>True when the name is that stream's alternative name.</returns>
    internal static bool IsAlternativeNameOf(string fileName, int streamId)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string tail = $" - {streamId}";
        if (stem.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int tag = stem.LastIndexOf(" - p", StringComparison.OrdinalIgnoreCase);
        if (tag <= 0)
        {
            return false;
        }

        var digits = stem.AsSpan(tag + 4);
        return digits.Length > 0
            && !digits.ContainsAnyExceptInRange('0', '9')
            && stem[..tag].EndsWith(tail, StringComparison.OrdinalIgnoreCase);
    }

    // A stream that wrote its file this run and found another stream's primary in the way takes
    // that primary over, once that other stream is known to be gone. Streams that already had a
    // file of their own are not touched: they stay alternatives, and cleanup plus promotion deal
    // with them.
    private void TakeOver(
        Slot slot,
        ConcurrentDictionary<string, byte> syncedFiles,
        ConcurrentDictionary<string, byte> claimedPaths,
        List<RoleChange> changes)
    {
        var waiting = slot.Alternatives.Values
            .Where(a => a.WaitsOn is int w && w >= 0 && !_catalogue!.Contains(w) && _created.Contains(a.Path) && File.Exists(a.Path))
            .GroupBy(a => a.WaitsOn!.Value)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in waiting)
        {
            int gone = group.Key;
            var taker = group.OrderBy(a => a.StreamId).First();
            string? stale = taker.GroupGuest
                ? FindStalePrimary(Path.GetDirectoryName(slot.PrimaryPath)!, gone, syncedFiles)
                : (File.Exists(slot.PrimaryPath) && _ownerOfFile(slot.PrimaryPath) == gone && !syncedFiles.ContainsKey(slot.PrimaryPath)
                    ? slot.PrimaryPath
                    : null);
            if (stale is null)
            {
                continue;
            }

            // A guest of a TMDB group whose owner carried another label goes to its own primary name,
            // the one it would write as the owner, and the departed owner's file is removed. Moving it
            // into the owner's labelled file would only rename it again on the next sync, and each
            // rename is a new item in Jellyfin (maintainer review).
            string target = taker.GroupGuest ? slot.PrimaryPath : stale;
            bool inPlace = string.Equals(target, stale, StringComparison.OrdinalIgnoreCase);
            if (!inPlace && (syncedFiles.ContainsKey(target) || File.Exists(target)))
            {
                continue;
            }

            if (!TryMove(taker.Path, target))
            {
                continue;
            }

            if (!inPlace)
            {
                TryDelete(stale);
            }

            syncedFiles.TryRemove(taker.Path, out _);
            syncedFiles.TryAdd(target, 0);
            claimedPaths.TryRemove(taker.Path, out _);
            claimedPaths.TryAdd(target, 0);
            slot.Alternatives.Remove(taker.StreamId);
            slot.Holder = taker.StreamId;
            slot.HolderEstablished = true;

            // In place, the existing item is rewritten, so it counts as an update. Under its own name
            // it is a new file, the same as a promotion.
            changes.Add(new RoleChange(inPlace ? "takeover" : "promotion", taker.StreamId, target, taker.Path));
        }
    }

    // A folder written for the first time this run has no history to respect, so the plain name goes
    // to the lowest stream id, not to whichever stream the loop reached first. A primary an earlier
    // run left on disk under its own stream's URL is history, and stays where it is.
    private void SwapToLowestStreamId(
        Slot slot,
        ConcurrentDictionary<string, byte> syncedFiles,
        ConcurrentDictionary<string, byte> claimedPaths,
        List<RoleChange> changes)
    {
        if (slot.Holder is not int holder || slot.HolderEstablished)
        {
            return;
        }

        // A stream that reached a primary this run wrote moments earlier saw the file on disk and
        // wrote an alternative that names the holder; that is not history, so it is eligible too.
        bool primaryIsNew = _created.Contains(slot.PrimaryPath);
        var lowest = slot.Alternatives.Values
            .Where(a => !a.GroupGuest && (a.WaitsOn is null || primaryIsNew) && a.Processed && a.StreamId < holder && File.Exists(a.Path))
            .OrderBy(a => a.StreamId)
            .FirstOrDefault();
        if (lowest is null || !File.Exists(slot.PrimaryPath))
        {
            return;
        }

        string folder = Path.GetDirectoryName(slot.PrimaryPath)!;
        string? holderAlternative = _alternativePathFor(folder, Path.GetFileName(slot.PrimaryPath), holder);
        if (holderAlternative is null)
        {
            return;
        }

        string temp = slot.PrimaryPath + ".swap";
        try
        {
            File.Move(slot.PrimaryPath, temp, overwrite: true);
            try
            {
                File.Move(lowest.Path, slot.PrimaryPath, overwrite: true);
            }
            catch
            {
                File.Move(temp, slot.PrimaryPath, overwrite: true);
                throw;
            }

            File.Move(temp, holderAlternative, overwrite: true);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        syncedFiles.TryRemove(lowest.Path, out _);
        syncedFiles.TryAdd(holderAlternative, 0);
        claimedPaths.TryRemove(lowest.Path, out _);
        claimedPaths.TryAdd(holderAlternative, 0);
        slot.Alternatives.Remove(lowest.StreamId);
        slot.Alternatives[holder] = new Alternative(holder, holderAlternative, null, false, true);
        slot.Holder = lowest.StreamId;
        slot.HolderEstablished = true;
        changes.Add(new RoleChange("swap", lowest.StreamId, slot.PrimaryPath, lowest.Path));
    }

    // The primary of a TMDB group's owner is named after the owner's own label, which a guest
    // cannot derive from its own, so it is found by what its URL says: a file in the folder that
    // belongs to the owner and is not one of the owner's alternatives.
    private string? FindStalePrimary(string folder, int ownerId, ConcurrentDictionary<string, byte> syncedFiles)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        return Directory.GetFiles(folder, "*.strm")
            .Where(f => !IsAlternativeNameOf(Path.GetFileName(f), ownerId)
                        && !syncedFiles.ContainsKey(f)
                        && _ownerOfFile(f) == ownerId)
            .OrderBy(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private Slot SlotFor(string primaryPath)
    {
        if (!_slots.TryGetValue(primaryPath, out var slot))
        {
            slot = new Slot(primaryPath);
            _slots[primaryPath] = slot;
        }

        return slot;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left for orphan cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Left for orphan cleanup.
        }
    }

    private static bool TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// One role change made by the ledger.
    /// </summary>
    /// <param name="Kind">"takeover" (rewritten in place), "swap" or "promotion" (moved to a new name).</param>
    /// <param name="StreamId">The stream that holds the primary file now.</param>
    /// <param name="PrimaryPath">The primary file it holds now.</param>
    /// <param name="PreviousPath">The alternative file it was moved from.</param>
    internal readonly record struct RoleChange(string Kind, int StreamId, string PrimaryPath, string PreviousPath);

    /// <summary>
    /// One movie file name that is a primary or would be one: who holds it and who waits.
    /// </summary>
    /// <param name="primaryPath">The primary file's path.</param>
    private sealed class Slot(string primaryPath)
    {
        /// <summary>Gets the primary file's path.</summary>
        public string PrimaryPath { get; } = primaryPath;

        /// <summary>Gets or sets the stream that wrote the primary this run.</summary>
        public int? Holder { get; set; }

        /// <summary>Gets or sets a value indicating whether the primary was already the holder's on disk.</summary>
        public bool HolderEstablished { get; set; }

        /// <summary>Gets the alternatives waiting on the primary, by stream id.</summary>
        public Dictionary<int, Alternative> Alternatives { get; } = new();
    }

    /// <summary>
    /// A stream's alternative file.
    /// </summary>
    /// <param name="streamId">The stream.</param>
    /// <param name="path">The alternative file.</param>
    /// <param name="waitsOn">The stream whose primary is in the way, if known.</param>
    /// <param name="groupGuest">Whether the stream is a guest in a TMDB group's folder.</param>
    /// <param name="processed">Whether the stream was written this run.</param>
    private sealed class Alternative(int streamId, string path, int? waitsOn, bool groupGuest, bool processed)
    {
        /// <summary>Gets the stream.</summary>
        public int StreamId { get; } = streamId;

        /// <summary>Gets or sets the alternative file.</summary>
        public string Path { get; set; } = path;

        /// <summary>Gets or sets the stream whose primary is in the way, if known.</summary>
        public int? WaitsOn { get; set; } = waitsOn;

        /// <summary>Gets or sets a value indicating whether the stream is a guest in a TMDB group's folder.</summary>
        public bool GroupGuest { get; set; } = groupGuest;

        /// <summary>Gets or sets a value indicating whether the stream was written this run.</summary>
        public bool Processed { get; set; } = processed;
    }
}
