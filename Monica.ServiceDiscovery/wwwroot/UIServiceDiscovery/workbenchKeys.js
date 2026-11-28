export function attach(root) {
    function onKeyDown(event) {
        const mark = event.target.closest("[data-sv-keymark]");
        const grid = mark?.closest("[data-sv-keygrid]");
        if (!grid || event.altKey || event.ctrlKey || event.metaKey) return;
        const lanes = [...grid.querySelectorAll("[data-sv-keyrow]")];
        const row = mark.closest("[data-sv-keyrow]");
        const rowIndex = lanes.indexOf(row);
        if (rowIndex < 0) return;
        const marks = [...row.querySelectorAll("[data-sv-keymark]:not(:disabled)")];
        const column = marks.indexOf(mark);
        const firstIn = lane => lane?.querySelector("[data-sv-keymark]:not(:disabled)");
        const matrix = grid.hasAttribute("data-sv-keymatrix");
        const adjacent = lane => matrix ? lane?.querySelectorAll("[data-sv-keymark]")[column] : firstIn(lane);
        let next;
        switch (event.key) {
            case "ArrowRight": next = marks[column + 1]; break;
            case "ArrowLeft": next = marks[column - 1]; break;
            case "ArrowDown": next = adjacent(lanes[rowIndex + 1]); break;
            case "ArrowUp": next = adjacent(lanes[rowIndex - 1]); break;
            case "Home": next = matrix ? marks[0] : firstIn(lanes[0]); break;
            case "End": next = matrix || lanes.length === 1 ? marks.at(-1) : firstIn(lanes.at(-1)); break;
            default: return;
        }
        event.preventDefault();
        next?.focus();
    }
    root.addEventListener("keydown", onKeyDown);
    return { dispose() { root.removeEventListener("keydown", onKeyDown); } };
}
