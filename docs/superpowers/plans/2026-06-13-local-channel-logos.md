# Local File Path Channel Logos Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-extended-cc:subagent-driven-development (recommended) or superpowers-extended-cc:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a Live TV Channel Override logo be a local file path (e.g. `/share/media/TV/Logos/logo.jpg`) instead of only an `http(s)://` URL (GitHub issue #53).

**Architecture:** A local path can't be read by Jellyfin clients (they're remote), so the plugin serves it over HTTP via a new anonymous endpoint `GET /XtreamLibrary/ChannelLogo/{streamId}`. The endpoint looks up the channel's override, and if its logo is a local path that exists on disk, serves the file. The three places that currently emit `channel.StreamIcon` (M3U `tvg-logo`, XMLTV `<icon src>`, and the native tuner's `ChannelInfo.ImageUrl`) rewrite local-path logos to `{serverUrl}/XtreamLibrary/ChannelLogo/{streamId}`, where `serverUrl` comes from `IServerApplicationHost.GetApiUrlForLocalAccess(...)` (a stable loopback/LAN URL — channel images are fetched server-side, so loopback is sufficient and keeps the cached M3U coherent). `http(s)://` logos pass through unchanged, so existing configs are unaffected.

**Tech Stack:** .NET 9 / C# Jellyfin plugin, Newtonsoft.Json models, xUnit + FluentAssertions + Moq, vanilla JS config UI.

**Hard constraint to document (not a code task):** "local path" means readable by the *Jellyfin server process*. In a Docker deployment the logo folder must be bind-mounted into the Jellyfin container. The feature bridges a server-visible path to HTTP; it cannot reach an un-mounted NAS path. This must be stated in the config UI help text (Task 5) and in the issue reply.

**Security model:** The proxy URL carries only a `streamId`. The endpoint resolves `streamId` → the admin-configured override path server-side. No filesystem path is ever taken from the request, so there is no directory-traversal surface. The endpoint serves only a path that an admin already put in `ChannelOverrides` (admin-only config), and only if the file exists.

---

### Task 1: ChannelLogoResolver helper

**Goal:** A testable static helper that decides whether a logo value is a local path and builds the proxy URL.

**Files:**
- Create: `Jellyfin.Xtream.Library/Service/ChannelLogoResolver.cs`
- Test: `Jellyfin.Xtream.Library.Tests/Service/ChannelLogoResolverTests.cs`

**Acceptance Criteria:**
- [ ] `IsLocalPath("http://x/y.png")` and `IsLocalPath("https://x/y.png")` are false (case-insensitive).
- [ ] `IsLocalPath("/share/logo.png")`, `IsLocalPath("C:\\logos\\a.png")`, `IsLocalPath("file:///share/logo.png")` are true.
- [ ] `IsLocalPath(null)` and `IsLocalPath("")` are false.
- [ ] `ResolveDisplayUrl("http://x/y.png", 5, "http://h")` returns `"http://x/y.png"` (unchanged).
- [ ] `ResolveDisplayUrl("/share/logo.png", 5, "http://h:8096")` returns `"http://h:8096/XtreamLibrary/ChannelLogo/5"`.
- [ ] `ResolveDisplayUrl("/share/logo.png", 5, "http://h:8096/")` (trailing slash) returns the same single-slash URL.
- [ ] `ResolveDisplayUrl(null, 5, "http://h")` returns null.

**Verify:** `dotnet test -c Release --filter "FullyQualifiedName~ChannelLogoResolverTests"` → all pass

**Steps:**

- [ ] **Step 1: Write the failing test file `ChannelLogoResolverTests.cs`:**

```csharp
// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using FluentAssertions;
using Jellyfin.Xtream.Library.Service;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Service;

public class ChannelLogoResolverTests
{
    [Theory]
    [InlineData("http://x/y.png", false)]
    [InlineData("https://x/y.png", false)]
    [InlineData("HTTP://x/y.png", false)]
    [InlineData("/share/logo.png", true)]
    [InlineData("C:\\logos\\a.png", true)]
    [InlineData("file:///share/logo.png", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLocalPath_ClassifiesCorrectly(string? icon, bool expected)
    {
        ChannelLogoResolver.IsLocalPath(icon).Should().Be(expected);
    }

    [Fact]
    public void ResolveDisplayUrl_HttpUrl_Unchanged()
    {
        ChannelLogoResolver.ResolveDisplayUrl("http://x/y.png", 5, "http://h").Should().Be("http://x/y.png");
    }

    [Fact]
    public void ResolveDisplayUrl_LocalPath_ReturnsProxyUrl()
    {
        ChannelLogoResolver.ResolveDisplayUrl("/share/logo.png", 5, "http://h:8096")
            .Should().Be("http://h:8096/XtreamLibrary/ChannelLogo/5");
    }

    [Fact]
    public void ResolveDisplayUrl_BaseUrlTrailingSlash_NoDoubleSlash()
    {
        ChannelLogoResolver.ResolveDisplayUrl("/share/logo.png", 5, "http://h:8096/")
            .Should().Be("http://h:8096/XtreamLibrary/ChannelLogo/5");
    }

    [Fact]
    public void ResolveDisplayUrl_NullIcon_ReturnsNull()
    {
        ChannelLogoResolver.ResolveDisplayUrl(null, 5, "http://h").Should().BeNull();
    }

    [Fact]
    public void ResolveDisplayUrl_LocalPath_EmptyBaseUrl_ReturnsOriginal()
    {
        // Defensive: if the server URL can't be determined, don't emit a broken proxy URL.
        ChannelLogoResolver.ResolveDisplayUrl("/share/logo.png", 5, "").Should().Be("/share/logo.png");
    }
}
```

- [ ] **Step 2: Run the test, confirm it fails to compile (helper missing).**

- [ ] **Step 3: Create `ChannelLogoResolver.cs`:**

```csharp
// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

using System;
using System.Globalization;

namespace Jellyfin.Xtream.Library.Service;

/// <summary>
/// Resolves Live TV channel logo values. Local filesystem paths are rewritten to the
/// plugin's <c>ChannelLogo</c> proxy endpoint so remote Jellyfin clients can fetch them;
/// http(s) URLs are passed through unchanged. See issue #53.
/// </summary>
internal static class ChannelLogoResolver
{
    /// <summary>
    /// The route segment (after the server base URL) that serves a channel logo file.
    /// </summary>
    public const string LogoRoute = "XtreamLibrary/ChannelLogo";

    /// <summary>
    /// Returns true if the logo value is a local filesystem path rather than an http(s) URL.
    /// </summary>
    /// <param name="icon">The configured logo value.</param>
    /// <returns>True for local paths (including <c>file://</c>), false for http(s) URLs or empty input.</returns>
    public static bool IsLocalPath(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return false;
        }

        return !icon.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !icon.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a logo value to the URL that should appear in the M3U/XMLTV/tuner output.
    /// Local paths become <c>{baseUrl}/XtreamLibrary/ChannelLogo/{streamId}</c>; http(s) URLs
    /// and empty values are returned unchanged. If <paramref name="baseUrl"/> is empty, the
    /// original value is returned (no broken proxy URL is produced).
    /// </summary>
    /// <param name="icon">The configured logo value (may be null).</param>
    /// <param name="streamId">The channel stream ID.</param>
    /// <param name="baseUrl">The server base URL (no path), e.g. <c>http://127.0.0.1:8096</c>.</param>
    /// <returns>The resolved logo URL, or null if <paramref name="icon"/> is null/empty.</returns>
    public static string? ResolveDisplayUrl(string? icon, int streamId, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return icon;
        }

        if (!IsLocalPath(icon) || string.IsNullOrEmpty(baseUrl))
        {
            return icon;
        }

        var trimmed = baseUrl.TrimEnd('/');
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{trimmed}/{LogoRoute}/{streamId}");
    }
}
```

- [ ] **Step 4: Run the test, confirm pass. Commit:**

```bash
git add Jellyfin.Xtream.Library/Service/ChannelLogoResolver.cs Jellyfin.Xtream.Library.Tests/Service/ChannelLogoResolverTests.cs
git commit -m "feat(livetv): add ChannelLogoResolver for local-path channel logos"
```

---

### Task 2: ChannelLogo proxy endpoint

**Goal:** `GET /XtreamLibrary/ChannelLogo/{streamId}` serves the local logo file for a channel whose override logo is a local path; 404 otherwise.

**Files:**
- Modify: `Jellyfin.Xtream.Library/Api/LiveTvController.cs` (add endpoint near the other `[AllowAnonymous]` endpoints; the controller already injects `LiveTvService _liveTvService`, `IXtreamClient _client`, `ILogger _logger`)
- Modify: `CLAUDE.md` (API Endpoints table — add the row)
- Test: `Jellyfin.Xtream.Library.Tests/Api/LiveTvControllerTests.cs` (create if absent; otherwise add to the existing controller test file)

**Acceptance Criteria:**
- [ ] Request for a `streamId` with no override (or no config) returns `NotFound`.
- [ ] Request for a `streamId` whose override logo is an `http(s)` URL returns `NotFound` (those aren't proxied).
- [ ] Request for a `streamId` whose override logo is a local path that exists returns the file bytes with an `image/*` content type derived from the extension.
- [ ] Request for a local path that does NOT exist on disk returns `NotFound`.
- [ ] Build warning-clean (XML docs; TreatWarningsAsErrors).

**Verify:** `dotnet test -c Release --filter "FullyQualifiedName~LiveTvControllerTests"` → all pass

**Steps:**

- [ ] **Step 1: Read `LiveTvController.cs`** to confirm using-directives and the existing `[AllowAnonymous]` endpoint style (e.g. `GetM3U`). Confirm `Plugin.Instance?.Configuration` access pattern used elsewhere (other endpoints use `Plugin.Instance.Configuration`).

- [ ] **Step 2: Add a private content-type helper and the endpoint** to `LiveTvController`. Use a small extension→MIME switch (avoid adding a StaticFiles dependency):

```csharp
    /// <summary>
    /// Serves the local logo file configured for a channel via Channel Overrides (issue #53).
    /// Only paths that an administrator configured server-side are served; the request carries
    /// only the stream ID, so there is no path-traversal surface.
    /// </summary>
    /// <param name="streamId">The channel stream ID.</param>
    /// <returns>The image file, or 404 if there is no local-path override logo for the channel.</returns>
    [HttpGet("ChannelLogo/{streamId:int}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetChannelLogo([FromRoute] int streamId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return NotFound();
        }

        var overrides = ChannelOverrideParser.Parse(config.ChannelOverrides);
        if (!overrides.TryGetValue(streamId, out var channelOverride)
            || string.IsNullOrEmpty(channelOverride.LogoUrl)
            || !ChannelLogoResolver.IsLocalPath(channelOverride.LogoUrl))
        {
            return NotFound();
        }

        var path = channelOverride.LogoUrl;

        // file:// scheme support — convert to a filesystem path.
        if (path.StartsWith("file://", System.StringComparison.OrdinalIgnoreCase)
            && System.Uri.TryCreate(path, System.UriKind.Absolute, out var fileUri))
        {
            path = fileUri.LocalPath;
        }

        if (!System.IO.Path.IsPathRooted(path) || !System.IO.File.Exists(path))
        {
            _logger.LogWarning("Channel logo file not found for stream {StreamId}: {Path}", streamId, path);
            return NotFound();
        }

        var contentType = GetImageContentType(path);
        return PhysicalFile(path, contentType);
    }

    private static string GetImageContentType(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
    }
```

Ensure the file's `using` block includes `Jellyfin.Xtream.Library.Service;` (for `ChannelOverrideParser` and `ChannelLogoResolver`) — add it if missing.

- [ ] **Step 3: Add the API row to `CLAUDE.md`** under the `Channels/Live` row:
```
| `/XtreamLibrary/ChannelLogo/{streamId}` | GET | Serve a local-path channel override logo |
```

- [ ] **Step 4: Add controller tests.** Check whether a `LiveTvControllerTests.cs` exists; if not create it following the `SyncControllerTests` construction pattern (mock `IXtreamClient`, `LiveTvService`, logger). These tests need `Plugin.Instance.Configuration` set — follow the `XtreamTunerHostTests` pattern that creates a `Plugin` to populate `Plugin.Instance` (and use the shared test collection if these tests touch the static singleton). At minimum:

```csharp
    [Fact]
    public void GetChannelLogo_NoOverride_ReturnsNotFound()
    {
        // No matching override → NotFound (works whether or not Plugin.Instance is set:
        // null config or empty overrides both yield NotFound).
        var result = _controller.GetChannelLogo(streamId: 999999);
        result.Should().BeOfType<NotFoundResult>();
    }
```

If `Plugin.Instance` is initialized in the fixture, also add: a test that sets `config.ChannelOverrides = "5=Name|1|" + tempLogoPath` (where `tempLogoPath` is a real temp .png file you write in the test and delete after), calls `GetChannelLogo(5)`, and asserts the result is a `PhysicalFileResult` with `ContentType == "image/png"`; and a test that an `http://` override logo for a stream returns `NotFound`. If initializing `Plugin.Instance` cleanly is not feasible in this fixture, add only the NotFound test and note the happy-path is covered by `ChannelLogoResolver` unit tests plus code inspection.

- [ ] **Step 5: Build + test**

Run: `dotnet build -c Release && dotnet test -c Release --filter "FullyQualifiedName~LiveTvControllerTests"`
Expected: build warning-clean, tests PASS

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Xtream.Library/Api/LiveTvController.cs Jellyfin.Xtream.Library.Tests/Api/LiveTvControllerTests.cs CLAUDE.md
git commit -m "feat(api): add ChannelLogo endpoint to serve local-path override logos (#53)"
```

---

### Task 3: Rewrite local logos in M3U + XMLTV (LiveTvService)

**Goal:** `LiveTvService` rewrites local-path channel logos to the proxy URL in M3U and XMLTV output, and exposes a method the tuner host can use for the same rewrite.

**Files:**
- Modify: `Jellyfin.Xtream.Library/Service/LiveTvService.cs` (constructor + `GetM3UPlaylistAsync`/`GetCatchupM3UPlaylistAsync`/`GetXmltvEpgAsync` + `GenerateM3U` + the XMLTV generator + a new `ResolveChannelLogoUrl` method)
- Modify: `Jellyfin.Xtream.Library.Tests/Api/SyncControllerTests.cs` (2 `new LiveTvService(...)` call sites) and `Jellyfin.Xtream.Library.Tests/Service/XtreamTunerHostTests.cs` (1 call site) — add the new constructor arg
- Test: `Jellyfin.Xtream.Library.Tests/Service/LiveTvServiceTests.cs`

**Acceptance Criteria:**
- [ ] `LiveTvService` constructor takes an `IServerApplicationHost` and stores it.
- [ ] `GenerateM3U` emits `tvg-logo="{serverUrl}/XtreamLibrary/ChannelLogo/{streamId}"` when a channel's `StreamIcon` is a local path, and emits the original value for http(s) logos.
- [ ] The XMLTV generator does the same for `<icon src=...>`.
- [ ] `ResolveChannelLogoUrl(streamIcon, streamId)` returns the proxy URL for local paths and the original for http(s)/empty.
- [ ] Existing M3U/XMLTV tests still pass (http logos unchanged).
- [ ] Build warning-clean.

**Verify:** `dotnet build -c Release && dotnet test -c Release --filter "FullyQualifiedName~LiveTvServiceTests|FullyQualifiedName~SyncControllerTests|FullyQualifiedName~XtreamTunerHostTests"` → all pass

**Steps:**

- [ ] **Step 1: Add `IServerApplicationHost` to the constructor.** Add `using MediaBrowser.Controller;` and a field. Change the constructor at line 64:

```csharp
    private readonly IServerApplicationHost _appHost;

    // ... in constructor parameter list add `IServerApplicationHost appHost` and assign:
    public LiveTvService(IXtreamClient client, IServerApplicationPaths appPaths, IServerApplicationHost appHost, ILogger<LiveTvService> logger)
    {
        _client = client;
        _appPaths = appPaths;
        _appHost = appHost;
        _logger = logger;
    }
```

Add the XML `<param name="appHost">` doc line to match the existing doc-comment style (TreatWarningsAsErrors requires it).

- [ ] **Step 2: Add a base-URL helper + public resolver method** to `LiveTvService`:

```csharp
    /// <summary>
    /// Gets the server base URL used to build channel-logo proxy links. Channel images are
    /// fetched server-side, so the loopback/LAN URL is sufficient and is stable across requests
    /// (keeping the cached M3U/EPG coherent). Returns an empty string if it cannot be resolved.
    /// </summary>
    private string GetServerBaseUrl()
    {
        try
        {
            return _appHost.GetApiUrlForLocalAccess(System.Net.IPAddress.Loopback, allowHttps: false) ?? string.Empty;
        }
        catch (System.Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve server base URL for channel logo proxy");
            return string.Empty;
        }
    }

    /// <summary>
    /// Resolves a channel logo value for output: local filesystem paths are rewritten to the
    /// ChannelLogo proxy endpoint; http(s) URLs pass through. See issue #53.
    /// </summary>
    /// <param name="streamIcon">The channel's logo value (may be null).</param>
    /// <param name="streamId">The channel stream ID.</param>
    /// <returns>The logo URL to expose to Jellyfin, or null if there is no logo.</returns>
    public string? ResolveChannelLogoUrl(string? streamIcon, int streamId)
        => ChannelLogoResolver.ResolveDisplayUrl(streamIcon, streamId, GetServerBaseUrl());
```

NOTE: verify the exact signature of `GetApiUrlForLocalAccess` against the referenced Jellyfin.Controller 10.11 assembly (the XML doc shows `GetApiUrlForLocalAccess(System.Net.IPAddress, System.Boolean)`). If the parameter is named differently or nullable, adjust the call so it compiles. If `allowHttps` is positional only, drop the name.

- [ ] **Step 3: Thread `baseUrl` into `GenerateM3U`.** Change its signature to `private static string GenerateM3U(List<LiveStreamInfo> channels, PluginConfiguration config, bool catchupOnly, string baseUrl)` and update both call sites (`GetM3UPlaylistAsync` line ~108 and `GetCatchupM3UPlaylistAsync`) to pass `GetServerBaseUrl()`. Then replace the `tvg-logo` block:

```csharp
            var logoUrl = ChannelLogoResolver.ResolveDisplayUrl(channel.StreamIcon, channel.StreamId, baseUrl);
            if (!string.IsNullOrEmpty(logoUrl))
            {
                extinf.Append(CultureInfo.InvariantCulture, $" tvg-logo=\"{EscapeAttribute(logoUrl)}\"");
            }
```

- [ ] **Step 4: Thread `baseUrl` into the XMLTV generator.** Find the XMLTV generation method (the one with `<icon src=...>` near line 491; likely `GenerateXmltv...`). Add a `string baseUrl` parameter, pass `GetServerBaseUrl()` from `GetXmltvEpgAsync`, and replace the icon block:

```csharp
            var iconUrl = ChannelLogoResolver.ResolveDisplayUrl(channel.StreamIcon, channel.StreamId, baseUrl);
            if (!string.IsNullOrEmpty(iconUrl))
            {
                sb.Append(CultureInfo.InvariantCulture, $"    <icon src=\"{EscapeXml(iconUrl)}\" />\n");
            }
```

(If the XMLTV method is `static`, pass `baseUrl` as a param like `GenerateM3U`. If it's an instance method, it can call `GetServerBaseUrl()` directly.)

- [ ] **Step 5: Update the three `new LiveTvService(...)` test call sites.** In each, add a mocked `IServerApplicationHost`. Example for `SyncControllerTests.cs` (both sites) and `XtreamTunerHostTests.cs`:

```csharp
var appHostMock = new Mock<IServerApplicationHost>();
appHostMock.Setup(h => h.GetApiUrlForLocalAccess(It.IsAny<System.Net.IPAddress>(), It.IsAny<bool>()))
    .Returns("http://127.0.0.1:8096");
var liveTvService = new LiveTvService(_mockClient.Object, appPathsMock.Object, appHostMock.Object, NullLogger<LiveTvService>.Instance);
```

Match each file's existing variable names (`appPathsMock` vs `serverAppPaths` etc.) and add `using MediaBrowser.Controller;` / `using Moq;` if not already present. Adjust the `Setup` to the real `GetApiUrlForLocalAccess` signature.

- [ ] **Step 6: Add `LiveTvServiceTests` for the rewrite.** Read the existing `LiveTvServiceTests.cs` to match construction conventions, then add tests that verify `GenerateM3U`/the resolver path emits a proxy URL for a local-path icon and the original for an http icon. If `GenerateM3U` is private, test via the public `ResolveChannelLogoUrl` method and/or via `GetM3UPlaylistAsync` with a stubbed channel set, whichever the existing tests already do. Concretely, at least:

```csharp
    [Fact]
    public void ResolveChannelLogoUrl_LocalPath_ReturnsProxyUrl()
    {
        // service constructed with appHost mock returning http://127.0.0.1:8096
        _service.ResolveChannelLogoUrl("/share/logo.png", 7)
            .Should().Be("http://127.0.0.1:8096/XtreamLibrary/ChannelLogo/7");
    }

    [Fact]
    public void ResolveChannelLogoUrl_HttpUrl_Unchanged()
    {
        _service.ResolveChannelLogoUrl("http://x/y.png", 7).Should().Be("http://x/y.png");
    }
```

- [ ] **Step 7: Build + targeted tests**

Run: `dotnet build -c Release && dotnet test -c Release --filter "FullyQualifiedName~LiveTvServiceTests|FullyQualifiedName~SyncControllerTests|FullyQualifiedName~XtreamTunerHostTests"`
Expected: build warning-clean, tests PASS

- [ ] **Step 8: Commit**

```bash
git add Jellyfin.Xtream.Library/Service/LiveTvService.cs Jellyfin.Xtream.Library.Tests/
git commit -m "feat(livetv): rewrite local-path channel logos to proxy URL in M3U/XMLTV (#53)"
```

---

### Task 4: Rewrite native tuner ImageUrl (XtreamTunerHost)

**Goal:** The native tuner's `ChannelInfo.ImageUrl` uses the proxy URL for local-path logos.

**Files:**
- Modify: `Jellyfin.Xtream.Library/Service/XtreamTunerHost.cs` (the `ChannelInfo` creation, ImageUrl line ~140)
- Test: `Jellyfin.Xtream.Library.Tests/Service/XtreamTunerHostTests.cs`

**Acceptance Criteria:**
- [ ] When a channel's `StreamIcon` is a local path, the produced `ChannelInfo.ImageUrl` is `{serverUrl}/XtreamLibrary/ChannelLogo/{streamId}`.
- [ ] When `StreamIcon` is an http(s) URL, `ImageUrl` is unchanged.
- [ ] When `StreamIcon` is empty, `ImageUrl` is null (unchanged behavior).
- [ ] Existing tuner tests still pass.

**Verify:** `dotnet test -c Release --filter "FullyQualifiedName~XtreamTunerHostTests"` → all pass

**Steps:**

- [ ] **Step 1: Change the ImageUrl assignment** at line ~140 from:

```csharp
                ImageUrl = string.IsNullOrEmpty(channel.StreamIcon) ? null : channel.StreamIcon,
```

to:

```csharp
                ImageUrl = _liveTvService.ResolveChannelLogoUrl(channel.StreamIcon, channel.StreamId),
```

(`ResolveChannelLogoUrl` already returns null for empty input, so behavior for no-logo channels is preserved.)

- [ ] **Step 2: Add/extend a tuner test.** The `XtreamTunerHostTests` fixture already builds a `LiveTvService` with the appHost mock (from Task 3). Add a test that a channel with a local-path `StreamIcon` yields `ImageUrl == "http://127.0.0.1:8096/XtreamLibrary/ChannelLogo/{id}"`, and one with an http icon is unchanged. Follow the existing test pattern for how channels are stubbed into `GetChannels` (the file already has ~20 tests exercising `GetChannels`; mirror their setup).

- [ ] **Step 3: Build + test**

Run: `dotnet build -c Release && dotnet test -c Release --filter "FullyQualifiedName~XtreamTunerHostTests"`
Expected: build warning-clean, tests PASS

- [ ] **Step 4: Commit**

```bash
git add Jellyfin.Xtream.Library/Service/XtreamTunerHost.cs Jellyfin.Xtream.Library.Tests/Service/XtreamTunerHostTests.cs
git commit -m "feat(livetv): use ChannelLogo proxy URL for native tuner channel images (#53)"
```

---

### Task 5: Config UI help text + docs

**Goal:** Tell users they can use a local path, with the server-must-read-the-path caveat.

**Files:**
- Modify: `Jellyfin.Xtream.Library/Configuration/Web/config.html` (Channel Overrides help text, lines 1015-1022)
- Modify: `README.md` (Live TV / Channel Overrides section, if present) and `docs/ARCHITECTURE.md` (note the ChannelLogo endpoint + resolver)

**Acceptance Criteria:**
- [ ] The Channel Overrides help text documents that the logo can be an http(s) URL OR a local file path, and that a local path must be readable by the Jellyfin server (bind-mounted into the container for Docker installs).
- [ ] README mentions local-path channel logos.
- [ ] No secrets/PII added (generic example paths only).

**Verify:** Open config.html in the dashboard, confirm the help text renders. (No JS harness; visual check.)

**Steps:**

- [ ] **Step 1: Update the help text** in `config.html`. Replace the `<div class="fieldDescription">…</div>` block (lines 1015-1022) so the LogoUrl examples include a local-path example and the caveat:

```html
                                <div class="fieldDescription">
                                    Override channel name, number, or logo. Format: StreamId=Name|Number|Logo<br/>
                                    Examples:<br/>
                                    <code>123=BBC One</code> - Just rename<br/>
                                    <code>456=CNN|2</code> - Rename and set channel number<br/>
                                    <code>789=Sky News|5|http://logo.png</code> - All fields (remote logo URL)<br/>
                                    <code>321=Fox|7|/media/logos/fox.png</code> - Local logo file<br/>
                                    <code>101=|10|</code> - Just channel number (keep original name)<br/>
                                    The logo can be an http(s) URL or a local file path. A local path must be readable by the
                                    Jellyfin server itself — for Docker installs, bind-mount the logo folder into the container.
                                </div>
```

- [ ] **Step 2: Update the placeholder** on the textarea (line 1014) to include a local example (optional but consistent):

```html
                                    placeholder="123=BBC One&#10;456=CNN|2&#10;789=Sky News|5|http://logo.png&#10;321=Fox|7|/media/logos/fox.png&#10;101=|10|"
```

- [ ] **Step 3: README + ARCHITECTURE.** Add a brief note under the Live TV / Channel Overrides documentation in `README.md` that the logo field accepts a local file path (served via the plugin's ChannelLogo endpoint; must be server-readable). In `docs/ARCHITECTURE.md`, note the `GET /XtreamLibrary/ChannelLogo/{streamId}` endpoint and `ChannelLogoResolver`. Keep prose plain (no AI tells).

- [ ] **Step 4: Commit**

```bash
git add Jellyfin.Xtream.Library/Configuration/Web/config.html README.md docs/ARCHITECTURE.md
git commit -m "docs(livetv): document local file path channel logos (#53)"
```

---

### Task 6: Version bump + full verification

**Goal:** Bump version and verify the whole build + test suite before release.

**Files:**
- Modify: `Jellyfin.Xtream.Library/Jellyfin.Xtream.Library.csproj` (version `1.37.0.0` → `1.38.0.0`)

**Acceptance Criteria:**
- [ ] `dotnet build -c Release` warning-clean.
- [ ] `dotnet test -c Release` full suite passes.
- [ ] Version `1.38.0.0` in `AssemblyVersion` + `FileVersion`.

**Verify:** `dotnet build -c Release && dotnet test -c Release` → build clean, all tests pass

**Steps:**

- [ ] **Step 1: Bump version** in the csproj to `1.38.0.0` (both `AssemblyVersion` and `FileVersion`). Minor bump — new user-facing feature.

- [ ] **Step 2: Full build + test**

Run: `dotnet build -c Release && dotnet test -c Release`
Expected: build warning-clean; all tests pass (baseline 460 + the new ones)

- [ ] **Step 3: Commit**

```bash
git add Jellyfin.Xtream.Library/Jellyfin.Xtream.Library.csproj docs/superpowers/plans/
git commit -m "Release v1.38.0.0: local file path channel logos (#53)"
```

- [ ] **Step 4: Release (user-gated — do NOT run autonomously).** After user approval, follow `CLAUDE.md` Release Process: tag `v1.38.0.0`, publish, zip the DLL, `gh release create`, add the beta-channel entry to `../jellyfin-plugin-repo/manifest-dev.json` (use `GH_INSECURE_NO_TLS_VERIFY=1` + `dangerouslyDisableSandbox`). Then draft (do not auto-post) a reply on issue #53 that explains the feature AND the server-must-read-the-path caveat, for the user's approval.

---

## Notes for the implementer

- **Why loopback base URL:** channel logos are fetched server-side (the native tuner downloads `ImageUrl`; Jellyfin's own M3U tuner downloads `tvg-logo`). A stable loopback/LAN URL therefore works and, unlike a per-request host, keeps the cached M3U/EPG strings valid. External IPTV players pointed directly at the plugin's M3U are an edge case — those users can keep using http URLs.
- **No parser/model change:** `ChannelOverride.LogoUrl` already stores any string; the parser already accepts local paths. The work is purely in *rendering* the value (proxy serving + URL rewriting), not parsing it.
- **Security:** the proxy never accepts a path from the request — only a `streamId` it maps to admin-configured config. Keep it that way; do not add a path query parameter.
- **Don't break http logos:** every rewrite point must pass http(s) URLs through unchanged. The `ChannelLogoResolver` tests lock this in.
