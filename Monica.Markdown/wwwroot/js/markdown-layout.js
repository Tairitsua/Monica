export function createMarkdownViewerSession(viewerRoot, dotNetReference) {
    return new MarkdownViewerSession(viewerRoot, dotNetReference);
}

class MarkdownViewerSession {
    constructor(viewerRoot, dotNetReference) {
        this.viewerRoot = viewerRoot;
        this.dotNetReference = dotNetReference;
        this.window = viewerRoot?.ownerDocument?.defaultView ?? window;
        this.activeHeadingTracker = null;
        this.activeSearchHit = null;
        this.pendingCallbacks = new Set();
        this.pendingViewport = null;
        this.workspaceSize = null;
        this.readingStage = null;
        this.readingStageHeight = null;
        this.lastViewport = null;
        this.viewportNotificationTask = null;
        this.disposed = false;
        this.shutdownPromise = null;
        this.removalObserver = new MutationObserver(() => {
            if (!this.viewerRoot?.isConnected) {
                void this.shutdown();
            } else {
                this.observeReadingStage();
            }
        });

        const observerRoot = viewerRoot?.ownerDocument?.body;
        if (observerRoot) {
            this.removalObserver.observe(observerRoot, { childList: true, subtree: true });
        }

        this.viewportObserver = new ResizeObserver(entries => {
            for (const entry of entries) {
                if (entry.target === this.viewerRoot) {
                    this.workspaceSize = entry.contentRect;
                } else if (entry.target === this.readingStage) {
                    this.readingStageHeight = entry.contentRect.height;
                }
            }

            this.queueMeasuredViewport();
        });
        this.viewportObserver.observe(viewerRoot);
        this.observeReadingStage();
    }

    observeReadingStage() {
        const stage = this.viewerRoot?.querySelector("[data-markdown-reading-stage='true']") ?? null;
        if (stage === this.readingStage) {
            return;
        }

        if (this.readingStage) {
            this.viewportObserver.unobserve(this.readingStage);
        }

        this.readingStage = stage;
        this.readingStageHeight = null;
        if (stage) {
            this.viewportObserver.observe(stage);
        } else {
            this.queueMeasuredViewport();
        }
    }

    queueMeasuredViewport() {
        if (!this.workspaceSize || (this.readingStage && this.readingStageHeight === null)) {
            return;
        }

        const width = this.workspaceSize.width;
        const height = this.readingStageHeight ?? this.workspaceSize.height;
        if (this.lastViewport?.width === width && this.lastViewport?.height === height) {
            return;
        }

        this.lastViewport = { width, height };
        this.queueViewport(width, height);
    }

    queueViewport(width, height) {
        if (this.disposed || !Number.isFinite(width) || !Number.isFinite(height)) {
            return;
        }

        this.pendingViewport = { width, height };
        if (this.viewportNotificationTask) {
            return;
        }

        const task = Promise.resolve().then(async () => {
            while (!this.disposed && this.pendingViewport) {
                const measurement = this.pendingViewport;
                this.pendingViewport = null;
                await this.dotNetReference?.invokeMethodAsync(
                    "OnViewportChanged", measurement.width, measurement.height);
            }
        }).catch(() => {}).finally(() => {
            this.pendingCallbacks.delete(task);
            this.viewportNotificationTask = null;
            if (!this.disposed && this.pendingViewport) {
                const measurement = this.pendingViewport;
                this.queueViewport(measurement.width, measurement.height);
            }
        });

        this.viewportNotificationTask = task;
        this.pendingCallbacks.add(task);
    }

    observeActiveHeading(headingIds, currentAnchorId) {
        if (this.disposed) {
            return;
        }

        this.disposeActiveHeadingTracker();

        const tracker = createActiveHeadingTracker(this, headingIds, currentAnchorId);
        if (!tracker) {
            return;
        }

        this.activeHeadingTracker = tracker;
        tracker.sync(true);
        tracker.scrollRoot.addEventListener("scroll", tracker.handleScroll, { passive: true });
        this.window.addEventListener("resize", tracker.handleResize);
    }

    revealDocumentHeading(groupKey, documentRelativePath, culture) {
        if (this.disposed || !this.viewerRoot?.isConnected) {
            return false;
        }

        const content = this.viewerRoot.querySelector("[data-markdown-content-root='true']");
        if (!content || content.dataset.markdownGroup !== groupKey
            || content.dataset.markdownDocument !== documentRelativePath
            || (content.dataset.markdownCulture ?? "").toLowerCase() !== (culture ?? "").toLowerCase()) {
            return false;
        }

        const heading = content.querySelector("[data-markdown-document-heading='true']");
        const scrollRoot = this.viewerRoot.querySelector("[data-markdown-scroll-root='true']");
        if (!heading || !scrollRoot) {
            return false;
        }

        heading.focus({ preventScroll: true });
        scrollRoot.scrollTo({ top: 0, behavior: "auto" });
        return true;
    }

    disposeActiveHeadingTracker() {
        const tracker = this.activeHeadingTracker;
        if (!tracker) {
            return;
        }

        tracker.scrollRoot.removeEventListener("scroll", tracker.handleScroll);
        this.window.removeEventListener("resize", tracker.handleResize);

        if (tracker.animationFrameId !== null) {
            this.window.cancelAnimationFrame(tracker.animationFrameId);
        }

        this.activeHeadingTracker = null;
    }

    clearSearchHit() {
        const searchHit = this.activeSearchHit;
        if (!searchHit?.parentNode) {
            this.activeSearchHit = null;
            return;
        }

        const parent = searchHit.parentNode;
        while (searchHit.firstChild) {
            parent.insertBefore(searchHit.firstChild, searchHit);
        }

        parent.removeChild(searchHit);
        parent.normalize();
        this.activeSearchHit = null;
    }

    highlightSearchHit(anchorId, headingLevel, matchedText, prefixContext, suffixContext) {
        if (this.disposed) {
            return false;
        }

        this.clearSearchHit();

        if (!matchedText) {
            return false;
        }

        const scrollRoot = this.viewerRoot.querySelector("[data-markdown-scroll-root='true']");
        const contentRoot = this.viewerRoot.querySelector("[data-markdown-content-root='true']");
        if (!scrollRoot || !contentRoot) {
            return false;
        }

        const documentHeading = contentRoot.querySelector("[data-markdown-document-heading='true']");
        const markdownBody = contentRoot.querySelector("[data-markdown-body='true']");
        const searchableRoots = [documentHeading, markdownBody].filter(Boolean);
        const textMap = buildNormalizedTextMap(contentRoot, searchableRoots);
        if (!textMap.text) {
            return false;
        }

        const searchWindow = resolveSearchWindow(contentRoot, textMap, anchorId, headingLevel);
        const match = locateSearchMatch(
            textMap.text,
            searchWindow,
            matchedText,
            prefixContext ?? "",
            suffixContext ?? "");

        if (!match) {
            return false;
        }

        const mark = wrapMatchRange(textMap, match.start, match.end);
        if (!mark) {
            return false;
        }

        this.activeSearchHit = mark;
        scrollElementIntoView(scrollRoot, mark);
        return true;
    }

    notifyActiveHeadingChanged(anchorId) {
        const callbackReference = this.dotNetReference;
        if (this.disposed || !callbackReference) {
            return;
        }

        const callback = callbackReference.invokeMethodAsync("OnHashChangedAsync", anchorId);
        this.pendingCallbacks.add(callback);
        callback
            .catch(() => {
            })
            .finally(() => this.pendingCallbacks.delete(callback));
    }

    shutdown() {
        if (this.shutdownPromise) {
            return this.shutdownPromise;
        }

        this.disposed = true;
        this.removalObserver.disconnect();
        this.viewportObserver.disconnect();
        this.pendingViewport = null;
        this.disposeActiveHeadingTracker();
        this.dotNetReference = null;
        this.activeSearchHit = null;
        this.viewerRoot = null;
        this.readingStage = null;
        this.workspaceSize = null;
        this.readingStageHeight = null;
        this.shutdownPromise = Promise.allSettled(Array.from(this.pendingCallbacks));
        return this.shutdownPromise;
    }
}

function createActiveHeadingTracker(session, headingIds, currentAnchorId) {
    if (!Array.isArray(headingIds) || headingIds.length === 0) {
        return null;
    }

    const scrollRoot = session.viewerRoot.querySelector("[data-markdown-scroll-root='true']");
    const markdownBody = session.viewerRoot.querySelector("[data-markdown-body='true']");
    if (!scrollRoot || !markdownBody) {
        return null;
    }

    const headings = headingIds
        .map(anchorId => {
            const element = resolveHeadingElement(markdownBody, anchorId);
            if (!element) {
                return null;
            }

            return {
                anchorId,
                element
            };
        })
        .filter(Boolean);

    if (headings.length === 0) {
        return null;
    }

    const tracker = {
        animationFrameId: null,
        currentAnchorId: null,
        headings,
        session,
        scrollRoot,
        handleResize: () => scheduleSync(tracker, false),
        handleScroll: () => scheduleSync(tracker, false),
        sync: alignCurrentAnchor => syncActiveHeading(tracker, alignCurrentAnchor)
    };

    tracker.currentAnchorId = normalizeAnchorId(currentAnchorId);
    return tracker;
}

function scheduleSync(tracker, alignCurrentAnchor) {
    if (tracker.animationFrameId !== null || tracker.session.disposed) {
        return;
    }

    tracker.animationFrameId = tracker.session.window.requestAnimationFrame(() => {
        tracker.animationFrameId = null;
        tracker.sync(alignCurrentAnchor);
    });
}

function syncActiveHeading(tracker, alignCurrentAnchor) {
    if (tracker.session.disposed) {
        return;
    }

    const activeHeading = resolveActiveHeading(tracker, alignCurrentAnchor);
    const nextAnchorId = activeHeading?.anchorId ?? null;

    if (!nextAnchorId || nextAnchorId === tracker.currentAnchorId) {
        return;
    }

    tracker.currentAnchorId = nextAnchorId;
    replaceLocationHash(tracker.session.window, nextAnchorId);
    tracker.session.notifyActiveHeadingChanged(nextAnchorId);
}

function resolveActiveHeading(tracker, alignCurrentAnchor) {
    const scrollRootRect = tracker.scrollRoot.getBoundingClientRect();
    const activationLine = scrollRootRect.top + Math.min(96, scrollRootRect.height * 0.2);

    if (alignCurrentAnchor) {
        const currentHeading = tracker.headings.find(heading => heading.anchorId === tracker.currentAnchorId);
        if (currentHeading) {
            scrollHeadingIntoView(tracker.scrollRoot, currentHeading.element);
        }
    }

    let activeHeading = tracker.headings[0];

    for (const heading of tracker.headings) {
        const rect = heading.element.getBoundingClientRect();
        if (rect.bottom <= scrollRootRect.top) {
            activeHeading = heading;
            continue;
        }

        if (rect.top <= activationLine) {
            activeHeading = heading;
            continue;
        }

        break;
    }

    return activeHeading;
}

function scrollHeadingIntoView(scrollRoot, headingElement) {
    const scrollRootRect = scrollRoot.getBoundingClientRect();
    const headingRect = headingElement.getBoundingClientRect();
    const targetTop = scrollRoot.scrollTop + (headingRect.top - scrollRootRect.top) - 24;

    scrollRoot.scrollTo({
        top: Math.max(targetTop, 0),
        behavior: "auto"
    });
}

function scrollElementIntoView(scrollRoot, element) {
    const scrollRootRect = scrollRoot.getBoundingClientRect();
    const elementRect = element.getBoundingClientRect();
    const targetTop = scrollRoot.scrollTop + (elementRect.top - scrollRootRect.top) - Math.max(48, scrollRootRect.height * 0.2);

    scrollRoot.scrollTo({
        top: Math.max(targetTop, 0),
        behavior: "smooth"
    });
}

function replaceLocationHash(targetWindow, anchorId) {
    const encodedAnchorId = normalizeAnchorId(anchorId);
    const url = new URL(targetWindow.location.href);
    const currentHash = normalizeAnchorId(targetWindow.location.hash);
    if (currentHash === encodedAnchorId) {
        return;
    }

    url.hash = encodedAnchorId ? `#${encodedAnchorId}` : "";
    targetWindow.history.replaceState(targetWindow.history.state, "", url);
}

function normalizeAnchorId(anchorId) {
    if (!anchorId) {
        return null;
    }

    const normalized = anchorId.startsWith("#")
        ? anchorId.slice(1)
        : anchorId;

    return normalized || null;
}

function resolveHeadingElement(markdownBody, anchorId) {
    const candidates = [
        anchorId,
        decodeAnchorId(anchorId)
    ].filter(Boolean);

    for (const candidate of candidates) {
        const element = markdownBody.querySelector(`#${CSS.escape(candidate)}`);
        if (element) {
            return element;
        }
    }

    return null;
}

function buildNormalizedTextMap(root, searchableRoots) {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
        acceptNode(node) {
            if (!node.textContent || !searchableRoots.some(element => element.contains(node))) {
                return NodeFilter.FILTER_REJECT;
            }

            const parentElement = node.parentElement;
            if (!parentElement || parentElement.closest('[data-markdown-search-ignore="true"]')) {
                return NodeFilter.FILTER_REJECT;
            }

            const tagName = parentElement.tagName;
            if (tagName === "SCRIPT" || tagName === "STYLE") {
                return NodeFilter.FILTER_REJECT;
            }

            return NodeFilter.FILTER_ACCEPT;
        }
    });

    const indexMap = [];
    let text = "";
    let previousWasWhitespace = true;
    let previousRoot = null;

    while (walker.nextNode()) {
        const node = walker.currentNode;
        const value = node.textContent ?? "";
        const currentRoot = searchableRoots.find(element => element.contains(node));

        // The title and authored body are separate text regions. Their intervening
        // breadcrumbs, summary, and inspector are deliberately outside the search map.
        if (previousRoot && currentRoot !== previousRoot && !previousWasWhitespace) {
            text += " ";
            indexMap.push({ node, offset: 0 });
            previousWasWhitespace = true;
        }
        previousRoot = currentRoot;

        for (let index = 0; index < value.length; index++) {
            const character = value[index];
            if (/\s/.test(character)) {
                if (!previousWasWhitespace) {
                    text += " ";
                    indexMap.push({ node, offset: index });
                    previousWasWhitespace = true;
                }

                continue;
            }

            text += character;
            indexMap.push({ node, offset: index });
            previousWasWhitespace = false;
        }
    }

    let trimStart = 0;
    while (trimStart < text.length && text[trimStart] === " ") {
        trimStart++;
    }

    let trimEnd = text.length;
    while (trimEnd > trimStart && text[trimEnd - 1] === " ") {
        trimEnd--;
    }

    return {
        text: text.slice(trimStart, trimEnd),
        indexMap: indexMap.slice(trimStart, trimEnd)
    };
}

function resolveSearchWindow(contentRoot, textMap, anchorId, headingLevel) {
    const windowRange = {
        start: 0,
        end: textMap.text.length
    };

    if (!anchorId) {
        return windowRange;
    }

    const anchorElement = resolveHeadingElement(contentRoot, anchorId);
    if (!anchorElement) {
        return windowRange;
    }

    const startIndex = findFirstIndexForElement(textMap, anchorElement);
    if (startIndex !== null) {
        windowRange.start = startIndex;
    }

    const boundaryHeading = findBoundaryHeading(contentRoot, anchorElement, headingLevel);
    if (!boundaryHeading) {
        return windowRange;
    }

    const endIndex = findFirstIndexForElement(textMap, boundaryHeading);
    if (endIndex !== null && endIndex > windowRange.start) {
        windowRange.end = endIndex;
    }

    return windowRange;
}

function findBoundaryHeading(contentRoot, anchorElement, headingLevel) {
    const headings = Array.from(contentRoot.querySelectorAll("h1, h2, h3, h4, h5, h6"));
    const anchorIndex = headings.indexOf(anchorElement);
    if (anchorIndex < 0) {
        return null;
    }

    const normalizedHeadingLevel = typeof headingLevel === "number"
        ? headingLevel
        : parseHeadingLevel(anchorElement);

    for (let index = anchorIndex + 1; index < headings.length; index++) {
        const candidate = headings[index];
        if (parseHeadingLevel(candidate) <= normalizedHeadingLevel) {
            return candidate;
        }
    }

    return null;
}

function parseHeadingLevel(element) {
    const match = /^H([1-6])$/i.exec(element.tagName);
    return match ? Number.parseInt(match[1], 10) : 6;
}

function findFirstIndexForElement(textMap, element) {
    for (let index = 0; index < textMap.indexMap.length; index++) {
        if (element.contains(textMap.indexMap[index].node)) {
            return index;
        }
    }

    return null;
}

function locateSearchMatch(text, searchWindow, matchedText, prefixContext, suffixContext) {
    const occurrences = findOccurrences(text, matchedText, searchWindow.start, searchWindow.end);
    if (occurrences.length === 0) {
        return null;
    }

    let best = null;

    for (const start of occurrences) {
        const end = start + matchedText.length;
        let score = 0;

        if (prefixContext) {
            const actualPrefix = text.slice(Math.max(searchWindow.start, start - prefixContext.length), start);
            score += actualPrefix.endsWith(prefixContext)
                ? 1000
                : commonSuffixLength(actualPrefix, prefixContext) * 12;
        }

        if (suffixContext) {
            const actualSuffix = text.slice(end, Math.min(searchWindow.end, end + suffixContext.length));
            score += actualSuffix.startsWith(suffixContext)
                ? 1000
                : commonPrefixLength(actualSuffix, suffixContext) * 12;
        }

        score -= Math.abs(start - searchWindow.start) * 0.05;

        if (!best || score > best.score) {
            best = { start, end, score };
        }
    }

    return best;
}

function findOccurrences(text, value, start, end) {
    if (!text || !value || end <= start) {
        return [];
    }

    const matches = [];
    let fromIndex = start;

    while (fromIndex < end) {
        const index = indexOfIgnoreCase(text, value, fromIndex);
        if (index < 0 || index + value.length > end) {
            break;
        }

        matches.push(index);
        fromIndex = index + Math.max(value.length, 1);
    }

    return matches;
}

function indexOfIgnoreCase(text, value, startIndex) {
    return text.toLocaleLowerCase().indexOf(value.toLocaleLowerCase(), startIndex);
}

function commonPrefixLength(left, right) {
    const maxLength = Math.min(left.length, right.length);

    for (let index = 0; index < maxLength; index++) {
        if (left[index] !== right[index]) {
            return index;
        }
    }

    return maxLength;
}

function commonSuffixLength(left, right) {
    const maxLength = Math.min(left.length, right.length);

    for (let offset = 0; offset < maxLength; offset++) {
        if (left[left.length - offset - 1] !== right[right.length - offset - 1]) {
            return offset;
        }
    }

    return maxLength;
}

function wrapMatchRange(textMap, start, end) {
    const startMap = textMap.indexMap[start];
    const endMap = textMap.indexMap[end - 1];

    if (!startMap || !endMap) {
        return null;
    }

    const range = document.createRange();
    range.setStart(startMap.node, startMap.offset);
    range.setEnd(endMap.node, endMap.offset + 1);

    const mark = document.createElement("mark");
    mark.setAttribute("data-markdown-search-hit", "true");
    mark.className = "mo-markdown-search-hit";

    try {
        range.surroundContents(mark);
    }
    catch {
        const fragment = range.extractContents();
        mark.appendChild(fragment);
        range.insertNode(mark);
    }

    return mark;
}

function decodeAnchorId(anchorId) {
    try {
        return decodeURIComponent(anchorId);
    }
    catch {
        return anchorId;
    }
}
