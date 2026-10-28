export function createTerminalScroller(element, receiver) {
    let disposed = false;
    let following = true;
    let frame = null;
    let shutdownPromise = null;
    let callback = null;
    let pendingState = null;
    let publishedState = null;
    let scrollIntent = null;
    let scrollbarPointer = null;
    let touch = null;
    let lastScrollTop = element.scrollTop;
    let document = element.ownerDocument;
    let workspace = element.closest(".terminal-body");
    let page = workspace.closest(".terminal-page");
    let owner = workspace.closest(".mo-body-content") ?? page.parentElement;

    const atEnd = () => element.scrollHeight - element.scrollTop - element.clientHeight <= 24;
    const capacity = () => {
        const bodyBounds = workspace.getBoundingClientRect();
        const pageBounds = page.getBoundingClientRect();
        const bottomInset = parseFloat(getComputedStyle(page).paddingBottom);
        const height = owner.clientHeight - (bodyBounds.top - pageBounds.top) - bottomInset;
        const main = workspace.querySelector(".terminal-main");
        const fixedHeight = [...main.children].filter(child => !child.classList.contains("terminal-transcript"))
            .reduce((total, child) => total + child.getBoundingClientRect().height, 0);
        return [workspace.clientWidth < 1040 || height < 480, height < fixedHeight + 224];
    };
    const publish = () => {
        if (disposed) return;
        const [compact, flow] = capacity();
        const state = [compact, !following && !atEnd(), flow];
        if (publishedState?.every((value, index) => value === state[index])) {
            pendingState = null;
            return;
        }
        pendingState = state;
        pump();
    };
    const pump = () => {
        if (disposed || callback || !pendingState) return;
        const state = pendingState;
        pendingState = null;
        publishedState = state;
        callback = receiver.invokeMethodAsync("ReceiveStateAsync", ...state);
        callback.catch(error => {
            if (!disposed) console.error("Terminal workspace observation failed", error);
        }).finally(() => {
            callback = null;
            pump();
        });
    };
    const update = (revealLatest = false) => {
        if (disposed) return false;
        if (revealLatest) {
            following = true;
            scrollIntent = null;
            scrollbarPointer = null;
        }
        if (frame !== null) cancelAnimationFrame(frame);
        frame = requestAnimationFrame(() => {
            frame = null;
            if (disposed || !element?.isConnected) return;
            if (following) element.scrollTop = element.scrollHeight;
            publish();
        });
        return !following && !atEnd();
    };
    const onScroll = () => {
        if (disposed) return;
        // Layout, anchoring, and our own writes do not change the user's follow choice.
        if (scrollbarPointer === null && scrollIntent === "toward-latest" &&
            element.scrollTop > lastScrollTop && atEnd()) {
            following = true;
            scrollIntent = null;
            update();
        }
        lastScrollTop = element.scrollTop;
        publish();
    };
    const beginScroll = direction => {
        if (disposed || scrollbarPointer !== null) return;
        lastScrollTop = element.scrollTop;
        if (direction < 0) {
            scrollIntent = null;
            if (element.scrollHeight > element.clientHeight) following = false;
        } else if (direction > 0) {
            if (atEnd()) {
                following = true;
                scrollIntent = null;
                update();
            } else {
                scrollIntent = "toward-latest";
            }
        }
        publish();
    };
    const onWheel = event => {
        if (!event.defaultPrevented && !event.ctrlKey) beginScroll(event.deltaY);
    };
    const onKeyDown = event => {
        if (event.defaultPrevented || event.altKey || event.metaKey) return;
        if (["ArrowUp", "PageUp", "Home"].includes(event.key) || event.key === " " && event.shiftKey) {
            beginScroll(-1);
        } else if (["ArrowDown", "PageDown", "End"].includes(event.key) || event.key === " ") {
            beginScroll(1);
        }
    };
    const onTouchStart = event => {
        const point = event.targetTouches[0];
        touch = point ? { id: point.identifier, y: point.clientY } : null;
    };
    const onTouchMove = event => {
        const point = touch && [...event.changedTouches].find(point => point.identifier === touch.id);
        if (!point) return;
        if (!event.defaultPrevented) beginScroll(touch.y - point.clientY);
        touch.y = point.clientY;
    };
    const onTouchEnd = event => {
        if (touch && [...event.changedTouches].some(point => point.identifier === touch.id)) touch = null;
    };
    const onPointerDown = event => {
        if (disposed || event.button !== 0 || event.pointerType !== "mouse" ||
            element.scrollHeight <= element.clientHeight) return;
        const bounds = element.getBoundingClientRect();
        const style = getComputedStyle(element);
        const innerLeft = bounds.left + parseFloat(style.borderLeftWidth);
        const innerRight = bounds.right - parseFloat(style.borderRightWidth);
        const viewportLeft = bounds.left + element.clientLeft;
        const viewportRight = viewportLeft + element.clientWidth;
        // The native vertical gutter can sit on either side of the local viewport.
        if (event.clientX >= innerLeft && event.clientX < innerRight &&
            (event.clientX < viewportLeft || event.clientX >= viewportRight)) {
            scrollbarPointer = event.pointerId;
            scrollIntent = null;
            following = false;
            publish();
        }
    };
    const onPointerEnd = event => {
        if (disposed || event.pointerId !== scrollbarPointer) return;
        scrollbarPointer = null;
        following = atEnd();
        if (following) update();
        publish();
    };
    const onScrollEnd = () => { scrollIntent = null; };
    element.addEventListener("scroll", onScroll, { passive: true });
    element.addEventListener("scrollend", onScrollEnd, { passive: true });
    element.addEventListener("wheel", onWheel, { passive: true });
    element.addEventListener("keydown", onKeyDown);
    element.addEventListener("touchstart", onTouchStart, { passive: true });
    element.addEventListener("touchmove", onTouchMove, { passive: true });
    element.addEventListener("touchend", onTouchEnd, { passive: true });
    element.addEventListener("touchcancel", onTouchEnd, { passive: true });
    element.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("pointerup", onPointerEnd);
    document.addEventListener("pointercancel", onPointerEnd);
    const resize = new ResizeObserver(() => update());
    resize.observe(workspace);
    resize.observe(element);
    resize.observe(owner);
    const shutdown = () => {
        if (shutdownPromise) return shutdownPromise;
        disposed = true;
        removal.disconnect();
        resize.disconnect();
        element?.removeEventListener("scroll", onScroll);
        element?.removeEventListener("scrollend", onScrollEnd);
        element?.removeEventListener("wheel", onWheel);
        element?.removeEventListener("keydown", onKeyDown);
        element?.removeEventListener("touchstart", onTouchStart);
        element?.removeEventListener("touchmove", onTouchMove);
        element?.removeEventListener("touchend", onTouchEnd);
        element?.removeEventListener("touchcancel", onTouchEnd);
        element?.removeEventListener("pointerdown", onPointerDown);
        document.removeEventListener("pointerup", onPointerEnd);
        document.removeEventListener("pointercancel", onPointerEnd);
        if (frame !== null) cancelAnimationFrame(frame);
        frame = null;
        pendingState = null;
        scrollIntent = null;
        scrollbarPointer = null;
        touch = null;
        shutdownPromise = (async () => {
            if (callback) await callback.catch(() => {});
            element = null;
            workspace = null;
            receiver = null;
            page = null;
            owner = null;
            document = null;
        })();
        return shutdownPromise;
    };
    const removal = new MutationObserver(() => {
        if (!element?.isConnected) void shutdown();
    });
    removal.observe(document.body, { childList: true, subtree: true });
    update(true);
    return { update, shutdown };
}
