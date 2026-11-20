// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Xtream.Library.Service.Models;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// The categories one sync run could not read, and what it kept because of them (GitHub #171).
/// <para>
/// A category is unread when its listing threw, or when it came back empty while the previous
/// snapshot still held items under it. Its items are then missing from the run, and without this
/// orphan cleanup deletes their files although the provider most likely still has them. Movies and
/// series are filled by two passes that can run at the same time, so each has its own collections.
/// </para>
/// </summary>
internal sealed class UnreadCategories
{
    /// <summary>
    /// Gets every VOD category that was unread this run, including ones no longer protected
    /// because they have been unread too long.
    /// </summary>
    public ConcurrentDictionary<int, byte> MovieCategoryIds { get; } = new();

    /// <summary>
    /// Gets the snapshot of the previous run, loaded whenever orphan cleanup is on, so the items
    /// recorded under a category can be kept even with incremental sync and grouping off.
    /// </summary>
    public ContentSnapshot? PreviousSnapshot { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the movie run listed every selected category: at
    /// least one was selected and none failed to fetch. Only such a full run has written every
    /// movie file, which is what lets the snapshot forget that files may still carry language labels.
    /// </summary>
    public bool MovieListingComplete { get; set; }

    /// <summary>
    /// Gets every series category that was unread this run. See <see cref="MovieCategoryIds"/>.
    /// </summary>
    public ConcurrentDictionary<int, byte> SeriesCategoryIds { get; } = new();

    /// <summary>
    /// Gets when each still-protected VOD category was first unread, to store in the next snapshot.
    /// </summary>
    public ConcurrentDictionary<int, DateTime> MovieCategoriesSince { get; } = new();

    /// <summary>
    /// Gets when each still-protected series category was first unread.
    /// </summary>
    public ConcurrentDictionary<int, DateTime> SeriesCategoriesSince { get; } = new();

    /// <summary>
    /// Gets the previous snapshot entries of the movies kept, so the next snapshot still records
    /// that their category had items. Without them a second unread run would find nothing to keep.
    /// </summary>
    public ConcurrentDictionary<int, MovieSnapshot> KeptMovies { get; } = new();

    /// <summary>
    /// Gets the previous snapshot entries of the series kept. See <see cref="KeptMovies"/>.
    /// </summary>
    public ConcurrentDictionary<int, SeriesSnapshot> KeptSeries { get; } = new();

    /// <summary>
    /// Gets the VOD categories that were unread this run.
    /// </summary>
    public IReadOnlySet<int> UnreadMovieCategoryIds => MovieCategoryIds.Keys.ToHashSet();

    /// <summary>
    /// Gets the series categories that were unread this run.
    /// </summary>
    public IReadOnlySet<int> UnreadSeriesCategoryIds => SeriesCategoryIds.Keys.ToHashSet();

    /// <summary>
    /// Works out which of the fetched categories are unread.
    /// </summary>
    /// <param name="itemCounts">Items each category listed this run, for the categories whose fetch did not throw.</param>
    /// <param name="failed">Categories whose fetch threw.</param>
    /// <param name="previousCategoryIds">Category ids the previous snapshot holds items under.</param>
    /// <returns>The unread category ids.</returns>
    public static HashSet<int> Find(
        IReadOnlyDictionary<int, int> itemCounts,
        IEnumerable<int> failed,
        IReadOnlySet<int> previousCategoryIds)
    {
        var unread = new HashSet<int>(failed);
        foreach (var (categoryId, count) in itemCounts)
        {
            // An empty answer is only suspect when the category had items before. A category that
            // was always empty is just empty.
            if (count == 0 && previousCategoryIds.Contains(categoryId))
            {
                unread.Add(categoryId);
            }
        }

        return unread;
    }

    /// <summary>
    /// Whether a category has been unread long enough to believe the provider really emptied it.
    /// The full sync interval is the provider's own measure of how stale the library may get.
    /// </summary>
    /// <param name="since">When the category was first unread.</param>
    /// <param name="now">The current time.</param>
    /// <param name="fullSyncIntervalDays">The provider's full sync interval.</param>
    /// <returns>True when its items should be cleaned up normally.</returns>
    public static bool UnreadTooLong(DateTime since, DateTime now, int fullSyncIntervalDays)
        => (now - since).TotalDays > fullSyncIntervalDays;
}
