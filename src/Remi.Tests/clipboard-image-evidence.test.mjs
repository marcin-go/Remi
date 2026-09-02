import assert from 'node:assert/strict';
import test from 'node:test';
import { randomUUID, webcrypto } from 'node:crypto';
import { readFile } from 'node:fs/promises';

Object.defineProperty(globalThis, 'crypto', { configurable: true, value: { randomUUID, subtle: webcrypto.subtle } });
class TestElement {
    constructor(owner, editing = false) { this.owner = owner; this.editing = editing; }
    closest() { return this.editing ? this : null; }
}
globalThis.Element = TestElement;
function eventTarget() {
    const listeners = new Map();
    return {
        classList: { add() {}, remove() {} },
        addEventListener(type, handler) { if (!listeners.has(type)) listeners.set(type, new Set()); listeners.get(type).add(handler); },
        removeEventListener(type, handler) { listeners.get(type)?.delete(handler); },
        fire(type, event) { for (const handler of listeners.get(type) ?? []) handler(event); },
    };
}
globalThis.document = eventTarget();
const source = await readFile(new URL('../Remi.Web/wwwroot/clipboard-image-evidence.js', import.meta.url), 'utf8');
const evidence = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
function fixture(callback = async () => {}) {
    const fileInput = eventTarget(), dropZone = eventTarget(), error = { hidden: true };
    const host = Object.assign(eventTarget(), {
        contains(target) { return target?.owner === host; },
        querySelector(selector) {
            return ({ 'input[type="file"]': fileInput, '.clipboard-document-dropzone': dropZone, 'input[name="__RequestVerificationToken"]': { value: 'test-token' }, '[data-clipboard-client-error]': error })[selector];
        },
    });
    evidence.attach(host, { invokeMethodAsync: callback });
    const paste = (files, shape = 'both') => document.fire('paste', {
        target: new TestElement(host),
        clipboardData: { files: shape === 'items' ? [] : files, items: shape === 'files' ? [] : files.map(file => ({ kind: 'file', type: file.type, getAsFile: () => file })) },
        preventDefault() {},
    });
    const disk = files => fileInput.fire('change', { target: { files, value: 'selected' } });
    const drop = files => dropZone.fire('drop', { dataTransfer: { files }, preventDefault() {} });
    return { host, fileInput, error, paste, disk, drop, dispose: () => evidence.dispose(host) };
}
function file(index, type = 'image/png', size = 80 * 1024) {
    const bytes = new Uint8Array(size).fill(index + 1);
    const blob = new Blob([bytes], { type });
    Object.defineProperty(blob, 'name', { value: `document-${index + 1}.${type === 'application/pdf' ? 'pdf' : 'png'}` });
    return blob;
}
function successfulUploads() {
    const requests = [];
    globalThis.fetch = async (url, options) => { requests.push({ url, options }); return { ok: true }; };
    return requests;
}
const entityId = '11111111-1111-1111-1111-111111111111';
for (const method of ['disk', 'disk-batches', 'drop', 'paste-items', 'paste-files', 'paste-both', 'repeated-paste', 'mixed']) {
    test(`20 attachments: ${method}, small metadata, exact upload bytes`, async () => {
        const fx = fixture(), requests = successfulUploads();
        const files = Array.from({ length: 20 }, (_, index) => file(index, method === 'disk' && index % 2 ? 'application/pdf' : 'image/png'));
        try {
            if (method === 'disk') fx.disk(files);
            else if (method === 'disk-batches') {
                fx.disk(files.slice(0, 10));
                assert.equal((await evidence.prepare(fx.host)).documents.length, 10);
                fx.disk(files.slice(10));
            }
            else if (method === 'drop') fx.drop(files);
            else if (method === 'repeated-paste') for (const item of files) fx.paste([item]);
            else if (method === 'mixed') { fx.disk(files.slice(0, 7)); fx.drop(files.slice(7, 14)); for (const item of files.slice(14)) fx.paste([item], 'files'); }
            else fx.paste(files, method.split('-')[1]);
            const state = await evidence.prepare(fx.host);
            assert.equal(state.documents.length, 20);
            assert.equal(state.error, null);
            assert.ok(Buffer.byteLength(JSON.stringify(state)) < 32 * 1024, '20 large previews must fit under the default Blazor message limit.');
            assert.ok(state.documents.filter(item => item.previewUrl).every(item => item.previewUrl.startsWith('blob:')));
            assert.ok(!JSON.stringify(state).includes('data:image'));
            evidence.rename(fx.host, state.documents[0].id, 'Signed agreement v1.2');
            const result = await evidence.archive(fx.host, 'contract', entityId);
            assert.equal(result.archivedCount, 20);
            assert.equal(result.documents.length, 0);
            assert.equal(result.error, null);
            assert.equal(requests.length, 20);
            assert.match(requests[0].url, /title=Signed%20agreement%20v1.2$/);
            for (let index = 0; index < 20; index++) {
                assert.equal(requests[index].options.headers.RequestVerificationToken, 'test-token');
                assert.deepEqual(await requests[index].options.body.get('file').arrayBuffer(), await files[index].arrayBuffer());
            }
        } finally { fx.dispose(); }
    });
}

test('save immediately after 20 pastes does not wait for blocked Blazor callbacks', async () => {
    const fx = fixture(() => new Promise(() => {})), requests = successfulUploads();
    try {
        for (let index = 0; index < 20; index++) fx.paste([file(index)]);
        const result = await evidence.archive(fx.host, 'invoice', entityId);
        assert.equal(result.archivedCount, 20);
        assert.equal(requests.length, 20);
    } finally { fx.dispose(); }
});

test('partial failure retains unfinished files and retry does not resend successful files', async () => {
    const fx = fixture();
    let attempted = 0;
    const uploaded = [];
    globalThis.fetch = async (url, options) => {
        attempted++;
        if (attempted === 8) return { ok: false, status: 503, text: async () => 'Temporary failure' };
        uploaded.push(options.body.get('file').name);
        return { ok: true };
    };
    try {
        fx.disk(Array.from({ length: 20 }, (_, index) => file(index)));
        const first = await evidence.archive(fx.host, 'contract-change', entityId);
        assert.equal(first.archivedCount, 7);
        assert.equal(first.documents.length, 13);
        assert.match(first.error, /Temporary failure.*save again/);
        const retry = await evidence.archive(fx.host, 'contract-change', entityId);
        assert.equal(retry.archivedCount, 13);
        assert.equal(retry.error, null);
        assert.equal(uploaded.length, 20);
        assert.equal(new Set(uploaded).size, 20);
    } finally { fx.dispose(); }
});

test('duplicates stay visible; remove allows the same image to be added again', async () => {
    const fx = fixture();
    try {
        const image = file(1);
        fx.disk([image]); fx.paste([image]);
        let state = await evidence.prepare(fx.host);
        assert.equal(state.documents.length, 1);
        assert.match(state.error, /already ready/);
        evidence.remove(fx.host, state.documents[0].id);
        assert.equal(evidence.getState(fx.host).documents.length, 0);
        fx.paste([image]);
        state = await evidence.prepare(fx.host);
        assert.equal(state.documents.length, 1);
        assert.equal(state.error, null);
    } finally { fx.dispose(); }
});

test('empty and oversized files are rejected without losing the other 20 files', async () => {
    const fx = fixture();
    try {
        fx.disk([file(30, 'image/png', 0), file(31, 'image/png', 15 * 1024 * 1024 + 1), ...Array.from({ length: 20 }, (_, i) => file(i))]);
        const state = await evidence.prepare(fx.host);
        assert.equal(state.documents.length, 20);
        assert.match(state.error, /empty/);
        assert.match(state.error, /15 MB/);
    } finally { fx.dispose(); }
});

test('15 MB image is accepted without sending its bytes over interop', async () => {
    const fx = fixture();
    try {
        fx.paste([file(1, 'image/png', 15 * 1024 * 1024)]);
        const state = await evidence.prepare(fx.host);
        assert.equal(state.documents.length, 1);
        assert.ok(JSON.stringify(state).length < 1000);
    } finally { fx.dispose(); }
});

test('paste belongs to the active form and leaves unrelated text editing alone', async () => {
    const first = fixture(), second = fixture();
    try {
        second.paste([file(1)]);
        assert.equal((await evidence.prepare(first.host)).documents.length, 0);
        assert.equal((await evidence.prepare(second.host)).documents.length, 1);
        let prevented = false;
        document.fire('paste', { target: new TestElement(null, true), clipboardData: { files: [file(2)] }, preventDefault() { prevented = true; } });
        assert.equal(prevented, false);
    } finally { first.dispose(); second.dispose(); }
});

test('callback failure produces a visible warning instead of an invisible pending file', async () => {
    const fx = fixture(async () => { throw new Error('Disconnected'); });
    try {
        fx.paste([file(1)]);
        await evidence.prepare(fx.host);
        await Promise.resolve();
        assert.equal(fx.error.hidden, false);
        assert.match(fx.error.textContent, /document list could not be updated/);
    } finally { fx.dispose(); }
});

test('invalid titles fail before any upload and remain editable', async () => {
    const fx = fixture(), requests = successfulUploads();
    try {
        fx.disk([file(1)]);
        const state = await evidence.prepare(fx.host);
        evidence.rename(fx.host, state.documents[0].id, '../escape');
        const result = await evidence.archive(fx.host, 'contract', entityId);
        assert.equal(result.documents.length, 1);
        assert.match(result.error, /valid document title/);
        assert.equal(requests.length, 0);
        assert.equal(fx.fileInput.disabled, false);
    } finally { fx.dispose(); }
});

test('disposing while preparation is running releases the form without stale listeners', async () => {
    const fx = fixture();
    fx.disk(Array.from({ length: 20 }, (_, i) => file(i)));
    const preparation = evidence.prepare(fx.host);
    fx.dispose();
    await assert.rejects(preparation, /not ready/);
    assert.throws(() => evidence.getState(fx.host), /not ready/);
});

test('upload timeout keeps the pending file available for retry', async () => {
    const fx = fixture(), originalTimeout = globalThis.setTimeout;
    globalThis.setTimeout = (callback, delay) => originalTimeout(callback, delay === 60000 ? 1 : delay);
    globalThis.fetch = async (_url, options) => new Promise((_resolve, reject) => options.signal.addEventListener('abort', () => reject(new DOMException('Timed out', 'AbortError'))));
    try {
        fx.disk([file(1)]);
        const result = await evidence.archive(fx.host, 'contract', entityId);
        assert.equal(result.documents.length, 1);
        assert.match(result.error, /timed out/);
        assert.equal(result.saving, false);
        successfulUploads();
        assert.equal((await evidence.archive(fx.host, 'contract', entityId)).archivedCount, 1);
    } finally { globalThis.setTimeout = originalTimeout; fx.dispose(); }
});

test('remove, archive and disposal release every browser-local preview', async () => {
    const fx = fixture(), revoke = URL.revokeObjectURL;
    const released = [];
    URL.revokeObjectURL = url => { released.push(url); revoke(url); };
    successfulUploads();
    try {
        fx.disk(Array.from({ length: 20 }, (_, i) => file(i)));
        const state = await evidence.prepare(fx.host);
        evidence.remove(fx.host, state.documents[0].id);
        assert.equal(released.length, 1);
        await evidence.archive(fx.host, 'contract', entityId);
        assert.equal(released.length, 20);
        fx.paste([file(1)]);
        await evidence.prepare(fx.host);
        fx.dispose();
        assert.equal(released.length, 21);
        assert.equal(new Set(released).size, 21);
    } finally { fx.dispose(); URL.revokeObjectURL = revoke; }
});
