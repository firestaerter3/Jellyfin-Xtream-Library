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

using System.Collections.Generic;
using System.Text.RegularExpressions;
using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class VersionLanguageLabelerTests
{
    [Theory]
    [InlineData("^DE\\b=DE", 1)]
    [InlineData("^DE\\b=DE\n^(EN|NF|AMZ)\\b=EN", 2)]
    [InlineData("^DE\\b=DE\n[unclosed=EN\n^EN=EN", 2)] // invalid line skipped
    [InlineData("", 0)]
    public void ParseRules_CountsValidLines(string rules, int expected)
        => VersionLanguageLabeler.ParseRules(rules).Should().HaveCount(expected);

    [Fact]
    public void Resolve_CategoryMatch_ReturnsLabel()
        => VersionLanguageLabeler.Resolve(["DE - Kino 2025"], "DE - Joker (2019)", Rules(), "EN").Should().Be("DE");

    [Fact]
    public void Resolve_MultipleCategories_FirstRuleWins()
        => VersionLanguageLabeler.Resolve(["AMZ - Action", "DE - Netflix"], "Joker (2019)", Rules(), "EN").Should().Be("DE");

    [Fact]
    public void Resolve_NoCategoryMatch_FallsBackToStreamName()
        => VersionLanguageLabeler.Resolve(["FIFA+ - Docs"], "DE - Joker (2019)", Rules(), "EN").Should().Be("DE");

    [Fact]
    public void Resolve_NothingMatches_ReturnsDefault()
        => VersionLanguageLabeler.Resolve(["FIFA+ - Docs"], "Joker (2019)", Rules(), "EN").Should().Be("EN");

    [Fact]
    public void Resolve_NothingMatches_EmptyDefault_ReturnsNull()
        => VersionLanguageLabeler.Resolve(["FIFA+ - Docs"], "Joker (2019)", Rules(), string.Empty).Should().BeNull();

    [Theory]
    [InlineData("DE 2160p", "DE 4K")]
    [InlineData("EN 1080p", "EN FHD")]
    [InlineData("EN 1080i", "EN FHD")]
    [InlineData("EN 720p", "EN HD")]
    [InlineData("HEVC 4K", "HEVC 4K")]
    [InlineData("V2", "V2")]
    public void NormalizeResolutionTokens_ReplacesDigitTokens(string input, string expected)
        => VersionLanguageLabeler.NormalizeResolutionTokens(input).Should().Be(expected);

    [Theory]
    [InlineData(null, "HEVC 4K", "HEVC 4K")]
    [InlineData("DE", null, "DE")]
    [InlineData("EN", "HEVC 4K", "EN HEVC 4K")]
    [InlineData(null, null, null)]
    public void Combine_JoinsLanguageFirst(string? lang, string? quality, string? expected)
        => VersionLanguageLabeler.Combine(lang, quality).Should().Be(expected);

    private static IReadOnlyList<(Regex Pattern, string Label)> Rules()
        => VersionLanguageLabeler.ParseRules("^DE\\b=DE\n^(EN|NF|AMZ|SKY|MAX)\\b=EN");
}
