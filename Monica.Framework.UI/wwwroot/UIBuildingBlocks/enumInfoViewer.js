export function createScrollController(root) {
    let disposed = false;
    const frames = new Map();
    const reducedMotion = () => window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    const cancelPending = () => {
        for (const pending of frames.values()) {
            cancelAnimationFrame(pending.first);
            if (pending.second !== null) cancelAnimationFrame(pending.second);
        }
        frames.clear();
    };
    const observer = new MutationObserver(() => {
        if (!root?.isConnected) dispose();
    });
    const dispose = () => {
        if (disposed) return;
        disposed = true;
        cancelPending();
        observer.disconnect();
    };
    if (root?.isConnected) observer.observe(document.documentElement, { childList: true, subtree: true });
    else dispose();

    return {
        cancelPending,
        scrollToMatch(container, searchKey) {
            if (disposed || !root?.isConnected || !container?.isConnected || !root.contains(container)) return;
            const previous = frames.get(container);
            if (previous) {
                cancelAnimationFrame(previous.first);
                if (previous.second !== null) cancelAnimationFrame(previous.second);
            }
            const pending = { first: null, second: null };
            pending.first = requestAnimationFrame(() => {
                pending.second = requestAnimationFrame(() => {
                    if (disposed || frames.get(container) !== pending || !container.isConnected || !root.contains(container)) return;
                    frames.delete(container);
                    const target = [...container.querySelectorAll("[data-search-key]")]
                        .find(element => element.dataset.searchKey === searchKey);
                    if (!target) return;
                    const containerRect = container.getBoundingClientRect();
                    const targetRect = target.getBoundingClientRect();
                    const relativeTop = targetRect.top - containerRect.top + container.scrollTop;
                    const centered = relativeTop - Math.max(0, (container.clientHeight - targetRect.height) / 2);
                    const maximum = Math.max(0, container.scrollHeight - container.clientHeight);
                    container.scrollTo({ top: Math.min(maximum, Math.max(centered, 0)), behavior: reducedMotion() ? "auto" : "smooth" });
                });
            });
            frames.set(container, pending);
        },
        revealPane(container) {
            if (disposed || !root?.isConnected || !container?.isConnected || !root.contains(container)) return;
            container.focus({ preventScroll: true });
            container.scrollIntoView({ block: "nearest", behavior: reducedMotion() ? "auto" : "smooth" });
        },
        dispose
    };
}

const legacyControllers = new WeakMap();

export function scrollToMatch(container, searchKey) {
    const root = container?.closest(".enum-info-viewer");
    if (!root) return;
    let controller = legacyControllers.get(root);
    if (!controller) {
        controller = createScrollController(root);
        legacyControllers.set(root, controller);
    }
    controller.scrollToMatch(container, searchKey);
}
