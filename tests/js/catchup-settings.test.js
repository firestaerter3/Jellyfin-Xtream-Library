// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #108. The global settings are read and written inline inside loadConfig/saveConfig, which
// are ApiClient promise chains rather than named functions, so the harness cannot drive them the
// way it drives updateActiveProviderFromUI. Asserting against a local copy of those lines would
// test the copy, not the page, so this checks the one thing that is both real and checkable: that
// every id the script binds actually exists in the markup.
//
// That is not a nicety. Both handlers reach ids unguarded, so one binding without a control throws
// and leaves the whole config page blank, which is what #101 looked like from the outside.

'use strict';

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const WEB = path.join(__dirname, '..', '..', 'Jellyfin.Xtream.Library', 'Configuration', 'Web');
const JS = fs.readFileSync(path.join(WEB, 'config.js'), 'utf8');
const HTML = fs.readFileSync(path.join(WEB, 'config.html'), 'utf8');

const CATCHUP_IDS = [
    'chkEnableCatchup',
    'txtCatchupDays',
    'chkShowCatchupInJellyfin',
    'txtCatchupTimeShiftMinutes',
    'txtCatchupBlockMinutes',
];

test('every catch-up control the script binds exists in the page', () => {
    for (const id of CATCHUP_IDS) {
        assert.ok(JS.includes(`getElementById('${id}')`), `config.js never binds ${id}`);
        assert.ok(HTML.includes(`id="${id}"`), `config.html has no control with id ${id}`);
    }
});

test('the catch-up settings are both read and written', () => {
    for (const id of CATCHUP_IDS) {
        const uses = JS.split(`getElementById('${id}')`).length - 1;
        assert.ok(uses >= 2, `${id} is bound ${uses} time(s); it needs a load and a save`);
    }
});

test('the time correction does not lose a negative value to a falsy default', () => {
    // `parseInt(...) || 0` turns -0 into 0 harmlessly but also turns any falsy parse into the
    // default, and the shape that actually bites is `config.X || 0` on load, which would discard a
    // stored 0 and re-read it as 0 while quietly masking a real value. Pin the guarded forms.
    assert.match(JS, /config\.CatchupTimeShiftMinutes != null \? config\.CatchupTimeShiftMinutes : 0/);
    assert.match(JS, /isNaN\(catchupShift\) \? 0 : catchupShift/);
});

test('the catch-up day limit agrees with what the server accepts', () => {
    // PluginConfiguration clamps to 1-30. The form said 14 until #108, so the page was refusing
    // values the server would have taken.
    assert.match(HTML, /id="txtCatchupDays"[\s\S]{0,200}max="30"/);
    assert.doesNotMatch(HTML, /id="txtCatchupDays"[\s\S]{0,200}max="14"/);
});

test('the Jellyfin catch-up toggle explains that it needs the other one too', () => {
    // Two settings for one feature is the cost of not changing what upgrading means for existing
    // users. It only works if the page says so.
    const section = HTML.slice(HTML.indexOf('id="chkShowCatchupInJellyfin"'));
    assert.match(section.slice(0, 1600), /Both have to be on/);
});

test('the setup URLs carry the security warning', () => {
    // GitHub #109. These four endpoints are anonymous by design, and the M3U they return has the
    // Xtream password in every stream line. The operator copies these URLs from this page, so this
    // is the one place the warning has to be.
    const section = HTML.slice(HTML.indexOf('sectionTitle">Setup URLs'));
    const block = section.slice(0, 4000);
    assert.match(block, /Treat these URLs like your Xtream password/);
    assert.match(block, /without a Jellyfin login/);
    assert.match(block, /Native Tuner above avoids this/);
});

test('the Live TV endpoint allow-list is bound and explained', () => {
    // GitHub #109. Empty means unrestricted, which is what every version so far has done, so the
    // binding must not coerce a blank value into something else.
    assert.ok(HTML.includes('id="txtLiveTvEndpointAllowedIps"'), 'config.html has no allow-list field');
    assert.ok(JS.includes("getElementById('txtLiveTvEndpointAllowedIps')"), 'config.js never binds it');
    assert.match(JS, /config\.LiveTvEndpointAllowedIps \|\| ''/);

    // The trap is that the operator's own server has to be on the list, because the URL this page
    // hands out is built from whatever origin the admin is browsing on.
    const section = HTML.slice(HTML.indexOf('id="txtLiveTvEndpointAllowedIps"'));
    assert.match(section.slice(0, 1800), /Your own server has to be on the list too/);
});
