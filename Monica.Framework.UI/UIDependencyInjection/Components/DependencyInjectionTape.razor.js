const tapes = new WeakMap();
const evidenceOpeners = new WeakMap();

export function rememberEvidenceOpener(root) {
    const opener = document.activeElement;
    if (root?.contains(opener)) evidenceOpeners.set(root, opener);
}

export function restoreEvidenceOpener(root) {
    if (!root?.isConnected) return;
    const opener = evidenceOpeners.get(root);
    evidenceOpeners.delete(root);
    const restore = () => {
        // Native dismissal may have restored focus before the observer runs.
        // Preserve any focus the user has already moved to another control.
        if (root.isConnected && opener?.isConnected && document.activeElement === document.body) {
            opener.focus({ preventScroll: true });
        }
    };
    const sheet = document.querySelector(".mo-side-sheet .di-sheet-title")?.closest(".mo-side-sheet");
    if (!sheet) {
        requestAnimationFrame(restore);
        return;
    }
    const observer = new MutationObserver(() => {
        if (!sheet.isConnected || !root.isConnected) {
            observer.disconnect();
            requestAnimationFrame(restore);
        }
    });
    observer.observe(sheet.parentElement.closest(".mud-dialog-container")?.parentElement ?? document.body, { childList: true, subtree: true });
    setTimeout(() => observer.disconnect(), 1500);
}

export async function focusEvidenceHeading(heading) {
    const sheet = heading?.closest(".mo-side-sheet");
    if (!sheet) return;
    await new Promise(resolve => requestAnimationFrame(resolve));
    await Promise.allSettled(sheet.getAnimations().map(animation => animation.finished));
    if (heading.isConnected) heading.focus({ preventScroll: true });
}

export function bindTape(root, dotnet) {
    if (!root?.isConnected) return;

    const controller = new AbortController();
    const options = { signal: controller.signal };
    let drag;
    let last;
    const emit = (hover, from = -1, to = -1, open = false) => {
        const key = [hover, from, to].join(":");
        if (controller.signal.aborted || (!open && key === last)) return;
        last = key;
        dotnet.invokeMethodAsync("ReadPointer", hover, from, to, open).catch(() => unbindTape(root));
    };

    const resize = new ResizeObserver(([entry]) => {
        if (!controller.signal.aborted) {
            dotnet.invokeMethodAsync("SetWidth", Math.round(entry.contentRect.width)).catch(() => unbindTape(root));
        }
    });
    resize.observe(root);
    tapes.set(root, { controller, resize });

    const position = (event, hit) => {
        const bounds = hit.getBoundingClientRect();
        const count = Number(root.dataset.count);
        return Math.max(0, Math.min(count - 1, Math.floor((event.clientX - bounds.left) * count / bounds.width)));
    };
    root.addEventListener("pointerdown", event => {
        const hit = event.target.closest(".di-tape-hit");
        if (!hit || event.button !== 0 || drag) return;
        drag = { id: event.pointerId, x: event.clientX, from: position(event, hit), moved: false };
        hit.focus({ preventScroll: true });
        hit.setPointerCapture(event.pointerId);
    }, options);
    root.addEventListener("pointermove", event => {
        const hit = event.target.closest(".di-tape-hit");
        if (!hit) {
            if (!drag) emit(-1);
            return;
        }

        const index = position(event, hit);
        if (drag && event.pointerId === drag.id && (drag.moved || Math.abs(event.clientX - drag.x) > 4)) {
            drag.moved = true;
            emit(index, Math.min(drag.from, index), Math.max(drag.from, index));
        } else if (!drag) {
            emit(index);
        }
    }, options);
    root.addEventListener("pointerleave", () => { if (!drag) emit(-1); }, options);
    root.addEventListener("pointerup", event => {
        if (drag?.id !== event.pointerId) return;
        const hit = event.target.closest(".di-tape-hit");
        const index = hit ? position(event, hit) : -1;
        if (!drag.moved && hit) emit(index, -1, -1, true);
        drag = undefined;
    }, options);
    root.addEventListener("pointercancel", event => {
        if (drag?.id === event.pointerId) {
            drag = undefined;
            emit(-1);
        }
    }, options);

    root.addEventListener("keydown", event => {
        if (!event.target.matches(".di-seg-btn")) return;
        const cells = [...root.querySelectorAll(".di-seg-btn")];
        const index = cells.indexOf(event.target);
        const next = { ArrowLeft: index - 1, ArrowRight: index + 1, Home: 0, End: cells.length - 1 }[event.key];
        if (next !== undefined) moveFocus(event, cells, Math.max(0, Math.min(next, cells.length - 1)));
    }, options);
}

export function unbindTape(root) {
    const tape = tapes.get(root);
    tape?.controller.abort();
    tape?.resize.disconnect();
    tapes.delete(root);
}

export function bindMatrix(root) {
    root?.addEventListener("keydown", event => {
        if (!event.target.matches("[data-cell]")) return;
        const cells = [...root.querySelectorAll("[data-cell]")];
        const index = cells.indexOf(event.target);
        const columns = root.querySelector("[role=row]:nth-child(2)").querySelectorAll("[data-cell]").length;
        const next = {
            ArrowLeft: index - 1, ArrowRight: index + 1, ArrowUp: index - columns, ArrowDown: index + columns,
            Home: index - index % columns, End: index - index % columns + columns - 1
        }[event.key];
        if (next !== undefined && cells[next]) moveFocus(event, cells, next);
    });
}

function moveFocus(event, cells, next) {
    event.preventDefault();
    for (const cell of cells) cell.tabIndex = -1;
    cells[next].tabIndex = 0;
    cells[next].focus();
}
