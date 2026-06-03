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
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Client;

// GitHub #41: Xtream providers send the `added` field as either a Unix-timestamp integer
// or a formatted date string. LiveStreamInfo.Added is typed as string (matching the sibling
// StreamInfo.Added) so JSON deserialization accepts both shapes without a custom converter,
// and live stream fetches don't blow up on providers that use the string form.
public class LiveStreamInfoTests
{
    [Fact]
    public void Deserialize_AddedAsUnixTimestamp_Succeeds()
    {
        var json = "{\"added\": \"1716372000\"}";

        var info = JsonConvert.DeserializeObject<LiveStreamInfo>(json);

        info!.Added.Should().Be("1716372000");
    }

    [Fact]
    public void Deserialize_AddedAsFormattedDateString_Succeeds()
    {
        var json = "{\"added\": \"22/05/2024 10:00:00\"}";

        var info = JsonConvert.DeserializeObject<LiveStreamInfo>(json);

        info!.Added.Should().Be("22/05/2024 10:00:00");
    }

    [Fact]
    public void Deserialize_AddedAsRawInteger_Succeeds()
    {
        // Some providers omit the quotes around the timestamp.
        var json = "{\"added\": 1716372000}";

        var info = JsonConvert.DeserializeObject<LiveStreamInfo>(json);

        info!.Added.Should().Be("1716372000");
    }

    [Fact]
    public void Deserialize_AddedMissing_DefaultsToEmptyString()
    {
        var json = "{}";

        var info = JsonConvert.DeserializeObject<LiveStreamInfo>(json);

        info!.Added.Should().BeEmpty();
    }
}
