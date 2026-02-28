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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

[Collection("PluginSingletonTests")]
public class MetadataLookupServiceTests
{
    private static void InitPlugin(PluginConfiguration config)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "claude", "test-metadata-config");
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(tempPath);
        appPaths.Setup(p => p.DataPath).Returns(tempPath);
        appPaths.Setup(p => p.ProgramDataPath).Returns(tempPath);
        appPaths.Setup(p => p.CachePath).Returns(tempPath);
        appPaths.Setup(p => p.LogDirectoryPath).Returns(tempPath);
        appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(tempPath);
        appPaths.Setup(p => p.TempDirectory).Returns(tempPath);
        appPaths.Setup(p => p.PluginsPath).Returns(tempPath);
        appPaths.Setup(p => p.WebPath).Returns(tempPath);
        appPaths.Setup(p => p.ProgramSystemPath).Returns(tempPath);
        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer
            .Setup(s => s.DeserializeFromFile(It.IsAny<Type>(), It.IsAny<string>()))
            .Returns(config);
        _ = new Plugin(appPaths.Object, xmlSerializer.Object);
    }

    // === Year mismatch (parameter years) ===

    [Fact]
    public void IsLikelyFalsePositive_YearMismatchByMoreThan2_ReturnsTrue()
    {
        MetadataLookupService.IsLikelyFalsePositive(
            "Some Movie", "Other Movie", searchYear: 2023, resultYear: 2013)
            .Should().BeTrue();
    }

    [Fact]
    public void IsLikelyFalsePositive_YearWithin2_ReturnsFalse()
    {
        MetadataLookupService.IsLikelyFalsePositive(
            "Some Movie", "Some Movie", searchYear: 2023, resultYear: 2022)
            .Should().BeFalse();
    }

    [Fact]
    public void IsLikelyFalsePositive_ExactYearMatch_ReturnsFalse()
    {
        MetadataLookupService.IsLikelyFalsePositive(
            "De Leeuwenkoning", "The Lion King", searchYear: 2019, resultYear: 2019)
            .Should().BeFalse();
    }

    // === Year extracted from title text ===

    [Fact]
    public void IsLikelyFalsePositive_YearInTitleMismatch_ReturnsTrue()
    {
        // "Formule 1 2023 USA Austin SprintRace" has 2023 in title, result is from 2013
        MetadataLookupService.IsLikelyFalsePositive(
            "Formule 1 2023 USA Austin SprintRace", "+1", searchYear: null, resultYear: 2013)
            .Should().BeTrue();
    }

    [Fact]
    public void IsLikelyFalsePositive_YearAtStartOfTitle_NotExtracted()
    {
        // "1917" starts with a year-like number — should not be extracted as year
        MetadataLookupService.IsLikelyFalsePositive(
            "1917", "1917", searchYear: null, resultYear: 2019)
            .Should().BeFalse();
    }

    [Fact]
    public void IsLikelyFalsePositive_2001SpaceOdyssey_NotExtracted()
    {
        // "2001: A Space Odyssey" — year-like number at start should not be extracted
        MetadataLookupService.IsLikelyFalsePositive(
            "2001: A Space Odyssey", "2001: A Space Odyssey", searchYear: null, resultYear: 1968)
            .Should().BeFalse();
    }

    // === Title length ratio ===

    [Fact]
    public void IsLikelyFalsePositive_VeryShortResultVsLongSearch_ReturnsTrue()
    {
        // "+1" (2 chars) result for a 37-char search — obvious false positive
        MetadataLookupService.IsLikelyFalsePositive(
            "Formule 1 2024 Pre Season Testing Dag 3 Bahrein", "+1", searchYear: null, resultYear: null)
            .Should().BeTrue();
    }

    [Fact]
    public void IsLikelyFalsePositive_ShortResultForShortSearch_ReturnsFalse()
    {
        // "Up" (2 chars) searching for "Up" (2 chars) — valid match
        MetadataLookupService.IsLikelyFalsePositive(
            "Up", "Up", searchYear: null, resultYear: null)
            .Should().BeFalse();
    }

    [Fact]
    public void IsLikelyFalsePositive_ShortResultForMediumSearch_ReturnsFalse()
    {
        // "It" (2 chars) result for "It (2017)" (9 chars) — valid
        MetadataLookupService.IsLikelyFalsePositive(
            "It (2017)", "It", searchYear: null, resultYear: null)
            .Should().BeFalse();
    }

    // === Cross-language matching (should NOT be rejected) ===

    [Fact]
    public void IsLikelyFalsePositive_DutchToEnglishSameYear_ReturnsFalse()
    {
        MetadataLookupService.IsLikelyFalsePositive(
            "De Leeuwenkoning", "The Lion King", searchYear: 2019, resultYear: 2019)
            .Should().BeFalse();
    }

    [Fact]
    public void IsLikelyFalsePositive_DutchToEnglishNoYear_ReturnsFalse()
    {
        // No year info at all — should pass (both titles are normal length)
        MetadataLookupService.IsLikelyFalsePositive(
            "De Leeuwenkoning", "The Lion King", searchYear: null, resultYear: null)
            .Should().BeFalse();
    }

    [Fact]
    public void IsLikelyFalsePositive_NullResultName_ReturnsFalse()
    {
        MetadataLookupService.IsLikelyFalsePositive(
            "Some Movie", null, searchYear: null, resultYear: null)
            .Should().BeFalse();
    }

    // === Real-world Formula 1 cases ===

    [Fact]
    public void IsLikelyFalsePositive_Formula1WithYear_ReturnsTrue()
    {
        MetadataLookupService.IsLikelyFalsePositive(
            "Formule 1 2023 Zandvoort Race", "+1", searchYear: null, resultYear: 2013)
            .Should().BeTrue();
    }

    [Fact]
    public void IsLikelyFalsePositive_Formula1NoResultYear_StillCaughtByLength()
    {
        // Even without year info, the length ratio catches it
        MetadataLookupService.IsLikelyFalsePositive(
            "Formule 1 2023 Zandvoort Race", "+1", searchYear: null, resultYear: null)
            .Should().BeTrue();
    }

    // === FallbackToYearlessLookup: movie ===

    [Fact]
    public async Task LookupMovieTmdbIdAsync_FallbackEnabled_RetriesWithoutYear_WhenYearQualifiedFails()
    {
        InitPlugin(new PluginConfiguration
        {
            EnableMetadataLookup = true,
            FallbackToYearlessLookup = true,
            LibraryPath = string.Empty,
        });

        var fallbackResult = new RemoteSearchResult
        {
            Name = "The Notebook",
            ProductionYear = 2004,
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "11036" },
        };

        var mockProvider = new Mock<IProviderManager>();
        mockProvider
            .SetupSequence(pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RemoteSearchResult>())   // primary (year=2009): no match
            .ReturnsAsync(new[] { fallbackResult });            // fallback (year=null): match

        var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
        var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

        var result = await svc.LookupMovieTmdbIdAsync("The Notebook", 2009, CancellationToken.None);

        result.Should().Be(11036);
        mockProvider.Verify(
            pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task LookupMovieTmdbIdAsync_FallbackDisabled_DoesNotRetry()
    {
        InitPlugin(new PluginConfiguration
        {
            EnableMetadataLookup = true,
            FallbackToYearlessLookup = false,
            LibraryPath = string.Empty,
        });

        var mockProvider = new Mock<IProviderManager>();
        mockProvider
            .Setup(pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RemoteSearchResult>());

        var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
        var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

        var result = await svc.LookupMovieTmdbIdAsync("The Notebook", 2009, CancellationToken.None);

        result.Should().BeNull();
        mockProvider.Verify(
            pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public async Task LookupMovieTmdbIdAsync_FallbackEnabled_NoDoubleCallWhenYearAlreadyNull()
    {
        // When the stream has no year, the initial lookup is already year-free; no fallback needed.
        InitPlugin(new PluginConfiguration
        {
            EnableMetadataLookup = true,
            FallbackToYearlessLookup = true,
            LibraryPath = string.Empty,
        });

        var mockProvider = new Mock<IProviderManager>();
        mockProvider
            .Setup(pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RemoteSearchResult>());

        var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
        var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

        var result = await svc.LookupMovieTmdbIdAsync("The Notebook", null, CancellationToken.None);

        result.Should().BeNull();
        mockProvider.Verify(
            pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public async Task LookupMovieTmdbIdAsync_FallbackEnabled_CachesFallbackNull_AvoidingRepeatCalls()
    {
        // When fallback also returns nothing, the null result is cached under the year-free key.
        // A second call with the same year-free key should hit cache, not call the provider again.
        InitPlugin(new PluginConfiguration
        {
            EnableMetadataLookup = true,
            FallbackToYearlessLookup = true,
            LibraryPath = string.Empty,
        });

        var mockProvider = new Mock<IProviderManager>();
        mockProvider
            .Setup(pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RemoteSearchResult>());

        var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
        var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

        // First call: primary (year) + fallback (no year) = 2 provider calls
        await svc.LookupMovieTmdbIdAsync("Unknown Movie", 2020, CancellationToken.None);
        // Second call with same title, no year: should hit fallback cache, 0 new provider calls
        await svc.LookupMovieTmdbIdAsync("Unknown Movie", null, CancellationToken.None);

        mockProvider.Verify(
            pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2)); // primary + fallback on first call; cache hit on second
    }

    // === FallbackToYearlessLookup: series ===

    [Fact]
    public async Task LookupSeriesTvdbIdAsync_FallbackEnabled_RetriesWithoutYear_WhenYearQualifiedFails()
    {
        InitPlugin(new PluginConfiguration
        {
            EnableMetadataLookup = true,
            FallbackToYearlessLookup = true,
            LibraryPath = string.Empty,
        });

        var fallbackResult = new RemoteSearchResult
        {
            Name = "Breaking Bad",
            ProductionYear = 2008,
            ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "81189" },
        };

        var mockProvider = new Mock<IProviderManager>();
        mockProvider
            .SetupSequence(pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
                It.IsAny<RemoteSearchQuery<SeriesInfo>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RemoteSearchResult>())
            .ReturnsAsync(new[] { fallbackResult });

        var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
        var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

        var result = await svc.LookupSeriesTvdbIdAsync("Breaking Bad", 2012, CancellationToken.None);

        result.Should().Be(81189);
        mockProvider.Verify(
            pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
                It.IsAny<RemoteSearchQuery<SeriesInfo>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task LookupSeriesTvdbIdAsync_FallbackDisabled_DoesNotRetry()
    {
        InitPlugin(new PluginConfiguration
        {
            EnableMetadataLookup = true,
            FallbackToYearlessLookup = false,
            LibraryPath = string.Empty,
        });

        var mockProvider = new Mock<IProviderManager>();
        mockProvider
            .Setup(pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
                It.IsAny<RemoteSearchQuery<SeriesInfo>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RemoteSearchResult>());

        var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
        var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

        var result = await svc.LookupSeriesTvdbIdAsync("Breaking Bad", 2012, CancellationToken.None);

        result.Should().BeNull();
        mockProvider.Verify(
            pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
                It.IsAny<RemoteSearchQuery<SeriesInfo>>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public async Task LookupSeriesTvdbIdAsync_FallbackEnabled_NoDoubleCallWhenYearAlreadyNull()
    {
        InitPlugin(new PluginConfiguration
        {
            EnableMetadataLookup = true,
            FallbackToYearlessLookup = true,
            LibraryPath = string.Empty,
        });

        var mockProvider = new Mock<IProviderManager>();
        mockProvider
            .Setup(pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
                It.IsAny<RemoteSearchQuery<SeriesInfo>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RemoteSearchResult>());

        var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
        var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

        var result = await svc.LookupSeriesTvdbIdAsync("Breaking Bad", null, CancellationToken.None);

        result.Should().BeNull();
        mockProvider.Verify(
            pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
                It.IsAny<RemoteSearchQuery<SeriesInfo>>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    [Fact]
    public async Task LookupSeriesTvdbIdAsync_FallbackEnabled_CachesFallbackNull_AvoidingRepeatCalls()
    {
        InitPlugin(new PluginConfiguration
        {
            EnableMetadataLookup = true,
            FallbackToYearlessLookup = true,
            LibraryPath = string.Empty,
        });

        var mockProvider = new Mock<IProviderManager>();
        mockProvider
            .Setup(pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
                It.IsAny<RemoteSearchQuery<SeriesInfo>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RemoteSearchResult>());

        var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
        var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

        await svc.LookupSeriesTvdbIdAsync("Unknown Series", 2020, CancellationToken.None);
        await svc.LookupSeriesTvdbIdAsync("Unknown Series", null, CancellationToken.None);

        mockProvider.Verify(
            pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
                It.IsAny<RemoteSearchQuery<SeriesInfo>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // === Release date year fallback: movie ===

    [Fact]
    public async Task LookupMovieTmdbIdAsync_ReleaseDateYear_MatchesWhenTitleYearWrong()
    {
        // "13 Geister (2025)" - title year is 2025 (wrong), release date year is 2001 (correct)
        InitPlugin(new PluginConfiguration
        {
            EnableMetadataLookup = true,
            FallbackToYearlessLookup = true,
            LibraryPath = string.Empty,
        });

        var correctResult = new RemoteSearchResult
        {
            Name = "Thir13en Ghosts",
            ProductionYear = 2001,
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "9806" },
        };

        var mockProvider = new Mock<IProviderManager>();
        mockProvider
            .SetupSequence(pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RemoteSearchResult>())    // Stage 1: title + year=2025: no match
            .ReturnsAsync(new[] { correctResult });              // Stage 2: title + releaseDateYear=2001: match!

        var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
        var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

        var result = await svc.LookupMovieTmdbIdAsync(
            "13 Geister", 2025, CancellationToken.None,
            releaseDateYear: 2001);

        result.Should().Be(9806);
        mockProvider.Verify(
            pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2)); // primary + release date year
    }

    [Fact]
    public async Task LookupMovieTmdbIdAsync_ReleaseDateYear_SkippedWhenSameAsTitleYear()
    {
        // When release date year equals title year, stage 2 is skipped (would be redundant)
        InitPlugin(new PluginConfiguration
        {
            EnableMetadataLookup = true,
            FallbackToYearlessLookup = true,
            LibraryPath = string.Empty,
        });

        var mockProvider = new Mock<IProviderManager>();
        mockProvider
            .SetupSequence(pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RemoteSearchResult>())    // Stage 1: primary
            .ReturnsAsync(Array.Empty<RemoteSearchResult>());   // Stage 3: year-free

        var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
        var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

        var result = await svc.LookupMovieTmdbIdAsync(
            "Some Movie", 2025, CancellationToken.None,
            releaseDateYear: 2025);

        result.Should().BeNull();
        // Only 2 calls: primary + year-free (stage 2 skipped because releaseDateYear == year)
        mockProvider.Verify(
            pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // === Alternative title fallback: movie ===

    [Fact]
    public async Task LookupMovieTmdbIdAsync_AlternativeTitle_MatchesWhenPrimaryTitleFails()
    {
        // German title "13 Geister" fails, but original name "Thir13en Ghosts" succeeds
        InitPlugin(new PluginConfiguration
        {
            EnableMetadataLookup = true,
            FallbackToYearlessLookup = true,
            LibraryPath = string.Empty,
        });

        var correctResult = new RemoteSearchResult
        {
            Name = "Thir13en Ghosts",
            ProductionYear = 2001,
            ProviderIds = new Dictionary<string, string> { ["Tmdb"] = "9806" },
        };

        var mockProvider = new Mock<IProviderManager>();
        mockProvider
            .SetupSequence(pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RemoteSearchResult>())    // Stage 1: "13 Geister" + year=2025
            .ReturnsAsync(Array.Empty<RemoteSearchResult>())    // Stage 2: "13 Geister" + year=2001
            .ReturnsAsync(Array.Empty<RemoteSearchResult>())    // Stage 3: "13 Geister" + year=null
            .ReturnsAsync(new[] { correctResult })              // Stage 4a: "Thir13en Ghosts" + year=2001
            ;

        var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
        var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

        var result = await svc.LookupMovieTmdbIdAsync(
            "13 Geister", 2025, CancellationToken.None,
            releaseDateYear: 2001, alternativeTitle: "Thir13en Ghosts");

        result.Should().Be(9806);
        mockProvider.Verify(
            pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(4));
    }

    [Fact]
    public async Task LookupMovieTmdbIdAsync_AlternativeTitle_SkippedWhenSameAsPrimaryTitle()
    {
        // When alternative title equals primary title, stage 4 is skipped
        InitPlugin(new PluginConfiguration
        {
            EnableMetadataLookup = true,
            FallbackToYearlessLookup = true,
            LibraryPath = string.Empty,
        });

        var mockProvider = new Mock<IProviderManager>();
        mockProvider
            .SetupSequence(pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RemoteSearchResult>())    // Stage 1: primary
            .ReturnsAsync(Array.Empty<RemoteSearchResult>());   // Stage 3: year-free

        var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
        var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

        var result = await svc.LookupMovieTmdbIdAsync(
            "Some Movie", 2025, CancellationToken.None,
            alternativeTitle: "Some Movie");

        result.Should().BeNull();
        // Only 2 calls: primary + year-free (stage 4 skipped because titles match)
        mockProvider.Verify(
            pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task LookupMovieTmdbIdAsync_FallbackDisabled_SkipsAllFallbackStages()
    {
        // When fallback is disabled, only the primary lookup runs, even with extra data
        InitPlugin(new PluginConfiguration
        {
            EnableMetadataLookup = true,
            FallbackToYearlessLookup = false,
            LibraryPath = string.Empty,
        });

        var mockProvider = new Mock<IProviderManager>();
        mockProvider
            .Setup(pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RemoteSearchResult>());

        var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
        var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

        var result = await svc.LookupMovieTmdbIdAsync(
            "13 Geister", 2025, CancellationToken.None,
            releaseDateYear: 2001, alternativeTitle: "Thir13en Ghosts");

        result.Should().BeNull();
        mockProvider.Verify(
            pm => pm.GetRemoteSearchResults<Movie, MovieInfo>(
                It.IsAny<RemoteSearchQuery<MovieInfo>>(), It.IsAny<CancellationToken>()),
            Times.Once());
    }

    // === Alternative title fallback: series ===

    [Fact]
    public async Task LookupSeriesTvdbIdAsync_AlternativeTitle_MatchesWhenPrimaryTitleFails()
    {
        InitPlugin(new PluginConfiguration
        {
            EnableMetadataLookup = true,
            FallbackToYearlessLookup = true,
            LibraryPath = string.Empty,
        });

        var correctResult = new RemoteSearchResult
        {
            Name = "Dark",
            ProductionYear = 2017,
            ProviderIds = new Dictionary<string, string> { ["Tvdb"] = "333916" },
        };

        var mockProvider = new Mock<IProviderManager>();
        mockProvider
            .SetupSequence(pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
                It.IsAny<RemoteSearchQuery<SeriesInfo>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<RemoteSearchResult>())    // Stage 1: "Dunkel" + year=2020
            .ReturnsAsync(Array.Empty<RemoteSearchResult>())    // Stage 2: "Dunkel" + year=null
            .ReturnsAsync(new[] { correctResult });              // Stage 3: "Dark" + year=null

        var cache = new MetadataCache(NullLogger<MetadataCache>.Instance);
        var svc = new MetadataLookupService(mockProvider.Object, cache, NullLogger<MetadataLookupService>.Instance);

        var result = await svc.LookupSeriesTvdbIdAsync(
            "Dunkel", 2020, CancellationToken.None,
            alternativeTitle: "Dark");

        result.Should().Be(333916);
        mockProvider.Verify(
            pm => pm.GetRemoteSearchResults<Series, SeriesInfo>(
                It.IsAny<RemoteSearchQuery<SeriesInfo>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }
}
