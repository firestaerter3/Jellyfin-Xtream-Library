# Live TV Channel Switch Benchmarks

Timing measurements for Live TV playback startup, measured via API from local network.

**Test setup:**
- Channel: NPO 1 (1280x720, H.264 High, 50fps, AAC stereo 44.1kHz)
- Server: Jellyfin 10.11.0, Docker, Intel CPU with QSV/NVENC
- Provider: Dispatcharr (Xtream-compatible, HLS output)
- Measurement: Wall clock from PlaybackInfo request to live.m3u8 returning first HLS segments

## Results

| Version | Tuner Type | OpenStream | live.m3u8 TTFB | Total | FFmpeg Mode | Notes |
|---------|-----------|------------|----------------|-------|-------------|-------|
| v1.31.18.0 | M3U tuner | 4.626s (probe) | 1.199s | **6.198s** | `copy/copy` (remux) | Baseline. Probe adds latency but enables remux |
| v1.31.19.0 | Native tuner | 0s (skip) | 7.404s | **~7.5s** | `libx264/copy` (transcode video) | Regression. No probe = wrong metadata = full transcode |

## Analysis

### v1.31.18 (M3U tuner) — 6.2s
```
PlaybackInfo:  0.200s   Get channel media sources
OpenStream:    4.626s   Jellyfin opens tuner stream, probes with ffprobe (gets real codec/resolution)
master.m3u8:   0.173s   HLS master playlist
live.m3u8:     1.199s   FFmpeg starts, produces 3x segments → client can play
```
- FFmpeg: `codec:v:0 copy` + `codec:a:0 copy` (pure remux, no transcoding)
- Segment duration: 5s
- Stream: `h264 1280x720 → copy` (ffprobe detected real resolution)

### v1.31.19 (native tuner) — 7.5s
```
PlaybackInfo:  0.057s   Native tuner returns hardcoded media source (no probe)
OpenStream:    SKIPPED   Native tuner doesn't require opening
master.m3u8:   0.086s   HLS master playlist
live.m3u8:     7.404s   FFmpeg starts with FULL TRANSCODE → much slower segment production
```
- FFmpeg: `h264 (native) → h264 (libx264)` (full video transcode!)
- Segment duration: 3s
- Root cause: Native tuner declares stream as **1920x1080** but actual source is **1280x720**
- Jellyfin sees resolution mismatch → adds scale filter → forces full transcode
- Scale filter: `scale=trunc(min(max(iw,ih*a),2560)/2)*2:trunc(ow/a/2)*2`

### Why the native tuner is slower despite skipping the probe

The M3U tuner spends 4.6s on `ffprobe` but gains accurate stream metadata. This enables
FFmpeg to **remux** (copy video/audio without re-encoding), which produces segments almost
instantly from the input data.

The native tuner skips the probe (saving 4.6s) but provides hardcoded metadata (1080p H.264).
When the actual stream is 720p, Jellyfin detects a mismatch and falls back to **full video
transcoding** with libx264. Software encoding at 1280x720@50fps is CPU-intensive and much
slower than remuxing, negating the probe time savings.

**Net effect:** Saving 4.6s on probe but adding ~6s on transcode = 1.4s slower overall.

## Fix path for native tuner

To make the native tuner actually faster, we need to either:

1. **Probe on first use** — cache ffprobe results per stream ID, use real metadata in MediaSource
2. **Use hardware transcoding** — if HW encoder is available, transcode is fast (~1s vs ~7s)
3. **Fix resolution declaration** — don't hardcode 1080p; either probe or declare unknown
4. **Force remux** — set `SupportsDirectStream=true` or container=mpegts to skip transcode

The ideal solution is option 1: probe once, cache, serve cached metadata. First channel switch
would be ~6s (same as M3U tuner), subsequent switches would be near-instant since metadata is
already known and remux can be used.

## How to run benchmarks

### M3U tuner test (v1.31.18)
```bash
# Requires M3U tuner host configured in Jellyfin pointing to Xtream provider
# 1. PlaybackInfo → get OpenToken
# 2. POST /LiveStreams/Open → probe + get TranscodingUrl
# 3. GET master.m3u8
# 4. GET live.m3u8 → measure TTFB (time to first HLS segments)
```

### Native tuner test (v1.31.19+)
```bash
# Native tuner provides MediaSource directly in PlaybackInfo
# 1. PlaybackInfo → get TranscodingUrl
# 2. GET master.m3u8
# 3. GET live.m3u8 → measure TTFB (time to first HLS segments)
```

---
*Last updated: 2026-02-13*
*Test environment: Jellyfin 10.11.0, NPO 1 channel via Dispatcharr*
