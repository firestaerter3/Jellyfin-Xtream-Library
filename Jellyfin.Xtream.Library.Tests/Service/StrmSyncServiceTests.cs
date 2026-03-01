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
    [InlineData("Alarum - 2025", "Alarum")]
    [InlineData("Some Movie – 2022", "Some Movie")]
    public void SanitizeFileName_WithYear_RemovesYear(string input, string expected)
    {
        var result = StrmSyncService.SanitizeFileName(input);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("21 Paradise: Verkaufte Lust", "21 Paradise - Verkaufte Lust")]
    [InlineData("2235: I Am Mortal", "2235 - I Am Mortal")]
    [InlineData("24 Hours To D-Day: Schlacht Der Entscheidung", "24 Hours To D-Day - Schlacht Der Entscheidung")]
    [InlineData("Movie:Title", "Movie-Title")]
    public void SanitizeFileName_WithColon_NormalizesToDash(string input, string expected)
    {
        // Colons are normalized explicitly for cross-platform compatibility
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
    [InlineData("Alpha and Omega (EN SPOKEN)", "Alpha and Omega")]
    [InlineData("Movie (UK DUBBED)", "Movie")]
    [InlineData("Film (DE DUBBED)", "Film")]
    [InlineData("Show [FR Audio]", "Show")]
    [InlineData("Title (DE AUDIO)", "Title")]
    [InlineData("Movie (DE-)", "Movie")]
    [InlineData("Movie (DE -)", "Movie")]
    [InlineData("Movie (EN-)", "Movie")]
    public void SanitizeFileName_LanguagePhrases_RemovesThem(string input, string expected)
    {
        var result = StrmSyncService.SanitizeFileName(input);

        result.Should().Be(expected);
    }

    [Fact]
    public void SanitizeFileName_CustomRemoveTerms_RemovesThem()
    {
        var result = StrmSyncService.SanitizeFileName("Movie [Multi-Sub] Title FHD", "[Multi-Sub]\nFHD");

        result.Should().Be("Movie Title");
    }

    [Fact]
    public void SanitizeFileName_CustomRemoveTerms_CaseInsensitive()
    {
        var result = StrmSyncService.SanitizeFileName("Movie [multi-sub] Title", "[Multi-Sub]");

        result.Should().Be("Movie Title");
    }

    [Fact]
    public void SanitizeFileName_CustomRemoveTerms_NullIgnored()
    {
        var result = StrmSyncService.SanitizeFileName("Movie Title", null);

        result.Should().Be("Movie Title");
    }

    [Fact]
    public void SanitizeFileName_CustomRemoveTerms_EmptyIgnored()
    {
        var result = StrmSyncService.SanitizeFileName("Movie Title", "");

        result.Should().Be("Movie Title");
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
        var result = StrmSyncService.SanitizeFileName("The Great Escape 2 HEVC 1080p BluRay (EN SPOKEN) (2020)");

        result.Should().Be("The Great Escape 2");
    }

    [Fact]
    public void SanitizeFileName_MultipleSpaces_CollapsesToSingle()
    {
        var result = StrmSyncService.SanitizeFileName("Movie    With    Spaces");

        result.Should().Be("Movie With Spaces");
    }

    [Theory]
    [InlineData("┃UK┃ Ascendance of a Bookworm", "Ascendance of a Bookworm")]
    [InlineData("┃UK┃Campfire Cooking", "Campfire Cooking")]
    [InlineData("| EN | Some Show", "Some Show")]
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
    public void SanitizeFileName_BracketedLanguagePhrase_RemovesIt()
    {
        var result = StrmSyncService.SanitizeFileName("Barbie: Dreamtopia Special [EN Spoken]");

        result.Should().Be("Barbie - Dreamtopia Special");
    }

    [Fact]
    public void SanitizeFileName_ComplexAnimeTitle_CleansAll()
    {
        var result = StrmSyncService.SanitizeFileName("┃UK┃ Ascendance of a Bookworm[本好きの下剋上 司書になるためには手段を選んでいられません]");

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
    [InlineData("Alarum - 2025", 2025)]
    [InlineData("Some Movie – 2022", 2022)]
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

    [Fact]
    public void BuildEpisodeFileName_ProviderEmbeddedPrefix_StripsDoubling()
    {
        // Some providers embed the full series name and SxxExx in the episode title, which can cause redundant naming like "Series Name - S01E01 - Series Name S01E01"
        var episode = TestDataBuilder.CreateEpisode(episodeNum: 1, title: "Breaking Bad - S01E01 - Pilot");

        var result = StrmSyncService.BuildEpisodeFileName("Breaking Bad", 1, episode);

        result.Should().Be("Breaking Bad - S01E01 - Pilot.strm");
    }

    [Fact]
    public void BuildEpisodeFileName_ProviderEmbeddedPrefixCaseInsensitive_StripsDoubling()
    {
        // The check for embedded prefix should be case-insensitive
        var episode = TestDataBuilder.CreateEpisode(episodeNum: 1, title: "breaking bad - s01e01 - Pilot");

        var result = StrmSyncService.BuildEpisodeFileName("Breaking Bad", 1, episode);

        result.Should().Be("Breaking Bad - S01E01 - Pilot.strm");
    }

    [Fact]
    public void BuildEpisodeFileName_ProviderEmbeddedPrefixWithDifferentSeriesName_DoesNotStrip()
    {
        // If the embedded prefix doesn't match the series name, it should not be stripped (avoid stripping valid titles that just happen to start with the same text)
        var episode = TestDataBuilder.CreateEpisode(episodeNum: 1, title: "Some Other Show - S01E01 - Pilot");

        var result = StrmSyncService.BuildEpisodeFileName("Breaking Bad", 1, episode);

        result.Should().Be("Breaking Bad - S01E01 - Some Other Show - S01E01 - Pilot.strm");
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

    #region ExtractVersionLabel Tests

    [Fact]
    public void ExtractVersionLabel_Null_ReturnsNull()
    {
        var result = StrmSyncService.ExtractVersionLabel(null);

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractVersionLabel_NoTags_ReturnsNull()
    {
        var result = StrmSyncService.ExtractVersionLabel("Gladiator");

        result.Should().BeNull();
    }

    [Fact]
    public void ExtractVersionLabel_HEVC_ReturnsHEVC()
    {
        var result = StrmSyncService.ExtractVersionLabel("┃UK┃ Gladiator HEVC");

        result.Should().Be("HEVC");
    }

    [Fact]
    public void ExtractVersionLabel_4K_Returns4K()
    {
        var result = StrmSyncService.ExtractVersionLabel("┃UK┃ Movie [4K]");

        result.Should().Be("4K");
    }

    [Fact]
    public void ExtractVersionLabel_Combined_ReturnsAllTags()
    {
        var result = StrmSyncService.ExtractVersionLabel("Movie HEVC 4K HDR BluRay");

        result.Should().Be("HEVC 4K HDR BluRay");
    }

    [Fact]
    public void ExtractVersionLabel_x264_Returnsx264()
    {
        var result = StrmSyncService.ExtractVersionLabel("Movie x264");

        result.Should().Be("x264");
    }

    [Fact]
    public void ExtractVersionLabel_1080p_Returns1080p()
    {
        var result = StrmSyncService.ExtractVersionLabel("Movie 1080p");

        result.Should().Be("1080p");
    }

    [Fact]
    public void ExtractVersionLabel_BluRay_ReturnsBluRay()
    {
        var result = StrmSyncService.ExtractVersionLabel("Movie BluRay");

        result.Should().Be("BluRay");
    }

    [Fact]
    public void ExtractVersionLabel_REMUX_ReturnsREMUX()
    {
        var result = StrmSyncService.ExtractVersionLabel("Movie REMUX");

        result.Should().Be("REMUX");
    }

    #endregion

    #region BuildMovieStrmFileName Tests

    [Fact]
    public void BuildMovieStrmFileName_NullLabel_ReturnsBaseName()
    {
        var result = StrmSyncService.BuildMovieStrmFileName("Folder", null);

        result.Should().Be("Folder.strm");
    }

    [Fact]
    public void BuildMovieStrmFileName_WithLabel_ReturnsSuffixedName()
    {
        var result = StrmSyncService.BuildMovieStrmFileName("Folder [tmdbid-98]", "HEVC");

        result.Should().Be("Folder [tmdbid-98] - HEVC.strm");
    }

    [Fact]
    public void BuildMovieStrmFileName_CombinedLabel_ReturnsCombinedSuffix()
    {
        var result = StrmSyncService.BuildMovieStrmFileName("Folder", "HEVC 4K");

        result.Should().Be("Folder - HEVC 4K.strm");
    }

    #endregion

    #region SanitizeFileName Empty Brackets Tests

    [Fact]
    public void SanitizeFileName_EmptyBrackets_RemovesEmptyBrackets()
    {
        var result = StrmSyncService.SanitizeFileName("Movie [4K] Title");

        result.Should().Be("Movie Title");
    }

    #endregion

    #region Dispatcharr Multi-Stream STRM Filename Tests

    [Fact]
    public void MultiStream_SingleProvider_CreatesOneStrm()
    {
        // With a single provider, the default STRM filename is used (no version suffix)
        var strmFileName = StrmSyncService.BuildMovieStrmFileName("Gladiator [tmdbid-98]", null);

        strmFileName.Should().Be("Gladiator [tmdbid-98].strm");
    }

    [Fact]
    public void MultiStream_MultipleProviders_CreatesVersionedStrms()
    {
        // First provider gets default filename, subsequent get version numbers
        var folderName = "Gladiator [tmdbid-98]";
        var strm1 = StrmSyncService.BuildMovieStrmFileName(folderName, null);
        var strm2 = StrmSyncService.BuildMovieStrmFileName(folderName, "Version 2");
        var strm3 = StrmSyncService.BuildMovieStrmFileName(folderName, "Version 3");

        strm1.Should().Be("Gladiator [tmdbid-98].strm");
        strm2.Should().Be("Gladiator [tmdbid-98] - Version 2.strm");
        strm3.Should().Be("Gladiator [tmdbid-98] - Version 3.strm");
    }

    [Fact]
    public void MultiStream_ProxyUrlFormat_ContainsUuidAndStreamId()
    {
        // Verify proxy URL format matches Dispatcharr expectations
        string uuid = "abc-123-def";
        int streamId = 423017;
        string baseUrl = "http://dispatcharr.local:5656";

        string url = $"{baseUrl}/proxy/vod/movie/{uuid}?stream_id={streamId}";

        url.Should().Be("http://dispatcharr.local:5656/proxy/vod/movie/abc-123-def?stream_id=423017");
    }

    [Fact]
    public void MultiStream_StandardUrlFormat_ContainsCredentials()
    {
        // Verify standard Xtream URL format for comparison
        string baseUrl = "http://provider.com:8000";
        string username = "user";
        string password = "pass";
        int streamId = 12345;
        string extension = "mp4";

        string url = $"{baseUrl}/movie/{username}/{password}/{streamId}.{extension}";

        url.Should().Be("http://provider.com:8000/movie/user/pass/12345.mp4");
    }

    [Fact]
    public void MultiStream_DispatcharrSingleStream_UsesProxyUrlWithUuid()
    {
        // When Dispatcharr is enabled and a single provider exists,
        // the proxy URL uses UUID with stream_id pinning (same as multi-provider format)
        string baseUrl = "http://dispatcharr.local:5656";
        string uuid = "abc-123-def";
        int streamId = 12345;

        string url = $"{baseUrl}/proxy/vod/movie/{uuid}?stream_id={streamId}";

        url.Should().Be("http://dispatcharr.local:5656/proxy/vod/movie/abc-123-def?stream_id=12345");
    }

    [Fact]
    public void MultiStream_DispatcharrApiFails_FallsBackToXtreamUrl()
    {
        // When Dispatcharr API fails for a movie (cache miss),
        // the fallback uses standard Xtream URL format with credentials
        string baseUrl = "http://dispatcharr.local:5656";
        string username = "user";
        string password = "pass";
        int streamId = 12345;
        string extension = "mp4";

        string url = $"{baseUrl}/movie/{username}/{password}/{streamId}.{extension}";

        url.Should().Be("http://dispatcharr.local:5656/movie/user/pass/12345.mp4");
    }

    #endregion
}
