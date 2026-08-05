const handlers = new WeakMap();

export function attach(host, dotNetReference) {
    const onPaste = async event => {
        const image = [...event.clipboardData?.items ?? []].find(item => item.type.startsWith('image/'));
        if (!image) return;

        const file = image.getAsFile();
        if (!file) return;

        event.preventDefault();
        await addFiles(host, [file], 'clipboard-image');
    };

    const fileInput = host.querySelector('input[type="file"]');
    const onFileChange = async event => { await addFiles(host, event.target.files, 'document'); event.target.value = ''; };
    const dropZone = host.querySelector('.clipboard-document-dropzone');
    const onDragOver = event => { event.preventDefault(); dropZone.classList.add('is-dragging'); };
    const onDragLeave = () => dropZone.classList.remove('is-dragging');
    const onDrop = async event => { event.preventDefault(); dropZone.classList.remove('is-dragging'); await addFiles(host, event.dataTransfer.files, 'document'); };
    host.addEventListener('paste', onPaste);
    fileInput.addEventListener('change', onFileChange);
    dropZone.addEventListener('dragover', onDragOver);
    dropZone.addEventListener('dragleave', onDragLeave);
    dropZone.addEventListener('drop', onDrop);
    handlers.set(host, { onPaste, onFileChange, fileInput, dropZone, onDragOver, onDragLeave, onDrop, dotNetReference, documents: new Map() });
}

export async function archive(host, entityType, entityId, titles) {
    const state = handlers.get(host);
    if (!state?.documents?.size) return 0;

    for (const document of state.documents.values()) {
        const title = titles.find(item => item.id === document.id)?.title ?? document.title;
        const body = new FormData();
        body.append('file', document.file, document.name);
        const response = await fetch(`/evidence/clipboard/${entityType}/${entityId}?title=${encodeURIComponent(title)}`, { method: 'POST', body });
        if (!response.ok) throw new Error('A pasted image could not be archived.');
    }
    const archivedCount = state.documents.size;
    state.documents.clear();
    return archivedCount;
}

export function remove(host, id) { handlers.get(host)?.documents.delete(id); }

function readAsDataUrl(file) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result);
        reader.onerror = reject;
        reader.readAsDataURL(file);
    });
}

async function addFiles(host, files, namePrefix) {
    const state = handlers.get(host);
    for (const file of files) {
        const id = crypto.randomUUID();
        const extension = file.type === 'image/jpeg' ? 'jpg' : file.type === 'image/gif' ? 'gif' : 'png';
        const name = namePrefix === 'clipboard-image' ? `${namePrefix}-${new Date().toISOString().replace(/[:.]/g, '-')}Z.${extension}` : file.name;
        const previewDataUrl = file.type.startsWith('image/') ? await readAsDataUrl(file) : null;
        state.documents.set(id, { file, name, title: name.replace(/\.[^.]+$/, '') });
        await state.dotNetReference.invokeMethodAsync('DocumentAdded', id, name, file.type || 'application/octet-stream', file.size, previewDataUrl);
    }
}

export function dispose(host) {
    const state = handlers.get(host);
    if (state?.onPaste) host.removeEventListener('paste', state.onPaste);
    if (state?.dropZone) {
        state.dropZone.removeEventListener('dragover', state.onDragOver);
        state.dropZone.removeEventListener('dragleave', state.onDragLeave);
        state.dropZone.removeEventListener('drop', state.onDrop);
    }
    handlers.delete(host);
}
