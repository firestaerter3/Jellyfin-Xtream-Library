using System.Xml.Linq;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public sealed class ArtworkUrlFilterTests
{
    private static async Task<XDocument> WriteAsync(string? poster, string? backdrop, string? exclusions = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nfo-artwork-{Guid.NewGuid():N}.nfo");
        try
        {
            var ok = await NfoWriter.WriteMovieNfoAsync(path, "Test", null, null, null, 757771, 2020, CancellationToken.None, posterUrl: poster, backdropUrl: backdrop, excludedArtworkHosts: exclusions);
            Assert.True(ok);
            return XDocument.Load(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task BlockedPosterIsOmittedWithoutRemovingTmdb()
    {
        var x = await WriteAsync("http://art.example.org:8080/poster.jpg", null, "art.example.org:8080");
        Assert.Null(x.Root!.Element("thumb"));
        Assert.Equal("757771", x.Root.Element("uniqueid")?.Value);
    }

    [Fact]
    public async Task BlockedFanartIsOmitted()
    {
        var x = await WriteAsync(null, "http://art.example.org:8080/fanart.jpg", "art.example.org:8080");
        Assert.Null(x.Root!.Element("fanart"));
    }

    [Fact]
    public async Task OtherArtworkIsPreserved()
    {
        var x = await WriteAsync("https://example.org/p.jpg", "https://example.org/f.jpg");
        Assert.Equal("https://example.org/p.jpg", x.Root!.Element("thumb")?.Value);
        Assert.Equal("https://example.org/f.jpg", x.Root.Element("fanart")?.Element("thumb")?.Value);
    }

    [Fact]
    public async Task HostIsCaseInsensitiveAndOtherPortsRemainAllowed()
    {
        var blocked = await WriteAsync("http://ART.EXAMPLE.ORG:8080/p.jpg", null, "art.example.org:8080");
        Assert.Null(blocked.Root!.Element("thumb"));
        var allowed = await WriteAsync("http://art.example.org:80/p.jpg", null, "art.example.org:8080");
        Assert.Equal("http://art.example.org:80/p.jpg", allowed.Root!.Element("thumb")?.Value);
    }
    [Fact]
    public async Task ArtworkPreservedWithoutExclusions()
    {
        var url = "http://art.example.org:8080/poster.jpg";
        var document = await WriteAsync(url, null);
        Assert.Equal(url, document.Root!.Element("thumb")?.Value);
    }

    [Fact]
    public async Task HostnameOnlyExclusionMatchesEveryPort()
    {
        var document = await WriteAsync("https://images.example.com/poster.jpg", "http://images.example.com:8080/fanart.jpg", "images.example.com");
        Assert.Null(document.Root!.Element("thumb"));
        Assert.Null(document.Root.Element("fanart"));
    }

    [Fact]
    public async Task CommaSeparatedExclusionsMatchIndependently()
    {
        var document = await WriteAsync("http://art.example.org:8080/poster.jpg", "https://images.example.com/fanart.jpg", "unused.example.com, art.example.org:8080, images.example.com");
        Assert.Null(document.Root!.Element("thumb"));
        Assert.Null(document.Root.Element("fanart"));
        var allowed = await WriteAsync("http://art.example.org:80/poster.jpg", null, "art.example.org:8080, images.example.com");
        Assert.Equal("http://art.example.org:80/poster.jpg", allowed.Root!.Element("thumb")?.Value);
    }

}
