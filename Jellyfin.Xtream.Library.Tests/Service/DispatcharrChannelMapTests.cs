// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #113. The Live TV playlist is served without a Jellyfin login and carries the provider
// password in every line. Dispatcharr can serve the same channel without credentials, but only if
// the plugin can work out which of its channels a given stream id is. That mapping is here.

using System;
using System.Collections.Generic;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client.Models;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class DispatcharrChannelMapTests
{
    /// <summary>A stable uuid per name; the map only takes real uuids.</summary>
    private static string U(string name) => new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToString();

    private static DispatcharrChannel Channel(int id, string uuid, params int?[]? streamIds) => new()
    {
        Id = id,
        Uuid = uuid,
        Name = $"Channel {id}",
        Streams = new List<DispatcharrChannelSource>(
            System.Linq.Enumerable.Select(streamIds ?? [], s => new DispatcharrChannelSource { StreamId = s })),
    };

    [Fact]
    public void AChannelIsFoundByItsUpstreamStreamId()
    {
        // The arrangement where the plugin points straight at an Xtream panel: the stream ids it
        // holds are that panel's, which Dispatcharr records against the channel.
        var map = DispatcharrChannelMap.Build([Channel(7, U("a"), 69307)]);

        map[69307].Should().Be(U("a"));
    }

    [Fact]
    public void AChannelIsAlsoFoundByDispatcharrsOwnChannelId()
    {
        // The arrangement where the plugin points at Dispatcharr's Xtream emulation: the stream id
        // it holds IS Dispatcharr's channel id. Both keys are written because the plugin cannot
        // tell which arrangement it is in.
        var map = DispatcharrChannelMap.Build([Channel(7, U("a"), 69307)]);

        map[7].Should().Be(U("a"));
    }

    [Fact]
    public void TheChannelIdWinsWhenAnotherChannelsStreamIdCollidesWithIt()
    {
        // Channel 42's own id must resolve to channel 42, even though channel 9 happens to carry an
        // upstream stream numbered 42. Otherwise one channel silently shadows another.
        var map = DispatcharrChannelMap.Build([Channel(9, U("nine"), 42), Channel(42, U("forty-two"), 5000)]);

        map[42].Should().Be(U("forty-two"));
        map[5000].Should().Be(U("forty-two"));
    }

    [Fact]
    public void TheFirstChannelToClaimAnUpstreamStreamKeepsIt()
    {
        var map = DispatcharrChannelMap.Build([Channel(1, U("one"), 500), Channel(2, U("two"), 500)]);

        map[500].Should().Be(U("one"));
    }

    [Fact]
    public void AChannelWithNoUuidIsSkippedEntirely()
    {
        // Nothing to build a credential-free URL from, so it must not occupy the key either.
        var map = DispatcharrChannelMap.Build([Channel(3, string.Empty, 900)]);

        map.Should().BeEmpty();
    }

    [Fact]
    public void AStreamWithNoStreamIdIsSkippedButTheChannelIdStillWorks()
    {
        // Explicit array: a lone null to a params parameter is a null array, not one null element.
        var map = DispatcharrChannelMap.Build([Channel(4, U("four"), new int?[] { null })]);

        map.Should().ContainKey(4);
        map.Should().HaveCount(1);
    }

    [Fact]
    public void AChannelWithNoStreamsAtAllStillMapsByItsId()
    {
        // include_streams can come back without the array; the channel id half must survive that.
        var map = DispatcharrChannelMap.Build([new DispatcharrChannel { Id = 11, Uuid = U("eleven") }]);

        map[11].Should().Be(U("eleven"));
    }

    [Fact]
    public void AnEmptyOrMissingListingGivesAnEmptyMap()
    {
        DispatcharrChannelMap.Build(null).Should().BeEmpty();
        DispatcharrChannelMap.Build([]).Should().BeEmpty();
    }

    [Fact]
    public void TheProxyUrlCarriesNoCredentials()
    {
        var url = DispatcharrChannelMap.BuildProxyStreamUrl("http://dispatcharr:9191", "abc-123");

        url.Should().Be("http://dispatcharr:9191/proxy/ts/stream/abc-123");
        url.Should().NotContain("@");
    }

    [Fact]
    public void ATrailingSlashOnTheBaseDoesNotDoubleUp()
    {
        DispatcharrChannelMap.BuildProxyStreamUrl("http://dispatcharr:9191/", "abc-123")
            .Should().Be("http://dispatcharr:9191/proxy/ts/stream/abc-123");
    }

    [Fact]
    public void ASubpathOnTheBaseIsPreserved()
    {
        // Dispatcharr behind a reverse proxy on a subpath, which is why DispatcharrBaseUrl exists
        // at all (GitHub #83).
        DispatcharrChannelMap.BuildProxyStreamUrl("http://host/dispatcharr", "abc-123")
            .Should().Be("http://host/dispatcharr/proxy/ts/stream/abc-123");
    }

    // homelab-ops #550: Dispatcharr's Xtream emulation never sends stream_stats, but its channel
    // listing does, per stream.
    private static DispatcharrChannel ChannelWithStats(int id, params (int StreamId, string StatsJson)[] streams) => new()
    {
        Id = id,
        Uuid = U(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Name = $"Channel {id}",
        Streams = new List<DispatcharrChannelSource>(System.Linq.Enumerable.Select(streams, s => new DispatcharrChannelSource
        {
            StreamId = s.StreamId,
            StreamStatsJson = Newtonsoft.Json.Linq.JToken.Parse(s.StatsJson),
        })),
    };

    [Fact]
    public void StatsUnderTheChannelIdAreThoseOfItsFirstStream()
    {
        var stats = DispatcharrChannelMap.BuildStats([ChannelWithStats(
            7,
            (500, """{"video_codec":"h264","resolution":"1024x576","source_fps":25,"ffmpeg_output_bitrate":2500.5}"""),
            (501, """{"video_codec":"hevc"}"""))]);

        stats[7].VideoCodec.Should().Be("h264");
        stats[7].Bitrate.Should().Be(2500.5);
        stats[500].VideoCodec.Should().Be("h264");
        stats[501].VideoCodec.Should().Be("hevc");
    }

    [Fact]
    public void AChannelWhoseFirstStreamHasNoStatsGetsNoneFromALaterStream()
    {
        // The later stream is a failover that may never play; its codec says nothing about the first.
        var stats = DispatcharrChannelMap.BuildStats([ChannelWithStats(7, (500, "{}"), (501, """{"video_codec":"hevc"}"""))]);

        stats.Should().NotContainKey(7);
        stats.Should().NotContainKey(500);
        stats[501].VideoCodec.Should().Be("hevc");
    }

    [Fact]
    public void UnreadableStatsLoseOnlyThatStream()
    {
        var stats = DispatcharrChannelMap.BuildStats([
            ChannelWithStats(7, (500, """{"video_codec":"h264","ffmpeg_output_bitrate":"fast"}""")),
            ChannelWithStats(8, (600, "\"not an object\"")),
            ChannelWithStats(9, (700, """{"video_codec":"h264"}""")),
        ]);

        stats.Should().NotContainKey(7);
        stats.Should().NotContainKey(8);
        stats[9].VideoCodec.Should().Be("h264");
    }

    [Theory]
    [InlineData("x\n#EXTINF:-1,Injected\n/config/data/jellyfin.db")]
    [InlineData("not-a-uuid")]
    [InlineData("086aab97-3c36-41f0-a417-b97f39a69f0f\n#EXTINF:-1,Injected")]
    [InlineData("\n086aab97-3c36-41f0-a417-b97f39a69f0f")]
    [InlineData("086aab97-3c36-41f0-a417-b97f39a69f0f\n")]
    [InlineData("")]
    public void AValueThatIsNotAUuidIsLeftOut(string uuid)
    {
        // The uuid goes into the M3U playlist as is; a line break would add a channel to it.
        DispatcharrChannelMap.Build([Channel(7, uuid, 500)]).Should().BeEmpty();
    }

    [Fact]
    public void AListingWithOddStatsStillDeserializes()
    {
        // The listing also carries the uuids that keep the password out of the Live TV URLs, so
        // stats of an unexpected shape must not fail it.
        const string Json = """
            [{"id":7,"uuid":"00000000-0000-0000-0000-000000000007","name":"A","streams":[{"id":1,"stream_id":500,"stream_stats":{"ffmpeg_output_bitrate":"fast"}}]},
             {"id":8,"uuid":"00000000-0000-0000-0000-000000000008","name":"B","streams":[{"id":2,"stream_id":600,"stream_stats":null}]},
             {"id":9,"uuid":"00000000-0000-0000-0000-000000000009","name":"C","streams":[{"id":3,"stream_id":700,"stream_stats":[1,2]}]}]
            """;

        var listing = Newtonsoft.Json.JsonConvert.DeserializeObject<List<DispatcharrChannel>>(Json);

        DispatcharrChannelMap.Build(listing).Should().HaveCount(6);
        DispatcharrChannelMap.BuildStats(listing).Should().BeEmpty();
    }
}
