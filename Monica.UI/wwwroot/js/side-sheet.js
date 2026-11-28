export function bindSheet(invoker, focusCloseOnOpen) {
    const dialog = invoker?.closest('[role="dialog"]');
    if (!dialog) return;

    if (focusCloseOnOpen) {
        // The native trap saves the opener before focusing its fallback element.
        const trap = invoker.closest(".mud-focus-trap");
        const focusClose = target => {
            if (target?.parentElement === trap && target.tabIndex === -1) {
                dialog.querySelector(".mo-side-sheet-close:not([disabled])")?.focus({ preventScroll: true });
            }
        };
        if (trap?.contains(document.activeElement)) focusClose(document.activeElement);
        else trap?.addEventListener("focusin", event => focusClose(event.target), { once: true });
    }

    dialog.addEventListener("keyup", event => {
        if (event.key !== "Escape" || !invoker.isConnected || !invoker.hasAttribute("data-dismiss-on-escape")) return;
        // Dispatch from the button, so Blazor doesn't serialize the focused editor's value.
        event.preventDefault();
        event.stopPropagation();
        invoker.click();
    }, true);
}
