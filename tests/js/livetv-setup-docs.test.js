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

test('Native Tuner description tells users to add the M3U URL as an M3U Tuner, not HDHomeRun', () => {
    // The old wording paired "manual HDHomeRun tuner" with the M3U URL, which fails because
    // Jellyfin's HDHomeRun type expects an HDHomeRun device or its discovery JSON.
    assert.match(
        CONFIG_HTML,
        /manual\s*<strong>\s*M3U Tuner\s*<\/strong>\s*\(not HDHomeRun\)/i,
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

test('Native Tuner description says the plugin lists itself under Tuner Devices', () => {
    // Jellyfin shows Live TV only once a tuner is listed under Tuner Devices (GitHub #152). The
    // plugin adds that entry itself when Live TV and Native Tuner are on, so the text has to say
    // both are needed, and that a second entry added by hand is not.
    assert.match(
        CONFIG_HTML,
        /With this and <strong>Enable Live TV<\/strong> on, the\s*plugin lists itself under Dashboard.*Tuner Devices/is,
        'expected the help text to say the plugin adds its own tuner entry when both checkboxes are on');
    assert.match(
        CONFIG_HTML,
        /do not add a second one by hand/is,
        'expected a warning against adding a second Xtream Library entry by hand');
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
