const handlers = new WeakMap();
const maxFileSizeBytes = 15 * 1024 * 1024;

export function attach(host, dotNetReference) {
    dispose(host);

    const state = { dotNetReference, documents: new Map(), queue: Promise.resolve(), disposed: false };
    const onPaste = event => {
        if (isTextEditingTarget(event.target) && !host.contains(event.target)) return;

        const image = [...event.clipboardData?.items ?? []].find(item => item.type.startsWith('image/'));
        if (!image) return;

        const file = image.getAsFile();
        if (!file) return;

        event.preventDefault();
        void enqueue(state, () => addFiles(state, [file], 'clipboard-image'));
    };

    const fileInput = host.querySelector('input[type="file"]');
    const onFileChange = event => {
        const files = [...event.target.files];
        event.target.value = '';
        void enqueue(state, () => addFiles(state, files, 'document'));
    };
    const dropZone = host.querySelector('.clipboard-document-dropzone');
    const onDragOver = event => { event.preventDefault(); dropZone.classList.add('is-dragging'); };
    const onDragLeave = () => dropZone.classList.remove('is-dragging');
    const onDrop = event => {
        event.preventDefault();
        dropZone.classList.remove('is-dragging');
        void enqueue(state, () => addFiles(state, [...event.dataTransfer.files], 'document'));
    };
    Object.assign(state, { onPaste, onFileChange, fileInput, dropZone, onDragOver, onDragLeave, onDrop });
    handlers.set(host, state);
    document.addEventListener('paste', onPaste);
    fileInput.addEventListener('change', onFileChange);
    dropZone.addEventListener('dragover', onDragOver);
    dropZone.addEventListener('dragleave', onDragLeave);
    dropZone.addEventListener('drop', onDrop);
}

export async function archive(host, entityType, entityId) {
    const state = handlers.get(host);
    if (!state) return 0;
    await state.queue;
    if (!state.documents.size) return 0;

    for (const document of state.documents.values()) {
        const body = new FormData();
        body.append('file', document.file, document.name);
        const antiforgeryToken = host.querySelector('input[name="__RequestVerificationToken"]')?.value;
        const response = await fetch(`/evidence/clipboard/${entityType}/${entityId}?title=${encodeURIComponent(document.title)}`, {
            method: 'POST',
            body,
            headers: antiforgeryToken ? { RequestVerificationToken: antiforgeryToken } : {}
        });
        if (!response.ok) {
            const reason = await response.text();
            throw new Error(reason || `The server rejected ${document.name}.`);
        }
    }
    const archivedCount = state.documents.size;
    state.documents.clear();
    return archivedCount;
}

export function remove(host, id) { handlers.get(host)?.documents.delete(id); }

export function rename(host, id, title) {
    const document = handlers.get(host)?.documents.get(id);
    if (document) document.title = title;
}

function readAsDataUrl(file) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result);
        reader.onerror = reject;
        reader.readAsDataURL(file);
    });
}

async function addFiles(state, files, namePrefix) {
    for (const file of files) {
        try {
            if (file.size === 0) throw new Error(`${file.name || 'This file'} is empty.`);
            if (file.size > maxFileSizeBytes) throw new Error(`${file.name} is larger than the 15 MB limit.`);

            const fingerprint = await fingerprintFile(file);
            if ([...state.documents.values()].some(document => document.fingerprint === fingerprint)) {
                await state.dotNetReference.invokeMethodAsync('DocumentIntakeFailed', `${file.name || 'This file'} is already ready to save.`);
                continue;
            }

            const id = crypto.randomUUID();
            const extension = file.type === 'image/jpeg' ? 'jpg' : file.type === 'image/gif' ? 'gif' : 'png';
            const name = namePrefix === 'clipboard-image' ? `${namePrefix}-${fingerprint.slice(0, 12)}.${extension}` : file.name;
            const previewDataUrl = file.type.startsWith('image/') ? await readAsDataUrl(file) : null;
            state.documents.set(id, { file, name, title: name.replace(/\.[^.]+$/, ''), fingerprint });
            await state.dotNetReference.invokeMethodAsync('DocumentAdded', id, name, file.type || 'application/octet-stream', file.size, previewDataUrl);
        }
        catch (error) {
            await state.dotNetReference.invokeMethodAsync('DocumentIntakeFailed', error instanceof Error ? `Could not add the file: ${error.message}` : 'Could not add the selected file.');
        }
    }
}

function enqueue(state, operation) {
    state.queue = state.queue.then(
        () => state.disposed ? undefined : operation(),
        () => state.disposed ? undefined : operation());
    return state.queue;
}

async function fingerprintFile(file) {
    const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer());
    return [...new Uint8Array(digest)].map(value => value.toString(16).padStart(2, '0')).join('');
}

export function dispose(host) {
    const state = handlers.get(host);
    if (state) state.disposed = true;
    if (state?.onPaste) document.removeEventListener('paste', state.onPaste);
    if (state?.fileInput) state.fileInput.removeEventListener('change', state.onFileChange);
    if (state?.dropZone) {
        state.dropZone.removeEventListener('dragover', state.onDragOver);
        state.dropZone.removeEventListener('dragleave', state.onDragLeave);
        state.dropZone.removeEventListener('drop', state.onDrop);
    }
    handlers.delete(host);
}

function isTextEditingTarget(target) {
    return target instanceof Element && Boolean(target.closest('input:not([type="file"]), textarea, [contenteditable="true"]'));
}
