// Copyright (C) 2026  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #117. On Jellyfin 12 the `X-Emby-Token` request header is no longer accepted by the
// `CustomAuthentication` scheme that ships with the server - the request returns
// `[INF] AuthenticationScheme: CustomAuthentication was challenged.` and the response fails,
// which surfaces in the UI as "Failed to load series. Retry" (and the same for Movies and LiveTV
// category expansion). Every other plugin endpoint in this file already uses the
// `Authorization: MediaBrowser Token=<token>` header that Jellyfin 12 accepts; two call sites
// were missed in the original conversion:
//
//   * fetchLiveChannelsForCategory (config.js ~line 1439) - "XtreamLibrary/Channels/Live"
//   * fetchContentItemsForCategory  (config.js ~line 1600) - "XtreamLibrary/Streams/Vod" and
//                                                       "XtreamLibrary/Series/List"
//
// This test parses config.js, locates both fetch call sites by the URL string they target, and
// asserts each one carries `Authorization: MediaBrowser Token=...` (not `X-Emby-Token`). It also
// asserts no `X-Emby-Token` literal remains anywhere in the file, so a future regression at any
// other call site is caught here too.
//
// We do not actually evaluate the script (the file depends on `ApiClient`/`document` which don't
// exist under vm), and we do not exercise the browser fetch path. Static parsing of the source is
// enough to prove the regression because the bug is purely a header literal at a known fetch
// site.

'use strict';

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const CONFIG_PATH = path.join(
    __dirname, '..', '..',
    'Jellyfin.Xtream.Library', 'Configuration', 'Web', 'config.js');

const SOURCE = fs.readFileSync(CONFIG_PATH, 'utf8');

/**
 * Extract the headers object literal from a fetch() call located by an anchor. The anchor can be
 * any substring inside the fetch() argument list (e.g. a unique URL fragment, or `fetch(...)`
 * itself). Returns the literal text including surrounding braces.
 *
 * Strategy: from the anchor position, walk backward to the nearest `fetch(` token, then walk
 * forward through the call tracking paren depth. The options object is the first top-level
 * `{ ... }` that appears inside the fetch() argument list - typically the last argument before
 * the closing `)`. We then read until the matching `}`.
 */
function extractHeadersFor(anchor) {
    const anchorIdx = SOURCE.indexOf(anchor);
    assert.notStrictEqual(anchorIdx, -1, 'config.js does not contain anchor: ' + anchor);

    // Walk backward to the nearest `fetch(` token before the anchor. We scan for `fetch(` as a
    // whole token (preceded by whitespace, `=`, `(`, `,`, or start-of-source) so we don't match
    // the substring inside an identifier or comment.
    let fetchStart = -1;
    for (let i = anchorIdx - 1; i >= 5; i--) {
        if (SOURCE.slice(i, i + 6) === 'fetch(') {
            const prev = i === 0 ? ' ' : SOURCE[i - 1];
            if (prev === ' ' || prev === '\n' || prev === '\t' || prev === '\r' ||
                prev === '(' || prev === ',' || prev === '=' || prev === ':') {
                fetchStart = i;
                break;
            }
        }
    }
    assert.notStrictEqual(fetchStart, -1, 'could not locate fetch( before anchor: ' + anchor);

    const callText = SOURCE.slice(fetchStart);
    // Walk forward through the call tracking paren depth until parenDepth returns to 0 - that
    // marks the closing `)` of fetch(). The options object literal `{ ... }` is somewhere inside
    // those parens as a top-level argument.
    let parenDepth = 0;
    let callEnd = -1;
    for (let i = 0; i < callText.length; i++) {
        const ch = callText[i];
        if (ch === '(') parenDepth++;
        else if (ch === ')') {
            parenDepth--;
            if (parenDepth === 0) { callEnd = i; break; }
        }
    }
    assert.notStrictEqual(callEnd, -1, 'could not locate end of fetch() call for anchor: ' + anchor);

    const argsText = callText.slice(0, callEnd);
    // Within the argument list, find the first top-level `{` (not nested inside a string, regex,
    // or another nested call). That's the start of the options object. We walk the args tracking
    // paren and bracket depth and skipping over string/regex literals. The first `{` at
    // parenDepth=1 (i.e. inside the fetch call but not in any nested call) is the options object.
    let braceDepth = 0;
    let openBrace = -1;
    let closeBrace = -1;
    let i = 0;
    while (i < argsText.length) {
        const ch = argsText[i];
        // Skip string literals.
        if (ch === "'" || ch === '"') {
            const quote = ch;
            i++;
            while (i < argsText.length && argsText[i] !== quote) {
                if (argsText[i] === '\\') i++;
                i++;
            }
            i++;
            continue;
        }
        // Skip line comments.
        if (ch === '/' && argsText[i + 1] === '/') {
            while (i < argsText.length && argsText[i] !== '\n') i++;
            continue;
        }
        // Skip block comments.
        if (ch === '/' && argsText[i + 1] === '*') {
            i += 2;
            while (i < argsText.length && !(argsText[i] === '*' && argsText[i + 1] === '/')) i++;
            i += 2;
            continue;
        }
        if (ch === '{') {
            if (parenDepth === 1 && braceDepth === 0) openBrace = i;
            braceDepth++;
        } else if (ch === '}') {
            braceDepth--;
            if (braceDepth === 0 && openBrace !== -1 && closeBrace === -1) {
                closeBrace = i;
                break;
            }
        } else if (ch === '(' || ch === '[') {
            parenDepth++;
        } else if (ch === ')' || ch === ']') {
            parenDepth--;
        }
        i++;
    }
    assert.notStrictEqual(openBrace, -1, 'no options object found inside fetch() for anchor: ' + anchor);
    assert.notStrictEqual(closeBrace, -1, 'could not locate end of options object for anchor: ' + anchor);
    return argsText.slice(openBrace, closeBrace + 1);
}

test('config.js auth header regression for Jellyfin 12 (issue #117)', async (t) => {
    await t.test('no X-Emby-Token literal remains anywhere in the file', () => {
        assert.ok(
            !SOURCE.includes("'X-Emby-Token'") && !SOURCE.includes('"X-Emby-Token"'),
            'config.js still contains an X-Emby-Token header literal; Jellyfin 12 will reject it');
    });

    await t.test('Channels/Live uses MediaBrowser Token auth header', () => {
        const headersObj = extractHeadersFor('XtreamLibrary/Channels/Live');
        assert.ok(
            headersObj.includes("'Authorization': 'MediaBrowser Token='") ||
                headersObj.includes('"Authorization": "MediaBrowser Token="'),
            'fetchLiveChannelsForCategory must use Authorization: MediaBrowser Token= header. Got: ' +
                headersObj);
        assert.ok(
            !headersObj.includes('X-Emby-Token'),
            'fetchLiveChannelsForCategory still uses X-Emby-Token. Got: ' + headersObj);
    });

    await t.test('Streams/Vod uses MediaBrowser Token auth header', () => {
        // The fetch URL is built dynamically from the `endpoint` variable, so anchor on the
        // `fetch(ApiClient.getUrl(endpoint)` call shape rather than the URL string itself.
        const headersObj = extractHeadersFor('fetch(ApiClient.getUrl(endpoint)');
        assert.ok(
            headersObj.includes("'Authorization': 'MediaBrowser Token='") ||
                headersObj.includes('"Authorization": "MediaBrowser Token="'),
            'fetchContentItemsForCategory (VOD/Series) must use Authorization: MediaBrowser Token= header. Got: ' +
                headersObj);
        assert.ok(
            !headersObj.includes('X-Emby-Token'),
            'fetchContentItemsForCategory (VOD/Series) still uses X-Emby-Token. Got: ' + headersObj);
    });

    await t.test('Series/List uses MediaBrowser Token auth header', () => {
        // The single fetch call in fetchContentItemsForCategory handles both VOD and Series paths
        // (via the `endpoint` ternary). Asserting again with a different anchor is redundant - the
        // previous test already covers this call site - but the issue originally affected both
        // Series and Movies expansion, so a dedicated assertion on the Series code path makes the
        // regression coverage explicit per surface. Anchor on the Series/List endpoint string
        // appearing inside the ternary; extractHeadersFor walks backward to the same fetch call.
        const headersObj = extractHeadersFor("'XtreamLibrary/Series/List'");
        assert.ok(
            headersObj.includes("'Authorization': 'MediaBrowser Token='") ||
                headersObj.includes('"Authorization": "MediaBrowser Token="'),
            'fetchContentItemsForCategory (Series code path) must use Authorization: MediaBrowser *** header. Got: ' +
                headersObj);
        assert.ok(
            !headersObj.includes('X-Emby-Token'),
            'fetchContentItemsForCategory (Series code path) still uses X-Emby-Token. Got: ' + headersObj);
    });
});
