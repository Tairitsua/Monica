const grids = new WeakMap();

export function attach(grid, wrap) {
    if (!grid || grids.has(grid)) return;
    const controller = new AbortController();
    grid.addEventListener("keydown", event => {
        const current = event.target.closest("button[data-row][data-column]");
        if (!current || !grid.contains(current)) return;

        const buttons = Array.from(grid.querySelectorAll("button[data-row][data-column]"))
            .filter(button => button.getClientRects().length > 0);
        const row = buttons.filter(button => button.dataset.row === current.dataset.row);
        const column = buttons.filter(button => button.dataset.column === current.dataset.column);
        let next;
        switch (event.key) {
            case "ArrowLeft": next = step(row, current, -1, wrap); break;
            case "ArrowRight": next = step(row, current, 1, wrap); break;
            case "ArrowUp": next = step(column, current, -1, wrap); break;
            case "ArrowDown": next = step(column, current, 1, wrap); break;
            case "Home": next = row[0]; break;
            case "End": next = row.at(-1); break;
            default: return;
        }

        event.preventDefault();
        next?.focus();
    }, { signal: controller.signal });
    grids.set(grid, controller);
}

export function detach(grid) {
    if (!grid) return;
    grids.get(grid)?.abort();
    grids.delete(grid);
}

function step(buttons, current, offset, wrap) {
    const index = buttons.indexOf(current) + offset;
    return buttons[wrap ? (index + buttons.length) % buttons.length : Math.max(0, Math.min(buttons.length - 1, index))];
}
