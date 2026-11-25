const scrollListenerMap = new WeakMap();
const downloadOwners = new Map();
const retiredDownloadOwners = new Set();

export async function scrollToBottom(element) {
    if (!element) {
        return;
    }

    try {
        await new Promise(resolve => requestAnimationFrame(() => {
            requestAnimationFrame(() => {
                if (element.isConnected) element.scrollTop = element.scrollHeight;
                resolve();
            });
        }));
    } catch (error) {
        console.error("Failed to scroll log viewer", error);
    }
}

export async function copyToClipboard(text) {
    if (text === null || text === undefined) {
        return false;
    }

    try {
        if (navigator.clipboard?.writeText) {
            await navigator.clipboard.writeText(text);
            return true;
        }

        const host = document.body ?? document.documentElement;
        if (!host) {
            return false;
        }

        const textArea = document.createElement("textarea");
        textArea.value = text;
        textArea.style.position = "fixed";
        textArea.style.left = "-999999px";
        textArea.style.top = "-999999px";
        host.appendChild(textArea);
        textArea.focus();
        textArea.select();

        try {
            return document.execCommand("copy");
        } catch (error) {
            console.error("Failed to copy text using fallback method", error);
            return false;
        } finally {
            if (textArea.isConnected) {
                textArea.remove();
            }
        }
    } catch (error) {
        console.error("Failed to copy text to clipboard", error);
        return false;
    }
}

export function getSelectedText() {
    try {
        return window.getSelection()?.toString().trim() ?? "";
    } catch (error) {
        console.error("Failed to get selected text", error);
        return "";
    }
}

export function getRowMenuContext(container, lineIndex) {
    const row = container?.querySelector('[data-line-index="' + lineIndex + '"]');
    const trigger = row?.querySelector(".log-row-menu-trigger");
    if (!trigger) {
        return null;
    }

    const bounds = trigger.getBoundingClientRect();
    const selection = window.getSelection();
    const selectedText = selection?.rangeCount > 0 && row.contains(selection.getRangeAt(0).commonAncestorContainer)
        ? selection.toString().trim()
        : "";
    return { pageX: bounds.right + window.scrollX, pageY: bounds.bottom + window.scrollY + 4, selection: selectedText };
}

export function scrollToTop(element) {
    if (!element) {
        return;
    }

    try {
        element.scrollTop = 0;
    } catch (error) {
        console.error("Failed to scroll to top", error);
    }
}

export function scrollToLineIndex(container, lineIndex) {
    if (!container) {
        return;
    }

    try {
        const targetElement = container.querySelector(`[data-line-index="${lineIndex}"]`);
        if (!targetElement) {
            return;
        }

        const containerRect = container.getBoundingClientRect();
        const elementRect = targetElement.getBoundingClientRect();
        const containerScrollTop = container.scrollTop;
        const elementTop = elementRect.top - containerRect.top + containerScrollTop;
        const centerOffset = (containerRect.height - elementRect.height) / 2;
        const scrollTo = elementTop - centerOffset;

        container.scrollTo({
            top: scrollTo,
            behavior: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth"
        });
    } catch (error) {
        console.error("Failed to scroll to line index", error);
    }
}

export async function addScrollTopListener(container, dotNetRef, methodName, threshold = 50) {
    if (!container || !dotNetRef) {
        return;
    }

    await removeScrollListener(container);

    const session = {
        acceptingCallbacks: true,
        isLoading: false,
        lastScrollTop: container.scrollTop,
        pendingCallbacks: new Set(),
        handleScroll: null
    };

    const handleScroll = () => {
        const scrollTop = container.scrollTop;
        const isScrollingUp = scrollTop < session.lastScrollTop;
        session.lastScrollTop = scrollTop;

        if (!session.acceptingCallbacks || !isScrollingUp || scrollTop > threshold || session.isLoading) {
            return;
        }

        session.isLoading = true;
        const callback = dotNetRef.invokeMethodAsync(methodName)
            .catch(error => {
                if (session.acceptingCallbacks) {
                    console.error("Error invoking load more:", error);
                }
            })
            .finally(() => {
                session.isLoading = false;
                session.pendingCallbacks.delete(callback);
            });

        session.pendingCallbacks.add(callback);
    };

    session.handleScroll = handleScroll;
    container.addEventListener("scroll", handleScroll, { passive: true });
    scrollListenerMap.set(container, session);
}

export async function removeScrollListener(container) {
    if (!container) {
        return;
    }

    const session = scrollListenerMap.get(container);
    if (!session) {
        return;
    }

    session.acceptingCallbacks = false;
    container.removeEventListener("scroll", session.handleScroll);
    if (scrollListenerMap.get(container) === session) {
        scrollListenerMap.delete(container);
    }

    await Promise.allSettled([...session.pendingCallbacks]);
    session.pendingCallbacks.clear();
}

export function getScrollAnchor(container) {
    if (!container) return null;
    const owner = container.getBoundingClientRect();
    for (const line of container.querySelectorAll("[data-line-index]")) {
        const rect = line.getBoundingClientRect();
        if (rect.bottom > owner.top && rect.top < owner.bottom) {
            return { lineIndex: Number(line.dataset.lineIndex), absoluteLineNumber: Number(line.dataset.absoluteLineNumber), offset: rect.top - owner.top };
        }
    }
    return null;
}

// Native Blazor scroll events own capture admission; this function adds no listeners or timers.
export function getViewerPosition(container) {
    if (!container?.isConnected) return null;
    const owner = container.getBoundingClientRect();
    const ledger = container.closest(".log-viewer-wrapper");
    for (const line of container.querySelectorAll("[data-line-index]")) {
        const rect = line.getBoundingClientRect();
        if (rect.bottom > owner.top && rect.top < owner.bottom) {
            return {
                lineIndex: Number(line.dataset.lineIndex),
                absoluteLineNumber: line.dataset.absoluteLineNumber ?? null,
                offset: rect.top - owner.top,
                atBottom: container.scrollHeight - container.clientHeight - container.scrollTop <= 2,
                generation: ledger?.dataset.sourceGeneration ?? null,
                revision: ledger?.dataset.positionRevision ?? null
            };
        }
    }
    return null;
}

export async function preserveScrollPosition(container, absoluteLineNumber, offset = 0) {
    if (!container || absoluteLineNumber === null || absoluteLineNumber === undefined) {
        return false;
    }

    await new Promise(resolve => requestAnimationFrame(() => {
        requestAnimationFrame(() => {
            try {
                if (!container.isConnected) return;
                const anchorElement = container.querySelector(
                    `[data-absolute-line-number="${absoluteLineNumber}"]`);

                if (anchorElement) {
                    const containerBox = container.getBoundingClientRect();
                    const anchorBox = anchorElement.getBoundingClientRect();
                    container.scrollTop += anchorBox.top - containerBox.top - offset;
                }
            } catch (error) {
                console.error("Failed to preserve scroll position", error);
            } finally {
                resolve();
            }
        });
    }));

    return true;
}

// Fetch the attachment before downloading a local Blob so HTTP deployments do not
// hand an insecure network URL to the browser's download manager.
export async function downloadFile(url, owner) {
    if (!url) {
        return false;
    }

    const host = document.body ?? document.documentElement;
    if (!host) {
        return false;
    }

    if (owner && retiredDownloadOwners.has(owner)) return false;
    const controller = new AbortController();
    const transfers = downloadOwners.get(owner) ?? new Set();
    transfers.add(controller);
    if (owner) downloadOwners.set(owner, transfers);
    try {
        const response = await fetch(url, {
            credentials: "same-origin", mode: "same-origin", redirect: "error", signal: controller.signal
        });
        if (!response.ok) throw new Error(`Log download failed: HTTP ${response.status} ${response.statusText}`.trim());
        const blob = await response.blob();
        if (controller.signal.aborted || (owner && retiredDownloadOwners.has(owner))) return false;
        return downloadBlob(blob, getDownloadFileName(response.headers.get("Content-Disposition")), host);
    } catch (error) {
        if (controller.signal.aborted) return false;
        throw error;
    } finally {
        transfers.delete(controller);
        if (owner && transfers.size === 0) downloadOwners.delete(owner);
    }
}

export function cancelDownloads(owner) {
    retiredDownloadOwners.add(owner);
    for (const controller of downloadOwners.get(owner) ?? []) controller.abort();
}

export function releaseDownloadOwner(owner) {
    downloadOwners.delete(owner);
    retiredDownloadOwners.delete(owner);
}

export function downloadContent(name, contentType, bytes) {
    const host = document.body ?? document.documentElement;
    if (!host) return false;
    return downloadBlob(new Blob([bytes], { type: contentType }), name, host);
}

function downloadBlob(blob, name, host) {
    const anchor = document.createElement("a");
    anchor.download = name;
    anchor.rel = "noopener";
    anchor.style.display = "none";
    const objectUrl = URL.createObjectURL(blob);
    anchor.href = objectUrl;

    try {
        host.appendChild(anchor);
        anchor.click();
        return true;
    } finally {
        if (anchor.isConnected) {
            anchor.remove();
        }

        // Allow the browser to consume the URL before releasing the attachment.
        setTimeout(() => URL.revokeObjectURL(objectUrl), 1000);
    }
}

function getDownloadFileName(contentDisposition) {
    // ASP.NET Core emits filename* for Unicode names alongside an ASCII fallback.
    const encodedName = /(?:^|;)\s*filename\*\s*=\s*UTF-8'[^']*'([^;]*)/i.exec(contentDisposition ?? "");
    if (encodedName) {
        try {
            const fileName = decodeURIComponent(encodedName[1].trim());
            if (fileName) {
                return fileName;
            }
        } catch {
            // A malformed extended parameter must not hide a usable plain filename.
        }
    }

    const plainName = /(?:^|;)\s*filename\s*=\s*(?:"((?:[^"\\]|\\.)*)"|([^;]*))/i.exec(contentDisposition ?? "");
    return plainName?.[1]?.replace(/\\(.)/g, "$1") || plainName?.[2]?.trim() || "logs.log";
}
