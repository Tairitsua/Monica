let mermaidLoadPromise;
let renderQueue = Promise.resolve();

function getMermaidSource() {
    return '/_content/Monica.UI/lib/mermaid/mermaid.min.js';
}

async function ensureMermaid() {
    if (window.mermaid) {
        return window.mermaid;
    }

    if (!mermaidLoadPromise) {
        mermaidLoadPromise = new Promise((resolve, reject) => {
            const existingScript = document.querySelector('script[data-mo-markdown-mermaid]');
            if (existingScript) {
                existingScript.addEventListener('load', () => resolve(window.mermaid), { once: true });
                existingScript.addEventListener('error', () => reject(new Error('Failed to load Mermaid runtime.')), { once: true });
                return;
            }

            const script = document.createElement('script');
            script.src = getMermaidSource();
            script.async = true;
            script.dataset.moMarkdownMermaid = 'true';
            script.onload = () => {
                if (window.mermaid) {
                    resolve(window.mermaid);
                    return;
                }

                reject(new Error('Mermaid runtime loaded without exposing the global API.'));
            };
            script.onerror = () => reject(new Error('Failed to load Mermaid runtime.'));
            document.head.appendChild(script);
        });
    }

    return mermaidLoadPromise;
}

function normalizeDiagramText(definition) {
    return (definition ?? '').replace(/\r\n/g, '\n').trim();
}

function getFontFamily(theme) {
    if (Array.isArray(theme?.fontFamily) && theme.fontFamily.length > 0) {
        return theme.fontFamily.join(', ');
    }

    return getComputedStyle(document.body).fontFamily;
}

function createConfig(theme) {
    return {
        startOnLoad: false,
        securityLevel: 'strict',
        // Nodes use the global setting; edges and clusters use the flowchart setting.
        htmlLabels: false,
        flowchart: { htmlLabels: false },
        theme: 'base',
        fontFamily: getFontFamily(theme),
        themeVariables: {
            primaryColor: theme.primary,
            primaryTextColor: theme.textPrimary,
            primaryBorderColor: theme.primary,
            secondaryColor: theme.secondary,
            secondaryTextColor: theme.textPrimary,
            secondaryBorderColor: theme.secondary,
            tertiaryColor: theme.surface,
            tertiaryTextColor: theme.textPrimary,
            tertiaryBorderColor: theme.primary,
            mainBkg: theme.surface,
            secondBkg: theme.background,
            tertiaryBkg: theme.surface,
            lineColor: theme.primary,
            textColor: theme.textPrimary,
            nodeTextColor: theme.textPrimary,
            edgeLabelBackground: theme.surface,
            clusterBkg: theme.background,
            clusterBorder: theme.primary,
            titleColor: theme.textPrimary,
            actorBkg: theme.surface,
            actorBorder: theme.primary,
            actorTextColor: theme.textPrimary,
            signalColor: theme.primary,
            signalTextColor: theme.textPrimary,
            labelBoxBkgColor: theme.surface,
            labelBoxBorderColor: theme.primary,
            labelTextColor: theme.textPrimary,
            activationBkgColor: theme.background,
            activationBorderColor: theme.primary,
            sectionBkgColor: theme.background,
            altSectionBkgColor: theme.surface,
            c0: theme.primary,
            c1: theme.secondary,
            c2: theme.info,
            c3: theme.success,
            c4: theme.warning,
            c5: theme.error,
            pie1: theme.primary,
            pie2: theme.secondary,
            pie3: theme.info,
            pie4: theme.success,
            pie5: theme.warning,
            pie6: theme.error,
            pie7: theme.background,
            pie8: theme.surface,
            pieOuterStrokeColor: theme.primary,
            pieTitleTextSize: '22px'
        }
    };
}

export async function clearMermaid(element) {
    if (element) {
        element.replaceChildren();
    }
}

function createDiagramId() {
    if (globalThis.crypto && typeof globalThis.crypto.randomUUID === 'function') {
        return `mo-markdown-mermaid-${globalThis.crypto.randomUUID()}`;
    }

    const fallback = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    return `mo-markdown-mermaid-${fallback}`;
}

function fitRenderedDiagram(element) {
    const svg = element.querySelector('svg');
    if (!svg) {
        return;
    }

    // Measure after insertion: browser engines can report stale bounds while Mermaid builds its SVG.
    const { x, y, width, height } = svg.getBBox();
    if (![x, y, width, height].every(Number.isFinite) || width <= 0 || height <= 0) {
        return;
    }

    const padding = 8;
    svg.setAttribute('viewBox', `${x - padding} ${y - padding} ${width + padding * 2} ${height + padding * 2}`);
    svg.setAttribute('width', '100%');
    svg.removeAttribute('height');
    svg.style.maxWidth = `${width + padding * 2}px`;
}

function renderDiagram(definition, theme, isCurrent) {
    // Mermaid has global configuration: serialize configuration and rendering across Markdown blocks.
    const pending = renderQueue.then(async () => {
        if (!isCurrent()) return null;
        const mermaid = await ensureMermaid();
        if (!isCurrent()) return null;
        mermaid.initialize(createConfig(theme));
        return mermaid.render(createDiagramId(), definition);
    });
    renderQueue = pending.catch(() => {});
    return pending;
}

export async function renderMermaid(element, definition, theme, isCurrent = () => true) {
    if (!element || !isCurrent()) {
        return false;
    }

    const normalizedDefinition = normalizeDiagramText(definition);
    if (!normalizedDefinition) {
        element.replaceChildren();
        return false;
    }

    const result = await renderDiagram(normalizedDefinition, theme, isCurrent);
    if (!result || !isCurrent()) return false;

    // This is Mermaid's strict-mode sanitized output, never caller-supplied HTML.
    element.innerHTML = result.svg;
    fitRenderedDiagram(element);
    if (typeof result.bindFunctions === 'function') {
        result.bindFunctions(element);
    }
    return true;
}

function cloneDiagram(source) {
    const clone = source.cloneNode(true);
    const prefix = `${createDiagramId()}-reader-`;
    const ids = new Map();
    for (const element of [clone, ...clone.querySelectorAll('[id]')]) {
        if (!element.id) continue;
        const previous = element.id;
        element.id = `${prefix}${ids.size}`;
        ids.set(previous, element.id);
    }
    const replaceUrls = value => value.replace(/url\(\s*(['"]?)#([^)'"\s]+)\1\s*\)/g,
        (match, quote, id) => ids.has(id) ? `url(#${ids.get(id)})` : match);
    for (const element of [clone, ...clone.querySelectorAll('*')]) {
        for (const attribute of [...element.attributes]) {
            if (attribute.name === 'id') continue;
            let value = replaceUrls(attribute.value);
            if (['href', 'xlink:href'].includes(attribute.name) && value.startsWith('#')) {
                value = ids.has(value.slice(1)) ? `#${ids.get(value.slice(1))}` : value;
            } else if (['aria-labelledby', 'aria-describedby', 'for'].includes(attribute.name)) {
                value = value.split(/\s+/).map(id => ids.get(id) ?? id).join(' ');
            }
            if (value !== attribute.value) element.setAttribute(attribute.name, value);
        }
        if (element.localName === 'style') {
            let css = replaceUrls(element.textContent);
            // Mermaid scopes generated CSS with the root SVG ID and references marker/clip IDs.
            for (const [previous, replacement] of ids) {
                const escaped = previous.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
                css = css.replace(new RegExp(`#${escaped}(?![\\w-])`, 'g'), `#${replacement}`);
            }
            element.textContent = css;
        }
    }
    return clone;
}

/** Owns browser producers only; the Blazor renderer owns the dialog and all DOM removal. */
export function connectReader(root, surface, dialog) {
    const viewport = dialog.querySelector('[data-mo-mermaid-viewport]');
    const canvas = dialog.querySelector('[data-mo-mermaid-canvas]');
    const zoomLabel = dialog.querySelector('[data-mo-mermaid-zoom]');
    const zoomOut = dialog.querySelector('[data-mo-mermaid-action="out"]');
    const zoomIn = dialog.querySelector('[data-mo-mermaid-action="in"]');
    const closeButton = dialog.querySelector('[data-mo-mermaid-action="close"]');
    const listeners = new AbortController();
    const MAX_SCALE = 8;
    const ZOOM_FACTOR = 1.2;
    let retired = false;
    let generation = 0;
    let width = 0, height = 0;
    let scale = 1, x = 0, y = 0;
    let lastWidth = 0, lastHeight = 0;
    let fitted = true;
    let opener;
    let pointer;
    let focusFrame = 0;

    function listen(element, event, handler, options = {}) {
        element.addEventListener(event, handler, { ...options, signal: listeners.signal });
    }
    function active() { return !retired && root.isConnected; }
    function hasLink(target) { return target instanceof Element && Boolean(target.closest('a')); }
    function fitScale() {
        return Math.min(MAX_SCALE, Math.max(0.000001,
            Math.min(Math.max(1, viewport.clientWidth - 48) / width,
                Math.max(1, viewport.clientHeight - 48) / height)));
    }
    function minimumScale() { return Math.min(0.05, fitScale()); }
    function paint() {
        canvas.style.transform = `translate(${x}px, ${y}px) scale(${scale})`;
        // Tiny fitted diagrams still need a useful, nonzero percentage indicator.
        zoomLabel.textContent = `${scale < 0.1 ? Number((scale * 100).toPrecision(3)) : Math.round(scale * 100)}%`;
        zoomOut.disabled = scale <= minimumScale() + 0.000001;
        zoomIn.disabled = scale >= MAX_SCALE;
    }
    function center(nextScale) {
        scale = nextScale;
        x = (viewport.clientWidth - width * scale) / 2;
        y = (viewport.clientHeight - height * scale) / 2;
        paint();
    }
    function fit() {
        if (!width || !height) return;
        fitted = true;
        center(fitScale());
    }
    function zoom(nextScale, anchorX = viewport.clientWidth / 2, anchorY = viewport.clientHeight / 2) {
        const next = Math.min(MAX_SCALE, Math.max(minimumScale(), nextScale));
        x = anchorX - (anchorX - x) * next / scale;
        y = anchorY - (anchorY - y) * next / scale;
        scale = next;
        fitted = false;
        paint();
    }
    function stopPan() {
        if (!pointer) return;
        const id = pointer.id;
        pointer = undefined;
        if (viewport.hasPointerCapture(id)) viewport.releasePointerCapture(id);
        viewport.classList.remove('is-panning');
    }
    function refreshClone() {
        const source = surface.querySelector('svg');
        if (!source) return false;
        const bounds = source.viewBox.baseVal;
        if (bounds.width <= 0 || bounds.height <= 0) return false;
        const clone = cloneDiagram(source);
        width = bounds.width;
        height = bounds.height;
        clone.setAttribute('width', width);
        clone.setAttribute('height', height);
        clone.style.width = `${width}px`;
        clone.style.height = `${height}px`;
        clone.style.maxWidth = 'none';
        canvas.style.width = `${width}px`;
        canvas.style.height = `${height}px`;
        // The source was already sanitized/rendered. Cloning preserves direct SVG links without
        // rerendering Mermaid or binding a second set of handlers against the source diagram.
        canvas.replaceChildren(clone);
        return true;
    }
    function open(trigger) {
        if (!active() || dialog.open || !refreshClone()) return;
        opener = trigger;
        dialog.showModal();
        lastWidth = viewport.clientWidth;
        lastHeight = viewport.clientHeight;
        fit();
        closeButton.focus({ preventScroll: true });
    }
    function close() {
        if (dialog.open) dialog.close();
    }
    function restoreFocus() {
        stopPan();
        cancelAnimationFrame(focusFrame);
        focusFrame = requestAnimationFrame(() => {
            if (active()) (opener?.isConnected ? opener : surface).focus({ preventScroll: true });
        });
    }
    function action(name, trigger) {
        if (name === 'open') { open(trigger); return; }
        if (!dialog.open) return;
        if (name === 'close') close();
        else if (name === 'in') zoom(scale * ZOOM_FACTOR);
        else if (name === 'out') zoom(scale / ZOOM_FACTOR);
        else if (name === 'fit') fit();
        else if (name === 'reset') { fitted = false; center(1); }
    }
    listen(root, 'click', event => {
        const button = event.target.closest?.('button[data-mo-mermaid-action]');
        if (button && root.contains(button)) { action(button.dataset.moMermaidAction, button); return; }
        if (surface.contains(event.target) && !hasLink(event.target)) open(surface);
    });
    listen(surface, 'keydown', event => {
        if (event.target !== surface || !['Enter', ' '].includes(event.key)) return;
        event.preventDefault();
        open(surface);
    });
    listen(dialog, 'click', event => {
        if (event.target !== dialog) return;
        const bounds = dialog.getBoundingClientRect();
        if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom) close();
    });
    listen(dialog, 'cancel', event => { event.preventDefault(); close(); });
    listen(dialog, 'close', restoreFocus);
    listen(dialog, 'keydown', event => {
        if (!dialog.open || event.altKey || event.ctrlKey || event.metaKey) return;
        if (event.key === 'Tab') {
            const focusable = [...dialog.querySelectorAll('button:not(:disabled), a[href], [tabindex="0"]')]
                .filter(element => element.tabIndex >= 0 && element.getClientRects().length > 0);
            const first = focusable[0], last = focusable.at(-1);
            if (event.shiftKey && (document.activeElement === first || !dialog.contains(document.activeElement))) {
                event.preventDefault(); last?.focus();
            } else if (!event.shiftKey && (document.activeElement === last || !dialog.contains(document.activeElement))) {
                event.preventDefault(); first?.focus();
            }
            return;
        }
        const key = event.key.toLowerCase();
        if (key === 'escape') { close(); event.stopPropagation(); }
        else if (key === '+' || key === '=') zoom(scale * ZOOM_FACTOR);
        else if (key === '-' || key === '_') zoom(scale / ZOOM_FACTOR);
        else if (key === '0') { fitted = false; center(1); }
        else if (key === 'f') fit();
        else if (key.startsWith('arrow')) {
            const distance = event.shiftKey ? 80 : 40;
            x += key === 'arrowleft' ? distance : key === 'arrowright' ? -distance : 0;
            y += key === 'arrowup' ? distance : key === 'arrowdown' ? -distance : 0;
            fitted = false;
            paint();
        } else if (!['pageup', 'pagedown', 'home', 'end'].includes(key) && !(key === ' ' && event.target === viewport)) return;
        event.preventDefault();
    });
    listen(viewport, 'wheel', event => {
        event.preventDefault();
        const bounds = viewport.getBoundingClientRect();
        const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? viewport.clientHeight : 1);
        zoom(scale * Math.exp(-Math.max(-100, Math.min(100, delta)) * 0.002), event.clientX - bounds.left, event.clientY - bounds.top);
    }, { passive: false });
    listen(viewport, 'pointerdown', event => {
        if (event.button !== 0 || pointer || hasLink(event.target)) return;
        pointer = { id: event.pointerId, x: event.clientX, y: event.clientY };
        viewport.setPointerCapture(event.pointerId);
        viewport.classList.add('is-panning');
        viewport.focus({ preventScroll: true });
        event.preventDefault();
    });
    listen(viewport, 'pointermove', event => {
        if (pointer?.id !== event.pointerId) return;
        x += event.clientX - pointer.x;
        y += event.clientY - pointer.y;
        pointer.x = event.clientX;
        pointer.y = event.clientY;
        fitted = false;
        paint();
    });
    listen(viewport, 'pointerup', stopPan);
    listen(viewport, 'pointercancel', stopPan);
    listen(viewport, 'lostpointercapture', stopPan);
    listen(viewport, 'dblclick', event => {
        if (!hasLink(event.target)) { event.preventDefault(); fit(); }
    });
    // A native dialog makes background content inert but does not lock its scroll position.
    // Cancel background gestures instead of changing body styles shared with other dialogs.
    listen(document, 'wheel', event => {
        if (dialog.open && !viewport.contains(event.target)) event.preventDefault();
    }, { passive: false });
    listen(document, 'touchmove', event => {
        if (dialog.open) event.preventDefault();
    }, { passive: false });

    const resizeObserver = new ResizeObserver(() => {
        if (!active() || !dialog.open) return;
        if (fitted) fit();
        else {
            x += (viewport.clientWidth - lastWidth) / 2;
            y += (viewport.clientHeight - lastHeight) / 2;
            paint();
        }
        lastWidth = viewport.clientWidth;
        lastHeight = viewport.clientHeight;
    });
    resizeObserver.observe(viewport);
    const removalObserver = new MutationObserver(() => { if (!root.isConnected) dispose(); });
    removalObserver.observe(document.body, { childList: true, subtree: true });
    function dispose() {
        if (retired) return;
        retired = true;
        generation++;
        listeners.abort();
        resizeObserver.disconnect();
        removalObserver.disconnect();
        cancelAnimationFrame(focusFrame);
        // Do not close/remove/clear DOM here: this can run after the Blazor renderer removed it.
    }
    return {
        async render(definition, theme) {
            if (!active()) return false;
            const version = ++generation;
            const isCurrent = () => active() && version === generation;
            try {
                const rendered = await renderMermaid(surface, definition, theme, isCurrent);
                if (!isCurrent()) return false;
                if (!rendered) close();
                else if (dialog.open) { refreshClone(); if (fitted) fit(); else paint(); }
                return rendered;
            } catch (error) {
                if (isCurrent()) { surface.replaceChildren(); close(); }
                throw error;
            }
        },
        dispose
    };
}
