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
using Jellyfin.Xtream.Library.Api;
using Jellyfin.Xtream.Library.Service;
using MediaBrowser.Model.LiveTv;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Api;

/// <summary>
/// The settings page warns when an M3U tuner reads the plugin's playlist next to the native tuner,
/// since every channel then appears twice.
/// </summary>
public class TunerCheckControllerTests
{
    [Theory]
    [InlineData("http://localhost:8096/XtreamLibrary/LiveTv.m3u")]
    [InlineData("https://jellyfin.example/xtreamlibrary/livetv.m3u")]
    [InlineData("http://jellyfin.lan:8096/XtreamLibrary/Catchup.m3u")]
    public void AnM3uTunerOnThePluginPlaylistIsFound(string url)
    {
        TunerCheckController.HasM3uTunerOnPluginPlaylist([new TunerHostInfo { Type = "m3u", Url = url }])
            .Should().BeTrue();
    }

    [Fact]
    public void OtherTunersAreNotCounted()
    {
        TunerCheckController.HasM3uTunerOnPluginPlaylist(
        [
            new TunerHostInfo { Type = "m3u", Url = "http://provider.example/get.php?type=m3u_plus" },
            new TunerHostInfo { Type = "hdhomerun", Url = "http://localhost:8096/XtreamLibrary/LiveTv.m3u" },
            new TunerHostInfo { Type = XtreamTunerHost.TunerType, FriendlyName = NativeTunerRegistration.FriendlyName },
            new TunerHostInfo { Type = "m3u", Url = null! },
        ]).Should().BeFalse();
    }
}
