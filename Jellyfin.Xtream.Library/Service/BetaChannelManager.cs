// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Collections.Generic;
using MediaBrowser.Model.Updates;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Syncs the plugin's <see cref="PluginConfiguration.UseBetaChannel"/> setting
/// into Jellyfin's <c>ServerConfiguration.PluginRepositories</c> list, so that
/// users opting in see beta releases of this plugin in Dashboard → Plugins →
/// Catalog like any other plugin.
/// </summary>
public sealed partial class BetaChannelManager
{
    /// <summary>
    /// Display name used when this manager appends the beta repository entry.
    /// Match logic uses URL only, so users renaming the entry will not break
    /// the match.
    /// </summary>
    internal const string BetaRepoName = "Xtream Library (Beta)";

    /// <summary>
    /// Manifest URL of the plugin's beta release channel.
    /// </summary>
    internal const string BetaRepoUrl =
        "https://firestaerter3.github.io/jellyfin-plugin-repo/manifest-dev.json";

    /// <summary>
    /// Computes the desired state of the plugin-repositories array given the
    /// current state and the desired <paramref name="useBeta"/> setting.
    /// Pure function — no side effects. Returns a new array; does not mutate
    /// <paramref name="current"/>.
    /// </summary>
    /// <param name="useBeta">Whether the beta channel should be enabled.</param>
    /// <param name="current">The current list of plugin repositories.</param>
    /// <returns>A new array reflecting the desired state.</returns>
    internal static RepositoryInfo[] ComputeNextRepositories(bool useBeta, RepositoryInfo[] current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var matchIndex = -1;
        for (var i = 0; i < current.Length; i++)
        {
            if (string.Equals(current[i].Url, BetaRepoUrl, StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = i;
                break;
            }
        }

        if (useBeta)
        {
            if (matchIndex >= 0)
            {
                // Already registered. Respect any edits the user made (rename, disable).
                return current;
            }

            var appended = new List<RepositoryInfo>(current.Length + 1);
            appended.AddRange(current);
            appended.Add(new RepositoryInfo
            {
                Name = BetaRepoName,
                Url = BetaRepoUrl,
                Enabled = true,
            });
            return appended.ToArray();
        }

        if (matchIndex < 0)
        {
            return current;
        }

        // Remove the matched entry, preserve order of the rest.
        var filtered = new List<RepositoryInfo>(current.Length - 1);
        for (var i = 0; i < current.Length; i++)
        {
            if (i != matchIndex)
            {
                filtered.Add(current[i]);
            }
        }

        return filtered.ToArray();
    }

    /// <summary>
    /// Structural equality on <c>Name</c> + <c>Url</c> + <c>Enabled</c>, in order.
    /// Drives the "skip SaveConfiguration when nothing changed" optimisation.
    /// </summary>
    /// <param name="a">The first array to compare.</param>
    /// <param name="b">The second array to compare.</param>
    /// <returns><see langword="true"/> if both arrays have identical Name/Url/Enabled triples in order.</returns>
    internal static bool StructurallyEqual(RepositoryInfo[] a, RepositoryInfo[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal)
                || !string.Equals(a[i].Url, b[i].Url, StringComparison.Ordinal)
                || a[i].Enabled != b[i].Enabled)
            {
                return false;
            }
        }

        return true;
    }
}
