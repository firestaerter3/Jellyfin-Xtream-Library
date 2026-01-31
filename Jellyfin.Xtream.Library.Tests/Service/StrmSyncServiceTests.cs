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
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Jellyfin.Xtream.Library.Tests.Helpers;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class StrmSyncServiceTests
{
    #region SanitizeFileName Tests

    [Fact]
    public void SanitizeFileName_NullInput_ReturnsUnknown()
    {
        var result = StrmSyncService.SanitizeFileName(null);

        result.Should().Be("Unknown");
    }

    [Fact]
    public void SanitizeFileName_EmptyInput_ReturnsUnknown()
    {
        var result = StrmSyncService.SanitizeFileName(string.Empty);

        result.Should().Be("Unknown");
    }

    [Theory]
    [InlineData("The Matrix (1999)", "The Matrix")]
    [InlineData("Inception (2010)", "Inception")]
    [InlineData("Movie Title (2024)", "Movie Title")]
    public void SanitizeFileName_WithYear_RemovesYear(string input, string expected)
    {
        var result = StrmSyncService.SanitizeFileName(input);

        result.Should().Be(expected);
    }

    [Fact]
    public void SanitizeFileName_WithSlash_ReplacesWithUnderscore()
    {
        // Forward slash is invalid on all platforms
        var result = StrmSyncService.SanitizeFileName("Test/Movie");

        result.Should().Be("Test_Movie");
    }

    [Fact]
    public void SanitizeFileName_WithNullChar_ReplacesWithUnderscore()
    {
        // Null character is invalid on all platforms
        var result = StrmSyncService.SanitizeFileName("Test\0Movie");

        result.Should().Be("Test_Movie");
    }

    [Theory]
    [InlineData("___test___", "test")]
    [InlineData("a___b___c", "a_b_c")]
    public void SanitizeFileName_MultipleUnderscores_CollapsesToSingle(string input, string expected)
    {
        var result = StrmSyncService.SanitizeFileName(input);

        result.Should().Be(expected);
    }

    [Fact]
    public void SanitizeFileName_LeadingTrailingSpaces_TrimsSpaces()
    {
        var result = StrmSyncService.SanitizeFileName("  test  ");

        result.Should().Be("test");
    }

    [Fact]
    public void SanitizeFileName_AlreadyClean_ReturnsUnchanged()
    {
        var result = StrmSyncService.SanitizeFileName("Already Clean Name");

        result.Should().Be("Already Clean Name");
    }

    [Fact]
    public void SanitizeFileName_OnlySlashes_ReturnsUnknown()
    {
        // Slash is invalid on all platforms
        var result = StrmSyncService.SanitizeFileName("///");

        result.Should().Be("Unknown");
    }

    #endregion

    #region ExtractYear Tests

    [Fact]
    public void ExtractYear_NullInput_ReturnsNull()
    {
        var result = StrmSyncService.ExtractYear(null);

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractYear_EmptyInput_ReturnsNull()
    {
        var result = StrmSyncService.ExtractYear(string.Empty);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("The Matrix (1999)", 1999)]
    [InlineData("Inception (2010)", 2010)]
    [InlineData("Movie (2024)", 2024)]
    public void ExtractYear_ValidYear_ReturnsYear(string input, int expected)
    {
        var result = StrmSyncService.ExtractYear(input);

        result.Should().Be(expected);
    }

    [Fact]
    public void ExtractYear_NoYear_ReturnsNull()
    {
        var result = StrmSyncService.ExtractYear("Movie Without Year");

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractYear_YearTooOld_ReturnsNull()
    {
        var result = StrmSyncService.ExtractYear("Old Movie (1800)");

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractYear_YearTooFarFuture_ReturnsNull()
    {
        var result = StrmSyncService.ExtractYear("Future Movie (2050)");

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractYear_YearInMiddle_ReturnsNull()
    {
        var result = StrmSyncService.ExtractYear("Movie (2024) Extra Text");

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractYear_BoundaryYear1900_ReturnsYear()
    {
        var result = StrmSyncService.ExtractYear("Old Movie (1900)");

        result.Should().Be(1900);
    }

    [Fact]
    public void ExtractYear_BoundaryYearCurrentPlus5_ReturnsYear()
    {
        int futureYear = DateTime.Now.Year + 5;
        var result = StrmSyncService.ExtractYear($"Future Movie ({futureYear})");

        result.Should().Be(futureYear);
    }

    [Fact]
    public void ExtractYear_BoundaryYearCurrentPlus6_ReturnsNull()
    {
        int tooFarYear = DateTime.Now.Year + 6;
        var result = StrmSyncService.ExtractYear($"Future Movie ({tooFarYear})");

        result.Should().BeNull();
    }

    #endregion

    #region BuildEpisodeFileName Tests

    [Fact]
    public void BuildEpisodeFileName_WithTitle_ReturnsFullFileName()
    {
        var episode = TestDataBuilder.CreateEpisode(episodeNum: 1, title: "Pilot");

        var result = StrmSyncService.BuildEpisodeFileName("Breaking Bad", 1, episode);

        result.Should().Be("Breaking Bad - S01E01 - Pilot.strm");
    }

    [Fact]
    public void BuildEpisodeFileName_EmptyStringTitle_IncludesUnknownTitle()
    {
        // Empty string gets sanitized to "Unknown" by SanitizeFileName
        var episode = TestDataBuilder.CreateEpisode(episodeNum: 10, title: "");

        var result = StrmSyncService.BuildEpisodeFileName("Show", 2, episode);

        result.Should().Be("Show - S02E10 - Unknown.strm");
    }

    [Fact]
    public void BuildEpisodeFileName_GenericEpisodeTitle_StripsTitle()
    {
        var episode = TestDataBuilder.CreateEpisode(episodeNum: 5, title: "Episode 5");

        var result = StrmSyncService.BuildEpisodeFileName("Show", 1, episode);

        result.Should().Be("Show - S01E05.strm");
    }

    [Fact]
    public void BuildEpisodeFileName_GenericEpisodeTitleCaseInsensitive_StripsTitle()
    {
        var episode = TestDataBuilder.CreateEpisode(episodeNum: 5, title: "EPISODE 5");

        var result = StrmSyncService.BuildEpisodeFileName("Show", 1, episode);

        result.Should().Be("Show - S01E05.strm");
    }

    [Fact]
    public void BuildEpisodeFileName_ZeroPaddedNumbers_FormatsCorrectly()
    {
        var episode = TestDataBuilder.CreateEpisode(episodeNum: 5, title: "The Title");

        var result = StrmSyncService.BuildEpisodeFileName("Show", 1, episode);

        result.Should().Be("Show - S01E05 - The Title.strm");
    }

    [Fact]
    public void BuildEpisodeFileName_DoubleDigitNumbers_FormatsCorrectly()
    {
        var episode = TestDataBuilder.CreateEpisode(episodeNum: 12, title: "The Title");

        var result = StrmSyncService.BuildEpisodeFileName("Show", 10, episode);

        result.Should().Be("Show - S10E12 - The Title.strm");
    }

    [Fact]
    public void BuildEpisodeFileName_TitleWithSlash_SanitizesTitle()
    {
        // Slash is universally invalid in filenames
        var episode = TestDataBuilder.CreateEpisode(episodeNum: 1, title: "The Special/Episode");

        var result = StrmSyncService.BuildEpisodeFileName("Show", 1, episode);

        result.Should().Be("Show - S01E01 - The Special_Episode.strm");
    }

    [Fact]
    public void BuildEpisodeFileName_WhitespaceOnlyTitle_BecomesUnknown()
    {
        // Whitespace-only title gets sanitized to "Unknown" by SanitizeFileName
        var episode = TestDataBuilder.CreateEpisode(episodeNum: 1, title: "   ");

        var result = StrmSyncService.BuildEpisodeFileName("Show", 1, episode);

        result.Should().Be("Show - S01E01 - Unknown.strm");
    }

    #endregion

    #region CleanupEmptyDirectories Tests

    /// <summary>
    /// Gets a resolved temp directory path that handles macOS symlink from /tmp to /private/tmp.
    /// </summary>
    private static string GetResolvedTempPath()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"xtream_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempPath);
        // Get the resolved path after directory creation
        var resolvedPath = new DirectoryInfo(tempPath).FullName;
        return resolvedPath;
    }

    [Fact]
    public void CleanupEmptyDirectories_EmptyDirectory_DeletesIt()
    {
        var tempDir = GetResolvedTempPath();
        var subDir = Path.Combine(tempDir, "sub");
        Directory.CreateDirectory(subDir);

        StrmSyncService.CleanupEmptyDirectories(subDir, tempDir);

        Directory.Exists(subDir).Should().BeFalse();
        Directory.Exists(tempDir).Should().BeTrue();

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public void CleanupEmptyDirectories_StopsAtBasePath()
    {
        var tempDir = GetResolvedTempPath();

        StrmSyncService.CleanupEmptyDirectories(tempDir, tempDir);

        Directory.Exists(tempDir).Should().BeTrue();

        // Cleanup
        Directory.Delete(tempDir);
    }

    [Fact]
    public void CleanupEmptyDirectories_NonEmptyDirectory_KeepsIt()
    {
        var tempDir = GetResolvedTempPath();
        var subDir = Path.Combine(tempDir, "sub");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "test.txt"), "content");

        StrmSyncService.CleanupEmptyDirectories(subDir, tempDir);

        Directory.Exists(subDir).Should().BeTrue();

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public void CleanupEmptyDirectories_NestedEmptyDirs_DeletesAll()
    {
        var tempDir = GetResolvedTempPath();
        var level1 = Path.Combine(tempDir, "level1");
        var level2 = Path.Combine(level1, "level2");
        var level3 = Path.Combine(level2, "level3");
        Directory.CreateDirectory(level3);

        StrmSyncService.CleanupEmptyDirectories(level3, tempDir);

        Directory.Exists(level3).Should().BeFalse();
        Directory.Exists(level2).Should().BeFalse();
        Directory.Exists(level1).Should().BeFalse();
        Directory.Exists(tempDir).Should().BeTrue();

        // Cleanup
        Directory.Delete(tempDir);
    }

    #endregion
}
