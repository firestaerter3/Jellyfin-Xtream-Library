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

    [Theory]
    [InlineData("Alpha and Omega (NL GESPROKEN)", "Alpha and Omega")]
    [InlineData("Movie (EN SPOKEN)", "Movie")]
    [InlineData("Film (NL DUBBED)", "Film")]
    [InlineData("Show [NL Gesproken]", "Show")]
    [InlineData("Title (DE AUDIO)", "Title")]
    public void SanitizeFileName_LanguagePhrases_RemovesThem(string input, string expected)
    {
        var result = StrmSyncService.SanitizeFileName(input);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Movie HEVC", "Movie")]
    [InlineData("Movie x264", "Movie")]
    [InlineData("Movie x265", "Movie")]
    [InlineData("Movie H.264", "Movie")]
    [InlineData("Movie H265", "Movie")]
    [InlineData("Movie 10bit", "Movie")]
    public void SanitizeFileName_CodecTags_RemovesThem(string input, string expected)
    {
        var result = StrmSyncService.SanitizeFileName(input);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Movie 4K", "Movie")]
    [InlineData("Movie 1080p", "Movie")]
    [InlineData("Movie 720p", "Movie")]
    [InlineData("Movie 2160p", "Movie")]
    [InlineData("Movie HDR", "Movie")]
    [InlineData("Movie HDR10", "Movie")]
    [InlineData("Movie UHD", "Movie")]
    public void SanitizeFileName_QualityTags_RemovesThem(string input, string expected)
    {
        var result = StrmSyncService.SanitizeFileName(input);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Movie BluRay", "Movie")]
    [InlineData("Movie Blu-Ray", "Movie")]
    [InlineData("Movie WEBRip", "Movie")]
    [InlineData("Movie WEB-DL", "Movie")]
    [InlineData("Movie HDTV", "Movie")]
    [InlineData("Movie DVDRip", "Movie")]
    [InlineData("Movie REMUX", "Movie")]
    public void SanitizeFileName_SourceTags_RemovesThem(string input, string expected)
    {
        var result = StrmSyncService.SanitizeFileName(input);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Angela'\\'s Christmas", "Angela's Christmas")]
    [InlineData("A Witches'\\'' Ball", "A Witches' Ball")]
    public void SanitizeFileName_MalformedQuotes_FixesThem(string input, string expected)
    {
        var result = StrmSyncService.SanitizeFileName(input);

        result.Should().Be(expected);
    }

    [Fact]
    public void SanitizeFileName_CombinedTags_RemovesAll()
    {
        var result = StrmSyncService.SanitizeFileName("Baas In Eigen Bos 2 HEVC 1080p BluRay (NL GESPROKEN) (2020)");

        result.Should().Be("Baas In Eigen Bos 2");
    }

    [Fact]
    public void SanitizeFileName_MultipleSpaces_CollapsesToSingle()
    {
        var result = StrmSyncService.SanitizeFileName("Movie    With    Spaces");

        result.Should().Be("Movie With Spaces");
    }

    [Theory]
    [InlineData("┃NL┃ Ascendance of a Bookworm", "Ascendance of a Bookworm")]
    [InlineData("┃NL┃Campfire Cooking", "Campfire Cooking")]
    [InlineData("| NL | Some Show", "Some Show")]
    public void SanitizeFileName_PrefixLanguageTags_RemovesThem(string input, string expected)
    {
        var result = StrmSyncService.SanitizeFileName(input);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Ascendance of a Bookworm[本好きの下剋上]", "Ascendance of a Bookworm")]
    [InlineData("Show Name [日本語タイトル]", "Show Name")]
    [InlineData("Anime (韓国語)", "Anime")]
    public void SanitizeFileName_AsianBracketedText_RemovesThem(string input, string expected)
    {
        var result = StrmSyncService.SanitizeFileName(input);

        result.Should().Be(expected);
    }

    [Fact]
    public void SanitizeFileName_MisspelledGesproken_RemovesIt()
    {
        var result = StrmSyncService.SanitizeFileName("Barbie: Dreamtopia Special [NL Gepsroken]");

        result.Should().Be("Barbie: Dreamtopia Special");
    }

    [Fact]
    public void SanitizeFileName_ComplexAnimeTitle_CleansAll()
    {
        var result = StrmSyncService.SanitizeFileName("┃NL┃ Ascendance of a Bookworm[本好きの下剋上 司書になるためには手段を選んでいられません]");

        result.Should().Be("Ascendance of a Bookworm");
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
        var result = new SyncResult();

        StrmSyncService.CleanupEmptyDirectories(subDir, tempDir, tempDir, result);

        Directory.Exists(subDir).Should().BeFalse();
        Directory.Exists(tempDir).Should().BeTrue();

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public void CleanupEmptyDirectories_StopsAtBasePath()
    {
        var tempDir = GetResolvedTempPath();
        var result = new SyncResult();

        StrmSyncService.CleanupEmptyDirectories(tempDir, tempDir, tempDir, result);

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
        var result = new SyncResult();

        StrmSyncService.CleanupEmptyDirectories(subDir, tempDir, tempDir, result);

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
        var result = new SyncResult();

        StrmSyncService.CleanupEmptyDirectories(level3, tempDir, tempDir, result);

        Directory.Exists(level3).Should().BeFalse();
        Directory.Exists(level2).Should().BeFalse();
        Directory.Exists(level1).Should().BeFalse();
        Directory.Exists(tempDir).Should().BeTrue();

        // Cleanup
        Directory.Delete(tempDir);
    }

    [Fact]
    public void CleanupEmptyDirectories_TracksSeriesDeleted()
    {
        var tempDir = GetResolvedTempPath();
        var seriesDir = Path.Combine(tempDir, "Series");
        var showDir = Path.Combine(seriesDir, "Test Show (2024)");
        var seasonDir = Path.Combine(showDir, "Season 1");
        Directory.CreateDirectory(seasonDir);
        // Keep a file in seriesDir so it doesn't get deleted
        File.WriteAllText(Path.Combine(seriesDir, ".keep"), string.Empty);
        var result = new SyncResult();

        // Clean up from season folder (simulating last episode deleted)
        StrmSyncService.CleanupEmptyDirectories(seasonDir, tempDir, seriesDir, result);

        Directory.Exists(seasonDir).Should().BeFalse();
        Directory.Exists(showDir).Should().BeFalse();
        Directory.Exists(seriesDir).Should().BeTrue();
        result.SeasonsDeleted.Should().Be(1);
        result.SeriesDeleted.Should().Be(1);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public void CleanupEmptyDirectories_TracksOnlySeasonDeleted_WhenSeriesNotEmpty()
    {
        var tempDir = GetResolvedTempPath();
        var seriesDir = Path.Combine(tempDir, "Series");
        var showDir = Path.Combine(seriesDir, "Test Show (2024)");
        var season1Dir = Path.Combine(showDir, "Season 1");
        var season2Dir = Path.Combine(showDir, "Season 2");
        Directory.CreateDirectory(season1Dir);
        Directory.CreateDirectory(season2Dir);
        // Put a file in Season 2 so the series folder won't be empty
        File.WriteAllText(Path.Combine(season2Dir, "episode.strm"), "url");
        var result = new SyncResult();

        // Clean up Season 1 (empty)
        StrmSyncService.CleanupEmptyDirectories(season1Dir, tempDir, seriesDir, result);

        Directory.Exists(season1Dir).Should().BeFalse();
        Directory.Exists(showDir).Should().BeTrue();
        result.SeasonsDeleted.Should().Be(1);
        result.SeriesDeleted.Should().Be(0);

        // Cleanup
        Directory.Delete(tempDir, true);
    }

    #endregion

    #region ParseFolderMappings Tests

    [Fact]
    public void ParseFolderMappings_NullInput_ReturnsEmptyDictionary()
    {
        var result = StrmSyncService.ParseFolderMappings(null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseFolderMappings_EmptyInput_ReturnsEmptyDictionary()
    {
        var result = StrmSyncService.ParseFolderMappings(string.Empty);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseFolderMappings_SingleMapping_ParsesCorrectly()
    {
        var result = StrmSyncService.ParseFolderMappings("Kids=10,15,20");

        result.Should().ContainKey(10);
        result.Should().ContainKey(15);
        result.Should().ContainKey(20);
        result[10].Should().Contain("Kids");
        result[15].Should().Contain("Kids");
        result[20].Should().Contain("Kids");
    }

    [Fact]
    public void ParseFolderMappings_MultipleLines_ParsesAll()
    {
        var result = StrmSyncService.ParseFolderMappings("Kids=10,15\nDocumentary=30");

        result.Should().HaveCount(3);
        result[10].Should().Contain("Kids");
        result[15].Should().Contain("Kids");
        result[30].Should().Contain("Documentary");
    }

    [Fact]
    public void ParseFolderMappings_CategoryInMultipleFolders_TracksAllFolders()
    {
        var result = StrmSyncService.ParseFolderMappings("Kids=10\nFamily=10");

        result.Should().ContainKey(10);
        result[10].Should().HaveCount(2);
        result[10].Should().Contain("Kids");
        result[10].Should().Contain("Family");
    }

    [Fact]
    public void ParseFolderMappings_WhitespaceHandling_TrimsCorrectly()
    {
        var result = StrmSyncService.ParseFolderMappings("  Kids  =  10 , 15  ");

        result.Should().ContainKey(10);
        result.Should().ContainKey(15);
        result[10].Should().Contain("Kids");
    }

    [Fact]
    public void ParseFolderMappings_InvalidLine_SkipsIt()
    {
        var result = StrmSyncService.ParseFolderMappings("Kids=10\nInvalidLine\nDocumentary=20");

        result.Should().HaveCount(2);
        result.Should().ContainKey(10);
        result.Should().ContainKey(20);
    }

    [Fact]
    public void ParseFolderMappings_NonNumericCategoryId_SkipsIt()
    {
        var result = StrmSyncService.ParseFolderMappings("Kids=abc,10,xyz");

        result.Should().HaveCount(1);
        result.Should().ContainKey(10);
    }

    #endregion
}
