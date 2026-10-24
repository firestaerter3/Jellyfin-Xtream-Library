// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// config.js looks every field up by id when it loads and saves the settings. The other tests stub
// the DOM, so a field that was moved, renamed or dropped from config.html goes unnoticed there:
// in the browser getElementById returns null and saving throws, so nothing gets saved. This test
// reads the real page and fails on any id config.js asks for that the page does not have.

'use strict';

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const WEB = path.join(__dirname, '..', '..', 'Jellyfin.Xtream.Library', 'Configuration', 'Web');
const JS = fs.readFileSync(path.join(WEB, 'config.js'), 'utf8');
const HTML = fs.readFileSync(path.join(WEB, 'config.html'), 'utf8');

test('every element config.js looks up exists in config.html', () => {
    const wanted = new Set([
        ...JS.matchAll(/getElementById\(\s*['"]([^'"]+)['"]\s*\)/g),
        ...JS.matchAll(/querySelector(?:All)?\(\s*['"]#([A-Za-z_][\w-]*)/g),
    ].map((m) => m[1]));
    // (?:^|\s) rather than \b, so a data-id="..." attribute does not count as an id.
    const present = new Set([...HTML.matchAll(/(?:^|\s)id="([^"]+)"/g)].map((m) => m[1]));
    const missing = [...wanted].filter((id) => !present.has(id)).sort();

    assert.ok(wanted.size > 100, `expected config.js to look up many ids, found ${wanted.size}`);
    assert.deepStrictEqual(missing, [], `config.js looks up ids the page does not have: ${missing.join(', ')}`);
});

test('no id appears twice on the page', () => {
    const ids = [...HTML.matchAll(/(?:^|\s)id="([^"]+)"/g)].map((m) => m[1]);
    const seen = new Set();
    const twice = ids.filter((id) => (seen.has(id) ? true : (seen.add(id), false)));
    assert.deepStrictEqual(twice, [], `ids used twice: ${twice.join(', ')}`);
});
