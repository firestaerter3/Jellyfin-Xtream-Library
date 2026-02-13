# Native Tuner Performance & Development Guide

## Overview

The native tuner (`XtreamTunerHost`) provides live TV channels to Jellyfin with optional pre-populated media info from Dispatcharr stream stats. This eliminates FFmpeg probing and enables video stream copy instead of full transcoding.

## Performance Results

### Before Optimization
- **Startup time**: ~8.5s
- **FFmpeg mode**: Full transcode (`libx264 -preset veryfast` + yadif deinterlacing)
- **FFmpeg speed**: 0.66x realtime
- **Root cause**: `IsInterlaced` defaulting to `true` forced yadif deinterlacing, and missing `Codec` blocked `CanStreamCopyVideo`

### After Optimization — WITH stream stats (Dispatcharr)
- **Startup time**: ~3.5–5.7s (average ~4.6s)
- **FFmpeg mode**: DirectStream (`-codec:v:0 copy`, no video encoding)
- **FFmpeg speed**: 4.67x realtime
- **Improvement**: ~45% faster startup, 7x faster encoding

### After Optimization — WITHOUT stream stats (generic Xtream)
- **Startup time**: ~4.3–5.1s (average ~4.9s)
- **FFmpeg mode**: Transcode (`libx264`), but **no yadif** deinterlacing
- **FFmpeg speed**: 2.6x realtime (vs 0.66x before)
- **Improvement**: ~42% faster startup, 4x faster encoding

### Summary Table
| Scenario | FFmpeg Mode | Speed | Startup | vs Baseline |
|----------|-------------|-------|---------|-------------|
| **Baseline** (before) | Transcode + yadif | 0.66x | ~8.5s | — |
| **With stats** (Dispatcharr) | DirectStream (copy) | 4.67x | ~4.6s | **46% faster** |
| **Without stats** (generic) | Transcode, no yadif | 2.6x | ~4.9s | **42% faster** |

### Timing Breakdown (typical)
| Step | With Stats | Without Stats | Notes |
|------|-----------|---------------|-------|
| PlaybackInfo API | ~60ms | ~60ms | Fast |
| Master playlist | ~60ms | ~70ms | Triggers FFmpeg start |
| First HLS segment | ~3–5s | ~3.5–4.5s | Bottleneck: accumulate 3s of content |
| Segment download | ~200ms | ~200ms | Network transfer |
| **Total** | **~3.5–5.7s** | **~4.3–5.1s** | Varies by GOP alignment |

### Theoretical Minimum
With 3-second HLS segments, the minimum startup time is ~3s (must wait for one full segment). Both paths are near-optimal for HLS delivery. The bottleneck is waiting for source data, not encoding speed.

### Why Both Paths Are Similar Speed
Even though the generic path uses video transcode, it runs at 2.6x realtime — well above 1x. This means encoding is NOT the bottleneck. Both paths are bottlenecked by the same thing: waiting for 3 seconds of source video data to arrive from the Xtream provider. Whether that data is copied or transcoded matters less when the transcode is fast enough to keep up.

## How Stream Copy Works

Jellyfin decides between stream copy and transcode in `EncodingHelper.CanStreamCopyVideo`. Key conditions for copy:

1. `MediaStream.Codec` must be non-null AND in client's supported codecs
2. `MediaStream.IsInterlaced` must be `false` (or deinterlacing must be disabled)
3. Bitrate must be within limits (Jellyfin allows unknown bitrate for live TV)
4. `VideoRangeType` must not be Unknown (defaults to SDR)

When stream copy fails, Jellyfin builds a full FFmpeg filter chain:
```
-codec:v:0 libx264 -preset veryfast -vf "setparams=...,yadif=0:-1:0,format=yuv420p"
```

When stream copy succeeds:
```
-codec:v:0 copy -bsf:v h264_mp4toannexb
```

## AAC Audio: Why We Transcode Audio

Audio **cannot** be stream-copied (`-c:a copy`) when:
- Source audio is AAC in ADTS format (common in MPEG-TS)
- Output is fMP4 HLS segments

This fails with: `"Malformed AAC bitstream detected: use the audio bitstream filter 'aac_adtstoasc'"`. Jellyfin doesn't add the `aac_adtstoasc` BSF.

**Solution**: Provide audio MediaStream with `Codec = null`. This forces Jellyfin to transcode audio via `libfdk_aac` (fast) while keeping video copy. Audio transcoding adds negligible time compared to video transcoding.

## How to Build & Deploy

### Build
```bash
dotnet build -c Release
```
Note: `dotnet build` and `dotnet test` require NuGet package restore, which needs network access to nuget.org.

### Run Tests
```bash
dotnet test -c Release
```

### Deploy to Server
```bash
# Publish
dotnet publish Jellyfin.Xtream.Library -c Release -o /tmp/claude/xtream-library-release

# Copy to server (replace <host> with server IP)
scp /tmp/claude/xtream-library-release/Jellyfin.Xtream.Library.dll <user>@<host>:/path/to/jellyfin/plugins/Xtream\ Library/

# Restart Jellyfin (Docker)
ssh <user>@<host> "docker restart jellyfin"
```

After restart, wait ~30s for Jellyfin to fully start, then test.

## How to Test Channel Playback & Measure Timing

### Step 1: Find Channel IDs
```bash
# List Live TV channels with Jellyfin internal IDs
curl -s "http://<host>:8096/LiveTv/Channels?api_key=<api_key>&Limit=10" | \
  python3 -c "import sys,json; [print(f'{c[\"Id\"]}: {c[\"Name\"]} (#{c.get(\"ChannelNumber\",\"?\")})')
    for c in json.load(sys.stdin).get('Items',[])]"
```

### Step 2: Trigger Playback via API
```bash
# Request PlaybackInfo (starts the stream)
CHANNEL_ID="<jellyfin-channel-id>"
USER_ID="<user-id>"
curl -s -X POST \
  "http://<host>:8096/Items/$CHANNEL_ID/PlaybackInfo?UserId=$USER_ID&StartTimeTicks=0&IsPlayback=true&AutoOpenLiveStream=true&MaxStreamingBitrate=120000000" \
  -H "X-Emby-Token: <api_key>" \
  -H "Content-Type: application/json" \
  -d '{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[{"Container":"mp4,m4v","Type":"Video","VideoCodec":"h264","AudioCodec":"aac"}],"TranscodingProfiles":[{"Container":"ts","Type":"Video","AudioCodec":"aac","VideoCodec":"h264","Context":"Streaming","Protocol":"hls","MaxAudioChannels":"2","MinSegments":"1","BreakOnNonKeyFrames":true}]}}'
```

Key fields in the response:
- `SupportsProbing: false` — confirms no FFprobe step
- `AnalyzeDurationMs: 0` — confirms no analysis delay
- `TranscodingUrl` — the HLS master playlist URL

### Step 3: Request HLS Segments
```bash
# Request master playlist (triggers FFmpeg)
TRANSCODE_URL="<from PlaybackInfo response>"
curl -s "http://<host>:8096$TRANSCODE_URL"

# Extract and request sub-playlist
# Poll until segments appear (first segment takes ~3-5s)
```

### Step 4: Check FFmpeg Logs
```bash
# List recent FFmpeg logs (inside Docker container)
docker exec jellyfin ls -lt /config/log/ | grep FFmpeg | head -5

# Key indicators:
# - Filename: FFmpeg.DirectStream-* = stream copy (good)
#             FFmpeg.Transcode-*    = full transcode (slow)

# Check the FFmpeg command (line 2 of the log)
docker exec jellyfin sed -n '2p' /config/log/<logfile>

# Verify stream mapping (should show "copy" for video)
docker exec jellyfin grep "Stream mapping" -A3 /config/log/<logfile>
# Expected: Stream #0:0 -> #0:0 (copy)
#           Stream #0:1 -> #0:1 (aac (native) -> aac (libfdk_aac))

# Check encoding speed
docker exec jellyfin grep "speed=" /config/log/<logfile> | head -3
# DirectStream speed: 3-5x realtime (fast)
# Transcode speed: 0.5-0.7x realtime (slow)
```

### Step 5: Stop Playback
```bash
curl -s -X DELETE "http://<host>:8096/Videos/ActiveEncodings?DeviceId=<device_id>&PlaySessionId=<session_id>&api_key=<api_key>"
```

## Two Paths: With Stats vs Without Stats

### Channels WITH Stream Stats (from Dispatcharr)
- `SupportsProbing = false` — skips FFprobe (~1.5s savings)
- `AnalyzeDurationMs = 0`
- Video MediaStream has `Codec = "h264"`, `IsInterlaced = false`, resolution, fps, bitrate
- Audio MediaStream has `Codec = null` (forces transcode)
- Result: **DirectStream** with video copy + audio transcode

### Channels WITHOUT Stream Stats (generic Xtream)
- `SupportsProbing = true`, `AnalyzeDurationMs = 500`
- Video MediaStream has `Codec = null`, `IsInterlaced = false`
- Audio MediaStream has `Codec = null` (ensures audio is included and transcoded)
- `Codec = null` means `CanStreamCopyVideo` returns false — video is transcoded
- But `IsInterlaced = false` removes the yadif deinterlace filter, making transcode ~4x faster
- Result: **Transcode** (no yadif) + audio transcode — still ~42% faster than baseline
- Note: `SupportsProbing = true` does NOT help with stream copy — Jellyfin decides the
  transcode profile BEFORE FFmpeg starts. The `-analyzeduration` flag only affects FFmpeg's
  internal input analysis, not Jellyfin's codec decision.

## Key Code Locations

| What | File | Method/Line |
|------|------|-------------|
| Stream stats collection | `XtreamTunerHost.cs` | `GetChannels` — collects `StreamStats` per channel |
| MediaSource construction | `XtreamTunerHost.cs` | `CreateMediaSourceInfo` — builds MediaStreams |
| Video codec mapping | `XtreamTunerHost.cs` | `MapVideoCodec` — maps Dispatcharr codec names |
| Stream stats model | `StreamStatsInfo.cs` | VideoCodec, AudioCodec, Resolution, etc. |
| Channel ID parsing | `XtreamTunerHost.cs` | `TryParseStreamId` — handles `xtream_` and `hdhr_` prefixes |

## Troubleshooting

### FFmpeg uses Transcode instead of DirectStream
- Check MediaStreams in PlaybackInfo: `Codec` must be non-null, `IsInterlaced` must be false
- Check if client DeviceProfile includes the codec in `TranscodingProfiles.VideoCodec`
- Jellyfin may fall back to transcode if DirectStream fails (check earlier DirectStream log for errors)

### Slow startup despite DirectStream
- The minimum is ~3s due to HLS segment duration
- Check FFmpeg speed — should be >1x for copy mode
- Network latency to source stream adds ~100-500ms
- GOP structure affects first segment timing

### No audio in playback
- Verify audio MediaStream exists (Index 1, Type Audio)
- Audio `Codec = null` forces transcode — this is intentional
- If audio stream is missing entirely, FFmpeg uses `-map -0:a` which excludes audio
