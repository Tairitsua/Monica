// This enhancement owns only scroll synchronization; the native textarea owns input,
// selection, undo, IME and keyboard behavior. It never calls back into .NET.
export function connect(root) {
    const synchronize = () => {
        const input = root.querySelector('.json-input');
        const source = input ?? root.querySelector('.json-readout');
        if (!source) return;
        const gutter = root.querySelector('.json-gutter');
        const highlight = root.querySelector('.json-highlight');
        if (gutter) gutter.scrollTop = source.scrollTop;
        if (highlight && input) {
            highlight.scrollTop = input.scrollTop;
            highlight.scrollLeft = input.scrollLeft;
            // Keep a native draft visible while a server render or IME update is in flight.
            // The highlight layer becomes visible again only when it describes that draft.
            root.dataset.editorPending = highlight.textContent !== `${input.value}\n` ? 'true' : 'false';
        }
    };
    root.addEventListener('scroll', synchronize, true);
    root.addEventListener('input', synchronize, true);
    const observer = new MutationObserver(synchronize);
    observer.observe(root, { childList: true, subtree: true, characterData: true });
    root.dataset.editorReady = 'true';
    synchronize();
    return {
        dispose() {
            root.removeEventListener('scroll', synchronize, true);
            root.removeEventListener('input', synchronize, true);
            observer.disconnect();
        }
    };
}
