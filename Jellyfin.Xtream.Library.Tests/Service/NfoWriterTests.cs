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

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class NfoWriterTests : IDisposable
{
    private readonly string _tempDirectory;

    public NfoWriterTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    #region Movie NFO Tests

    [Fact]
    public async Task WriteMovieNfo_WithVideoAndAudio_WritesValidXml()
    {
        var nfoPath = Path.Combine(_tempDirectory, "movie.nfo");
        var video = new VideoInfo { CodecName = "h264", Width = 1920, Height = 1080, AspectRatio = "16:9" };
        var audio = new AudioInfo { CodecName = "aac", Channels = 6 };

        var result = await NfoWriter.WriteMovieNfoAsync(nfoPath, "Test Movie", video, audio, 7200, null, CancellationToken.None);

        result.Should().BeTrue();
        File.Exists(nfoPath).Should().BeTrue();

        var xml = XDocument.Load(nfoPath);
        xml.Root.Should().NotBeNull();
        xml.Root!.Name.LocalName.Should().Be("movie");
        xml.Root.Element("title")!.Value.Should().Be("Test Movie");
        xml.Root.Descendants("codec").Should().HaveCount(2);  // Video and audio codecs
        xml.Root.Descendants("width").First().Value.Should().Be("1920");
        xml.Root.Descendants("height").First().Value.Should().Be("1080");
        xml.Root.Descendants("channels").First().Value.Should().Be("6");
        xml.Root.Descendants("durationinseconds").First().Value.Should().Be("7200");
    }

    [Fact]
    public async Task WriteMovieNfo_WithTmdbId_WritesUniqueIdTag()
    {
        var nfoPath = Path.Combine(_tempDirectory, "movie_tmdb.nfo");

        var result = await NfoWriter.WriteMovieNfoAsync(nfoPath, "The Matrix", null, null, null, 603, CancellationToken.None);

        result.Should().BeTrue();
        File.Exists(nfoPath).Should().BeTrue();

        var xml = XDocument.Load(nfoPath);
        xml.Root!.Name.LocalName.Should().Be("movie");
        xml.Root.Element("title")!.Value.Should().Be("The Matrix");
        var uniqueid = xml.Root.Element("uniqueid");
        uniqueid.Should().NotBeNull();
        uniqueid!.Value.Should().Be("603");
        uniqueid.Attribute("type")!.Value.Should().Be("tmdb");
        uniqueid.Attribute("default")!.Value.Should().Be("true");
        xml.Root.Element("fileinfo").Should().BeNull();  // No media info, no fileinfo element
    }

    [Fact]
    public async Task WriteMovieNfo_WithTmdbIdAndMediaInfo_WritesUniqueIdAndFileInfo()
    {
        var nfoPath = Path.Combine(_tempDirectory, "movie_tmdb_media.nfo");
        var video = new VideoInfo { CodecName = "h264", Width = 1920, Height = 1080 };
        var audio = new AudioInfo { CodecName = "aac", Channels = 6 };

        var result = await NfoWriter.WriteMovieNfoAsync(nfoPath, "The Matrix", video, audio, 8160, 603, CancellationToken.None);

        result.Should().BeTrue();

        var xml = XDocument.Load(nfoPath);
        var uniqueid = xml.Root!.Element("uniqueid");
        uniqueid.Should().NotBeNull();
        uniqueid!.Value.Should().Be("603");
        xml.Root.Element("fileinfo").Should().NotBeNull();
    }

    [Fact]
    public async Task WriteMovieNfo_NullTmdbIdAndNullMedia_ReturnsFalse()
    {
        var nfoPath = Path.Combine(_tempDirectory, "movie_no_data.nfo");

        var result = await NfoWriter.WriteMovieNfoAsync(nfoPath, "Test Movie", null, null, null, null, CancellationToken.None);

        result.Should().BeFalse();
        File.Exists(nfoPath).Should().BeFalse();
    }

    [Fact]
    public async Task WriteMovieNfo_VideoOnly_OmitsAudio()
    {
        var nfoPath = Path.Combine(_tempDirectory, "movie_video_only.nfo");
        var video = new VideoInfo { CodecName = "h265", Width = 3840, Height = 2160 };

        var result = await NfoWriter.WriteMovieNfoAsync(nfoPath, "4K Movie", video, null, 5400, null, CancellationToken.None);

        result.Should().BeTrue();
        File.Exists(nfoPath).Should().BeTrue();

        var xml = XDocument.Load(nfoPath);
        xml.Root!.Descendants("video").Should().ContainSingle();
        xml.Root.Descendants("audio").Should().BeEmpty();
    }

    [Fact]
    public async Task WriteMovieNfo_XmlEscapesSpecialCharacters()
    {
        var nfoPath = Path.Combine(_tempDirectory, "movie_special_chars.nfo");
        var video = new VideoInfo { CodecName = "h264" };

        var result = await NfoWriter.WriteMovieNfoAsync(
            nfoPath,
            "Movie with <Special> & \"Chars\"",
            video,
            null,
            null,
            null,
            CancellationToken.None);

        result.Should().BeTrue();

        var xml = XDocument.Load(nfoPath);
        xml.Root!.Element("title")!.Value.Should().Be("Movie with <Special> & \"Chars\"");
    }

    #endregion

    #region Show NFO Tests

    [Fact]
    public async Task WriteShowNfo_WithTvdbId_WritesPrimaryIdentifier()
    {
        var nfoPath = Path.Combine(_tempDirectory, "tvshow.nfo");

        var result = await NfoWriter.WriteShowNfoAsync(nfoPath, "Breaking Bad", null, 81189, CancellationToken.None);

        result.Should().BeTrue();
        File.Exists(nfoPath).Should().BeTrue();

        var xml = XDocument.Load(nfoPath);
        xml.Root!.Name.LocalName.Should().Be("tvshow");
        xml.Root.Element("title")!.Value.Should().Be("Breaking Bad");
        var uniqueid = xml.Root.Element("uniqueid");
        uniqueid.Should().NotBeNull();
        uniqueid!.Value.Should().Be("81189");
        uniqueid.Attribute("type")!.Value.Should().Be("tvdb");
        uniqueid.Attribute("default")!.Value.Should().Be("true");
    }

    [Fact]
    public async Task WriteShowNfo_WithTmdbIdOnly_WritesTmdbAsDefault()
    {
        var nfoPath = Path.Combine(_tempDirectory, "tvshow_tmdb.nfo");

        var result = await NfoWriter.WriteShowNfoAsync(nfoPath, "Stranger Things", 66732, null, CancellationToken.None);

        result.Should().BeTrue();

        var xml = XDocument.Load(nfoPath);
        var uniqueid = xml.Root!.Element("uniqueid");
        uniqueid.Should().NotBeNull();
        uniqueid!.Value.Should().Be("66732");
        uniqueid.Attribute("type")!.Value.Should().Be("tmdb");
        uniqueid.Attribute("default")!.Value.Should().Be("true");
    }

    [Fact]
    public async Task WriteShowNfo_WithBothIds_WritesTvdbAsDefaultAndTmdbAsSecondary()
    {
        var nfoPath = Path.Combine(_tempDirectory, "tvshow_both.nfo");

        var result = await NfoWriter.WriteShowNfoAsync(nfoPath, "Breaking Bad", 1396, 81189, CancellationToken.None);

        result.Should().BeTrue();

        var xml = XDocument.Load(nfoPath);
        var uniqueids = xml.Root!.Elements("uniqueid").ToList();
        uniqueids.Should().HaveCount(2);

        var tvdb = uniqueids.First(e => e.Attribute("type")!.Value == "tvdb");
        tvdb.Value.Should().Be("81189");
        tvdb.Attribute("default")!.Value.Should().Be("true");

        var tmdb = uniqueids.First(e => e.Attribute("type")!.Value == "tmdb");
        tmdb.Value.Should().Be("1396");
        tmdb.Attribute("default").Should().BeNull();
    }

    [Fact]
    public async Task WriteShowNfo_NoIds_ReturnsFalse()
    {
        var nfoPath = Path.Combine(_tempDirectory, "tvshow_no_ids.nfo");

        var result = await NfoWriter.WriteShowNfoAsync(nfoPath, "Unknown Show", null, null, CancellationToken.None);

        result.Should().BeFalse();
        File.Exists(nfoPath).Should().BeFalse();
    }

    #endregion

    #region Episode NFO Tests

    [Fact]
    public async Task WriteEpisodeNfo_WritesAllFields()
    {
        var nfoPath = Path.Combine(_tempDirectory, "episode.nfo");
        var video = new VideoInfo { CodecName = "h264", Width = 1920, Height = 1080 };
        var audio = new AudioInfo { CodecName = "aac", Channels = 2 };

        var result = await NfoWriter.WriteEpisodeNfoAsync(
            nfoPath,
            "Test Series",
            1,
            5,
            "Pilot Episode",
            video,
            audio,
            2700,
            CancellationToken.None);

        result.Should().BeTrue();
        File.Exists(nfoPath).Should().BeTrue();

        var xml = XDocument.Load(nfoPath);
        xml.Root.Should().NotBeNull();
        xml.Root!.Name.LocalName.Should().Be("episodedetails");
        xml.Root.Element("title")!.Value.Should().Be("Pilot Episode");
        xml.Root.Element("showtitle")!.Value.Should().Be("Test Series");
        xml.Root.Element("season")!.Value.Should().Be("1");
        xml.Root.Element("episode")!.Value.Should().Be("5");
        xml.Root.Descendants("durationinseconds").First().Value.Should().Be("2700");
    }

    [Fact]
    public async Task WriteEpisodeNfo_NullTitle_UsesDefaultEpisodeNumber()
    {
        var nfoPath = Path.Combine(_tempDirectory, "episode_no_title.nfo");
        var video = new VideoInfo { CodecName = "h264" };

        var result = await NfoWriter.WriteEpisodeNfoAsync(
            nfoPath,
            "Series",
            2,
            3,
            null,
            video,
            null,
            null,
            CancellationToken.None);

        result.Should().BeTrue();

        var xml = XDocument.Load(nfoPath);
        xml.Root!.Element("title")!.Value.Should().Be("Episode 3");
    }

    [Fact]
    public async Task WriteEpisodeNfo_NullVideoAndAudio_ReturnsFalse()
    {
        var nfoPath = Path.Combine(_tempDirectory, "episode_no_media.nfo");

        var result = await NfoWriter.WriteEpisodeNfoAsync(
            nfoPath,
            "Series",
            1,
            1,
            "Title",
            null,
            null,
            null,
            CancellationToken.None);

        result.Should().BeFalse();
        File.Exists(nfoPath).Should().BeFalse();
    }

    #endregion
}
