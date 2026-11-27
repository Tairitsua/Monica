/**
 * A service membership atlas. The provider does not expose service-to-service
 * dependencies, so this controller deliberately renders no inferred links.
 */
import * as d3 from '../../Monica.UI/lib/d3.min.mjs';
import { GraphBase } from '../../Monica.UI/js/d3js/d3-graph-base.js';

const roleColors = {
    primary: 'var(--mud-palette-primary)', secondary: 'var(--mud-palette-secondary)',
    tertiary: 'var(--mud-palette-tertiary)', info: 'var(--mud-palette-info)',
    success: 'var(--mud-palette-success)', warning: 'var(--mud-palette-warning)',
    default: 'var(--mud-palette-text-secondary)'
};
const statusColors = {
    Running: 'var(--mud-palette-success)', Updating: 'var(--mud-palette-info)',
    Unhealthy: 'var(--mud-palette-warning)', Error: 'var(--mud-palette-error)',
    Offline: 'var(--mud-palette-text-secondary)', Unknown: 'var(--mud-palette-text-secondary)'
};

class ServiceGraph extends GraphBase {
    constructor(containerId, options) {
        super(containerId, { ...options, showArrows: false, scaleExtent: [0.1, 4] });
        this.reference = options.dotNetRef;
        this.texts = options.texts;
        this.nodes = [];
        this.pendingCallbacks = new Set();
        this.disposed = false;
        this.currentLayout = 'grid';
        this.topologyKey = null;
        this.cameraAdjusted = false;
        this.forceDistance = 150;
        this.forceStrength = -500;
        this.layer = this.mainGroup.append('g').attr('class', 'nodes-layer');
        this.simulation = d3.forceSimulation().stop()
            .force('charge', d3.forceManyBody().strength(this.forceStrength))
            .force('center', d3.forceCenter(this.width / 2, this.height / 2))
            .force('x', d3.forceX(this.width / 2).strength(.025))
            .force('y', d3.forceY(this.height / 2).strength(.025))
            .force('collision', d3.forceCollide().radius(106));
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
        const nextTopology = JSON.stringify((data.nodes || []).map(node => node.id).sort());
        const topologyChanged = this.topologyKey !== nextTopology;
        const hadGeometry = this.nodes.length > 0;
        this.topologyKey = nextTopology;
        const previous = new Map(this.nodes.map(node => [node.id, node]));
        this.nodes = (data.nodes || []).map(node => ({ ...node,
            x: previous.get(node.id)?.x, y: previous.get(node.id)?.y }));
        this.forceDistance = distance; this.forceStrength = strength;
        this.simulation.stop().force('charge').strength(strength);
        this.simulation.force('collision').radius(Math.max(106, distance / 2));
        this.renderNodes();
        if (topologyChanged || layout !== this.currentLayout) {
            this.setLayout(layout, !hadGeometry || !this.cameraAdjusted);
        } else {
            this.simulation.nodes(this.nodes); this.bindDrag(layout === 'force'); this.updatePositions();
        }
    }

    renderNodes() {
        const enter = this.layer.selectAll('.service-node').data(this.nodes, node => node.id).join(
            enter => {
                const group = enter.append('g').attr('class', 'service-node');
                group.append('rect').attr('class', 'node-surface').attr('x', -91).attr('y', -38)
                    .attr('width', 182).attr('height', 76).attr('rx', 9);
                group.append('rect').attr('class', 'domain-accent').attr('x', -91).attr('y', -22)
                    .attr('width', 3).attr('height', 44).attr('rx', 1.5);
                group.append('text').attr('class', 'node-name').attr('x', -77).attr('y', -12);
                group.append('text').attr('class', 'node-domain').attr('x', -77).attr('y', 5);
                group.append('circle').attr('class', 'status-indicator').attr('cx', -74).attr('cy', 23).attr('r', 3);
                group.append('text').attr('class', 'instance-count').attr('x', -64).attr('y', 27);
                return group;
            }, update => update, exit => exit.interrupt().remove());
        this.nodeElements = enter;
        enter.select('.node-surface').attr('fill', 'var(--mud-palette-surface)')
            .attr('stroke', 'var(--mud-palette-divider)').attr('stroke-width', 1);
        enter.select('.domain-accent').attr('fill', node => roleColors[node.colorRole] || roleColors.default);
        enter.select('.node-name').attr('fill', 'var(--mud-palette-text-primary)')
            .style('font-family', 'var(--mud-typography-default-family)')
            .style('font-size', '12px').style('font-weight', '500').text(node => this.truncate(node.name, 22));
        enter.select('.node-domain').attr('fill', 'var(--mud-palette-text-secondary)')
            .style('font-family', 'var(--mud-typography-default-family)').style('font-size', '10px')
            .text(node => this.truncate(node.domain, 26));
        enter.select('.status-indicator').attr('fill', node => statusColors[node.status] || statusColors.Unknown);
        enter.select('.instance-count').attr('fill', 'var(--mud-palette-text-secondary)')
            .style('font-family', 'var(--mo-font-family-mono)').style('font-size', '10px')
            .text(node => `${node.runningInstances}/${node.totalInstances} · ${this.statusText(node.status)}`);
        enter.style('cursor', 'pointer')
            .on('mouseenter.atlas', (event, node) => {
                d3.select(event.currentTarget).select('.node-surface').attr('stroke', 'var(--mud-palette-primary)');
                this.showTooltip(event, node);
            })
            .on('mouseleave.atlas', event => {
                d3.select(event.currentTarget).select('.node-surface').attr('stroke', 'var(--mud-palette-divider)');
                this.hideTooltip();
            })
            .on('click.atlas', (event, node) => {
                event.stopPropagation(); this.hideTooltip(); this.invokeDotNet('OnNodeClick', node.id);
            })
            .on('contextmenu.atlas', (event, node) => {
                event.preventDefault(); event.stopPropagation(); this.hideTooltip();
                this.invokeDotNet('OnNodeRightClick', node.id, event.pageX, event.pageY);
            });
    }

    setLayout(layout, fit = true) {
        if (this.disposed) return;
        this.currentLayout = layout;
        this.simulation.stop();
        this.nodes.forEach(node => { node.fx = null; node.fy = null; });
        if (layout === 'force') {
            this.simulation.nodes(this.nodes).alpha(1);
            for (let index = 0; index < 130; index++) this.simulation.tick();
        } else if (layout === 'hierarchy') {
            const domains = d3.group(this.nodes, node => node.domain);
            let row = 0;
            for (const members of domains.values()) {
                members.forEach((node, index) => { node.x = 112 + index * 212; node.y = 75 + row * 124; });
                row++;
            }
        } else if (layout === 'circular') {
            const radius = Math.max(140, this.nodes.length * 212 / (2 * Math.PI));
            this.nodes.forEach((node, index) => {
                const angle = index * 2 * Math.PI / this.nodes.length - Math.PI / 2;
                node.x = this.width / 2 + Math.cos(angle) * radius;
                node.y = this.height / 2 + Math.sin(angle) * radius;
            });
        } else {
            const columns = Math.max(1, Math.ceil(Math.sqrt(this.nodes.length)));
            this.nodes.forEach((node, index) => { node.x = 112 + index % columns * 212; node.y = 75 + Math.floor(index / columns) * 124; });
        }
        this.bindDrag(layout === 'force');
        this.updatePositions();
        if (fit) this.resetView();
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

    setForceDistance(distance) {
        this.forceDistance = distance;
        this.simulation.force('collision').radius(Math.max(106, distance / 2));
        if (this.currentLayout === 'force') this.setLayout('force', false);
    }
    setForceStrength(strength) {
        this.forceStrength = strength;
        this.simulation.force('charge').strength(strength);
        if (this.currentLayout === 'force') this.setLayout('force', false);
    }
    updatePositions() {
        if (!this.disposed) this.nodeElements?.attr('transform', node => `translate(${node.x},${node.y})`);
    }
    resetView() {
        if (this.disposed) return;
        this.cameraAdjusted = false;
        this.svg.interrupt();
        if (!this.nodes.length) { this.svg.call(this.zoom.transform, d3.zoomIdentity); return; }
        const minX = d3.min(this.nodes, node => node.x) - 108, maxX = d3.max(this.nodes, node => node.x) + 108;
        const minY = d3.min(this.nodes, node => node.y) - 58, maxY = d3.max(this.nodes, node => node.y) + 58;
        // Large registries keep legible cards and can be explored by panning.
        const scale = Math.min(1.25, Math.max(.85, Math.min(this.width / (maxX - minX), this.height / (maxY - minY)) * .88));
        this.svg.call(this.zoom.transform, d3.zoomIdentity.translate(this.width / 2, this.height / 2)
            .scale(scale).translate(-(minX + maxX) / 2, -(minY + maxY) / 2));
    }
    focusOnNode(id) {
        const node = this.nodes.find(node => node.id === id);
        if (!node || this.disposed) return;
        this.cameraAdjusted = true;
        this.svg.interrupt().call(this.zoom.transform, d3.zoomIdentity.translate(this.width / 2, this.height / 2)
            .scale(1.3).translate(-node.x, -node.y));
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

    showTooltip(event, node) {
        this.hideTooltip();
        const tooltip = document.createElement('div');
        tooltip.className = 'service-tooltip';
        tooltip.setAttribute('role', 'tooltip');
        Object.assign(tooltip.style, { position: 'fixed', background: 'var(--mud-palette-surface)', color: 'var(--mud-palette-text-primary)',
            border: '1px solid var(--mud-palette-divider)', borderRadius: '8px', padding: '16px', fontSize: '12px', lineHeight: '1.6',
            pointerEvents: 'none', zIndex: '10000', maxWidth: '300px', overflowWrap: 'anywhere' });
        const title = document.createElement('strong'); title.textContent = node.name; tooltip.append(title);
        const values = [[this.texts.labels.appId, node.id], [this.texts.labels.domain, node.domain],
            [this.texts.labels.project, node.project], [this.texts.labels.status, this.statusText(node.status)],
            [this.texts.labels.instances, `${node.runningInstances}/${node.totalInstances}`], [this.texts.labels.version, node.version]];
        values.forEach(([label, value]) => {
            if (value === null || value === undefined || value === '') return;
            const row = document.createElement('div'); row.textContent = `${label}: ${value}`; tooltip.append(row);
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
            if (!this.disposed) console.error(`Service atlas callback '${method}' failed.`, error);
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
        this.hideTooltip();
        await Promise.allSettled([...this.pendingCallbacks]);
        this.pendingCallbacks.clear();
    }
}

/** The caller owns the controller and must dispose it before releasing its .NET callback target. */
export function createGraph(containerId, isDarkMode = false, dotNetRef = null, texts = {}) {
    return new ServiceGraph(containerId, { isDarkMode, dotNetRef, texts });
}
