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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Jellyfin.Xtream.Library.Client;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Client;

/// <summary>
/// Request Delay is the gap between requests to a provider, not a pause each worker takes on
/// its own (GitHub #122). Before, ten workers each paused 50 ms after their own response and
/// together sent up to 200 requests a second, which got a reporter's IP blocked.
/// Each test uses its own host because the spacing is shared process-wide, per host.
/// </summary>
public class RequestGateTests
{
    // Timer resolution can shave a little off each wait. The old behaviour started every
    // request within a few milliseconds of each other, so this margin cannot hide it.
    private const double Slack = 10;

    [Fact]
    public async Task ParallelXtreamRequests_StartAtLeastTheDelayApart()
    {
        var host = UniqueHost();
        var starts = new ConcurrentBag<double>();
        var clock = Stopwatch.StartNew();
        var client = XtreamClientRecording(starts, clock, _ => HttpStatusCode.OK);
        client.RequestDelayMs = 50;

        await Task.WhenAll(Enumerable.Range(1, 8).Select(id =>
            client.GetVodInfoAsync(new ConnectionInfo(host, "u", "p"), id, CancellationToken.None)));

        GapsBetween(starts).Should().OnlyContain(gap => gap >= 50 - Slack);
    }

    // The clients are typed HttpClients, so each consumer holds its own instance. Spacing held
    // on the instance would let two of them run side by side at full rate.
    [Fact]
    public async Task TwoClientInstances_ShareTheSpacingForTheSameHost()
    {
        var host = UniqueHost();
        var starts = new ConcurrentBag<double>();
        var clock = Stopwatch.StartNew();
        var first = XtreamClientRecording(starts, clock, _ => HttpStatusCode.OK);
        var second = XtreamClientRecording(starts, clock, _ => HttpStatusCode.OK);
        first.RequestDelayMs = 50;
        second.RequestDelayMs = 50;

        await Task.WhenAll(Enumerable.Range(1, 4).SelectMany(id => new[]
        {
            first.GetVodInfoAsync(new ConnectionInfo(host, "u", "p"), id, CancellationToken.None),
            second.GetVodInfoAsync(new ConnectionInfo(host, "u", "p"), id + 100, CancellationToken.None),
        }));

        GapsBetween(starts).Should().OnlyContain(gap => gap >= 50 - Slack);
    }

    // A 429 refuses the caller, not one request. Before, only the worker that got it backed off
    // while the others kept sending.
    [Fact]
    public async Task A429_HoldsBackEveryRequestToThatHost()
    {
        var host = UniqueHost();
        var starts = new ConcurrentBag<double>();
        var clock = Stopwatch.StartNew();
        double refusedAt = 0;
        var refused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var client = XtreamClientRecording(starts, clock, _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                refusedAt = clock.Elapsed.TotalMilliseconds;
                refused.TrySetResult();
                return HttpStatusCode.TooManyRequests;
            }

            return HttpStatusCode.OK;
        });
        client.RetryDelayMs = 300;

        var refusedCall = client.GetVodInfoAsync(new ConnectionInfo(host, "u", "p"), 6, CancellationToken.None);
        await refused.Task;
        await client.GetVodInfoAsync(new ConnectionInfo(host, "u", "p"), 7, CancellationToken.None);
        await refusedCall;

        starts.Where(s => s > refusedAt).Should().HaveCount(2)
            .And.OnlyContain(s => s >= refusedAt + 300 - Slack, "nothing may reach the provider while it is refusing");
    }

    [Fact]
    public async Task ParallelDispatcharrRequests_StartAtLeastTheDelayApart()
    {
        var host = UniqueHost();
        var starts = new ConcurrentBag<double>();
        var clock = Stopwatch.StartNew();
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/accounts/token/", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, JsonConvert.SerializeObject(new { access = "test-token", refresh = "refresh-token" }));
            }

            starts.Add(clock.Elapsed.TotalMilliseconds);
            return (HttpStatusCode.OK, "[]");
        });
        var client = new DispatcharrClient(new HttpClient(handler), new Mock<ILogger<DispatcharrClient>>().Object);
        client.Configure("admin", "secret");
        client.RequestDelayMs = 50;

        await Task.WhenAll(Enumerable.Range(1, 6).Select(id =>
            client.GetMovieProvidersAsync(host, id, CancellationToken.None)));

        starts.Should().HaveCount(6);
        GapsBetween(starts).Should().OnlyContain(gap => gap >= 50 - Slack);
    }

    private static string UniqueHost() => $"http://gate-{Guid.NewGuid():N}.test";

    private static IEnumerable<double> GapsBetween(IEnumerable<double> starts)
    {
        var ordered = starts.OrderBy(s => s).ToList();
        return ordered.Zip(ordered.Skip(1), (a, b) => b - a);
    }

    private static XtreamClient XtreamClientRecording(
        ConcurrentBag<double> starts,
        Stopwatch clock,
        Func<HttpRequestMessage, HttpStatusCode> status)
    {
        var handler = new RecordingHandler(request =>
        {
            starts.Add(clock.Elapsed.TotalMilliseconds);
            return (status(request), "{}");
        });
        var client = new XtreamClient(new HttpClient(handler), new Mock<ILogger<XtreamClient>>().Object);
        client.UpdateUserAgent(null);
        return client;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = respond(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
