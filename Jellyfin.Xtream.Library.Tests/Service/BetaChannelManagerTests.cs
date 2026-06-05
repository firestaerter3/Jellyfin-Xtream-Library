// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Model.Updates;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class BetaChannelManagerTests
{
    private static RepositoryInfo Beta(string? url = null, bool enabled = true, string? name = null) =>
        new()
        {
            Name = name ?? BetaChannelManager.BetaRepoName,
            Url = url ?? BetaChannelManager.BetaRepoUrl,
            Enabled = enabled,
        };

    private static RepositoryInfo OtherRepo() =>
        new() { Name = "Jellyfin Official", Url = "https://repo.jellyfin.org/files/plugin/manifest.json", Enabled = true };

    [Fact]
    public void Enable_NoEntries_AppendsBetaRepo()
    {
        var next = BetaChannelManager.ComputeNextRepositories(true, System.Array.Empty<RepositoryInfo>());

        next.Should().HaveCount(1);
        next[0].Name.Should().Be(BetaChannelManager.BetaRepoName);
        next[0].Url.Should().Be(BetaChannelManager.BetaRepoUrl);
        next[0].Enabled.Should().BeTrue();
    }

    [Fact]
    public void Enable_OtherEntriesOnly_AppendsBetaRepo()
    {
        var other = OtherRepo();

        var next = BetaChannelManager.ComputeNextRepositories(true, new[] { other });

        next.Should().HaveCount(2);
        next[0].Should().BeSameAs(other);
        next[1].Url.Should().Be(BetaChannelManager.BetaRepoUrl);
    }

    [Fact]
    public void Enable_BetaAlreadyPresent_NoOp()
    {
        var current = new[] { OtherRepo(), Beta() };

        var next = BetaChannelManager.ComputeNextRepositories(true, current);

        next.Should().BeSameAs(current);
    }

    [Fact]
    public void Enable_BetaPresentButRenamedByUser_NoOp()
    {
        var current = new[] { Beta(name: "My Custom Beta Name") };

        var next = BetaChannelManager.ComputeNextRepositories(true, current);

        next.Should().HaveCount(1);
        next[0].Name.Should().Be("My Custom Beta Name");
    }

    [Fact]
    public void Enable_BetaPresentButDisabledByUser_LeavesDisabled()
    {
        var current = new[] { Beta(enabled: false) };

        var next = BetaChannelManager.ComputeNextRepositories(true, current);

        next.Should().HaveCount(1);
        next[0].Enabled.Should().BeFalse();
    }

    [Fact]
    public void Disable_BetaPresent_Removes()
    {
        var other = OtherRepo();
        var current = new[] { other, Beta() };

        var next = BetaChannelManager.ComputeNextRepositories(false, current);

        next.Should().HaveCount(1);
        next[0].Should().BeSameAs(other);
    }

    [Fact]
    public void Disable_BetaAbsent_NoOp()
    {
        var current = new[] { OtherRepo() };

        var next = BetaChannelManager.ComputeNextRepositories(false, current);

        next.Should().BeSameAs(current);
    }

    [Fact]
    public void Disable_EmptyList_NoOp()
    {
        var next = BetaChannelManager.ComputeNextRepositories(false, System.Array.Empty<RepositoryInfo>());

        next.Should().BeEmpty();
    }

    [Fact]
    public void Match_IsCaseInsensitiveOnUrl()
    {
        var upperUrl = BetaChannelManager.BetaRepoUrl.ToUpperInvariant();
        var current = new[] { Beta(url: upperUrl) };

        var next = BetaChannelManager.ComputeNextRepositories(false, current);

        next.Should().BeEmpty();
    }

    [Fact]
    public void StructuralEqual_TrueWhenSameContents()
    {
        var a = new[] { OtherRepo(), Beta() };
        var b = new[] { OtherRepo(), Beta() };

        BetaChannelManager.StructurallyEqual(a, b).Should().BeTrue();
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("Url")]
    [InlineData("Enabled")]
    public void StructuralEqual_FalseWhenAnyFieldDiffers(string differingField)
    {
        var a = new[] { Beta() };
        RepositoryInfo b = differingField switch
        {
            "Name" => Beta(name: "Different"),
            "Url" => Beta(url: "https://example.com/different.json"),
            "Enabled" => Beta(enabled: false),
            _ => throw new System.ArgumentException("unknown field"),
        };

        BetaChannelManager.StructurallyEqual(a, new[] { b }).Should().BeFalse();
    }
}
