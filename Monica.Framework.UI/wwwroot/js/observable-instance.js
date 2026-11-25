export function reveal(root, selector, block, focus = false) {
    const item = root?.querySelector(selector);
    item?.scrollIntoView({ behavior: "instant", block, inline: "nearest" });
    if (focus) item?.focus({ preventScroll: true });
}

export async function revealRegistry(root) {
    await document.fonts.ready;
    if (root?.isConnected && root.querySelector(".is-registry-anchor"))
        reveal(root, "#oi-registry", "start");
}

export function initialize(root, receiver) {
    const scope = new AbortController();
    const signal = scope.signal;
    const interaction = { track: null, point: null, keyboardTrack: null, cursor: -1, plot: null, bucket: 0, drag: null };
    const targetOf = event => event.target instanceof Element ? event.target : null;
    const chartPoints = track => [...track.querySelectorAll("[data-oi-point]")];
    const chartBuckets = plot => [...plot.querySelectorAll("[data-oi-bucket]")];
    const clamp = (number, low, high) => Math.max(low, Math.min(number, high));

    function dispatch(name, ...args) {
        if (signal.aborted) return;
        receiver.invokeMethodAsync(name, ...args).catch(error => {
            if (!signal.aborted) console.error(error);
        });
    }

    function readout(chart, text) {
        const status = chart?.querySelector("[data-oi-status]");
        if (!status) return;
        const next = text || status.dataset.default;
        if (status.textContent !== next) status.textContent = next;
        status.closest(".oi-caption")?.classList.toggle("is-active", Boolean(text));
    }

    function showPoint(horizon, track, point, at) {
        const clock = horizon.querySelector("[data-oi-clock]");
        horizon.classList.toggle("oi-cursor-active", at !== undefined);
        horizon.style.setProperty("--oi-cursor", at === undefined ? "0%" : at + "%");
        if (clock) {
            clock.textContent = point?.dataset.time ?? "";
            clock.hidden = !clock.textContent;
        }
        if (interaction.track && interaction.track !== track)
            interaction.track.closest(".oi-hz-row")?.classList.remove("is-hover");
        track?.closest(".oi-hz-row")?.classList.toggle("is-hover", at !== undefined);
        interaction.track = track;
        interaction.point = point;
        readout(horizon, point?.dataset.hint ?? track?.dataset.hint);
    }

    function selectPoint(track, point) {
        const seq = point?.dataset.seq || track.dataset.exception;
        const tab = point?.dataset.tab || (seq ? "exceptions" : "current");
        dispatch("OpenChartEvidence", track.dataset.id, tab, seq || null);
    }

    function focusTrack(track, last = true) {
        const horizon = track.closest("[data-oi-horizon]");
        if (interaction.keyboardTrack !== track) {
            horizon.querySelector("[data-oi-track][tabindex='0']")?.setAttribute("tabindex", "-1");
            track.tabIndex = 0;
            interaction.keyboardTrack = track;
            interaction.cursor = last ? chartPoints(track).length - 1 : 0;
        }
        const point = chartPoints(track)[interaction.cursor];
        showPoint(horizon, track, point, point ? Number(point.dataset.position) : 100);
    }

    function pickTrack(event, track) {
        const rect = track.getBoundingClientRect();
        if (!rect.width) return;
        const at = clamp((event.clientX - rect.left) / rect.width * 100, 0, 100);
        let radius = 1400 / rect.width;
        let closest;
        for (const point of chartPoints(track)) {
            const distance = Math.abs(Number(point.dataset.position) - at);
            if (distance <= radius) {
                radius = distance;
                closest = point;
            }
        }
        showPoint(track.closest("[data-oi-horizon]"), track, closest, closest ? Number(closest.dataset.position) : at);
    }

    function trackKey(event, track) {
        const horizon = track.closest("[data-oi-horizon]");
        const tracks = [...horizon.querySelectorAll("[data-oi-track]")];
        if (event.key === "ArrowDown" || event.key === "ArrowUp") {
            const index = clamp(tracks.indexOf(track) + (event.key === "ArrowDown" ? 1 : -1), 0, tracks.length - 1);
            tracks[index].focus();
        } else if (["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) {
            const points = chartPoints(track);
            if (interaction.keyboardTrack !== track) focusTrack(track);
            interaction.cursor = event.key === "Home" ? 0 : event.key === "End" ? points.length - 1 :
                clamp(interaction.cursor + (event.key === "ArrowRight" ? 1 : -1), 0, points.length - 1);
            const point = points[interaction.cursor];
            showPoint(horizon, track, point, point ? Number(point.dataset.position) : 100);
        } else if (event.key === "Enter" || event.key === " ") {
            selectPoint(track, chartPoints(track)[interaction.cursor]);
        } else if (event.key === "Escape") {
            showPoint(horizon, null, null, undefined);
        } else {
            return;
        }
        event.preventDefault();
    }

    function plotPosition(event, plot) {
        const rect = plot.getBoundingClientRect();
        const pixel = clamp(event.clientX - rect.left, 0, rect.width);
        return { pixel, index: Math.min(chartBuckets(plot).length - 1, Math.floor(pixel / Math.max(1, rect.width) * chartBuckets(plot).length)) };
    }

    function showBuckets(plot, first, last = first, brushing = false) {
        const buckets = chartBuckets(plot);
        const scale = 100 / buckets.length;
        plot.style.setProperty("--oi-brush-start", first * scale + "%");
        plot.style.setProperty("--oi-brush-width", (last - first + 1) * scale + "%");
        plot.classList.add("oi-plot-active");
        plot.classList.toggle("oi-plot-brushing", brushing);
        plot.setAttribute("aria-valuenow", String(last + 1));
        plot.setAttribute("aria-valuetext", buckets[last].dataset.hint);
        readout(plot.closest("[data-oi-exceptions]"), buckets[last].dataset.hint);
    }

    function clearPlot(plot) {
        plot.classList.remove("oi-plot-active", "oi-plot-brushing");
        readout(plot.closest("[data-oi-exceptions]"));
    }

    function zoom(plot, first, last) {
        const buckets = chartBuckets(plot);
        if (first < 0 || last >= buckets.length) return;
        dispatch("SetChartWindow", buckets[first].dataset.from, buckets[last].dataset.to);
        clearPlot(plot);
    }

    function plotKey(event, plot) {
        if (interaction.plot !== plot) {
            interaction.plot = plot;
            interaction.bucket = 0;
        }
        const end = chartBuckets(plot).length - 1;
        switch (event.key) {
            case "ArrowLeft": case "ArrowDown": interaction.bucket = Math.max(0, interaction.bucket - 1); break;
            case "ArrowRight": case "ArrowUp": interaction.bucket = Math.min(end, interaction.bucket + 1); break;
            case "Home": interaction.bucket = 0; break;
            case "End": interaction.bucket = end; break;
            case "Enter": case " ":
                zoom(plot, interaction.bucket, interaction.bucket);
                event.preventDefault();
                return;
            case "Escape":
                dispatch("SetChartWindow", null, null);
                clearPlot(plot);
                event.preventDefault();
                return;
            default: return;
        }
        showBuckets(plot, interaction.bucket);
        event.preventDefault();
    }

    function matrixKey(event, mark) {
        const matrix = mark.closest("[data-oi-matrix]");
        const marks = [...matrix.querySelectorAll("[data-oi-mark]")];
        let next;
        switch (event.key) {
            case "Home": next = marks[0]; break;
            case "End": next = marks.at(-1); break;
            case "ArrowLeft": next = marks[marks.indexOf(mark) - 1]; break;
            case "ArrowRight": next = marks[marks.indexOf(mark) + 1]; break;
            case "ArrowUp": case "ArrowDown": {
                const row = mark.closest("tr");
                const neighbor = event.key === "ArrowDown" ? row.nextElementSibling : row.previousElementSibling;
                const column = mark.closest("td").cellIndex;
                const choices = [...(neighbor?.querySelectorAll("[data-oi-mark]") ?? [])];
                next = choices.reduce((best, item) => !best || Math.abs(item.closest("td").cellIndex - column) <
                    Math.abs(best.closest("td").cellIndex - column) ? item : best, null);
                break;
            }
            default: return;
        }
        event.preventDefault();
        if (!next) return;
        mark.tabIndex = -1;
        next.tabIndex = 0;
        next.focus();
    }

    root.addEventListener("keydown", event => {
        const element = targetOf(event);
        if (element?.hasAttribute("data-oi-track")) trackKey(event, element);
        else if (element?.hasAttribute("data-oi-plot")) plotKey(event, element);
        else if (element?.hasAttribute("data-oi-mark")) matrixKey(event, element);
    }, { signal });

    root.addEventListener("focusin", event => {
        const element = targetOf(event);
        if (element?.hasAttribute("data-oi-track")) focusTrack(element);
        else if (element?.hasAttribute("data-oi-plot")) {
            interaction.plot = element;
            interaction.bucket = 0;
            showBuckets(element, 0);
        }
    }, { signal });

    root.addEventListener("focusout", event => {
        const element = targetOf(event);
        const horizon = element?.closest("[data-oi-horizon]");
        if (horizon && !event.relatedTarget?.closest?.("[data-oi-track]")) showPoint(horizon, null, null, undefined);
        if (element?.hasAttribute("data-oi-plot") && !interaction.drag) clearPlot(element);
    }, { signal });

    root.addEventListener("pointermove", event => {
        const element = targetOf(event);
        const track = element?.closest("[data-oi-track]");
        const flag = element?.closest("[data-oi-burst]");
        const plot = element?.closest("[data-oi-plot]");
        if (track) pickTrack(event, track);
        else if (flag) {
            const chart = flag.closest("[data-oi-horizon]");
            showPoint(chart, null, null, Number(flag.dataset.oiBurst));
            readout(chart, flag.dataset.oiReadout);
        } else if (plot) {
            const position = plotPosition(event, plot);
            const start = interaction.drag?.start;
            interaction.plot = plot;
            interaction.bucket = position.index;
            if (start && Math.abs(start.pixel - position.pixel) > 6)
                showBuckets(plot, Math.min(start.index, position.index), Math.max(start.index, position.index), true);
            else showBuckets(plot, position.index);
        }
    }, { signal });

    root.addEventListener("pointerout", event => {
        const element = targetOf(event);
        const related = event.relatedTarget;
        const track = element?.closest("[data-oi-track], [data-oi-burst]");
        const plot = element?.closest("[data-oi-plot]");
        if (track && !track.contains(related) && document.activeElement !== track)
            showPoint(track.closest("[data-oi-horizon]"), null, null, undefined);
        if (plot && !plot.contains(related) && !interaction.drag && document.activeElement !== plot) clearPlot(plot);
    }, { signal });

    root.addEventListener("click", event => {
        const track = targetOf(event)?.closest("[data-oi-track]");
        if (!track) return;
        if (event.detail) pickTrack(event, track);
        selectPoint(track, interaction.track === track ? interaction.point : null);
    }, { signal });

    root.addEventListener("pointerdown", event => {
        const plot = targetOf(event)?.closest("[data-oi-plot]");
        if (!plot || event.button !== 0) return;
        plot.focus({ preventScroll: true });
        plot.setPointerCapture(event.pointerId);
        interaction.drag = { id: event.pointerId, start: plotPosition(event, plot) };
    }, { signal });

    root.addEventListener("pointerup", event => {
        const plot = targetOf(event)?.closest("[data-oi-plot]");
        if (!plot || interaction.drag?.id !== event.pointerId) return;
        const start = interaction.drag.start;
        const end = plotPosition(event, plot);
        interaction.drag = null;
        zoom(plot, Math.min(start.index, end.index), Math.max(start.index, end.index));
    }, { signal });

    root.addEventListener("lostpointercapture", event => {
        if (interaction.drag?.id !== event.pointerId) return;
        interaction.drag = null;
        const plot = targetOf(event)?.closest("[data-oi-plot]");
        if (plot) clearPlot(plot);
    }, { signal });

    return { stop: () => scope.abort() };
}
