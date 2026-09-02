const handlers = new WeakMap();
const hosts = new Set();
const maxFileSizeBytes = 15 * 1024 * 1024;
let activeHost;

export function attach(host, dotNetReference) {
    dispose(host);
    const state = { host, dotNetReference, documents: new Map(), queue: Promise.resolve(), pendingOperations: 0, disposed: false, saving: false, error: null, progress: null, revision: 0 };
    const activate = () => { activeHost = host; };
    const onPaste = event => {
        const owner = [...hosts].find(candidate => candidate.contains(event.target)) ?? activeHost ?? (hosts.size === 1 ? [...hosts][0] : null);
        if (owner !== host || (isTextEditingTarget(event.target) && !host.contains(event.target))) return;
        const transfer = event.clipboardData;
        // files and items usually describe the same files. Prefer files, but support item-only clipboards too.
        const files = transfer?.files?.length
            ? [...transfer.files]
            : [...transfer?.items ?? []].filter(item => item.kind === 'file' || item.type?.startsWith('image/')).map(item => item.getAsFile()).filter(Boolean);
        if (!files.length) return;
        event.preventDefault();
        intake(state, files, true);
    };
    const fileInput = host.querySelector('input[type="file"]');
    const dropZone = host.querySelector('.clipboard-document-dropzone');
    const onFileChange = event => {
        const files = [...event.target.files];
        event.target.value = '';
        intake(state, files, false);
    };
    const onDragOver = event => { event.preventDefault(); dropZone.classList.add('is-dragging'); };
    const onDragLeave = () => dropZone.classList.remove('is-dragging');
    const onDrop = event => {
        event.preventDefault();
        dropZone.classList.remove('is-dragging');
        intake(state, [...event.dataTransfer.files], false);
    };
    Object.assign(state, { onPaste, activate, onFileChange, fileInput, dropZone, onDragOver, onDragLeave, onDrop });
    handlers.set(host, state);
    hosts.add(host);
    document.addEventListener('paste', onPaste);
    host.addEventListener('pointerdown', activate);
    host.addEventListener('focusin', activate);
    fileInput.addEventListener('change', onFileChange);
    dropZone.addEventListener('dragover', onDragOver);
    dropZone.addEventListener('dragleave', onDragLeave);
    dropZone.addEventListener('drop', onDrop);
}

export function getState(host) {
    const state = requireState(host);
    return {
        revision: state.revision,
        documents: [...state.documents].map(([id, item]) => ({ id, fileName: item.name, title: item.title, contentType: item.file.type, fileSizeBytes: item.file.size, previewUrl: item.previewUrl })),
        busy: state.pendingOperations !== 0,
        saving: state.saving,
        error: state.error,
        progress: state.progress,
    };
}

export async function prepare(host) {
    const state = requireState(host);
    await state.queue;
    return getState(host);
}

export async function archive(host, entityType, entityId) {
    const state = requireState(host);
    if (state.saving) throw new Error('Documents are already being saved.');
    state.saving = true;
    state.fileInput.disabled = true;
    state.error = null;
    let archivedCount = 0;
    try {
        // This queue contains browser work only: never await a Blazor callback from it.
        await state.queue;
        state.error = null;
        const total = state.documents.size;
        for (const item of state.documents.values()) {
            if (!item.title.trim() || item.title.length > 200 || /[<>:"/\\|?*\x00-\x1f]/.test(item.title) || /^\.{1,2}$/.test(item.title.trim()))
                throw new Error(`Enter a valid document title for ${item.name}.`);
        }
        for (const [id, item] of state.documents) {
            if (state.disposed) throw new Error('The document form was closed before saving finished.');
            state.progress = `Saving document ${archivedCount + 1} of ${total}…`;
            notify(state);
            const body = new FormData();
            body.append('file', item.file, item.name);
            const token = host.querySelector('input[name="__RequestVerificationToken"]')?.value;
            const controller = new AbortController();
            state.controller = controller;
            const timeout = setTimeout(() => controller.abort(), 60000);
            try {
                const response = await fetch(`/evidence/clipboard/${entityType}/${entityId}?title=${encodeURIComponent(item.title)}`, {
                    method: 'POST', body, signal: controller.signal,
                    headers: token ? { RequestVerificationToken: token } : {}
                });
                if (!response.ok) {
                    const reason = await response.text();
                    throw new Error(reason && !reason.trimStart().startsWith('<') ? reason : `The server rejected ${item.name} (HTTP ${response.status}).`);
                }
            } finally {
                clearTimeout(timeout);
                state.controller = null;
            }
            releasePreview(item);
            state.documents.delete(id);
            archivedCount++;
            notify(state);
        }
        state.progress = archivedCount ? `${archivedCount} document${archivedCount === 1 ? '' : 's'} saved.` : null;
    } catch (error) {
        state.error = error?.name === 'AbortError'
            ? 'The upload timed out. Check the connection and save again; unfinished documents have been kept.'
            : `${error instanceof Error ? error.message : 'Documents could not be saved.'} Unfinished documents have been kept; save again to retry.`;
    } finally {
        state.saving = false;
        state.fileInput.disabled = false;
        notify(state);
    }
    return { ...getState(host), archivedCount };
}

export function remove(host, id) {
    const state = requireState(host);
    if (state.saving) return;
    const item = state.documents.get(id);
    if (item) releasePreview(item);
    state.documents.delete(id);
    state.error = null;
    notify(state);
}

export function rename(host, id, title) {
    const state = requireState(host);
    if (state.saving) return;
    const item = state.documents.get(id);
    if (item) { item.title = title; state.revision++; }
}

function intake(state, files, fromClipboard) {
    if (state.saving) {
        return;
    }
    state.pendingOperations++;
    state.queue = state.queue.then(async () => {
        if (state.disposed) return;
        state.error = null;
        state.progress = null;
        const errors = [];
        for (const file of files) {
            try {
                if (file.size === 0) throw new Error(`${file.name || 'This file'} is empty.`);
                if (file.size > maxFileSizeBytes) throw new Error(`${file.name || 'This file'} is larger than the 15 MB limit.`);
                const fingerprint = await fingerprintFile(file);
                if (state.disposed) return;
                if ([...state.documents.values()].some(item => item.fingerprint === fingerprint)) {
                    throw new Error(`${file.name || 'This file'} is already ready to save.`);
                }
                const id = crypto.randomUUID();
                const extension = file.name?.match(/\.[^.]+$/)?.[0] || ({ 'image/jpeg': '.jpg', 'image/gif': '.gif', 'image/webp': '.webp' }[file.type] ?? '.png');
                const name = fromClipboard && file.type.startsWith('image/') ? `clipboard-image-${fingerprint.slice(0, 12)}${extension}` : file.name;
                // Only this short local URL crosses SignalR. Image bytes remain in the browser until HTTP upload.
                const previewUrl = file.type.startsWith('image/') ? URL.createObjectURL(file) : null;
                state.documents.set(id, { file, name, title: name.replace(/\.[^.]+$/, ''), fingerprint, previewUrl });
            } catch (error) { errors.push(error instanceof Error ? error.message : 'Could not add the selected file.'); }
        }
        state.error = errors.length ? errors.join(' ') : null;
    }).finally(() => {
        state.pendingOperations--;
        notify(state);
    });
    notify(state);
}

function notify(state) {
    if (state.disposed) return;
    state.revision++;
    // Notifications never gate preparation or upload, preventing cross-runtime circular waits.
    state.dotNetReference.invokeMethodAsync('DocumentsChanged').catch(() => {
        if (state.disposed) return;
        const error = state.host.querySelector('[data-clipboard-client-error]');
        if (error) {
            error.textContent = 'The document list could not be updated. Your connection may have been lost. Reload before trying again.';
            error.hidden = false;
        }
    });
}

function requireState(host) {
    const state = handlers.get(host);
    if (!state || state.disposed) throw new Error('The document form is not ready. Reload the page and try again.');
    return state;
}

function releasePreview(item) { if (item.previewUrl) URL.revokeObjectURL(item.previewUrl); }

async function fingerprintFile(file) {
    const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer());
    return [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, '0')).join('');
}

export function dispose(host) {
    const state = handlers.get(host);
    if (!state) return;
    state.disposed = true;
    state.controller?.abort();
    for (const item of state.documents.values()) releasePreview(item);
    state.documents.clear();
    document.removeEventListener('paste', state.onPaste);
    host.removeEventListener('pointerdown', state.activate);
    host.removeEventListener('focusin', state.activate);
    state.fileInput.removeEventListener('change', state.onFileChange);
    state.dropZone.removeEventListener('dragover', state.onDragOver);
    state.dropZone.removeEventListener('dragleave', state.onDragLeave);
    state.dropZone.removeEventListener('drop', state.onDrop);
    handlers.delete(host);
    hosts.delete(host);
    if (activeHost === host) activeHost = null;
}

function isTextEditingTarget(target) {
    return target instanceof Element && Boolean(target.closest('input:not([type="file"]), textarea, [contenteditable="true"]'));
}
