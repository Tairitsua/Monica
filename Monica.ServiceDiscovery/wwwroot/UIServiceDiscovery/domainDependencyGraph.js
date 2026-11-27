import * as d3 from '../../Monica.UI/lib/d3.min.mjs';

// The layered, radial and force layouts share the same graph, semantic edges and camera.
const easeOutCubic = progress => 1 - (1 - progress) ** 3;

const ROLE_VAR = {
    primary: "var(--mud-palette-primary)",
    info: "var(--mud-palette-info)",
    error: "var(--mud-palette-error)",
    warning: "var(--mud-palette-warning)",
    success: "var(--mud-palette-success)",
    third: "var(--mo-color-role-third)",
    neutral: "var(--mud-palette-text-secondary)"
};

function getCardSize(layout, granularity) {
    if (layout === "radial") {
        return granularity === "role" ? { w: 174, h: 46 } : { w: 154, h: 44 };
    }
    return granularity === "role" ? { w: 204, h: 52 } : { w: 180, h: 50 };
}

function truncateText(text, max) {
    if (!text) return "";
    return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

/** Matches a link against the active semantic routing filter. */
export function matchRoutingFilter(link, filter) {
    if (!filter || filter === "all") return true;
    if (filter === "remote") return Boolean(link.isRealRemote);
    if (filter === "runonrole") return Boolean(link.runOnRoleCalls > 0);
    if (filter === "risk") return Boolean(link.hasRisk);
    if (filter === "local") return link.routing === "local-ordinary";
    return link.routing === filter;
}

/** Deterministic 3-tier assignment + 2-pass barycenter crossing minimization. */
function computeLayeredPositions(nodes, links, width, height, cardSize) {
    const inMap = new Map(nodes.map(n => [n.id, []]));
    const outMap = new Map(nodes.map(n => [n.id, []]));
    for (const l of links) {
        outMap.get(l.sourceId)?.push(l.targetId);
        inMap.get(l.targetId)?.push(l.sourceId);
    }

    const tiers = [[], [], []]; /* 0: upstream callers, 1: bidirectional hubs, 2: target sinks & unlisted */
    for (const n of nodes) {
        const degIn = inMap.get(n.id)?.length ?? 0;
        const degOut = outMap.get(n.id)?.length ?? 0;
        if (!n.isCatalog || degOut === 0) tiers[2].push(n);
        else if (degIn === 0) tiers[0].push(n);
        else tiers[1].push(n);
    }

    for (const list of tiers) {
        list.sort((a, b) => (b.totalCalls - a.totalCalls) || a.id.localeCompare(b.id));
    }

    const indexIn = list => new Map(list.map((n, i) => [n.id, i]));
    for (let pass = 0; pass < 2; pass++) {
        const idx0 = indexIn(tiers[0]);
        const idx2 = indexIn(tiers[2]);
        const bary = (node, neighborIds, refMap) => {
            const vals = neighborIds.map(id => refMap.get(id)).filter(v => v !== undefined);
            if (!vals.length) return 999;
            return vals.reduce((a, b) => a + b, 0) / vals.length;
        };
        tiers[1].sort((a, b) => {
            const ba = (bary(a, inMap.get(a.id), idx0) + bary(a, outMap.get(a.id), idx2)) / 2;
            const bb = (bary(b, inMap.get(b.id), idx0) + bary(b, outMap.get(b.id), idx2)) / 2;
            return (ba - bb) || (b.totalCalls - a.totalCalls);
        });
        const idx1 = indexIn(tiers[1]);
        tiers[0].sort((a, b) => (bary(a, outMap.get(a.id), idx1) - bary(b, outMap.get(b.id), idx1)) || (b.totalCalls - a.totalCalls));
        tiers[2].sort((a, b) => {
            if (a.isCatalog !== b.isCatalog) return a.isCatalog ? -1 : 1;
            return (bary(a, inMap.get(a.id), idx1) - bary(b, inMap.get(b.id), idx1)) || (b.totalCalls - a.totalCalls);
        });
    }

    const maxRows = Math.max(tiers[0].length, tiers[1].length, tiers[2].length, 1);
    const rowGap = cardSize.h + 18;
    const padTop = 72;
    const padBottom = 42;
    const virtualH = Math.max(height, padTop + padBottom + (maxRows - 1) * rowGap);
    const virtualW = Math.max(width, cardSize.w * 3 + 380);
    const tierXs = [virtualW * 0.145, virtualW * 0.475, virtualW * 0.855];

    const coords = new Map();
    tiers.forEach((list, tIdx) => {
        const span = Math.max(0, (list.length - 1) * rowGap);
        const startY = padTop + Math.max(0, (virtualH - padTop - padBottom - span) / 2);
        list.forEach((n, i) => {
            coords.set(n.id, { x: tierXs[tIdx], y: startY + i * rowGap, tier: tIdx });
        });
    });

    return { coords, tierXs, virtualW, virtualH };
}

/** Elliptical radial compass layout with extra space for the wider capsules at the extremes. */
function computeRadialPositions(nodes, width, height, cardSize) {
    const sorted = [...nodes].sort((a, b) => {
        if (a.isCatalog !== b.isCatalog) return a.isCatalog ? -1 : 1;
        return (b.totalCalls - a.totalCalls) || a.id.localeCompare(b.id);
    });
    const n = Math.max(sorted.length, 1);
    const rx = Math.max(340, Math.round(n * (cardSize.w * 0.175)));
    const ry = Math.max(260, Math.round(n * (cardSize.h * 0.47)));
    const cx = Math.max(width, rx * 2 + cardSize.w + 90) / 2;
    const cy = Math.max(height, ry * 2 + cardSize.h + 80) / 2;

    const coords = new Map();
    sorted.forEach((node, idx) => {
        const angle = (idx / n) * Math.PI * 2;
        coords.set(node.id, { x: cx + Math.sin(angle) * rx, y: cy - Math.cos(angle) * ry, angle, tier: 1 });
    });

    return { coords, cx, cy, rx, ry };
}

/** Structured force simulation with directional tier gravity and generous capsule collision radius. */
function computeForcePositions(d3, nodes, links, width, height, cardSize) {
    const layered = computeLayeredPositions(nodes, links, width, height, cardSize);
    const simNodes = nodes.map(n => {
        const seed = layered.coords.get(n.id) ?? { x: width / 2, y: height / 2, tier: 1 };
        return { id: n.id, tier: seed.tier, x: seed.x, y: seed.y };
    });
    const simLinks = links.map(l => ({ source: l.sourceId, target: l.targetId, count: l.count }));
    const cy = layered.virtualH / 2;

    const sim = d3.forceSimulation(simNodes)
        .force("link", d3.forceLink(simLinks).id(d => d.id).distance(cardSize.w + 54).strength(0.26))
        .force("charge", d3.forceManyBody().strength(-760))
        .force("x", d3.forceX(d => layered.tierXs[d.tier] ?? layered.virtualW / 2).strength(0.46))
        .force("y", d3.forceY(cy).strength(0.08))
        .force("collide", d3.forceCollide().radius(cardSize.w * 0.43).iterations(3))
        .stop();

    for (let i = 0; i < 160; i++) sim.tick();

    const coords = new Map();
    for (const s of simNodes) {
        coords.set(s.id, { x: s.x, y: s.y, tier: s.tier });
    }
    return { coords, tierXs: layered.tierXs, virtualW: layered.virtualW, virtualH: layered.virtualH };
}

/** Computes clean non-colliding edge SVG path + exact midpoint for inline call-count badges. */
function computeEdgeGeometry(link, posMap, layout, radialCenter, portOffsets, cardSize) {
    const s = posMap.get(link.sourceId);
    const g = posMap.get(link.targetId);
    if (!s || !g) return { d: "", mx: 0, my: 0 };

    const halfW = cardSize.w / 2;
    const halfH = cardSize.h / 2;

    if (layout === "radial" && radialCenter) {
        const dx = g.x - s.x;
        const dy = g.y - s.y;
        const dist = Math.hypot(dx, dy) || 1;
        const ux = dx / dist;
        const uy = dy / dist;
        const startX = s.x + ux * (halfW * 0.72);
        const startY = s.y + uy * (halfH * 0.95);
        const endX = g.x - ux * (halfW * 0.78);
        const endY = g.y - uy * (halfH * 1.02);

        const pull = 0.44;
        const baseCx = (startX + endX) / 2 * (1 - pull) + radialCenter.cx * pull;
        const baseCy = (startY + endY) / 2 * (1 - pull) + radialCenter.cy * pull;
        const perp = link.hasReverse ? (link.sourceId < link.targetId ? 22 : -22) : 0;
        const cx = baseCx - uy * perp;
        const cy = baseCy + ux * perp;
        const d = `M${startX.toFixed(1)},${startY.toFixed(1)} Q${cx.toFixed(1)},${cy.toFixed(1)} ${endX.toFixed(1)},${endY.toFixed(1)}`;
        const mx = 0.25 * startX + 0.5 * cx + 0.25 * endX;
        const my = 0.25 * startY + 0.5 * cy + 0.25 * endY;
        return { d, mx, my };
    }

    const sameColumn = Math.abs(s.x - g.x) < 40;
    if (sameColumn) {
        const dir = g.y >= s.y ? 1 : -1;
        const span = Math.abs(g.y - s.y);
        const bidirOffset = link.hasReverse ? (dir > 0 ? 18 : -10) : 0;
        const arc = 48 + Math.min(112, span * 0.34) + bidirOffset + (link.edgeIndex % 3) * 14;
        const x1 = s.x + halfW;
        const y1 = s.y + dir * 7;
        const x2 = g.x + halfW + 4;
        const y2 = g.y - dir * 7;
        const c1x = x1 + arc;
        const c2x = x2 + arc;
        const d = `M${x1.toFixed(1)},${y1.toFixed(1)} C${c1x.toFixed(1)},${y1.toFixed(1)} ${c2x.toFixed(1)},${y2.toFixed(1)} ${x2.toFixed(1)},${y2.toFixed(1)}`;
        const mx = 0.125 * x1 + 0.375 * c1x + 0.375 * c2x + 0.125 * x2;
        const my = (y1 + y2) / 2;
        return { d, mx, my };
    }

    const goingRight = g.x > s.x;
    const outPort = portOffsets.out.get(link.key) ?? 0;
    const inPort = portOffsets.in.get(link.key) ?? 0;
    const x1 = s.x + (goingRight ? halfW : -halfW);
    const y1 = s.y + outPort;
    const x2 = g.x + (goingRight ? -(halfW + 5) : (halfW + 5));
    const y2 = g.y + inPort;

    const dx = Math.abs(x2 - x1);
    const bend = Math.max(46, dx * 0.42);
    const bidirLift = link.hasReverse ? (goingRight ? -18 : 18) : 0;
    const c1x = x1 + (goingRight ? bend : -bend);
    const c2x = x2 + (goingRight ? -bend : bend);
    const c1y = y1 + bidirLift;
    const c2y = y2 + bidirLift;

    const d = `M${x1.toFixed(1)},${y1.toFixed(1)} C${c1x.toFixed(1)},${c1y.toFixed(1)} ${c2x.toFixed(1)},${c2y.toFixed(1)} ${x2.toFixed(1)},${y2.toFixed(1)}`;
    const mx = 0.125 * x1 + 0.375 * c1x + 0.375 * c2x + 0.125 * x2;
    const my = 0.125 * y1 + 0.375 * c1y + 0.375 * c2y + 0.125 * y2;
    return { d, mx, my };
}

/** Computes neat vertical port offsets so multiple edges leaving/entering a node fan smoothly. */
function computePortOffsets(links, posMap) {
    const outGroups = new Map();
    const inGroups = new Map();
    for (const l of links) {
        const s = posMap.get(l.sourceId);
        const g = posMap.get(l.targetId);
        if (!s || !g || Math.abs(s.x - g.x) < 40) continue;
        const dirKey = g.x > s.x ? "R" : "L";
        const ok = `${l.sourceId}:${dirKey}`;
        const ik = `${l.targetId}:${dirKey}`;
        if (!outGroups.has(ok)) outGroups.set(ok, []);
        if (!inGroups.has(ik)) inGroups.set(ik, []);
        outGroups.get(ok).push({ key: l.key, otherY: g.y });
        inGroups.get(ik).push({ key: l.key, otherY: s.y });
    }
    const spread = groupMap => {
        const res = new Map();
        for (const list of groupMap.values()) {
            list.sort((a, b) => a.otherY - b.otherY);
            const n = list.length;
            const maxSpan = Math.min(26, Math.max(0, (n - 1) * 7));
            list.forEach((item, idx) => {
                const offset = n === 1 ? 0 : -maxSpan / 2 + (idx / (n - 1)) * maxSpan;
                res.set(item.key, offset);
            });
        }
        return res;
    };
    return { out: spread(outGroups), in: spread(inGroups) };
}

/**
 * Creates a reusable D3.js v7 directed dependency graph mounted inside `container`.
 */
function createDomainDependencyGraph(d3, container, callbacks = {}) {
    const uid = `sv-d3-${Math.random().toString(36).slice(2, 8)}`;
    const root = d3.select(container);
    root.selectAll("*").remove();

    const svg = root.append("svg")
        .attr("class", "sv-d3-svg")
        .attr("role", "img")
        .attr("tabindex", "0");

    const defs = svg.append("defs");

    const gridPattern = defs.append("pattern")
        .attr("id", `${uid}-dots`)
        .attr("width", 22)
        .attr("height", 22)
        .attr("patternUnits", "userSpaceOnUse");
    gridPattern.append("circle")
        .attr("cx", 1.5)
        .attr("cy", 1.5)
        .attr("r", 1)
        .attr("class", "sv-d3-grid-dot");

    const hatchPattern = defs.append("pattern")
        .attr("id", `${uid}-hatch`)
        .attr("width", 8)
        .attr("height", 8)
        .attr("patternUnits", "userSpaceOnUse")
        .attr("patternTransform", "rotate(45)");
    hatchPattern.append("line")
        .attr("x1", 0).attr("y1", 0).attr("x2", 0).attr("y2", 8)
        .attr("class", "sv-d3-hatch-line");

    const shadow = defs.append("filter")
        .attr("id", `${uid}-shadow`)
        .attr("x", "-15%").attr("y", "-20%")
        .attr("width", "130%").attr("height", "150%");
    shadow.append("feDropShadow")
        .attr("dx", 0).attr("dy", 2)
        .attr("stdDeviation", 3.5)
        .attr("flood-color", "currentColor")
        .attr("flood-opacity", 0.08);

    /* Semantic directional arrowheads covering all Monica routing roles including `third`. */
    for (const role of ["primary", "info", "third", "error", "warning", "neutral"]) {
        defs.append("marker")
            .attr("id", `${uid}-arr-${role}`)
            .attr("viewBox", "0 0 10 10")
            .attr("refX", 8.5)
            .attr("refY", 5)
            .attr("markerWidth", 6.5)
            .attr("markerHeight", 6.5)
            .attr("orient", "auto")
            .append("path")
            .attr("d", "M 0.5 1.5 L 8.8 5 L 0.5 8.5 L 2.2 5 Z")
            .attr("fill", ROLE_VAR[role]);
    }

    const bgRect = svg.append("rect")
        .attr("class", "sv-d3-bg")
        .attr("width", "100%")
        .attr("height", "100%")
        .attr("fill", `url(#${uid}-dots)`);

    const viewport = svg.append("g").attr("class", "sv-d3-viewport");
    const guideLayer = viewport.append("g").attr("class", "sv-d3-guides");
    const edgeLayer = viewport.append("g").attr("class", "sv-d3-edges");
    const badgeLayer = viewport.append("g").attr("class", "sv-d3-badges");
    const nodeLayer = viewport.append("g").attr("class", "sv-d3-nodes");

    let state = {
        nodes: [],
        links: [],
        layout: "layered",
        granularity: "domain",
        focusDomain: null,
        selectedPairKey: null,
        hoverPairKey: null,
        hoverNodeId: null,
        routingFilter: "all",
        texts: {}
    };
    let posMap = new Map();
    let radialCenter = null;
    let tierMeta = null;
    let lastLayoutKey = "";

    const zoom = d3.zoom()
        .scaleExtent([0.32, 2.4])
        .on("zoom", event => {
            viewport.attr("transform", event.transform);
            callbacks.onZoomChange?.(Math.round(event.transform.k * 100));
        });

    svg.call(zoom).on("dblclick.zoom", null);

    bgRect.on("click", () => {
        callbacks.onClearSelection?.();
    });

    const getBox = () => {
        const rect = container.getBoundingClientRect();
        return { width: Math.max(480, rect.width || 720), height: Math.max(460, rect.height || 540) };
    };

    const getActiveNeighborhood = () => {
        const hoverEdge = state.hoverPairKey;
        const pinnedEdge = state.selectedPairKey;
        const focusNode = state.hoverNodeId ?? state.focusDomain;

        if (hoverEdge) {
            const pair = state.links.find(l => l.key === hoverEdge);
            if (pair) {
                return { mode: "edge", edgeKey: hoverEdge, nodeIds: new Set([pair.sourceId, pair.targetId]) };
            }
        }
        if (pinnedEdge && !state.hoverNodeId) {
            const pair = state.links.find(l => l.key === pinnedEdge);
            if (pair) {
                return { mode: "edge", edgeKey: pinnedEdge, nodeIds: new Set([pair.sourceId, pair.targetId]) };
            }
        }
        if (focusNode) {
            const nodeIds = new Set([focusNode]);
            for (const l of state.links) {
                if (!matchRoutingFilter(l, state.routingFilter)) continue;
                if (l.sourceId === focusNode) nodeIds.add(l.targetId);
                if (l.targetId === focusNode) nodeIds.add(l.sourceId);
            }
            return { mode: "node", focusNode, nodeIds };
        }
        if (state.routingFilter && state.routingFilter !== "all") {
            const nodeIds = new Set();
            for (const l of state.links) {
                if (matchRoutingFilter(l, state.routingFilter)) {
                    nodeIds.add(l.sourceId);
                    nodeIds.add(l.targetId);
                }
            }
            return { mode: "filter", nodeIds };
        }
        return { mode: "all", nodeIds: null };
    };

    const applyVisualEmphasis = () => {
        const nb = getActiveNeighborhood();
        const maxCalls = Math.max(1, ...state.links.map(l => l.count));
        const sqrtSpan = Math.sqrt(maxCalls) - 1;
        const widthScale = count => sqrtSpan === 0 ? 2.6 : 1.4 + (Math.sqrt(count) - 1) / sqrtSpan * 2.4;

        edgeLayer.selectAll("g.sv-d3-edge-group").each(function (l) {
            const g = d3.select(this);
            const matchesFilter = matchRoutingFilter(l, state.routingFilter);
            const isSel = l.key === state.selectedPairKey;
            const isHov = l.key === state.hoverPairKey;
            const inNeighborhood = nb.mode === "all" || nb.mode === "filter"
                ? true
                : nb.mode === "edge"
                    ? l.key === nb.edgeKey
                    : (l.sourceId === nb.focusNode || l.targetId === nb.focusNode);

            const active = matchesFilter && inNeighborhood;
            const strong = isSel || isHov;
            const isOrdinary = l.routing === "local-ordinary";
            const isDashed = isOrdinary || l.routing === "unlisted";

            g.classed("is-dim", !active)
                .classed("is-selected", isSel)
                .classed("is-hovered", isHov)
                .classed("is-ordinary", isOrdinary)
                .classed("is-remote", Boolean(l.isRealRemote))
                .classed("is-risk", Boolean(l.hasRisk));

            const baseW = isOrdinary ? Math.min(2.1, widthScale(l.count) * 0.72) : widthScale(l.count) + 0.5;
            const sw = baseW + (strong ? 1.2 : 0);
            const r = l.role ?? "neutral";
            g.select("path.sv-d3-edge-path")
                .attr("stroke-width", sw)
                .attr("stroke", ROLE_VAR[r] ?? ROLE_VAR.neutral)
                .attr("stroke-dasharray", isDashed ? (isOrdinary ? "4 3.5" : "6 3.5") : null)
                .attr("marker-end", `url(#${uid}-arr-${r})`);

            g.select("path.sv-d3-edge-flow")
                .attr("stroke", ROLE_VAR[r] ?? ROLE_VAR.primary)
                .style("opacity", (strong && !isOrdinary) || (strong && isSel) ? 0.95 : 0);
        });

        /* Show crisp call-count badges on active/hovered/selected edges, or real-remote edges when filtered */
        badgeLayer.selectAll("g.sv-d3-badge").each(function (l) {
            const b = d3.select(this);
            const matchesFilter = matchRoutingFilter(l, state.routingFilter);
            const show = matchesFilter && (
                l.key === state.selectedPairKey ||
                l.key === state.hoverPairKey ||
                (nb.mode === "node" && (l.sourceId === nb.focusNode || l.targetId === nb.focusNode)) ||
                (state.routingFilter === "remote" || state.routingFilter === "risk")
            );
            b.classed("is-visible", show)
                .classed("is-selected", l.key === state.selectedPairKey || l.key === state.hoverPairKey);
        });

        nodeLayer.selectAll("g.sv-d3-node").each(function (n) {
            const g = d3.select(this);
            const isFocus = n.id === state.focusDomain;
            const isHover = n.id === state.hoverNodeId;
            const inNb = nb.mode === "all" ? true : nb.nodeIds.has(n.id);
            const isPairEnd = Boolean(state.selectedPairKey && state.links.some(l => l.key === state.selectedPairKey && (l.sourceId === n.id || l.targetId === n.id)));

            g.classed("is-dim", !inNb)
                .classed("is-focused", isFocus)
                .classed("is-hovered", isHover)
                .classed("is-endpoint", isPairEnd);
        });
    };

    const redrawGeometry = (animate = false) => {
        const reduced = window.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches;
        const dur = animate && !reduced ? 460 : 0;
        const cardSize = getCardSize(state.layout, state.granularity);
        const portOffsets = computePortOffsets(state.links, posMap);

        guideLayer.selectAll("*").remove();
        if (state.layout === "layered" && tierMeta) {
            const labels = state.texts.tierLabels ?? ["Upstream callers", "Core hubs", "Targets & unlisted"];
            tierMeta.tierXs.forEach((tx, idx) => {
                const bandW = cardSize.w + 28;
                guideLayer.append("rect")
                    .attr("class", "sv-d3-tier-band")
                    .attr("x", tx - bandW / 2)
                    .attr("y", 18)
                    .attr("width", bandW)
                    .attr("height", Math.max(260, tierMeta.virtualH - 36))
                    .attr("rx", 14);
                guideLayer.append("text")
                    .attr("class", "sv-d3-tier-title")
                    .attr("x", tx)
                    .attr("y", 38)
                    .attr("text-anchor", "middle")
                    .text(labels[idx] ?? "");
            });
        } else if (state.layout === "radial" && radialCenter) {
            guideLayer.append("ellipse")
                .attr("class", "sv-d3-radial-ring")
                .attr("cx", radialCenter.cx)
                .attr("cy", radialCenter.cy)
                .attr("rx", radialCenter.rx)
                .attr("ry", radialCenter.ry);
            guideLayer.append("ellipse")
                .attr("class", "sv-d3-radial-inner")
                .attr("cx", radialCenter.cx)
                .attr("cy", radialCenter.cy)
                .attr("rx", radialCenter.rx * 0.54)
                .attr("ry", radialCenter.ry * 0.54);
        }

        const t = dur > 0 ? svg.transition().duration(dur).ease(easeOutCubic) : null;
        const nodeSel = nodeLayer.selectAll("g.sv-d3-node");
        const moveNodes = sel => sel.attr("transform", n => {
            const p = posMap.get(n.id) ?? { x: 0, y: 0 };
            return `translate(${(p.x - cardSize.w / 2).toFixed(1)}, ${(p.y - cardSize.h / 2).toFixed(1)})`;
        });
        if (t) moveNodes(nodeSel.transition(t)); else moveNodes(nodeSel);

        nodeSel.select("rect.sv-d3-card").attr("width", cardSize.w).attr("height", cardSize.h);
        nodeSel.select("rect.sv-d3-card-hatch").attr("width", cardSize.w).attr("height", cardSize.h);
        nodeSel.select("text.sv-d3-node-meta").attr("x", cardSize.w - 11);
        nodeSel.select("text.sv-d3-node-deg").attr("x", cardSize.w - 11);

        edgeLayer.selectAll("g.sv-d3-edge-group").each(function (l) {
            const geom = computeEdgeGeometry(l, posMap, state.layout, radialCenter, portOffsets, cardSize);
            l._mx = geom.mx;
            l._my = geom.my;
            const g = d3.select(this);
            if (t) {
                g.select("path.sv-d3-edge-hit").transition(t).attr("d", geom.d);
                g.select("path.sv-d3-edge-path").transition(t).attr("d", geom.d);
                g.select("path.sv-d3-edge-flow").transition(t).attr("d", geom.d);
            } else {
                g.select("path.sv-d3-edge-hit").attr("d", geom.d);
                g.select("path.sv-d3-edge-path").attr("d", geom.d);
                g.select("path.sv-d3-edge-flow").attr("d", geom.d);
            }
        });

        const badgeSel = badgeLayer.selectAll("g.sv-d3-badge");
        const moveBadges = sel => sel.attr("transform", l => `translate(${(l._mx ?? 0).toFixed(1)}, ${(l._my ?? 0).toFixed(1)})`);
        if (t) moveBadges(badgeSel.transition(t)); else moveBadges(badgeSel);
    };

    const fitView = (animate = true) => {
        if (!state.nodes.length || !posMap.size) return;
        const { width, height } = getBox();
        const cardSize = getCardSize(state.layout, state.granularity);
        let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
        for (const p of posMap.values()) {
            if (p.x - cardSize.w / 2 < minX) minX = p.x - cardSize.w / 2;
            if (p.x + cardSize.w / 2 > maxX) maxX = p.x + cardSize.w / 2;
            if (p.y - cardSize.h / 2 < minY) minY = p.y - cardSize.h / 2;
            if (p.y + cardSize.h / 2 > maxY) maxY = p.y + cardSize.h / 2;
        }
        if (state.layout === "layered") minY = Math.min(minY, 18);
        const padX = 28, padY = 28;
        const spanX = Math.max(1, maxX - minX + padX * 2);
        const spanY = Math.max(1, maxY - minY + padY * 2);
        const k = Math.max(0.36, Math.min(1.15, Math.min(width / spanX, height / spanY)));
        const tx = (width - (minX + maxX) * k) / 2;
        const ty = (height - (minY + maxY) * k) / 2;
        const target = d3.zoomIdentity.translate(tx, ty).scale(k);
        const reduced = window.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches;
        if (animate && !reduced) {
            svg.transition().duration(380).ease(easeOutCubic).call(zoom.transform, target);
        } else {
            svg.call(zoom.transform, target);
        }
    };

    const drag = d3.drag()
        .on("start", function () { d3.select(this).classed("is-dragging", true); })
        .on("drag", function (event, n) {
            const p = posMap.get(n.id);
            if (!p) return;
            p.x += event.dx;
            p.y += event.dy;
            redrawGeometry(false);
        })
        .on("end", function () { d3.select(this).classed("is-dragging", false); });

    const rebuildElements = () => {
        const nameMax = state.granularity === "role" ? 19 : 15;
        const subMax = state.granularity === "role" ? 21 : 15;

        const edgeJoin = edgeLayer.selectAll("g.sv-d3-edge-group")
            .data(state.links, l => l.key)
            .join(enter => {
                const g = enter.append("g").attr("class", "sv-d3-edge-group");
                g.append("path").attr("class", "sv-d3-edge-hit").attr("fill", "none");
                g.append("path").attr("class", "sv-d3-edge-path").attr("fill", "none");
                g.append("path").attr("class", "sv-d3-edge-flow").attr("fill", "none");
                g.append("title");
                return g;
            });

        edgeJoin.select("title").text(l => `${l.sourceId} → ${l.targetId} (${l.count}) · ${l.routingLabel}`);
        edgeJoin
            .on("mouseenter", (_, l) => {
                state.hoverPairKey = l.key;
                applyVisualEmphasis();
                callbacks.onHoverPair?.(l.key);
            })
            .on("mouseleave", () => {
                state.hoverPairKey = null;
                applyVisualEmphasis();
                callbacks.onHoverPair?.(null);
            })
            .on("click", (event, l) => {
                event.stopPropagation();
                callbacks.onSelectPair?.(l.key);
            });

        const badgeJoin = badgeLayer.selectAll("g.sv-d3-badge")
            .data(state.links, l => l.key)
            .join(enter => {
                const g = enter.append("g").attr("class", "sv-d3-badge");
                g.append("rect").attr("x", -14).attr("y", -9.5).attr("width", 28).attr("height", 19).attr("rx", 9.5);
                g.append("text").attr("text-anchor", "middle").attr("dy", "0.34em");
                return g;
            });
        badgeJoin
            .attr("class", l => `sv-d3-badge role-${l.role}`)
            .on("click", (event, l) => {
                event.stopPropagation();
                callbacks.onSelectPair?.(l.key);
            });
        badgeJoin.select("text").text(l => l.runOnRoleCalls > 0 ? `${l.count}R` : String(l.count));

        const nodeJoin = nodeLayer.selectAll("g.sv-d3-node")
            .data(state.nodes, n => n.id)
            .join(enter => {
                const g = enter.append("g")
                    .attr("class", "sv-d3-node")
                    .attr("tabindex", "0")
                    .attr("role", "button");
                g.append("rect").attr("class", "sv-d3-card").attr("rx", 10).attr("filter", `url(#${uid}-shadow)`);
                g.append("rect").attr("class", "sv-d3-card-hatch").attr("rx", 10).attr("fill", `url(#${uid}-hatch)`);
                g.append("circle").attr("class", "sv-d3-dot").attr("cx", 14).attr("cy", 17).attr("r", 4);
                g.append("text").attr("class", "sv-d3-node-name").attr("x", 24).attr("y", 20.5);
                g.append("text").attr("class", "sv-d3-node-sub").attr("x", 24).attr("y", 36.5);
                g.append("text").attr("class", "sv-d3-node-meta").attr("y", 20).attr("text-anchor", "end");
                g.append("text").attr("class", "sv-d3-node-deg").attr("y", 36.5).attr("text-anchor", "end");
                g.append("title");
                return g;
            });

        nodeJoin
            .attr("class", n => `sv-d3-node role-${n.nodeRole}${!n.isCatalog ? " is-unlisted" : ""}${n.isUnhosted || n.isFaulted ? " is-unbacked" : ""}`)
            .attr("aria-label", n => `${n.id} (${n.displayName}) · ${n.svcBadge} · ↓${n.inboundCalls} ↑${n.outboundCalls}`);

        nodeJoin.select("circle.sv-d3-dot").attr("fill", n => ROLE_VAR[n.nodeRole] ?? ROLE_VAR.neutral);
        nodeJoin.select("rect.sv-d3-card-hatch").style("opacity", n => (n.isUnhosted || n.isFaulted) ? 0.45 : 0);
        nodeJoin.select("text.sv-d3-node-name").text(n => truncateText(n.id, nameMax));
        nodeJoin.select("text.sv-d3-node-sub").text(n => truncateText(n.subtitle, subMax));
        nodeJoin.select("text.sv-d3-node-meta").text(n => n.svcBadge);
        nodeJoin.select("text.sv-d3-node-deg").text(n => `↓${n.inboundCalls} ↑${n.outboundCalls}`);
        nodeJoin.select("title").text(n => `${n.id} · ${n.displayName}\n${n.svcBadge} · ↓${n.inboundCalls} ↑${n.outboundCalls}`);

        nodeJoin
            .call(drag)
            .on("mouseenter", (_, n) => {
                state.hoverNodeId = n.id;
                applyVisualEmphasis();
                callbacks.onHoverNode?.(n.id);
            })
            .on("mouseleave", () => {
                state.hoverNodeId = null;
                applyVisualEmphasis();
                callbacks.onHoverNode?.(null);
            })
            .on("click", (event, n) => {
                event.stopPropagation();
                callbacks.onSelectNode?.(n.id);
            })
            .on("keydown", (event, n) => {
                if (event.key === "Enter" || event.key === " ") {
                    event.preventDefault();
                    callbacks.onSelectNode?.(n.id);
                } else if (event.key === "Escape") {
                    callbacks.onClearSelection?.();
                }
            });
    };

    const update = payload => {
        const prevLayout = state.layout;
        const prevGran = state.granularity;
        state = {
            ...state,
            nodes: payload.nodes ?? [],
            links: payload.links ?? [],
            layout: payload.layout ?? "layered",
            granularity: payload.granularity ?? "domain",
            focusDomain: payload.focusDomain ?? null,
            selectedPairKey: payload.selectedPairKey ?? null,
            hoverPairKey: payload.externalHoverPairKey ?? state.hoverPairKey,
            routingFilter: payload.routingFilter ?? "all",
            texts: payload.texts ?? {}
        };

        svg.attr("aria-label", state.texts.ariaLabel ?? "Dependency graph");

        const topologySig = `${state.granularity}|${state.layout}|${state.nodes.map(n => n.id).sort().join(",")}|${state.links.map(l => l.key).sort().join(",")}`;
        const layoutChanged = topologySig !== lastLayoutKey;
        const animateTransition = Boolean(lastLayoutKey) && (prevLayout !== state.layout || prevGran !== state.granularity);

        rebuildElements();

        if (layoutChanged) {
            lastLayoutKey = topologySig;
            const { width, height } = getBox();
            const cardSize = getCardSize(state.layout, state.granularity);
            if (state.layout === "radial") {
                const rad = computeRadialPositions(state.nodes, width, height, cardSize);
                posMap = rad.coords;
                radialCenter = { cx: rad.cx, cy: rad.cy, rx: rad.rx, ry: rad.ry };
                tierMeta = null;
            } else if (state.layout === "force") {
                const frc = computeForcePositions(d3, state.nodes, state.links, width, height, cardSize);
                posMap = frc.coords;
                radialCenter = null;
                tierMeta = null;
            } else {
                const lay = computeLayeredPositions(state.nodes, state.links, width, height, cardSize);
                posMap = lay.coords;
                radialCenter = null;
                tierMeta = lay;
            }
            redrawGeometry(animateTransition);
            fitView(animateTransition);
        } else {
            redrawGeometry(false);
        }

        applyVisualEmphasis();
    };

    const resizeObserver = new ResizeObserver(() => {
        if (state.nodes.length) fitView(false);
    });
    resizeObserver.observe(container);

    return {
        update,
        setExternalHoverPair(key) {
            state.hoverPairKey = key;
            applyVisualEmphasis();
        },
        zoomIn() { svg.transition().duration(220).call(zoom.scaleBy, 1.24); },
        zoomOut() { svg.transition().duration(220).call(zoom.scaleBy, 0.8); },
        fitView() { fitView(true); },
        destroy() {
            resizeObserver.disconnect();
            svg.interrupt().on(".zoom", null);
            root.selectAll("*").interrupt().remove();
        }
    };
}

export function create(container, zoomOutput, reference) {
    let disposed = false;
    const pending = new Set();
    function notify(kind, key) {
        if (disposed) return;
        const call = reference.invokeMethodAsync("GraphInteraction", kind, key).catch(() => {});
        pending.add(call);
        call.finally(() => pending.delete(call));
    }

    const controller = createDomainDependencyGraph(d3, container, {
        onSelectPair: key => notify("pair", key),
        onSelectNode: key => notify("node", key),
        onClearSelection: () => notify("clear", null),
        onHoverPair: key => notify("hoverPair", key),
        onHoverNode: key => notify("hoverNode", key),
        onZoomChange: percent => { zoomOutput.textContent = percent + "%"; }
    });
    return {
        update: model => controller.update(model),
        hover: key => controller.setExternalHoverPair(key),
        zoomIn: () => controller.zoomIn(),
        zoomOut: () => controller.zoomOut(),
        fitView: () => controller.fitView(),
        async dispose() {
            disposed = true;
            controller.destroy();
            await Promise.allSettled(pending);
        }
    };
}
