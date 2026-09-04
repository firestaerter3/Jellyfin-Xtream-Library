// Copyright (C) 2024  Roland Breitschaft
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// GitHub #84. Every field read back by updateActiveProviderFromUI was trimmed except the two
// password fields, so a password pasted with a trailing space was saved verbatim and ended up
// interpolated into every STRM URL as ".../<password> /12345.mkv", giving a 403 on every item.
//
// The server side trims as well (ConnectionInfo), which is what repairs configurations already
// saved with whitespace. This layer stops it being stored in the first place.

'use strict';

const test = require('node:test');
const assert = require('node:assert');
const { loadConfig, element, withDocument } = require('./helpers/config-harness');

/**
 * updateActiveProviderFromUI reads 30-odd element ids and would throw on any it cannot find.
 * Only the credential fields matter here, so back the lookup with a proxy that invents an empty
 * stub for every other id. New fields on the form therefore cannot break this test.
 */
function fields(overrides) {
    const cache = {};
    return new Proxy({}, {
        get(_target, id) {
            if (typeof id !== 'string') {
                return undefined;
            }

            if (!(id in cache)) {
                cache[id] = element(Object.prototype.hasOwnProperty.call(overrides, id) ? overrides[id] : {});
            }

            return cache[id];
        },
    });
}

/** Runs updateActiveProviderFromUI against the given field values and returns the saved provider. */
function readBack(overrides) {
    const config = loadConfig();
    config.providers = [{}];
    config.activeProviderIndex = 0;

    const restore = withDocument(fields(overrides));
    try {
        config.updateActiveProviderFromUI();
    } finally {
        restore();
    }

    return config.providers[0];
}

test('the provider password is trimmed before it is saved', async (t) => {
    await t.test('a trailing space is stripped', () => {
        assert.strictEqual(readBack({ txtPassword: { value: 'secret ' } }).Password, 'secret');
    });

    await t.test('leading and trailing whitespace is stripped', () => {
        assert.strictEqual(readBack({ txtPassword: { value: '  secret\t' } }).Password, 'secret');
    });

    await t.test('a non-breaking space is stripped', () => {
        // The usual artefact of copying credentials out of a provider's web page.
        assert.strictEqual(readBack({ txtPassword: { value: 'secret\u00A0' } }).Password, 'secret');
    });

    await t.test('a whitespace-only value becomes empty', () => {
        assert.strictEqual(readBack({ txtPassword: { value: '   ' } }).Password, '');
    });

    await t.test('whitespace inside the password is preserved', () => {
        assert.strictEqual(readBack({ txtPassword: { value: ' pass word ' } }).Password, 'pass word');
    });
});

test('the Dispatcharr API password is trimmed before it is saved', async (t) => {
    await t.test('a trailing space is stripped', () => {
        assert.strictEqual(readBack({ txtDispatcharrApiPass: { value: 'apipass ' } }).DispatcharrApiPass, 'apipass');
    });

    await t.test('the user field keeps the trimming it already had', () => {
        assert.strictEqual(readBack({ txtDispatcharrApiUser: { value: ' admin ' } }).DispatcharrApiUser, 'admin');
    });
});

test('the saved credentials cannot produce a stream URL with a stray space', () => {
    const provider = readBack({
        txtBaseUrl: { value: 'http://host:8901 ' },
        txtUsername: { value: 'user ' },
        txtPassword: { value: 'secret ' },
    });

    const url = `${provider.BaseUrl}/movie/${provider.Username}/${provider.Password}/572499.mkv`;

    assert.strictEqual(url, 'http://host:8901/movie/user/secret/572499.mkv');
    assert.ok(!url.includes(' '), 'stream URL must not contain a space');
});
