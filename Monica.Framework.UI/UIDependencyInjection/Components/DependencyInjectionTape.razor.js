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

    const viewport = root.querySelector(".di-tape-viewport");
    const track = root.querySelector(".di-tape-track");
    const axis = root.querySelector(".di-tape-axis");
    const count = Number(root.dataset.count);
    const zoomButtons = [...root.querySelectorAll("[data-tape-zoom]")];
    const scaleLabel = root.querySelector("[data-tape-scale]");
    const percent = new Intl.NumberFormat(document.documentElement.lang || undefined, { style: "percent" });

    const controller = new AbortController();
    const options = { signal: controller.signal };
    let fitWidth = 0;
    let scale = 1;
    let axisFrame = 0;
    let drag;
    let last;

    // Keep the plot and scrolling native. A wheel gesture only changes the track width and scroll offset.
    const maxScale = () => Math.max(4, count * 28 / fitWidth);
    const applyZoom = (requested, offset, indexFraction) => {
        scale = Math.max(1, Math.min(requested, maxScale()));
        const width = scale * fitWidth;
        track.style.width = `${width}px`;
        viewport.scrollLeft = indexFraction * width - offset;

        if (scaleLabel) scaleLabel.textContent = percent.format(scale);
        for (const button of zoomButtons) {
            const atLimit = button.dataset.tapeZoom === "in" ? scale >= maxScale() : scale === 1;
            button.setAttribute("aria-disabled", String(atLimit));
        }

        scheduleAxis();
    };

    const zoomAt = (requested, offset = fitWidth / 2) => {
        if (!fitWidth) return;
        const next = Math.max(1, Math.min(requested, maxScale()));
        if (next === scale) return;
        const fraction = (viewport.scrollLeft + offset) / track.clientWidth;
        applyZoom(next, offset, fraction);
    };

    const resize = new ResizeObserver(() => {
        const width = viewport.clientWidth;
        if (!width || width === fitWidth) return;

        const fraction = fitWidth ? (viewport.scrollLeft + fitWidth / 2) / track.clientWidth : .5;
        fitWidth = width;
        applyZoom(scale, width / 2, fraction);
    });
    resize.observe(viewport);
    tapes.set(root, {
        dispose() {
            controller.abort();
            resize.disconnect();
            cancelAnimationFrame(axisFrame);
        },
        viewport,
        track
    });

    function scheduleAxis() {
        if (!axisFrame) axisFrame = requestAnimationFrame(drawAxis);
    }

    function drawAxis() {
        axisFrame = 0;
        const pitch = track.clientWidth / count;
        const unitsPerLabel = Math.max(1, 68 / pitch);
        const magnitude = 10 ** Math.floor(Math.log10(unitsPerLabel));
        const step = [1, 2, 5, 10].map(unit => unit * magnitude).find(interval => interval >= unitsPerLabel);
        const start = Math.ceil(viewport.scrollLeft / pitch / step) * step;
        const end = Math.min(count - 1, Math.floor((viewport.scrollLeft + fitWidth) / pitch));
        const marks = [];
        for (let index = start; index <= end; index += step) {
            const x = index * pitch - viewport.scrollLeft;
            const mark = document.createElement("span");
            mark.className = index === 0 ? "di-t-tick is-start" : "di-t-tick";
            mark.style.left = `${index * 100 / count}%`;
            if (index === 0 || (x >= 20 && x <= fitWidth - 20)) mark.textContent = `#${index}`;
            marks.push(mark);
        }
        axis.replaceChildren(...marks);
    }

    viewport.addEventListener("wheel", event => {
        if (!fitWidth || drag || event.ctrlKey || event.metaKey || event.shiftKey ||
            Math.abs(event.deltaY) <= Math.abs(event.deltaX)) return;
        const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? fitWidth : 1);
        const next = scale * Math.exp(-Math.max(-160, Math.min(160, delta)) * .003);
        if ((scale === 1 && next < 1) || (scale >= maxScale() && next > scale)) return;

        event.preventDefault();
        zoomAt(next, event.clientX - viewport.getBoundingClientRect().left);
    }, { passive: false, signal: controller.signal });
    viewport.addEventListener("scroll", scheduleAxis, options);
    root.addEventListener("click", event => {
        const zoom = event.target.closest("[data-tape-zoom]");
        if (zoom) {
            const command = zoom.dataset.tapeZoom;
            zoomAt(command === "fit" ? 1 : command === "in" ? scale * 1.5 : scale / 1.5);
            return;
        }

        // The curve and its endpoint use the same button and evidence focus path.
        const path = event.target.closest(".di-t-arc-hit");
        if (path) {
            const marker = track.querySelector(`.di-arc-btn[data-arc-index="${path.dataset.arcIndex}"]`);
            marker.focus();
            marker.click();
        }
    }, options);

    const emit = (hover, from = -1, to = -1, open = false) => {
        const key = [hover, from, to].join(":");
        if (controller.signal.aborted || (!open && key === last)) return;
        last = key;
        dotnet.invokeMethodAsync("ReadPointer", hover, from, to, open).catch(() => unbindTape(root));
    };

    const position = (event, hit) => {
        const bounds = hit.getBoundingClientRect();
        const visible = viewport.getBoundingClientRect();
        const clientX = Math.max(visible.left, Math.min(event.clientX, visible.right - .5));
        return Math.max(0, Math.min(count - 1, Math.floor((clientX - bounds.left) * count / bounds.width)));
    };
    viewport.addEventListener("pointerdown", event => {
        const hit = event.target.closest(".di-tape-hit");
        if (!hit || event.button !== 0 || drag) return;
        drag = { id: event.pointerId, x: event.clientX, from: position(event, hit), moved: false };
        hit.focus({ preventScroll: true });
        hit.setPointerCapture(event.pointerId);
    }, options);
    viewport.addEventListener("pointermove", event => {
        const hit = event.target.closest(".di-tape-hit");
        if (!hit) {
            if (!drag) emit(-1);
            return;
        }

        const index = position(event, hit);
        if (drag && event.pointerId === drag.id && (drag.moved || Math.abs(event.clientX - drag.x) > 4)) {
            drag.moved = true;
            if (event.pointerType !== "touch") emit(index, Math.min(drag.from, index), Math.max(drag.from, index));
        } else if (!drag && event.pointerType !== "touch") {
            emit(index);
        }
    }, options);
    viewport.addEventListener("pointerleave", () => { if (!drag) emit(-1); }, options);
    viewport.addEventListener("pointerup", event => {
        if (drag?.id !== event.pointerId) return;
        const hit = event.target.closest(".di-tape-hit");
        if (!drag.moved && hit) emit(position(event, hit), -1, -1, true);
        drag = undefined;
    }, options);
    viewport.addEventListener("lostpointercapture", event => {
        if (drag?.id === event.pointerId) {
            drag = undefined;
            emit(-1);
        }
    }, options);

    viewport.addEventListener("keydown", event => {
        if (!event.target.matches(".di-seg-btn")) return;
        const cells = [...track.querySelectorAll(".di-seg-btn")];
        const index = cells.indexOf(event.target);
        const next = { ArrowLeft: index - 1, ArrowRight: index + 1, Home: 0, End: cells.length - 1 }[event.key];
        if (next !== undefined) moveFocus(event, cells, Math.max(0, Math.min(next, cells.length - 1)));
    }, options);
}

export function revealTapeFinding(root, implementation, moveFocus) {
    const tape = tapes.get(root);
    if (!tape || !root.isConnected) return;
    const { viewport, track } = tape;
    const markers = [...track.querySelectorAll(".di-arc-btn")]
        .filter(marker => marker.dataset.implementation === implementation);
    if (!markers.length) return;

    const pitch = track.clientWidth / Number(root.dataset.count);
    const center = index => (Number(index) + .5) * pitch;
    const endpoints = markers.flatMap(marker => [center(marker.dataset.from), center(marker.dataset.to)]);
    const first = markers[0];
    const padding = 12;
    let min = Math.min(...endpoints);
    let max = Math.max(...endpoints);
    if (max - min > viewport.clientWidth - 2 * padding) {
        min = max = center(first.dataset.to);
    }

    if (min < viewport.scrollLeft + padding || max > viewport.scrollLeft + viewport.clientWidth - padding) {
        viewport.scrollLeft = (min + max - viewport.clientWidth) / 2;
    }
    if (moveFocus) {
        root.scrollIntoView({ block: "nearest", inline: "nearest" });
        first.focus({ preventScroll: true });
    }
}

export function unbindTape(root) {
    tapes.get(root)?.dispose();
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
