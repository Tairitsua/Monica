export function bindHorizonKeys(root) {
    if (!root) return;
    root.addEventListener("keydown", event => {
        const lane = event.target.closest(".dc-eh-lane");
        if (!lane || !["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) return;
        const lanes = [...root.querySelectorAll(".dc-eh-lane")];
        const index = lanes.indexOf(lane);
        const next = {
            ArrowDown: Math.min(lanes.length - 1, index + 1),
            ArrowUp: Math.max(0, index - 1),
            Home: 0,
            End: lanes.length - 1
        }[event.key];
        event.preventDefault();
        lanes[next]?.focus();
    });
}
