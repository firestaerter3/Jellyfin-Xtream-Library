# Dispatcharr VOD Proxy Connection Leak

## Summary

The Dispatcharr VOD proxy leaks `profile_connections` Redis counters when clients disconnect abruptly (e.g., ffprobe, Jellyfin library scans). The counter is incremented when a stream starts but never decremented when the client closes the connection mid-stream. With `max_streams = 1`, a single ffprobe run permanently blocks all subsequent VOD requests with HTTP 503.

## Related

- **PR #949** ([Fix/947-Connection capacity leak](https://github.com/Dispatcharr/Dispatcharr/pull/949)) fixes the same class of bug but only for the **TS proxy** (live TV). The VOD proxy has the identical problem in a different code path.
- **Issue #947** ([Connection capacity leak](https://github.com/Dispatcharr/Dispatcharr/issues/947)) — TS proxy variant
- **Issue #451** ([VOD connections lingering](https://github.com/Dispatcharr/Dispatcharr/issues/451)) — VOD variant, community plugin workaround exists but doesn't fix the ffprobe/GeneratorExit path

## Reproduction

1. Set `max_streams = 1` on an M3U account profile (default for most setups)
2. Reset the counter: `redis-cli SET 'profile_connections:7' 0`
3. Run ffprobe against a Dispatcharr VOD proxy URL:
   ```
   ffprobe -v quiet -show_format 'http://<dispatcharr>:5656/movie/<user>/<pass>/<stream_id>.mkv'
   ```
4. Check the counter: `redis-cli GET 'profile_connections:7'` → **stuck at 1**
5. All subsequent VOD requests return HTTP 503: `"All profiles at capacity"`

**Contrast with curl** (clean connection close):
```
curl -s -L -r 0-65535 -o /dev/null 'http://<dispatcharr>:5656/movie/<user>/<pass>/<stream_id>.mkv'
redis-cli GET 'profile_connections:7'  → 0 (correctly decremented)
```

## Root Cause

### File: `apps/proxy/vod_proxy/multi_worker_connection_manager.py`

The `profile_connections:{profile_id}` Redis counter is:
- **Incremented** synchronously in `stream_content_with_session()` via `_increment_profile_connections()` (line ~808)
- **Decremented** asynchronously in a deferred daemon thread spawned from the stream generator's cleanup path

The decrement relies on a `delayed_cleanup` function that sleeps 1 second then calls `cleanup()` which eventually calls `_decrement_profile_connections()`:

```python
# Inside stream_generator() — both normal completion and GeneratorExit paths:
if not redis_connection.has_active_streams():
    def delayed_cleanup():
        time.sleep(1)  # Sleep 1 second
        redis_connection.cleanup(connection_manager=self, current_worker_id=self.worker_id)

    import threading
    cleanup_thread = threading.Thread(target=delayed_cleanup)
    cleanup_thread.daemon = True
    cleanup_thread.start()
```

### Why it fails for ffprobe / abrupt disconnects

ffprobe issues 3 HTTP requests against the same session within ~500ms:

| Request | Range | Behavior |
|---------|-------|----------|
| 1 | `bytes=0-` (full file) | Starts streaming, interrupted when request 2 arrives |
| 2 | `bytes=1500635211-` (end of file) | Reads container metadata (983 bytes), completes normally |
| 3 | `bytes=5724-` (beginning) | Reads stream headers, disconnects after ~50ms |

Each request increments/decrements `active_streams` in Redis. The `delayed_cleanup` daemon threads are spawned but **never execute** — verified by the complete absence of cleanup log messages (`"Checking for smart cleanup"`, `"Profile connection count decremented"`) in `journalctl` output, even after waiting 18+ seconds.

The greenlets spawned inside `GeneratorExit` handlers in gunicorn's gevent worker appear to never be scheduled by the gevent Hub.

### Evidence from logs

```
08:32:06,285  [PROFILE-INCR] Profile 7 connections: 1        ← incremented
08:32:06,733  Starting Redis-backed stream                    ← request 1 starts
08:32:06,975  Starting Redis-backed stream                    ← request 2 starts
08:32:06,979  Redis-backed stream completed: 983 bytes sent   ← request 2 completes
08:32:06,980  Client disconnected from Redis-backed stream    ← request 1 GeneratorExit
08:32:07,260  Starting Redis-backed stream                    ← request 3 starts
08:32:07,313  Client disconnected from Redis-backed stream    ← request 3 GeneratorExit
              *** NO FURTHER LOG ENTRIES ***                   ← delayed_cleanup never runs
```

Counter stays at 1 permanently. Database shows `current_viewers = 0` but Redis `profile_connections:7 = 1`.

## Proposed Fix

Decrement `profile_connections` **synchronously** in the `GeneratorExit` handler and normal completion path, rather than deferring it to a daemon thread. This mirrors the approach PR #949 takes for the TS proxy (`release_stream()` called directly in error/cleanup paths).

### Option A: Synchronous decrement in generator (minimal change)

In `multi_worker_connection_manager.py`, add direct profile connection decrement in the stream generator's cleanup paths:

```python
# After decrement_active_streams() in BOTH normal completion and GeneratorExit handlers:
if not redis_connection.has_active_streams():
    # Decrement profile connections synchronously instead of deferring
    if hasattr(redis_connection, '_get_connection_state'):
        state = redis_connection._get_connection_state()
        if state and state.m3u_profile_id:
            self._decrement_profile_connections(state.m3u_profile_id)
    # Still schedule cleanup for Redis state removal
    redis_connection.cleanup(connection_manager=self, current_worker_id=self.worker_id)
```

### Option B: Add TTL to profile_connections keys (safety net)

Set a TTL on the `profile_connections:{id}` Redis key when incrementing, so stale counters self-heal:

```python
def _increment_profile_connections(self, m3u_profile):
    profile_connections_key = self._get_profile_connections_key(m3u_profile.id)
    new_count = self.redis_client.incr(profile_connections_key)
    self.redis_client.expire(profile_connections_key, 300)  # 5-minute TTL safety net
```

### Option C: Periodic reconciliation (workaround)

Cron job to reset stale counters:
```bash
# Every 60 seconds, reset VOD profile connections to 0
* * * * * redis-cli SET 'profile_connections:7' 0
```

## Impact

This bug blocks:
- **Jellyfin library scans**: ffprobe of STRM files fails after the first movie, leaving all movies without media info (no duration, codec, resolution data)
- **Jellyfin playback**: Movies marked as "played" after seconds because duration is unknown
- **Any short-lived VOD connection**: Any client that probes/previews and disconnects quickly

## Environment

- Dispatcharr: direct install at `/opt/dispatcharr` (not Docker)
- gunicorn: 4 gevent workers, timeout 300s
- Redis: local instance
- Upstream: Xtream-compatible provider via M3U account
