export function bindEscape(invoker) {
    const dialog = invoker?.closest('[role="dialog"]');
    if (!dialog) return;

    dialog.addEventListener("keyup", event => {
        if (event.key !== "Escape" || !invoker.isConnected) return;
        // Dispatch from the button, so Blazor doesn't serialize the focused editor's value.
        event.preventDefault();
        event.stopPropagation();
        invoker.click();
    }, true);

    if (!dialog.contains(dialog.ownerDocument.activeElement)) {
        dialog.querySelector(".mo-side-sheet-close:not([disabled])")?.focus({ preventScroll: true });
    }
}
