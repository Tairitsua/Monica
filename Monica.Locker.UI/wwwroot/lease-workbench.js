// The workbench owns both listeners, including when the horizon is regrouped or paged.
export function watchVisibility(target, root) {
    let disposed = false;
    const changed = () => {
        if (!disposed) target.invokeMethodAsync("VisibilityChanged", !document.hidden).catch(() => {});
    };

    document.addEventListener("visibilitychange", changed);
    root?.addEventListener("keydown", moveBetweenRows);
    changed();
    return {
        dispose() {
            disposed = true;
            document.removeEventListener("visibilitychange", changed);
            root?.removeEventListener("keydown", moveBetweenRows);
        }
    };
}

function moveBetweenRows(event) {
    if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
    const row = event.target.closest?.("[data-lease-row]");
    if (!row) return;
    const rows = Array.from(row.closest(".lh-board").querySelectorAll("[data-lease-row]"));
    const index = rows.indexOf(row);
    const last = rows.length - 1;
    const next = {
        Home: 0,
        End: last,
        ArrowUp: index === 0 ? last : index - 1,
        ArrowDown: index === last ? 0 : index + 1
    };
    event.preventDefault();
    rows[next[event.key]].focus();
}
