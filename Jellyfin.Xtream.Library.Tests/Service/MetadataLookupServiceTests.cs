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

using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class MetadataLookupServiceTests
{
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
}
