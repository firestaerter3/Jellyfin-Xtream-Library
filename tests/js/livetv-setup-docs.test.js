// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

'use strict';

// GitHub #102. The Live TV config tab had three documentation defects:
//   1. The Native Tuner warning instructed users to add the M3U URL as a "HDHomeRun tuner",
//      which Jellyfin's Add Tuner dialog rejects for M3U input. The correct type is "M3U Tuner".
//   2. The text claimed the plugin's own tuner "does not appear under Dashboard -> Live TV ->
//      Tuner Devices". XtreamTunerHost registers an ITunerHost with Type "xtream-library", which
//      Jellyfin lists in the Add Tuner type dropdown unconditionally. The docs must acknowledge
//      that and tell users not to add it manually.
//   3. The "Setup URLs" section lumped all three URLs under "Dashboard -> Live TV -> Add Tuner",
//      but only the M3U goes there. The EPG belongs in TV Guide Data Providers, and the Catch-up
//      M3U is not consumed by Jellyfin at all.

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const CONFIG_HTML = fs.readFileSync(
    path.join(__dirname, '..', '..',
        'Jellyfin.Xtream.Library', 'Configuration', 'Web', 'config.html'),
    'utf8');
const CONFIG_JS = fs.readFileSync(
    path.join(__dirname, '..', '..',
        'Jellyfin.Xtream.Library', 'Configuration', 'Web', 'config.js'),
    'utf8');

test('the M3U tuner choice tells users to add an M3U Tuner, not HDHomeRun', () => {
    // The old wording paired "manual HDHomeRun tuner" with the M3U URL, which fails because
    // Jellyfin's HDHomeRun type expects an HDHomeRun device or its discovery JSON. Since the tuner
    // became a choice, this text is the hint config.js shows for "My own M3U tuner".
    assert.match(
        CONFIG_JS,
        /<strong>M3U Tuner<\/strong> \(not HDHomeRun\)/,
        'expected the M3U URL to be added as an M3U Tuner, not an HDHomeRun tuner');
});

test('Native Tuner description no longer claims the tuner is hidden from the Dashboard', () => {
    // Old wording: "the tuner does not appear under Dashboard -> Live TV -> Tuner Devices because
    // Jellyfin only lists tuners added through that UI". The plugin's tuner IS visible, because
    // XtreamTunerHost registers itself as an ITunerHost implementation.
    assert.doesNotMatch(
        CONFIG_HTML,
        /does not appear under Dashboard.+Tuner Devices.+because Jellyfin only lists tuners added through that UI/i,
        'old text claimed the plugin tuner is hidden, but it is registered as an ITunerHost');
});

test('the native tuner choice says the plugin lists itself under Tuner Devices', () => {
    // Jellyfin shows Live TV only once a tuner is listed under Tuner Devices (GitHub #152). The
    // plugin adds that entry itself, so the hint has to say so, and that a second entry added by
    // hand is not needed. The choice is only shown while Enable Live TV is on.
    assert.match(
        CONFIG_JS,
        /The plugin lists itself under Dashboard &rarr; Live TV &rarr; Tuner Devices/,
        'expected the hint to say the plugin adds its own tuner entry');
    assert.match(CONFIG_JS, /Do not add a second one by hand/, 'expected a warning against a second entry');
    assert.match(CONFIG_HTML, /id="liveTvDependentTop"[\s\S]*id="radTunerNative"/,
        'expected the tuner choice inside the part that only shows with Live TV on');
});

test('Setup URLs section explains the three URLs go in three different places', () => {
    // M3U -> Add Tuner
    assert.match(
        CONFIG_HTML,
        /<strong>M3U Playlist URL<\/strong>.*Tuner Devices.*M3U Tuner/is,
        'M3U URL must point users to the M3U Tuner type under Tuner Devices');
    // EPG -> TV Guide Data Providers
    assert.match(
        CONFIG_HTML,
        /<strong>EPG \(XMLTV\) URL<\/strong>.*TV Guide Data Providers/is,
        'EPG URL must point users to TV Guide Data Providers, not the tuner dialog');
    // Catch-up -> not Jellyfin
    assert.match(
        CONFIG_HTML,
        /<strong>Catch-up M3U URL<\/strong>.*Jellyfin does not use it/is,
        'Catch-up URL must say Jellyfin has no catch-up field');
});

test('Setup URLs section no longer says all three URLs go in Add Tuner', () => {
    // Old wording implied the M3U, EPG and Catch-up all belong in the same Add Tuner dialog.
    // The page uses HTML entities (&rarr;) for arrows, but the legacy sentence opener
    // "Add these URLs to Jellyfin's Live TV settings" is the real regression signal and
    // does not depend on arrow encoding.
    assert.doesNotMatch(
        CONFIG_HTML,
        /Add these URLs to Jellyfin's Live TV settings/,
        'old single-line setup instruction implied all URLs go in one place');
});
