export function insertPlacement(editor, token, block) {
    if (!editor) return;

    const start = editor.selectionStart ?? editor.value.length;
    const end = editor.selectionEnd ?? start;
    let insertion = token;

    if (block) {
        const before = editor.value.slice(0, start);
        const after = editor.value.slice(end);
        if (before.length && !before.endsWith("\n\n")) {
            insertion = (before.endsWith("\n") ? "\n" : "\n\n") + insertion;
        }
        if (after.length && !after.startsWith("\n\n")) {
            insertion += after.startsWith("\n") ? "\n" : "\n\n";
        }
    }

    editor.setRangeText(insertion, start, end, "end");
    editor.dispatchEvent(new Event("input", { bubbles: true }));
    editor.focus();
}
