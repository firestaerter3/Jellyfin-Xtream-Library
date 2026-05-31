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

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service.Models;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class LiveChannelSnapshotTests
{
    private static LiveStreamInfo MakeChannel(int streamId, string name, string epg = "", string icon = "", int num = 0)
    {
        return new LiveStreamInfo
        {
            StreamId = streamId,
            Name = name,
            EpgChannelId = epg,
            StreamIcon = icon,
            Num = num,
        };
    }

    [Fact]
    public void ComputeDelta_NullPrevious_AllAdded()
    {
        var channels = new[]
        {
            MakeChannel(1, "BBC One"),
            MakeChannel(2, "ITV"),
            MakeChannel(3, "Channel 4"),
        };

        var delta = LiveChannelSnapshot.ComputeDelta(previous: null, current: channels);

        delta.AddedCount.Should().Be(3);
        delta.UpdatedCount.Should().Be(0);
        delta.RemovedCount.Should().Be(0);
        delta.UnchangedCount.Should().Be(0);
        delta.TotalChannels.Should().Be(3);
        delta.AddedStreamIds.Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }

    [Fact]
    public void ComputeDelta_SameChannels_AllSkipped()
    {
        var channels = new[]
        {
            MakeChannel(1, "BBC One"),
            MakeChannel(2, "ITV"),
        };
        var previous = LiveChannelSnapshot.FromChannels(channels);

        var delta = LiveChannelSnapshot.ComputeDelta(previous, channels);

        delta.AddedCount.Should().Be(0);
        delta.UpdatedCount.Should().Be(0);
        delta.RemovedCount.Should().Be(0);
        delta.UnchangedCount.Should().Be(2);
        delta.TotalChannels.Should().Be(2);
    }

    [Fact]
    public void ComputeDelta_ChannelRemoved_CountedAsRemoved()
    {
        var previous = LiveChannelSnapshot.FromChannels(new[]
        {
            MakeChannel(1, "BBC One"),
            MakeChannel(2, "ITV"),
            MakeChannel(3, "Channel 4"),
        });

        var current = new[]
        {
            MakeChannel(1, "BBC One"),
            MakeChannel(2, "ITV"),
        };

        var delta = LiveChannelSnapshot.ComputeDelta(previous, current);

        delta.AddedCount.Should().Be(0);
        delta.UnchangedCount.Should().Be(2);
        delta.RemovedCount.Should().Be(1);
        delta.RemovedStreamIds.Should().Contain(3);
    }

    [Theory]
    [InlineData("BBC One HD", "BBC One")] // name changed
    [InlineData("BBC One", "BBC One")] // unchanged for the EPG test below
    public void ComputeDelta_NameChange_DetectedAsUpdate(string oldName, string newName)
    {
        var previous = LiveChannelSnapshot.FromChannels(new[] { MakeChannel(1, oldName) });
        var current = new[] { MakeChannel(1, newName) };

        var delta = LiveChannelSnapshot.ComputeDelta(previous, current);

        if (oldName != newName)
        {
            delta.UpdatedCount.Should().Be(1);
            delta.UnchangedCount.Should().Be(0);
            delta.UpdatedStreamIds.Should().Contain(1);
        }
        else
        {
            delta.UpdatedCount.Should().Be(0);
            delta.UnchangedCount.Should().Be(1);
        }
    }

    [Fact]
    public void ComputeDelta_EpgChange_DetectedAsUpdate()
    {
        var previous = LiveChannelSnapshot.FromChannels(new[] { MakeChannel(1, "BBC One", epg: "bbc1.uk") });
        var current = new[] { MakeChannel(1, "BBC One", epg: "bbc1.bbc.co.uk") };

        var delta = LiveChannelSnapshot.ComputeDelta(previous, current);

        delta.UpdatedCount.Should().Be(1);
    }

    [Fact]
    public void ComputeDelta_IconChange_DetectedAsUpdate()
    {
        var previous = LiveChannelSnapshot.FromChannels(new[] { MakeChannel(1, "BBC One", icon: "https://example/bbc.png") });
        var current = new[] { MakeChannel(1, "BBC One", icon: "https://example/bbc-2024.png") };

        var delta = LiveChannelSnapshot.ComputeDelta(previous, current);

        delta.UpdatedCount.Should().Be(1);
    }

    [Fact]
    public void ComputeDelta_NumberChange_DetectedAsUpdate()
    {
        var previous = LiveChannelSnapshot.FromChannels(new[] { MakeChannel(1, "BBC One", num: 101) });
        var current = new[] { MakeChannel(1, "BBC One", num: 1) };

        var delta = LiveChannelSnapshot.ComputeDelta(previous, current);

        delta.UpdatedCount.Should().Be(1);
    }

    [Fact]
    public void ComputeDelta_MixedChanges_CountsCorrectly()
    {
        var previous = LiveChannelSnapshot.FromChannels(new[]
        {
            MakeChannel(1, "BBC One"),         // will be removed
            MakeChannel(2, "ITV"),              // will stay unchanged
            MakeChannel(3, "Channel 4"),        // will be updated (name)
        });

        var current = new[]
        {
            MakeChannel(2, "ITV"),
            MakeChannel(3, "Channel Four"),
            MakeChannel(4, "Channel 5"),
        };

        var delta = LiveChannelSnapshot.ComputeDelta(previous, current);

        delta.AddedCount.Should().Be(1);
        delta.AddedStreamIds.Should().Contain(4);
        delta.UpdatedCount.Should().Be(1);
        delta.UpdatedStreamIds.Should().Contain(3);
        delta.RemovedCount.Should().Be(1);
        delta.RemovedStreamIds.Should().Contain(1);
        delta.UnchangedCount.Should().Be(1);
        delta.UnchangedStreamIds.Should().Contain(2);
        delta.TotalChannels.Should().Be(3);
    }

    [Fact]
    public void ComputeDelta_DuplicateStreamIdInCurrent_CountsOnce()
    {
        var previous = LiveChannelSnapshot.FromChannels(new[] { MakeChannel(1, "BBC One") });
        var current = new[]
        {
            MakeChannel(1, "BBC One"),
            MakeChannel(1, "BBC One"), // duplicate from a different category
        };

        var delta = LiveChannelSnapshot.ComputeDelta(previous, current);

        delta.UnchangedCount.Should().Be(1);
        delta.AddedCount.Should().Be(0);
        delta.RemovedCount.Should().Be(0);
        delta.TotalChannels.Should().Be(1);
    }

    [Fact]
    public void Snapshot_JsonRoundtrip_PreservesData()
    {
        var original = LiveChannelSnapshot.FromChannels(new[]
        {
            MakeChannel(101, "BBC One", epg: "bbc1.uk", icon: "https://example/bbc.png", num: 1),
            MakeChannel(202, "ITV", epg: "itv.uk", num: 3),
        });

        var json = JsonConvert.SerializeObject(original);
        var restored = JsonConvert.DeserializeObject<LiveChannelSnapshot>(json);

        restored.Should().NotBeNull();
        restored!.Channels.Should().HaveCount(2);
        restored.Channels[101].Name.Should().Be("BBC One");
        restored.Channels[101].EpgChannelId.Should().Be("bbc1.uk");
        restored.Channels[101].StreamIcon.Should().Be("https://example/bbc.png");
        restored.Channels[101].Num.Should().Be(1);
        restored.Channels[101].Checksum.Should().Be(original.Channels[101].Checksum);
    }

    [Fact]
    public void ComputeChecksum_StableForSameInput()
    {
        var ch = MakeChannel(1, "BBC One", epg: "bbc1.uk", icon: "https://example/bbc.png", num: 101);
        var a = LiveChannelSnapshotEntry.ComputeChecksum(ch);
        var b = LiveChannelSnapshotEntry.ComputeChecksum(ch);
        a.Should().Be(b);
        a.Length.Should().Be(32); // MD5 hex
    }

    [Fact]
    public void FromChannels_LastWriteWinsOnDuplicateStreamId()
    {
        // Mirrors GetFilteredChannelsAsync's de-dup-on-StreamId behavior. We don't fail loudly,
        // we just keep the last entry.
        var channels = new List<LiveStreamInfo>
        {
            MakeChannel(1, "BBC One", num: 101),
            MakeChannel(1, "BBC One HD", num: 1101),
        };

        var snapshot = LiveChannelSnapshot.FromChannels(channels);

        snapshot.Channels.Should().HaveCount(1);
        snapshot.Channels[1].Name.Should().Be("BBC One HD");
        snapshot.Channels[1].Num.Should().Be(1101);
    }
}
