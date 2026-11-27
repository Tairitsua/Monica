export function setIndeterminate(input, value) {
    if (input?.isConnected) input.indeterminate = value;
}

export function bindTape(bar) {
    if (!bar?.isConnected) return;

    const buttons = () => [...bar.querySelectorAll(".fo-tape-seg")];
    bar.addEventListener("focusin", event => {
        if (!event.target.matches(".fo-tape-seg")) return;
        for (const button of buttons()) button.tabIndex = button === event.target ? 0 : -1;
    });
    bar.addEventListener("keydown", event => {
        const items = buttons();
        const current = items.indexOf(event.target);
        if (current < 0) return;
        if (event.key === "Escape") {
            event.preventDefault();
            bar.querySelector("[data-fo-clear]")?.click();
            return;
        }
        const next = {
            ArrowLeft: current - 1,
            ArrowRight: current + 1,
            Home: 0,
            End: items.length - 1,
        }[event.key];
        if (next === undefined) return;
        event.preventDefault();
        items[Math.max(0, Math.min(items.length - 1, next))]?.focus({ preventScroll: true });
    });
}

export async function downloadStream(fileName, contentType, streamReference) {
    if (!streamReference) return false;
    const arrayBuffer = await streamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer], { type: contentType || "application/octet-stream" });
    return triggerDownload(fileName || "download", blob);
}

function triggerDownload(fileName, blob) {
    const objectUrl = URL.createObjectURL(blob);
    let anchor;
    try {
        anchor = document.createElement("a");
        anchor.href = objectUrl;
        anchor.download = fileName || "download";
        anchor.rel = "noopener";
        anchor.style.display = "none";
        document.body.appendChild(anchor);
        anchor.click();
        return true;
    } finally {
        try { anchor?.remove(); }
        finally { URL.revokeObjectURL(objectUrl); }
    }
}
