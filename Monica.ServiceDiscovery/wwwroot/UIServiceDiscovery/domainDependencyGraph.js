/**
 * A directed atlas of compiled API usages, with observed service membership. Dashed endpoints
 * identify domain names absent from the catalog; they are not registered domains.
 */
import { GraphBase } from '../../Monica.UI/js/d3js/d3-graph-base.js';

const roleColors = {
    primary: 'var(--mud-palette-primary)', secondary: 'var(--mud-palette-secondary)',
    tertiary: 'var(--mud-palette-tertiary)', info: 'var(--mud-palette-info)',
    success: 'var(--mud-palette-success)', warning: 'var(--mud-palette-warning)',
    default: 'var(--mud-palette-text-secondary)'
};

class DomainDependencyGraph extends GraphBase {
    constructor(containerId, options) {
        super(containerId, { ...options, showArrows: true, arrowSize: 8, scaleExtent: [.1, 4] });
        this.reference = options.dotNetHelper;
        this.texts = options.texts;
        this.nodes = []; this.links = [];
        this.pendingCallbacks = new Set();
        this.disposed = false;
        this.currentLayout = 'force';
        this.topologyKey = null;
        this.cameraAdjusted = false;
        this.forceDistance = 200; this.forceStrength = -800;
        this.linkLayer = this.mainGroup.append('g').attr('class', 'links-layer');
        this.nodeLayer = this.mainGroup.append('g').attr('class', 'nodes-layer');
        this.simulation = d3.forceSimulation().stop()
            .force('link', d3.forceLink().id(node => node.id).distance(this.forceDistance))
            .force('charge', d3.forceManyBody().strength(this.forceStrength))
            .force('center', d3.forceCenter(this.width / 2, this.height / 2))
            .force('x', d3.forceX(this.width / 2).strength(.045))
            .force('y', d3.forceY(this.height / 2).strength(.045))
            .force('collision', d3.forceCollide().radius(80));
        this.simulation.on('tick', () => this.updatePositions());
        this.resizeObserver = new ResizeObserver(() => this.resize());
        this.resizeObserver.observe(this.container);
        this.zoom.on('zoom.atlas', event => {
            if (event.sourceEvent) this.cameraAdjusted = true;
        });
        this.svg.on('click.atlas', event => {
            if (event.target === this.svg.node()) this.invokeDotNet('OnSvgBackgroundClick');
        });
    }

    updateGraph(data, layout = this.currentLayout, distance = this.forceDistance, strength = this.forceStrength) {
        if (this.disposed) return;
        const nextTopology = JSON.stringify([(data.nodes || []).map(node => node.id).sort(),
            (data.links || []).map(link => [link.source, link.target]).sort()]);
        const topologyChanged = this.topologyKey !== nextTopology;
        const hadGeometry = this.nodes.length > 0;
        this.topologyKey = nextTopology;
        const previous = new Map(this.nodes.map(node => [node.id, node]));
        this.nodes = (data.nodes || []).map(node => ({ ...node,
            x: previous.get(node.id)?.x, y: previous.get(node.id)?.y }));
        const known = new Set(this.nodes.map(node => node.id));
        this.links = (data.links || []).filter(link => known.has(link.source) && known.has(link.target)).map(link => ({ ...link }));
        this.forceDistance = distance; this.forceStrength = strength;
        this.simulation.stop().force('charge').strength(strength);
        this.simulation.force('link').distance(distance);
        this.render();
        if (topologyChanged || layout !== this.currentLayout) {
            this.setLayout(layout, !hadGeometry || !this.cameraAdjusted);
        } else {
            this.simulation.nodes(this.nodes); this.simulation.force('link').links(this.links);
            this.bindDrag(layout === 'force'); this.updatePositions();
        }
    }

    render() {
        this.linkElements = this.linkLayer.selectAll('.domain-link').data(this.links, link => JSON.stringify([this.nodeId(link.source), this.nodeId(link.target)]))
            .join('path').attr('class', 'domain-link').attr('fill', 'none')
            .attr('stroke', 'color-mix(in srgb, var(--mud-palette-primary) 40%, var(--mud-palette-text-secondary))')
            .attr('stroke-width', 1.5).attr('marker-end', `url(#${this.markerIds.markerId})`)
            .on('mouseenter.atlas', (event, link) => this.showTooltip(event,
                `${this.nodeId(link.source)} → ${this.nodeId(link.target)}`,
                [[this.texts.labels.dependencyServiceCount, link.evidenceCount]]))
            .on('mouseleave.atlas', () => this.hideTooltip());
        this.nodeElements = this.nodeLayer.selectAll('.domain-node').data(this.nodes, node => node.id).join(
            enter => {
                const group = enter.append('g').attr('class', 'domain-node');
                group.append('circle').attr('class', 'node-circle').attr('r', 29);
                group.append('text').attr('class', 'node-count').attr('y', 4).attr('text-anchor', 'middle');
                group.append('text').attr('class', 'node-name').attr('y', 51).attr('text-anchor', 'middle');
                return group;
            }, update => update, exit => exit.interrupt().remove());
        this.nodeElements.select('.node-circle')
            .attr('fill', node => `color-mix(in srgb, ${roleColors[node.colorRole] || roleColors.default} 10%, var(--mud-palette-surface))`)
            .attr('stroke', node => roleColors[node.colorRole] || roleColors.default)
            .attr('stroke-width', 1.5).attr('stroke-dasharray', node => node.isCatalogDomain ? null : '4 3');
        this.nodeElements.select('.node-count').attr('fill', 'var(--mud-palette-text-primary)')
            .style('font-family', 'var(--mo-font-family-mono)').style('font-size', '15px')
            .text(node => node.isCatalogDomain ? (node.services?.length ?? 0) : '?');
        this.nodeElements.select('.node-name').attr('fill', 'var(--mud-palette-text-primary)')
            .style('font-family', 'var(--mud-typography-default-family)').style('font-size', '12px').style('font-weight', '500')
            .text(node => this.truncate(node.name, 22));
        this.nodeElements.style('cursor', 'pointer')
            .on('mouseenter.atlas', (event, node) => {
                this.highlight(node.id);
                const rows = [[this.texts.labels.serviceCount, node.services?.length ?? 0]];
                if (!node.isCatalogDomain) rows.push(['', this.texts.labels.unlisted]);
                if (node.description) rows.push(['', node.description]);
                for (const service of node.services || []) {
                    rows.push(['', `${service.name ?? service.appName} · ${this.statusText(service.status ?? service.overallStatus)}`]);
                    rows.push([this.texts.labels.appId, service.appId]);
                    if (service.roles?.length) rows.push([this.texts.labels.roles, service.roles.join(', ')]);
                }
                this.showTooltip(event, node.name, rows);
            })
            .on('mouseleave.atlas', () => { this.highlight(null); this.hideTooltip(); })
            .on('click.atlas', (event, node) => {
                event.stopPropagation(); this.hideTooltip(); this.focusOnNode(node.id);
                this.invokeDotNet('OnDomainClickFromJS', node.id);
            })
            .on('contextmenu.atlas', (event, node) => {
                event.preventDefault(); event.stopPropagation(); this.hideTooltip();
                this.invokeDotNet('OnDomainRightClick', node.id, event.pageX, event.pageY);
            });
    }

    setLayout(layout, fit = true) {
        if (this.disposed) return;
        this.currentLayout = layout;
        this.simulation.stop();
        this.nodes.forEach(node => { node.fx = null; node.fy = null; });
        if (layout === 'force') {
            this.simulation.nodes(this.nodes); this.simulation.force('link').links(this.links); this.simulation.alpha(1);
            for (let index = 0; index < 140; index++) this.simulation.tick();
        } else if (layout === 'hierarchy') {
            const degree = new Map(this.nodes.map(node => [node.id, 0]));
            this.links.forEach(link => { degree.set(this.nodeId(link.source), degree.get(this.nodeId(link.source)) + 1); degree.set(this.nodeId(link.target), degree.get(this.nodeId(link.target)) + 1); });
            const groups = [...d3.group(this.nodes, node => degree.get(node.id))].sort((left, right) => right[0] - left[0]);
            groups.forEach(([, nodes], row) => nodes.forEach((node, index) => { node.x = 100 + index * 160; node.y = 85 + row * 150; }));
        } else if (layout === 'radial_tree') {
            this.layoutRadial();
        } else {
            const radius = Math.max(120, this.nodes.length * 160 / (2 * Math.PI));
            this.nodes.forEach((node, index) => {
                const angle = index * 2 * Math.PI / this.nodes.length - Math.PI / 2;
                node.x = this.width / 2 + Math.cos(angle) * radius; node.y = this.height / 2 + Math.sin(angle) * radius;
            });
        }
        this.bindDrag(layout === 'force'); this.updatePositions();
        if (fit) this.resetView();
    }

    layoutRadial() {
        const children = new Map(this.nodes.map(node => [node.id, []]));
        const incoming = new Set();
        this.links.forEach(link => { children.get(this.nodeId(link.source))?.push(this.nodeId(link.target)); incoming.add(this.nodeId(link.target)); });
        const depth = new Map();
        const traverse = root => {
            const queue = [[root.id, 0]];
            while (queue.length) {
                const [id, level] = queue.shift();
                if (depth.has(id)) continue;
                depth.set(id, level);
                for (const child of children.get(id) || []) if (!depth.has(child)) queue.push([child, level + 1]);
            }
        };
        this.nodes.filter(node => !incoming.has(node.id)).forEach(traverse);
        // Every disconnected component and directed cycle gets a deterministic root.
        this.nodes.filter(node => !depth.has(node.id)).forEach(traverse);
        const rings = d3.group(this.nodes, node => depth.get(node.id));
        for (const [level, nodes] of rings) {
            const radius = Math.max(100 + level * 160, nodes.length * 160 / (2 * Math.PI));
            nodes.forEach((node, index) => {
                const angle = index * 2 * Math.PI / nodes.length - Math.PI / 2;
                node.x = this.width / 2 + Math.cos(angle) * radius; node.y = this.height / 2 + Math.sin(angle) * radius;
            });
        }
    }

    bindDrag(force) {
        this.nodeElements.call(d3.drag()
            .on('start', (event, node) => {
                if (this.disposed) return;
                this.hideTooltip();
                this.cameraAdjusted = true;
                if (force) { this.simulation.alphaTarget(.15).restart(); node.fx = node.x; node.fy = node.y; }
            })
            .on('drag', (event, node) => {
                if (this.disposed) return;
                if (force) { node.fx = event.x; node.fy = event.y; }
                else { node.x = event.x; node.y = event.y; this.updatePositions(); }
            })
            .on('end', (event, node) => {
                if (this.disposed) return;
                if (force) { this.simulation.alphaTarget(0); node.fx = null; node.fy = null; }
            }));
    }
    nodeId(reference) { return typeof reference === 'object' ? reference.id : reference; }
    findNode(reference) { const id = this.nodeId(reference); return this.nodes.find(node => node.id === id); }
    linkPath(link) {
        const source = this.findNode(link.source), target = this.findNode(link.target);
        if (!source || !target) return '';
        if (source.id === target.id) return `M${source.x - 20},${source.y - 22} C${source.x - 90},${source.y - 110} ${source.x + 90},${source.y - 110} ${source.x + 23},${source.y - 30}`;
        const dx = target.x - source.x, dy = target.y - source.y, distance = Math.hypot(dx, dy);
        if (!distance) return '';
        const startX = source.x + dx / distance * 30, startY = source.y + dy / distance * 30;
        const endX = target.x - dx / distance * 38, endY = target.y - dy / distance * 38;
        // Curved directed edges keep reciprocal declarations visually distinct.
        const middleX = (startX + endX) / 2 - dy / distance * 22, middleY = (startY + endY) / 2 + dx / distance * 22;
        return `M${startX},${startY} Q${middleX},${middleY} ${endX},${endY}`;
    }
    updatePositions() {
        if (this.disposed) return;
        this.nodeElements?.attr('transform', node => `translate(${node.x},${node.y})`);
        this.linkElements?.attr('d', link => this.linkPath(link));
    }
    highlight(id) {
        this.linkElements?.attr('stroke-width', link => id && (this.nodeId(link.source) === id || this.nodeId(link.target) === id) ? 2.5 : 1.5);
        this.nodeElements?.select('.node-circle').attr('stroke-width', node => node.id === id ? 2.5 : 1.5);
    }
    setForceDistance(distance) {
        this.forceDistance = distance; this.simulation.force('link').distance(distance);
        if (this.currentLayout === 'force') this.setLayout('force', false);
    }
    setForceStrength(strength) {
        this.forceStrength = strength; this.simulation.force('charge').strength(strength);
        if (this.currentLayout === 'force') this.setLayout('force', false);
    }
    resetView() {
        if (this.disposed) return;
        this.cameraAdjusted = false;
        this.svg.interrupt();
        if (!this.nodes.length) { this.svg.call(this.zoom.transform, d3.zoomIdentity); return; }
        const minX = d3.min(this.nodes, node => node.x) - 98, maxX = d3.max(this.nodes, node => node.x) + 98;
        const minY = d3.min(this.nodes, node => node.y) - 100, maxY = d3.max(this.nodes, node => node.y) + 78;
        // Larger topologies remain pannable instead of shrinking working labels into illegible marks.
        const scale = Math.min(1.5, Math.max(.85, Math.min(this.width / (maxX - minX), this.height / (maxY - minY)) * .88));
        this.svg.call(this.zoom.transform, d3.zoomIdentity.translate(this.width / 2, this.height / 2)
            .scale(scale).translate(-(minX + maxX) / 2, -(minY + maxY) / 2));
    }
    focusOnNode(id) {
        const node = this.nodes.find(node => node.id === id);
        if (!node || this.disposed) return;
        this.cameraAdjusted = true;
        this.svg.interrupt().call(this.zoom.transform, d3.zoomIdentity.translate(this.width / 2, this.height / 2)
            .scale(1.5).translate(-node.x, -node.y));
    }
    resize() {
        if (this.disposed) return;
        const width = this.container.clientWidth, height = this.container.clientHeight;
        if (!width || !height || (width === this.width && height === this.height)) return;
        const previousWidth = this.width, previousHeight = this.height;
        const camera = d3.zoomTransform(this.svg.node());
        this.width = width; this.height = height;
        this.svg.attr('width', width).attr('height', height).attr('viewBox', [0, 0, width, height]);
        this.simulation.force('center', d3.forceCenter(width / 2, height / 2));
        this.simulation.force('x').x(width / 2); this.simulation.force('y').y(height / 2);
        if (this.cameraAdjusted) {
            this.svg.call(this.zoom.transform, d3.zoomIdentity
                .translate(camera.x + (width - previousWidth) / 2, camera.y + (height - previousHeight) / 2).scale(camera.k));
        } else {
            this.resetView();
        }
    }
    statusText(status) { return this.texts.statuses?.[String(status).toLowerCase()] || this.texts.statuses?.unknown || status; }
    truncate(value, length) { const text = String(value ?? ''); return text.length > length ? `${text.slice(0, length - 1)}…` : text; }
    showTooltip(event, titleValue, values) {
        this.hideTooltip();
        const tooltip = document.createElement('div'); tooltip.className = 'domain-tooltip'; tooltip.setAttribute('role', 'tooltip');
        Object.assign(tooltip.style, { position: 'fixed', background: 'var(--mud-palette-surface)', color: 'var(--mud-palette-text-primary)',
            border: '1px solid var(--mud-palette-divider)', borderRadius: '8px', padding: '16px', fontSize: '12px', lineHeight: '1.6',
            pointerEvents: 'none', zIndex: '10000', maxWidth: '300px', maxHeight: '60vh', overflow: 'hidden', overflowWrap: 'anywhere' });
        const title = document.createElement('strong'); title.textContent = titleValue; tooltip.append(title);
        values.forEach(([label, value]) => {
            const row = document.createElement('div'); row.textContent = label ? `${label}: ${value}` : value; tooltip.append(row);
        });
        document.body.append(tooltip); this.tooltip = tooltip;
        const bounds = tooltip.getBoundingClientRect();
        tooltip.style.left = `${Math.max(12, Math.min(event.clientX + 14, innerWidth - bounds.width - 12))}px`;
        tooltip.style.top = `${Math.max(12, Math.min(event.clientY + 14, innerHeight - bounds.height - 12))}px`;
    }
    hideTooltip() { this.tooltip?.remove(); this.tooltip = null; }
    invokeDotNet(method, ...args) {
        const reference = this.reference;
        if (this.disposed || !reference) return Promise.resolve();
        const task = reference.invokeMethodAsync(method, ...args).catch(error => {
            if (!this.disposed) console.error(`Domain atlas callback '${method}' failed.`, error);
        }).finally(() => this.pendingCallbacks.delete(task));
        this.pendingCallbacks.add(task); return task;
    }
    async dispose() {
        if (this.disposed) return;
        this.disposed = true; this.reference = null;
        this.resizeObserver.disconnect(); this.simulation.stop().on('tick', null);
        this.svg.interrupt().on('.atlas', null).on('.zoom', null);
        this.zoom.on('zoom', null).on('zoom.atlas', null);
        this.nodeElements?.interrupt().on('.atlas', null).on('.drag', null);
        this.linkElements?.interrupt().on('.atlas', null); this.hideTooltip();
        await Promise.allSettled([...this.pendingCallbacks]); this.pendingCallbacks.clear();
    }
}

/** The caller owns the controller and must dispose it before releasing its .NET callback target. */
export function createGraph(containerId, isDarkMode = false, dotNetHelper = null, texts = {}) {
    return new DomainDependencyGraph(containerId, { isDarkMode, dotNetHelper, texts });
}
