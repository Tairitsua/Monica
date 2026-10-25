const visits = new WeakMap();
function live(root, visit) { return root?.isConnected && root.dataset.visit === visit && visits.get(root)?.visit === visit; }
function measure(root, visit) {
    if (!live(root, visit)) return;
    const shell = root.closest(".mo-body-content");
    if (!shell) return;
    root.dataset.compact = String(root.clientWidth < 760);
    const panel = root.querySelector(".workspace-master-detail,.workspace-state");
    if (panel) {
        const note = root.querySelector(".results-note");
        const noteHeight = note ? note.getBoundingClientRect().height + parseFloat(getComputedStyle(note).marginTop) : 0;
        const bottomPadding = parseFloat(getComputedStyle(root.parentElement).paddingBottom) || 0;
        const available = shell.getBoundingClientRect().bottom - panel.getBoundingClientRect().top - bottomPadding - noteHeight;
        root.style.setProperty("--state-panel-height", Math.max(root.dataset.compact === "true" ? 220 : 320, available) + "px");
    }
}
export function sync(root, visit) {
    if (!live(root, visit)) return;
    const all = root.querySelector(".select-all");
    if (all) all.indeterminate = all.dataset.mixed === "true";
    measure(root, visit);
}
export function observe(root, visit) {
    if (visits.has(root)) return;
    const observer = new ResizeObserver(() => measure(root, visit));
    const resize = () => measure(root, visit);
    let drag = null;
    const setWidth = value => {
        const width = Math.max(30, Math.min(65, value));
        root.style.setProperty("--state-list-width", width + "%");
        root.querySelector(".pane-resizer")?.setAttribute("aria-valuenow", String(Math.round(width)));
    };
    const down = event => {
        const handle = event.target.closest(".pane-resizer");
        if (!handle) return;
        event.preventDefault();
        handle.setPointerCapture(event.pointerId);
        drag = { handle, id: event.pointerId };
    };
    const move = event => {
        if (!drag || event.pointerId !== drag.id) return;
        const rect = root.querySelector(".workspace-master-detail").getBoundingClientRect();
        setWidth((event.clientX - rect.left) / rect.width * 100);
    };
    const up = () => { drag = null; };
    const keydown = event => {
        if (!event.target.matches(".pane-resizer") || !["ArrowLeft", "ArrowRight", "Home"].includes(event.key)) return;
        event.preventDefault();
        const current = Number(event.target.getAttribute("aria-valuenow")) || 43;
        setWidth(event.key === "Home" ? 43 : current + (event.key === "ArrowLeft" ? -2 : 2));
    };
    visits.set(root, { visit, observer, resize, down, move, up, keydown });
    observer.observe(root);
    const shell = root.closest(".mo-body-content");
    if (shell) observer.observe(shell);
    window.addEventListener("resize", resize);
    root.addEventListener("pointerdown", down);
    root.addEventListener("pointermove", move);
    root.addEventListener("pointerup", up);
    root.addEventListener("pointercancel", up);
    root.addEventListener("keydown", keydown);
    measure(root, visit);
}
function focusReveal(root, target, revealed = target, takeFocus = true) {
    const shell = root.closest(".mo-body-content");
    if (!shell || !target) return;
    if (takeFocus) target.focus({ preventScroll: true });
    const bounds = shell.getBoundingClientRect(), band = revealed.getBoundingClientRect();
    const appBar = document.querySelector(".mud-appbar")?.getBoundingClientRect();
    const usableTop = Math.max(bounds.top, appBar?.bottom ?? bounds.top) + 12;
    if (band.top < usableTop || band.bottom > bounds.bottom - 12)
        shell.scrollTo({ top: shell.scrollTop + band.top - usableTop, behavior: "auto" });
}
export function revealProvider(root, identity) {
    if (!root?.isConnected || root.dataset.provider !== identity) return;
    const heading = root.querySelector(".selected-provider .identity");
    const style = getComputedStyle(root);
    const width = root.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
    focusReveal(root, heading?.querySelector("h2"), heading, width < 760);
}
function matches(root, visit, provider, result, selection, focus, key) {
    return live(root, visit) && root.dataset.focusVersion === String(focus) && root.dataset.providerGeneration === String(provider)
        && root.dataset.resultGeneration === String(result) && root.dataset.selectionGeneration === String(selection) && root.dataset.selectedKey === key;
}
export function reveal(root, visit, provider, result, selection, focus, key) {
    if (!matches(root, visit, provider, result, selection, focus, key)) return;
    measure(root, visit);
    if (root.dataset.compact !== "true" || root.dataset.detailOpen !== "true") return;
    const heading = root.querySelector(".inspector-heading");
    focusReveal(root, heading?.querySelector("h2"), heading);
}
export function back(root, visit, provider, result, selection, focus, key) {
    if (!matches(root, visit, provider, result, selection, focus, key)) return;
    measure(root, visit);
    if (root.dataset.compact !== "true" || root.dataset.detailOpen === "true") return;
    const selected = [...root.querySelectorAll(".result-select")].find(item => item.dataset.key === key);
    if (selected) {
        const group = selected.closest("details");
        if (group) group.open = true;
        const list = selected.closest(".results-list"), bounds = list?.getBoundingClientRect(), row = selected.getBoundingClientRect();
        if (list && (row.top < bounds.top || row.bottom > bounds.bottom)) list.scrollTop += row.top - bounds.top;
    }
    focusReveal(root, selected ?? root.querySelector(".inventory-heading"));
}
export async function copy(text) {
    try { await navigator.clipboard.writeText(text); return true; }
    catch { return false; }
}
export function stop(root, visit) {
    const state = visits.get(root);
    if (!state || state.visit !== visit) return;
    state.observer.disconnect();
    window.removeEventListener("resize", state.resize);
    root.removeEventListener("pointerdown", state.down);
    root.removeEventListener("pointermove", state.move);
    root.removeEventListener("pointerup", state.up);
    root.removeEventListener("pointercancel", state.up);
    root.removeEventListener("keydown", state.keydown);
    visits.delete(root);
}
