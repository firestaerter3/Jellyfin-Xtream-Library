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

public class ChannelLogoResolverTests
{
    [Theory]
    [InlineData("http://x/y.png", false)]
    [InlineData("https://x/y.png", false)]
    [InlineData("HTTP://x/y.png", false)]
    [InlineData("/share/logo.png", true)]
    [InlineData("C:\\logos\\a.png", true)]
    [InlineData("file:///share/logo.png", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLocalPath_ClassifiesCorrectly(string? icon, bool expected)
    {
        ChannelLogoResolver.IsLocalPath(icon).Should().Be(expected);
    }

    [Fact]
    public void ResolveDisplayUrl_HttpUrl_Unchanged()
    {
        ChannelLogoResolver.ResolveDisplayUrl("http://x/y.png", 5, "http://h").Should().Be("http://x/y.png");
    }

    [Fact]
    public void ResolveDisplayUrl_LocalPath_ReturnsProxyUrl()
    {
        ChannelLogoResolver.ResolveDisplayUrl("/share/logo.png", 5, "http://h:8096")
            .Should().Be("http://h:8096/XtreamLibrary/ChannelLogo/5");
    }

    [Fact]
    public void ResolveDisplayUrl_BaseUrlTrailingSlash_NoDoubleSlash()
    {
        ChannelLogoResolver.ResolveDisplayUrl("/share/logo.png", 5, "http://h:8096/")
            .Should().Be("http://h:8096/XtreamLibrary/ChannelLogo/5");
    }

    [Fact]
    public void ResolveDisplayUrl_NullIcon_ReturnsNull()
    {
        ChannelLogoResolver.ResolveDisplayUrl(null, 5, "http://h").Should().BeNull();
    }

    [Fact]
    public void ResolveDisplayUrl_EmptyIcon_ReturnsNull()
    {
        // "no logo" normalizes to null so consumers (tuner ImageUrl) get null, not "".
        ChannelLogoResolver.ResolveDisplayUrl(string.Empty, 5, "http://h").Should().BeNull();
    }

    [Fact]
    public void ResolveDisplayUrl_LocalPath_EmptyBaseUrl_ReturnsOriginal()
    {
        // Defensive: if the server URL can't be determined, don't emit a broken proxy URL.
        ChannelLogoResolver.ResolveDisplayUrl("/share/logo.png", 5, string.Empty).Should().Be("/share/logo.png");
    }
}
