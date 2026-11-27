const editors = new WeakMap();

export function sync(textarea, gutter, inputSignal) {
    if (!textarea?.isConnected || !gutter?.isConnected) return;
    let editor = editors.get(textarea);
    if (!editor) {
        const numbers = gutter.firstElementChild;
        editor = { lines: 1, text: null, timer: 0 };

        editor.paint = () => {
            if (editor.text !== textarea.value) {
                editor.text = textarea.value;
                editor.lines = Math.max(12, textarea.value.split("\n").length);
            }
            const lineHeight = parseFloat(getComputedStyle(textarea).lineHeight);
            const first = Math.floor(textarea.scrollTop / lineHeight);
            const count = Math.min(editor.lines - first, Math.ceil(textarea.clientHeight / lineHeight) + 2);
            numbers.textContent = Array.from({ length: count }, (_, index) => first + index + 1).join("\n");
            numbers.style.transform = "translateY(" + (first * lineHeight - textarea.scrollTop) + "px)";
        };

        // The browser owns text editing and streams the draft explicitly. Bubbling a key event
        // into a Blazor ancestor would send the complete textarea value as event field data.
        textarea.addEventListener("keydown", event => event.stopPropagation());
        textarea.addEventListener("keyup", event => event.stopPropagation());
        textarea.addEventListener("scroll", editor.paint, { passive: true });
        textarea.addEventListener("input", () => {
            editor.paint();
            clearTimeout(editor.timer);
            editor.timer = setTimeout(() => {
                if (inputSignal.isConnected) inputSignal.click();
            }, 140);
        });
        editors.set(textarea, editor);
    }
    editor.paint();
}

export function read(textarea) {
    return new TextEncoder().encode(textarea?.value ?? "");
}

export function selectMatch(textarea, start, length) {
    if (!textarea?.isConnected) return;
    const line = textarea.value.slice(0, start).split("\n").length - 1;
    const lineHeight = parseFloat(getComputedStyle(textarea).lineHeight);
    textarea.focus({ preventScroll: true });
    textarea.setSelectionRange(start, start + length);
    textarea.scrollTop = Math.max(0, line * lineHeight - 80);
    editors.get(textarea)?.paint();
}
