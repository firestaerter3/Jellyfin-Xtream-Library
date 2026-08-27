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

    // GitHub #79 added CategoryIds for exclude-mode filtering. It is deserialized on the shared
    // fetch path, so a shape that throws takes down every Live TV mode - including the two that
    // never read the field. Same hazard as `added` above, so the same tolerance is required.

    [Fact]
    public void Deserialize_CategoryIdsAsIntArray_Succeeds()
    {
        var json = "{\"category_ids\": [10, 20]}";

        var info = JsonConvert.DeserializeObject<LiveStreamInfo>(json);

        info!.CategoryIds.Should().BeEquivalentTo(new[] { 10, 20 });
    }

    [Fact]
    public void Deserialize_CategoryIdsAsStringArray_Succeeds()
    {
        var json = "{\"category_ids\": [\"10\", \"20\"]}";

        var info = JsonConvert.DeserializeObject<LiveStreamInfo>(json);

        info!.CategoryIds.Should().BeEquivalentTo(new[] { 10, 20 });
    }

    [Fact]
    public void Deserialize_CategoryIdsAsBareScalar_Succeeds()
    {
        // A provider reporting one category may send it unwrapped rather than as a 1-element array.
        var json = "{\"category_ids\": 10}";

        var info = JsonConvert.DeserializeObject<LiveStreamInfo>(json);

        info!.CategoryIds.Should().BeEquivalentTo(new[] { 10 });
    }

    [Fact]
    public void Deserialize_CategoryIdsAsCommaSeparatedString_Succeeds()
    {
        var json = "{\"category_ids\": \"10,20\"}";

        var info = JsonConvert.DeserializeObject<LiveStreamInfo>(json);

        info!.CategoryIds.Should().BeEquivalentTo(new[] { 10, 20 });
    }

    [Fact]
    public void Deserialize_CategoryIdsNullOrMissingOrEmpty_DoesNotThrow()
    {
        JsonConvert.DeserializeObject<LiveStreamInfo>("{\"category_ids\": null}")!
            .CategoryIds.Should().BeNull();

        JsonConvert.DeserializeObject<LiveStreamInfo>("{}")!
            .CategoryIds.Should().BeNull();

        JsonConvert.DeserializeObject<LiveStreamInfo>("{\"category_ids\": []}")!
            .CategoryIds.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_CategoryIdsGarbage_DegradesInsteadOfThrowing()
    {
        // Whatever a provider invents here, losing the secondary membership of one channel is
        // survivable; failing the fetch is not.
        JsonConvert.DeserializeObject<LiveStreamInfo>("{\"category_ids\": \"\"}")!
            .CategoryIds.Should().BeEmpty();

        JsonConvert.DeserializeObject<LiveStreamInfo>("{\"category_ids\": {\"a\": 1}}")!
            .CategoryIds.Should().BeNull();

        JsonConvert.DeserializeObject<LiveStreamInfo>("{\"category_ids\": [\"x\", 10]}")!
            .CategoryIds.Should().BeEquivalentTo(new[] { 10 });
    }

    [Fact]
    public void Deserialize_FullChannelWithAwkwardCategoryIds_KeepsOtherFields()
    {
        // The point of the tolerance: one odd field must not cost the rest of the channel.
        var json = "{\"stream_id\": 7, \"name\": \"BBC One\", \"category_id\": \"10\", \"category_ids\": 10}";

        var info = JsonConvert.DeserializeObject<LiveStreamInfo>(json);

        info!.StreamId.Should().Be(7);
        info.Name.Should().Be("BBC One");
        info.CategoryId.Should().Be(10);
        info.CategoryIds.Should().BeEquivalentTo(new[] { 10 });
    }
}
