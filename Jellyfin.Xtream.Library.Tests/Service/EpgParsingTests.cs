
using System;
using System.Reflection;
using Jellyfin.Xtream.Library.Service;
using Xunit;
using FluentAssertions;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class EpgParsingTests
{
    [Theory]
    [InlineData("20240522100000 +0200", 1716364800)]
    [InlineData("20240522100000 +02:00", 1716364800)]
    [InlineData("202405221000 +0000", 1716372000)]
    [InlineData("20240522 +0000", 1716336000)]
    [InlineData("20240522100000", 1716372000)] // Defaults to +0000
    [InlineData("20240522100000 -0500", 1716390000)]
    [InlineData("20240522100000 -05:00", 1716390000)]
    public void TryParseXmltvTime_HandlesVariousFormats(string input, long expectedUnix)
    {
        var method = typeof(LiveTvService).GetMethod("TryParseXmltvTime", BindingFlags.Static | BindingFlags.NonPublic);
        var args = new object[] { input, 0L };
        var result = (bool)method.Invoke(null, args);

        result.Should().BeTrue();
        ((long)args[1]).Should().Be(expectedUnix);
    }
}
