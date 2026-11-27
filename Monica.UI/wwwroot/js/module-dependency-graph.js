import * as d3 from "../lib/d3.min.mjs";

const EDGE_SOURCE_PADDING = 2;
const EDGE_TARGET_PADDING = 6;
const DEFAULT_NODE_RADIUS = 24;
const SELECTED_NODE_RADIUS = 31;
const MIN_CANVAS_SIZE = 320;
const GRAPH_PADDING = 64;

export function calculateDependencyEdgeEndpoints(source, target, sourceRadius, targetRadius) {
    const deltaX = target.x - source.x;
    const deltaY = target.y - source.y;
    const distance = Math.hypot(deltaX, deltaY);
    if (distance === 0) return null;

    const unitX = deltaX / distance;
    const unitY = deltaY / distance;
    return {
        x1: source.x + unitX * (sourceRadius + EDGE_SOURCE_PADDING),
        y1: source.y + unitY * (sourceRadius + EDGE_SOURCE_PADDING),
        x2: target.x - unitX * (targetRadius + EDGE_TARGET_PADDING),
        y2: target.y - unitY * (targetRadius + EDGE_TARGET_PADDING)
    };
}

export function calculateLayeredDependencyLayout(nodes, requestedWidth, requestedHeight) {
    const groups = new Map();
    for (const node of nodes) {
        const depth = Math.max(0, Number.isFinite(node.depth) ? node.depth : 0);
        const group = groups.get(depth) ?? [];
        group.push(node);
        groups.set(depth, group);
    }

    const depths = [...groups.keys()].sort((left, right) => left - right);
    const maximumGroupSize = Math.max(1, ...[...groups.values()].map(group => group.length));
    const width = Math.max(MIN_CANVAS_SIZE, requestedWidth || 0, depths.length * 190 + GRAPH_PADDING * 2);
    const height = Math.max(MIN_CANVAS_SIZE, requestedHeight || 0, maximumGroupSize * 76 + GRAPH_PADDING * 2);
    const positions = [];
    depths.forEach((depth, depthIndex) => {
        const group = groups.get(depth).sort((left, right) => left.label.localeCompare(right.label));
        const spacing = (height - GRAPH_PADDING * 2) / Math.max(1, group.length);
        group.forEach((node, index) => positions.push({
            id: node.id,
            x: depths.length === 1
                ? width / 2
                : GRAPH_PADDING + depthIndex * ((width - GRAPH_PADDING * 2) / (depths.length - 1)),
            y: GRAPH_PADDING + spacing * (index + 0.5)
        }));
    });

    return { width, height, positions };
}

export function calculateRadialDependencyLayout(nodes, requestedWidth, requestedHeight) {
    const maximumDepth = Math.max(0, ...nodes.map(node => Math.max(0, node.depth ?? 0)));
    const outerRadius = Math.max(150, (maximumDepth + 1) * 92);
    const width = Math.max(MIN_CANVAS_SIZE, requestedWidth || 0, outerRadius * 2 + GRAPH_PADDING * 2);
    const height = Math.max(MIN_CANVAS_SIZE, requestedHeight || 0, outerRadius * 2 + GRAPH_PADDING * 2);
    const centerX = width / 2;
    const centerY = height / 2;
    const positions = [];
    const groups = new Map();
    for (const node of nodes) {
        const depth = Math.max(0, node.depth ?? 0);
        const group = groups.get(depth) ?? [];
        group.push(node);
        groups.set(depth, group);
    }

    for (const [depth, group] of groups) {
        const radius = depth === 0 && group.length === 1 ? 0 : Math.max(72, (depth + 0.65) * 92);
        group.sort((left, right) => left.label.localeCompare(right.label));
        group.forEach((node, index) => {
            const angle = (Math.PI * 2 * index / group.length) - Math.PI / 2;
            positions.push({
                id: node.id,
                x: centerX + Math.cos(angle) * radius,
                y: centerY + Math.sin(angle) * radius
            });
        });
    }

    return { width, height, positions };
}

export function createDotNetCallbackGate(initialReference) {
    let reference = initialReference;
    let acceptingCallbacks = true;
    let disposePromise;
    const pendingCallbacks = new Set();

    const invoke = (methodName, ...args) => {
        const admittedReference = acceptingCallbacks ? reference : null;
        if (!admittedReference) return undefined;

        let callback;
        try {
            callback = Promise.resolve(admittedReference.invokeMethodAsync(methodName, ...args));
        } catch (error) {
            callback = Promise.reject(error);
        }

        pendingCallbacks.add(callback);
        const removeCallback = () => pendingCallbacks.delete(callback);
        callback.then(removeCallback, removeCallback);
        return callback;
    };

    const dispose = () => disposePromise ??= disposeCore();
    const disposeCore = async () => {
        acceptingCallbacks = false;
        await Promise.allSettled([...pendingCallbacks]);
        reference = null;
    };

    return { invoke, dispose };
}

export function createGraph(element, initialModel, dotNetReference) {
    let model = initialModel ?? {};
    let nodes = [];
    let links = [];
    let nodeSelection;
    let linkSelection;
    let simulation;
    let disposed = false;
    let renderFrame = 0;
    let fitFrame = 0;
    let hasInitialFit = false;
    let disposePromise;
    const callbacks = createDotNetCallbackGate(dotNetReference);
    dotNetReference = null;
    const instanceToken = globalThis.crypto?.randomUUID?.()
        ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
    const markerId = `module-dependency-arrow-${instanceToken}`;
    const outgoingMarkerId = `${markerId}-outgoing`;
    const incomingMarkerId = `${markerId}-incoming`;

    const svgElement = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svgElement.setAttribute("focusable", "false");
    svgElement.setAttribute("aria-hidden", "true");
    element.replaceChildren(svgElement);

    const svg = d3.select(svgElement);
    const viewport = svg.append("g").attr("class", "graph-viewport");
    const linkLayer = viewport.append("g").attr("class", "graph-links");
    const nodeLayer = viewport.append("g").attr("class", "graph-nodes");
    createMarkers(svg, markerId, outgoingMarkerId, incomingMarkerId);

    const zoom = d3.zoom()
        .scaleExtent([0.12, 6])
        .on("zoom.graph", event => viewport.attr("transform", event.transform));
    svg.call(zoom).on("dblclick.zoom", null);

    const scheduleRender = () => {
        if (disposed) return;
        cancelAnimationFrame(renderFrame);
        renderFrame = requestAnimationFrame(() => {
            try {
                render();
            } catch {
                callbacks.invoke("OnGraphRenderFailed");
                void cleanup();
            }
        });
    };

    const render = () => {
        if (disposed) return;
        simulation?.stop();
        simulation = undefined;
        linkLayer.selectAll("*").remove();
        nodeLayer.selectAll("*").remove();

        nodes = (Array.isArray(model.nodes) ? model.nodes : []).map(node => ({ ...node }));
        links = (Array.isArray(model.edges) ? model.edges : []).map(edge => ({ ...edge }));
        const width = Math.max(MIN_CANVAS_SIZE, element.clientWidth || 0);
        const height = Math.max(MIN_CANVAS_SIZE, element.clientHeight || 0);
        svg.attr("viewBox", `0 0 ${width} ${height}`);

        linkSelection = linkLayer.selectAll("path")
            .data(links, link => `${link.source?.id ?? link.source}>${link.target?.id ?? link.target}`)
            .join("path")
            .attr("class", "graph-link")
            .attr("fill", "none")
            .attr("stroke", "var(--mud-palette-lines-inputs)")
            .attr("stroke-width", 1.8)
            .attr("marker-end", `url(#${markerId})`);

        nodeSelection = nodeLayer.selectAll("g")
            .data(nodes, node => node.id)
            .join("g")
            .attr("class", "graph-node")
            .attr("tabindex", -1)
            .style("cursor", "pointer")
            .on("click.graph", (event, node) => {
                event.stopPropagation();
                callbacks.invoke("OnNodeSelected", node.id);
            })
            .on("mouseenter.graph", (_, node) => showNodeEvidence(node))
            .on("mouseleave.graph", clearNodeEvidence);

        nodeSelection.append("circle")
            .attr("class", "graph-node-halo");
        nodeSelection.append("circle")
            .attr("class", "graph-node-core");
        nodeSelection.append("text")
            .attr("class", "graph-node-glyph")
            .attr("text-anchor", "middle")
            .attr("dy", ".35em");
        nodeSelection.append("text")
            .attr("class", "graph-node-label")
            .attr("text-anchor", "middle");
        nodeSelection.append("title");

        applyNodePresentation();
        applySearchAndSelection();
        const layout = model.layout ?? "force";
        if (layout === "layered") {
            applyStaticLayout(calculateLayeredDependencyLayout(nodes, width, height));
        } else if (layout === "radial") {
            applyStaticLayout(calculateRadialDependencyLayout(nodes, width, height));
        } else {
            applyForceLayout(width, height);
        }

        if (!hasInitialFit) {
            hasInitialFit = true;
            cancelAnimationFrame(fitFrame);
            fitFrame = requestAnimationFrame(() => fit(false));
        }
    };

    const applyStaticLayout = layout => {
        const positions = new Map(layout.positions.map(position => [position.id, position]));
        for (const node of nodes) Object.assign(node, positions.get(node.id));
        updateGeometry();
    };

    const applyForceLayout = (width, height) => {
        const seed = calculateRadialDependencyLayout(nodes, width, height);
        const positions = new Map(seed.positions.map(position => [position.id, position]));
        for (const node of nodes) Object.assign(node, positions.get(node.id));
        simulation = d3.forceSimulation(nodes)
            .force("link", d3.forceLink(links).id(node => node.id).distance(model.fullHost ? 92 : 130).strength(0.7))
            .force("charge", d3.forceManyBody().strength(model.fullHost ? -260 : -430))
            .force("center", d3.forceCenter(width / 2, height / 2))
            .force("collision", d3.forceCollide().radius(model.fullHost ? 38 : 52))
            .alphaDecay(0.035);
        if (globalThis.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches) {
            simulation.stop();
            simulation.tick(180);
            updateGeometry();
            nodeSelection.call(d3.drag().on("drag.graph", (event, node) => {
                node.x = event.x;
                node.y = event.y;
                updateGeometry();
            }));
            return;
        }

        simulation.on("tick", updateGeometry);
        nodeSelection.call(d3.drag()
            .on("start.graph", (event, node) => {
                if (!event.active) simulation.alphaTarget(0.22).restart();
                node.fx = node.x;
                node.fy = node.y;
            })
            .on("drag.graph", (event, node) => {
                node.fx = event.x;
                node.fy = event.y;
            })
            .on("end.graph", (event, node) => {
                if (!event.active) simulation.alphaTarget(0);
                node.fx = null;
                node.fy = null;
            }));
    };

    const updateGeometry = () => {
        if (disposed) return;
        nodeSelection?.attr("transform", node => `translate(${node.x ?? 0},${node.y ?? 0})`);
        linkSelection?.attr("d", linkPath);
    };

    const applyNodePresentation = () => {
        nodeSelection.select(".graph-node-halo")
            .attr("r", node => node.selected ? SELECTED_NODE_RADIUS + 7 : DEFAULT_NODE_RADIUS + 6)
            .attr("fill", node => node.failed
                ? "var(--mud-palette-error-hover)"
                : node.selected
                    ? "var(--mud-palette-primary-hover)"
                    : "transparent");
        nodeSelection.select(".graph-node-core")
            .attr("r", node => node.selected ? SELECTED_NODE_RADIUS : DEFAULT_NODE_RADIUS)
            .attr("fill", nodeFill)
            .attr("stroke", nodeStroke)
            .attr("stroke-width", node => node.selected || node.requiresWebHost ? 4 : 2)
            .attr("stroke-dasharray", node => node.requiresWebHost ? "7 4" : null);
        nodeSelection.select(".graph-node-glyph")
            .attr("fill", "var(--mud-palette-primary-text)")
            .text(nodeGlyph);
        nodeSelection.select(".graph-node-label")
            .attr("dy", node => node.selected ? 49 : 42)
            .attr("fill", "var(--mud-palette-text-primary)")
            .text(node => truncate(node.label, model.fullHost ? 22 : 30));
        nodeSelection.select("title").text(node => node.tooltip ?? node.label);
    };

    const linkPath = link => {
        const source = typeof link.source === "object" ? link.source : nodes.find(node => node.id === link.source);
        const target = typeof link.target === "object" ? link.target : nodes.find(node => node.id === link.target);
        if (!source || !target) return null;
        const endpoints = calculateDependencyEdgeEndpoints(
            source,
            target,
            source.selected ? SELECTED_NODE_RADIUS : DEFAULT_NODE_RADIUS,
            target.selected ? SELECTED_NODE_RADIUS : DEFAULT_NODE_RADIUS);
        if (!endpoints) return null;
        const bend = Math.min(34, Math.hypot(endpoints.x2 - endpoints.x1, endpoints.y2 - endpoints.y1) * 0.08);
        const middleX = (endpoints.x1 + endpoints.x2) / 2;
        const middleY = (endpoints.y1 + endpoints.y2) / 2 - bend;
        return `M${endpoints.x1},${endpoints.y1} Q${middleX},${middleY} ${endpoints.x2},${endpoints.y2}`;
    };

    const applySearchAndSelection = () => {
        const query = (model.searchText ?? "").trim().toLocaleLowerCase();
        nodeSelection
            ?.attr("opacity", node => !query || node.label.toLocaleLowerCase().includes(query) ? 1 : 0.16)
            .classed("graph-node-match", node => Boolean(query) && node.label.toLocaleLowerCase().includes(query));
        linkSelection?.attr("opacity", query ? 0.12 : 0.72);
    };

    const showNodeEvidence = node => {
        if (disposed) return;
        const outgoing = new Set(links
            .filter(link => idOf(link.source) === node.id)
            .map(link => idOf(link.target)));
        const incoming = new Set(links
            .filter(link => idOf(link.target) === node.id)
            .map(link => idOf(link.source)));
        nodeSelection.attr("opacity", candidate =>
            candidate.id === node.id || outgoing.has(candidate.id) || incoming.has(candidate.id) ? 1 : 0.13);
        linkSelection
            .attr("opacity", link => idOf(link.source) === node.id || idOf(link.target) === node.id ? 1 : 0.08)
            .attr("stroke", link => idOf(link.source) === node.id
                ? "var(--mud-palette-info)"
                : idOf(link.target) === node.id
                    ? "var(--mud-palette-success)"
                    : "var(--mud-palette-lines-inputs)")
            .attr("stroke-width", link => idOf(link.source) === node.id || idOf(link.target) === node.id ? 3.2 : 1.4)
            .attr("marker-end", link => idOf(link.source) === node.id
                ? `url(#${outgoingMarkerId})`
                : idOf(link.target) === node.id
                    ? `url(#${incomingMarkerId})`
                    : `url(#${markerId})`);
    };

    const clearNodeEvidence = () => {
        if (disposed) return;
        linkSelection
            ?.attr("stroke", "var(--mud-palette-lines-inputs)")
            .attr("stroke-width", 1.8)
            .attr("marker-end", `url(#${markerId})`);
        applySearchAndSelection();
    };

    const motionTarget = duration => globalThis.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches
        ? svg
        : svg.transition().duration(duration);

    const fit = animate => {
        if (disposed) return;
        const target = viewport.node();
        if (!target) return;
        let bounds;
        try { bounds = target.getBBox(); } catch { return; }
        if (!bounds.width || !bounds.height) return;
        const width = Math.max(MIN_CANVAS_SIZE, element.clientWidth || 0);
        const height = Math.max(MIN_CANVAS_SIZE, element.clientHeight || 0);
        const scale = Math.max(0.12, Math.min(2.2, 0.88 / Math.max(bounds.width / width, bounds.height / height)));
        const transform = d3.zoomIdentity
            .translate(width / 2, height / 2)
            .scale(scale)
            .translate(-(bounds.x + bounds.width / 2), -(bounds.y + bounds.height / 2));
        const targetSvg = animate ? motionTarget(280) : svg;
        targetSvg.call(zoom.transform, transform);
    };

    const focus = moduleId => {
        if (disposed) return;
        const node = nodes.find(candidate => candidate.id === moduleId);
        if (!node || !Number.isFinite(node.x) || !Number.isFinite(node.y)) return;
        const width = Math.max(MIN_CANVAS_SIZE, element.clientWidth || 0);
        const height = Math.max(MIN_CANVAS_SIZE, element.clientHeight || 0);
        motionTarget(240).call(
            zoom.transform,
            d3.zoomIdentity.translate(width / 2, height / 2).scale(1.65).translate(-node.x, -node.y));
    };

    const cleanup = () => disposePromise ??= cleanupCore();

    const cleanupCore = async () => {
        disposed = true;
        const callbackDrain = callbacks.dispose();
        cancelAnimationFrame(renderFrame);
        cancelAnimationFrame(fitFrame);
        simulation?.stop();
        simulation = undefined;
        resizeObserver.disconnect();
        removalObserver.disconnect();
        svg.interrupt();
        viewport.interrupt();
        nodeSelection?.interrupt();
        linkSelection?.interrupt();
        svg.on(".zoom", null);
        nodeSelection?.on(".graph", null);
        // DOM removal remains renderer/MutationObserver-owned. Disposal only stops callback producers.
        await callbackDrain;
    };

    const resizeObserver = new ResizeObserver(scheduleRender);
    resizeObserver.observe(element);
    const removalObserver = new MutationObserver(() => {
        if (!element.isConnected) void cleanup();
    });
    removalObserver.observe(document.body, { childList: true, subtree: true });
    try {
        render();
    } catch (error) {
        void cleanup();
        throw error;
    }

    return {
        update(nextModel) {
            if (disposed) return;
            const normalizedModel = nextModel ?? {};
            const layoutChanged = model?.layout !== normalizedModel.layout
                || model?.fullHost !== normalizedModel.fullHost;
            const structureChanged = graphStructureIdentity(model) !== graphStructureIdentity(normalizedModel);
            model = normalizedModel;
            if (layoutChanged || structureChanged) {
                hasInitialFit = false;
                render();
                return;
            }

            const nextNodesById = new Map(
                (Array.isArray(model.nodes) ? model.nodes : []).map(node => [node.id, node]));
            for (const node of nodes) Object.assign(node, nextNodesById.get(node.id));
            applyNodePresentation();
            applySearchAndSelection();
            updateGeometry();
        },
        zoomIn() {
            if (!disposed) motionTarget(180).call(zoom.scaleBy, 1.35);
        },
        zoomOut() {
            if (!disposed) motionTarget(180).call(zoom.scaleBy, 0.74);
        },
        fit() {
            fit(true);
        },
        focus,
        dispose: cleanup
    };
}

function graphStructureIdentity(model) {
    return JSON.stringify({
        nodes: (Array.isArray(model?.nodes) ? model.nodes : []).map(node => node.id),
        edges: (Array.isArray(model?.edges) ? model.edges : []).map(edge => [edge.source, edge.target])
    });
}

function createMarkers(svg, defaultId, outgoingId, incomingId) {
    const definitions = svg.append("defs");
    createMarker(definitions, defaultId, "var(--mud-palette-lines-inputs)");
    createMarker(definitions, outgoingId, "var(--mud-palette-info)");
    createMarker(definitions, incomingId, "var(--mud-palette-success)");
}

function createMarker(definitions, id, color) {
    const marker = definitions.append("marker")
        .attr("id", id)
        .attr("viewBox", "0 0 10 10")
        .attr("refX", 9)
        .attr("refY", 5)
        .attr("markerWidth", 8)
        .attr("markerHeight", 8)
        .attr("markerUnits", "userSpaceOnUse")
        .attr("orient", "auto");
    marker.append("path").attr("d", "M0,0 L10,5 L0,10 Z").attr("fill", color);
}

function nodeFill(node) {
    if (node.failed) return "var(--mud-palette-error)";
    if (node.isUiModule) return "var(--mud-palette-info)";
    if (node.isWebModule) return "var(--mud-palette-secondary)";
    return "var(--mud-palette-primary)";
}

function nodeStroke(node) {
    if (node.failed) return "var(--mud-palette-error-lighten)";
    if (node.selected) return "var(--mud-palette-primary-lighten)";
    if (node.requiresWebHost) return "var(--mud-palette-warning)";
    return "var(--mud-palette-surface)";
}

function nodeGlyph(node) {
    if (node.failed) return "!";
    if (node.isUiModule) return "UI";
    if (node.isWebModule) return "W";
    return "M";
}

function idOf(endpoint) {
    return typeof endpoint === "object" ? endpoint.id : endpoint;
}

function truncate(value, maximumLength) {
    if (!value || value.length <= maximumLength) return value ?? "";
    return `${value.slice(0, maximumLength - 1)}…`;
}
