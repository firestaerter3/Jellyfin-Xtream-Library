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

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Xtream.Library.Client;

/// <summary>
/// Spaces requests to one host, across every worker and every client instance (GitHub #122).
/// <para>
/// Request Delay used to be a pause each worker took after its own response, so with Sync
/// Parallelism at 10 there were ten independent loops and nothing bounded the total: 50 ms
/// allowed up to 200 requests a second, enough to get a provider to block the caller. The
/// setting is documented as the gap between API requests, and this is what makes it one.
/// </para>
/// <para>
/// Keyed by host rather than held on the client, because the clients are typed HttpClients and
/// every consumer gets its own instance. With Dispatcharr Mode on, the Xtream and Dispatcharr
/// clients also reach the same host, and the provider sees both.
/// </para>
/// </summary>
internal static class RequestGate
{
    private static readonly ConcurrentDictionary<string, Slot> Slots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets how a wait is slept, for tests only. A test replaces it to make timers fire late, the
    /// way they do on a loaded machine, which is the condition that exposed the burst. Async-local
    /// so a replacement reaches only the calls started from that test.
    /// </summary>
    internal static AsyncLocal<Func<TimeSpan, CancellationToken, Task>?> SleepOverride { get; } = new();

    /// <summary>
    /// Waits until a request to <paramref name="uri"/>'s host may start, and reserves that start.
    /// </summary>
    /// <param name="uri">The request address; only its host and port are used.</param>
    /// <param name="delayMs">Minimum gap between request starts to this host.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the request may be sent.</returns>
    public static async Task WaitTurnAsync(Uri uri, int delayMs, CancellationToken cancellationToken)
    {
        var slot = Slots.GetOrAdd(uri.Authority, _ => new Slot());

        // The turn is claimed on waking, against the clock, not reserved in advance. Reserving
        // start times up front and sleeping until them looked equivalent, but timers fire late
        // on a busy machine, and every sleeper whose slot had passed then woke together and sent
        // at once: CI measured two starts 0.03 ms apart. Claiming on waking turns a late timer
        // into a later start instead of a burst.
        while (true)
        {
            TimeSpan wait;
            lock (slot)
            {
                var now = DateTime.UtcNow;
                if (slot.NextStartUtc <= now)
                {
                    slot.NextStartUtc = now.AddMilliseconds(Math.Max(delayMs, 0));
                    return;
                }

                wait = slot.NextStartUtc - now;
            }

            var sleep = SleepOverride.Value ?? Task.Delay;
            await sleep(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Holds every request to <paramref name="uri"/>'s host back for <paramref name="pauseMs"/>.
    /// A provider answering 429 is refusing the caller, not one request, so the other workers
    /// sending at full rate while one of them backs off would keep the refusal going.
    /// </summary>
    /// <param name="uri">The request address; only its host and port are used.</param>
    /// <param name="pauseMs">How long no request to that host may start.</param>
    public static void Pause(Uri uri, int pauseMs)
    {
        var slot = Slots.GetOrAdd(uri.Authority, _ => new Slot());
        lock (slot)
        {
            var until = DateTime.UtcNow.AddMilliseconds(Math.Max(pauseMs, 0));
            if (until > slot.NextStartUtc)
            {
                slot.NextStartUtc = until;
            }
        }
    }

    private sealed class Slot
    {
        public DateTime NextStartUtc { get; set; }
    }
}
