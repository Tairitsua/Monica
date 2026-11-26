export function focusEvidence(headingId) {
    let timeout;
    const observer = new MutationObserver(focusWhenPresent);

    function focusWhenPresent() {
        const heading = document.getElementById(headingId);
        if (!heading) return false;
        observer.disconnect();
        clearTimeout(timeout);
        // Let the native focus trap remember its opener before focusing the evidence heading.
        requestAnimationFrame(() => requestAnimationFrame(() => {
            const active = document.activeElement;
            const inside = heading.closest(".mo-side-sheet")?.contains(active);
            const interactive = active?.matches("button, a[href], input, textarea, select, [contenteditable='true']");
            if (heading.isConnected && (!inside || !interactive)) {
                heading.focus({ preventScroll: true });
            }
        }));
        return true;
    }

    if (!focusWhenPresent()) {
        observer.observe(document.body, { childList: true, subtree: true });
        timeout = setTimeout(() => observer.disconnect(), 1200);
    }
}

export function attachMatrix(matrix) {
    let menuObserver;
    let menuTimeout;

    function stopMenuFocus() {
        menuObserver?.disconnect();
        clearTimeout(menuTimeout);
    }

    function focusMenuItem(last) {
        const items = [...document.querySelectorAll(".ep-menu-popover [role='menuitem']:not([disabled]):not([aria-disabled='true'])")]
            .filter(item => item.getClientRects().length > 0);
        const item = last ? items.at(-1) : items[0];
        if (!item) return false;
        item.focus();
        return true;
    }

    function onKeyDown(event) {
        const menu = event.target.closest("button[aria-haspopup='menu']");
        if (menu && ["ArrowDown", "ArrowUp", "Enter", " "].includes(event.key)) {
            const last = event.key === "ArrowUp";
            const expanded = menu.getAttribute("aria-expanded") === "true";
            if (expanded && (event.key === "Enter" || event.key === " ")) return;
            event.preventDefault();
            if (focusMenuItem(last)) return;
            stopMenuFocus();
            menuObserver = new MutationObserver(() => {
                if (focusMenuItem(last)) stopMenuFocus();
            });
            menuObserver.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["class"] });
            menuTimeout = setTimeout(stopMenuFocus, 1200);
            if (!expanded) menu.click();
            return;
        }

        const row = event.target.closest("[data-ep-nav]");
        if (!row || !matrix.contains(row) || event.altKey || event.ctrlKey || event.metaKey) return;

        const rows = Array.from(matrix.querySelectorAll("[data-ep-nav]"));
        const index = rows.indexOf(row);
        const expanded = row.getAttribute("aria-expanded");
        let next;
        switch (event.key) {
            case "ArrowDown": next = rows[index + 1]; break;
            case "ArrowUp": next = rows[index - 1]; break;
            case "Home": next = rows[0]; break;
            case "End": next = rows.at(-1); break;
            case "ArrowRight":
                if (expanded === "false") row.click();
                break;
            case "ArrowLeft":
                if (expanded === "true") row.click();
                else if (row.dataset.epParent) next = rows.find(candidate => candidate.dataset.epNav === row.dataset.epParent);
                break;
            default: return;
        }

        event.preventDefault();
        if (next) {
            row.tabIndex = -1;
            next.tabIndex = 0;
            next.focus();
        }
    }
    matrix.addEventListener("keydown", onKeyDown);
    return DotNet.createJSObjectReference({
        dispose() {
            stopMenuFocus();
            matrix.removeEventListener("keydown", onKeyDown);
        }
    });
}
