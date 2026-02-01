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

public class ChannelNameCleanerTests
{
    #region Country Prefix Tests

    [Theory]
    [InlineData("UK: BBC One", "BBC One")]
    [InlineData("US: CNN", "CNN")]
    [InlineData("DE: ARD", "ARD")]
    [InlineData("FR: TF1", "TF1")]
    [InlineData("NL: RTL 4", "RTL 4")]
    [InlineData("UK | BBC Two", "BBC Two")]
    [InlineData("US - Fox News", "Fox News")]
    public void CleanChannelName_RemovesCountryPrefixes(string input, string expected)
    {
        var result = ChannelNameCleaner.CleanChannelName(input);
        result.Should().Be(expected);
    }

    #endregion

    #region Quality Tag Tests

    [Theory]
    [InlineData("BBC One | HD |", "BBC One")]
    [InlineData("CNN | FHD |", "CNN")]
    [InlineData("Sports 1 | 4K |", "Sports 1")]
    [InlineData("Movie Channel | UHD |", "Movie Channel")]
    [InlineData("News | SD |", "News")]
    public void CleanChannelName_RemovesQualityTagsWithPipes(string input, string expected)
    {
        var result = ChannelNameCleaner.CleanChannelName(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("BBC One HD", "BBC One")]
    [InlineData("CNN FHD", "CNN")]
    [InlineData("Sports 4K", "Sports")]
    public void CleanChannelName_RemovesQualityTagsAtEnd(string input, string expected)
    {
        var result = ChannelNameCleaner.CleanChannelName(input);
        result.Should().Be(expected);
    }

    #endregion

    #region Bracketed Tag Tests

    [Theory]
    [InlineData("BBC One [HD]", "BBC One")]
    [InlineData("CNN (FHD)", "CNN")]
    [InlineData("Sports [4K]", "Sports")]
    [InlineData("Movie [HEVC]", "Movie")]
    [InlineData("News (720p)", "News")]
    public void CleanChannelName_RemovesBracketedTags(string input, string expected)
    {
        var result = ChannelNameCleaner.CleanChannelName(input);
        result.Should().Be(expected);
    }

    #endregion

    #region Codec Info Tests

    [Theory]
    [InlineData("BBC One HEVC", "BBC One")]
    [InlineData("CNN H.264", "CNN")]
    [InlineData("Sports H265", "Sports")]
    [InlineData("Movie H.265", "Movie")]
    public void CleanChannelName_RemovesCodecInfo(string input, string expected)
    {
        var result = ChannelNameCleaner.CleanChannelName(input);
        result.Should().Be(expected);
    }

    #endregion

    #region Resolution Tests

    [Theory]
    [InlineData("BBC One 1080p", "BBC One")]
    [InlineData("CNN 720p", "CNN")]
    [InlineData("Sports 4K", "Sports")]
    [InlineData("Movie 2160p", "Movie")]
    public void CleanChannelName_RemovesResolution(string input, string expected)
    {
        var result = ChannelNameCleaner.CleanChannelName(input);
        result.Should().Be(expected);
    }

    #endregion

    #region Pipe Cleaning Tests

    [Theory]
    [InlineData("| BBC One |", "BBC One")]
    [InlineData("| CNN", "CNN")]
    [InlineData("Sports |", "Sports")]
    public void CleanChannelName_RemovesLeadingTrailingPipes(string input, string expected)
    {
        var result = ChannelNameCleaner.CleanChannelName(input);
        result.Should().Be(expected);
    }

    #endregion

    #region Complex Cleaning Tests

    [Theory]
    [InlineData("UK: BBC One | HD | HEVC", "BBC One")]
    [InlineData("NL | RTL 4 FHD", "RTL 4")]
    [InlineData("US: CNN [HD] 1080p", "CNN")]
    [InlineData("| Sports 1 | H.264", "Sports 1")]
    [InlineData("DE: ZDF | 4K | H.265", "ZDF")]
    public void CleanChannelName_HandlesComplexNames(string input, string expected)
    {
        var result = ChannelNameCleaner.CleanChannelName(input);
        result.Should().Be(expected);
    }

    #endregion

    #region User Terms Tests

    [Fact]
    public void CleanChannelName_RemovesUserDefinedTerms()
    {
        var input = "BBC One [Premium]";
        var userTerms = "[Premium]";

        var result = ChannelNameCleaner.CleanChannelName(input, userTerms);

        result.Should().Be("BBC One");
    }

    [Fact]
    public void CleanChannelName_RemovesMultipleUserTerms()
    {
        var input = "BBC One [Premium] [VIP]";
        var userTerms = "[Premium]\n[VIP]";

        var result = ChannelNameCleaner.CleanChannelName(input, userTerms);

        result.Should().Be("BBC One");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void CleanChannelName_ReturnsOriginalIfCleaningDisabled()
    {
        var input = "UK: BBC One | HD |";

        var result = ChannelNameCleaner.CleanChannelName(input, null, enableCleaning: false);

        result.Should().Be(input);
    }

    [Fact]
    public void CleanChannelName_ReturnsOriginalIfResultIsEmpty()
    {
        var input = "| HD |";

        var result = ChannelNameCleaner.CleanChannelName(input);

        result.Should().Be(input.Trim());
    }

    [Fact]
    public void CleanChannelName_HandlesNullInput()
    {
        string? input = null;

        var result = ChannelNameCleaner.CleanChannelName(input!);

        result.Should().BeNull();
    }

    [Fact]
    public void CleanChannelName_HandlesEmptyInput()
    {
        var input = "";

        var result = ChannelNameCleaner.CleanChannelName(input);

        result.Should().Be("");
    }

    [Fact]
    public void CleanChannelName_CollapsesMultipleSpaces()
    {
        var input = "BBC   One    News";

        var result = ChannelNameCleaner.CleanChannelName(input);

        result.Should().Be("BBC One News");
    }

    #endregion

    #region ParseUserTerms Tests

    [Fact]
    public void ParseUserTerms_ReturnsEmptyForNull()
    {
        var result = ChannelNameCleaner.ParseUserTerms(null).ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseUserTerms_ReturnsEmptyForWhitespace()
    {
        var result = ChannelNameCleaner.ParseUserTerms("   ").ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseUserTerms_ParsesMultipleLines()
    {
        var input = "term1\nterm2\r\nterm3";

        var result = ChannelNameCleaner.ParseUserTerms(input).ToList();

        result.Should().HaveCount(3);
        result.Should().Contain("term1");
        result.Should().Contain("term2");
        result.Should().Contain("term3");
    }

    [Fact]
    public void ParseUserTerms_TrimsWhitespace()
    {
        var input = "  term1  \n  term2  ";

        var result = ChannelNameCleaner.ParseUserTerms(input).ToList();

        result.Should().HaveCount(2);
        result.Should().Contain("term1");
        result.Should().Contain("term2");
    }

    #endregion
}
