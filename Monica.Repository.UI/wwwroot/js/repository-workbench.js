const bound = new WeakSet();

export function bind(workbench) {
    if (bound.has(workbench)) return;
    bound.add(workbench);
    workbench.addEventListener("focusin", event => {
        const mark = event.target.closest("[data-rp-mark]");
        const region = mark?.closest("[data-rp-keyboard]");
        if (!region) return;
        for (const item of region.querySelectorAll("[data-rp-mark]")) {
            item.tabIndex = item === mark ? 0 : -1;
        }
    });
    workbench.addEventListener("keydown", event => {
        if (event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
        const mark = event.target.closest("[data-rp-mark]");
        const region = mark?.closest("[data-rp-keyboard]");
        if (!region) return;
        const marks = [...region.querySelectorAll("[data-rp-mark]")];
        const lane = mark.closest("[data-rp-lane]");
        const row = lane ? [...lane.querySelectorAll("[data-rp-mark]")] : marks;
        const step = event.key === "ArrowUp" || event.key === "ArrowLeft" ? -1 : 1;
        let next;
        switch (event.key) {
            case "Home": next = marks[0]; break;
            case "End": next = marks.at(-1); break;
            case "ArrowLeft":
            case "ArrowRight":
                next = row[row.indexOf(mark) + step];
                break;
            case "ArrowUp":
            case "ArrowDown":
                if (!lane) {
                    next = marks[marks.indexOf(mark) + step];
                    break;
                }
                const lanes = [...region.querySelectorAll("[data-rp-lane]")].filter(l => l.querySelector("[data-rp-mark]"));
                const candidates = lanes[lanes.indexOf(lane) + step]?.querySelectorAll("[data-rp-mark]");
                const position = Number(mark.dataset.rpMark);
                if (candidates) {
                    next = Array.from(candidates).reduce((best, item) =>
                        Math.abs(Number(best.dataset.rpMark) - position) <= Math.abs(Number(item.dataset.rpMark) - position) ? best : item);
                }
                break;
            default: return;
        }
        event.preventDefault();
        next?.focus();
    });
}

export function revealOptions(workbench) {
    const options = workbench?.querySelector("#repo-options");
    if (!options) return false;
    options.scrollIntoView({ block: "start" });
    return true;
}
