// Delegated native keyboard navigation; no .NET callback survives the region's lifetime.
export function wireKeyboard(root, selector) {
    const keydown = event => {
        if (event.target.id === "sr-qs-msg" && event.key === "Enter" && !event.shiftKey) {
            event.preventDefault();
            return;
        }
        if (!["ArrowDown", "ArrowUp", "ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) return;
        const choices = [...root.querySelectorAll(selector)].filter(element => !element.disabled);
        const index = choices.indexOf(event.target.closest(selector));
        if (index < 0) return;
        const next = event.key === "Home" ? 0 : event.key === "End" ? choices.length - 1
            : Math.max(0, Math.min(choices.length - 1, index + (["ArrowDown", "ArrowRight"].includes(event.key) ? 1 : -1)));
        event.preventDefault();
        choices[next]?.focus();
    };
    root.addEventListener("keydown", keydown);
    return { dispose() { root.removeEventListener("keydown", keydown); } };
}
