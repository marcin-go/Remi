import assert from 'node:assert/strict';
import { randomUUID, webcrypto } from 'node:crypto';
import { readFile } from 'node:fs/promises';

Object.defineProperty(globalThis, 'crypto', {
    configurable: true,
    value: { randomUUID, subtle: webcrypto.subtle },
});

class TestElement {
    closest() { return null; }
}

globalThis.Element = TestElement;
globalThis.FileReader = class {
    readAsDataURL(file) {
        file.arrayBuffer().then(bytes => {
            this.result = `data:${file.type};base64,${Buffer.from(bytes).toString('base64')}`;
            this.onload?.();
        }, error => this.onerror?.(error));
    }
};

function eventTarget() {
    const listeners = new Map();
    return {
        classList: { add() { }, remove() { } },
        addEventListener(type, handler) { listeners.set(type, handler); },
        removeEventListener(type) { listeners.delete(type); },
        fire(type, event) { return listeners.get(type)?.(event); },
    };
}

const documentTarget = eventTarget();
globalThis.document = documentTarget;

const fileInput = eventTarget();
const dropZone = eventTarget();
const antiforgeryToken = { value: 'test-token' };
const host = {
    contains() { return true; },
    querySelector(selector) {
        if (selector === 'input[type="file"]') return fileInput;
        if (selector === '.clipboard-document-dropzone') return dropZone;
        if (selector === 'input[name="__RequestVerificationToken"]') return antiforgeryToken;
        return null;
    },
};

const added = [];
const dotNetReference = {
    async invokeMethodAsync(method, ...args) {
        if (method === 'DocumentAdded') {
            await new Promise(resolve => setTimeout(resolve, 10));
            added.push(args);
        }
    },
};

const source = await readFile(new URL('../Remi.Web/wwwroot/clipboard-image-evidence.js', import.meta.url), 'utf8');
const evidence = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
evidence.attach(host, dotNetReference);

function pastedFile(content) {
    const file = new Blob([content], { type: 'image/png' });
    Object.defineProperty(file, 'name', { value: 'image.png' });
    return file;
}

function paste(file) {
    documentTarget.fire('paste', {
        target: new TestElement(),
        clipboardData: { items: [{ type: file.type, getAsFile: () => file }] },
        preventDefault() { },
    });
}

paste(pastedFile('first image'));
paste(pastedFile('second image'));

for (let attempt = 0; attempt < 50 && added.length < 2; attempt++) {
    await new Promise(resolve => setTimeout(resolve, 10));
}

assert.equal(added.length, 2, 'Both rapid paste events must reach the pending-document list.');
assert.notEqual(added[0][0], added[1][0], 'Each pending document must keep its own identity.');

evidence.rename(host, added[0][0], 'Signed agreement');
const requests = [];
globalThis.fetch = async (url, options) => {
    requests.push({ url, options });
    return { ok: true };
};

const archived = await evidence.archive(host, 'contract', '11111111-1111-1111-1111-111111111111');
assert.equal(archived, 2, 'Both queued documents must be archived.');
assert.match(requests[0].url, /title=Signed%20agreement$/, 'The edited title must be sent for the renamed document.');
assert.match(requests[1].url, /title=clipboard-image-[a-f0-9]{12}$/, 'An unedited document keeps its generated title.');

evidence.dispose(host);
