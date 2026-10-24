// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// Titles, category names and error messages come from the provider, and the settings page runs
// with the admin's Jellyfin session. Markup in any of them that reaches innerHTML unescaped runs
// as the admin. These pin the places that used to let it through.

'use strict';

const test = require('node:test');
const assert = require('node:assert');
const { loadConfig, element, withDocument } = require('./helpers/config-harness');

const PAYLOAD = '<img src=x onerror="alert(1)">\'';

/** Proxy document lookup: every id gets a stub element, kept so the test can read it back. */
function allElements() {
    const cache = {};
    return new Proxy({}, {
        get(_target, id) {
            if (typeof id !== 'string') {
                return undefined;
            }

            if (!(id in cache)) {
                cache[id] = element({});
            }

            return cache[id];
        },
    });
}

test('escapeHtml escapes quotes, so the result is safe inside a quoted attribute', () => {
    const config = loadConfig();

    assert.strictEqual(
        config.escapeHtml('a" onmouseover="x\' & <b>'),
        'a&quot; onmouseover=&quot;x&#39; &amp; &lt;b&gt;');
    assert.strictEqual(config.escapeHtml(null), '');
    assert.strictEqual(config.escapeHtml(undefined), '');
    assert.strictEqual(config.escapeHtml(42), '42');
});

for (const field of ['MoviePhase', 'SeriesPhase', 'Phase']) {
    test(`displayProgress escapes ${field}, which carries provider titles`, () => {
        const config = loadConfig();
        const elements = allElements();
        const restore = withDocument(elements);
        try {
            config.displayProgress({ [field]: 'Processing: ' + PAYLOAD });
        } finally {
            restore();
        }

        const html = elements.syncStatus.innerHTML;
        assert.ok(!html.includes('<img'), html);
        assert.ok(html.includes('&lt;img src=x onerror=&quot;alert(1)&quot;&gt;&#39;'), html);
    });
}

test('displaySyncResult escapes the sync error', () => {
    const config = loadConfig();
    const elements = allElements();
    const restore = withDocument(elements);
    try {
        config.displaySyncResult({
            Success: false,
            StartTime: '2026-10-20T10:00:00Z',
            EndTime: '2026-10-20T10:01:00Z',
            Error: 'Could not convert string to integer: ' + PAYLOAD,
        });
    } finally {
        restore();
    }

    const html = elements.lastSyncInfo.innerHTML;
    assert.ok(!html.includes('<img'), html);
    assert.ok(html.includes('&lt;img'), html);
});
