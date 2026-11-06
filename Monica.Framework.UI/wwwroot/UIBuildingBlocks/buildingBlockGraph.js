/**
 * One component-owned architecture graph. Data identities and links come from the
 * real Building Blocks catalog; camera, observers, and callbacks share this lifetime.
 */
import { GraphBase } from '../../Monica.UI/js/d3js/d3-graph-base.js';
import { ForceLayoutManager } from '../../Monica.UI/js/d3js/d3-force-layout.js';
import { createStaticDragBehavior } from '../../Monica.UI/js/d3js/d3-node-interaction.js';
import { createLayoutAlgorithms } from '../../Monica.UI/js/d3js/d3-layout-algorithms.js';
import { createBuildingBlockCardRenderer } from './buildingBlockCardRenderer.js';

const ROLE_COLORS = Object.fromEntries(
    ['primary', 'secondary', 'tertiary', 'info', 'success', 'warning', 'error', 'dark']
        .map(role => [role, 'var(--mud-palette-' + role + ')']));
ROLE_COLORS.default = 'var(--mud-palette-text-secondary)';
const MUTED_INK = 'var(--building-blocks-muted-ink, color-mix(in srgb, var(--mud-palette-text-secondary) 85%, var(--mud-palette-text-primary)))';
const LINK_INK = 'color-mix(in srgb, var(--mud-palette-text-secondary) 70%, var(--mud-palette-text-primary))';

class BuildingBlockGraph {
    constructor(containerId, fallbackDarkMode, dotNetRef) {
        this.containerId = containerId;
        this.dotNetRef = dotNetRef;
        this.pendingCallbacks = new Set();
        this.nodes = [];
        this.links = [];
        this.currentLayout = 'force';
        this.forceDistance = 500;
        this.forceStrength = -300;
        this.selectedId = null;
        this.disposed = false;
        this.isDarkMode = fallbackDarkMode;
        this.frame = null;
        this.autoFitPending = false;
        this.motion = window.matchMedia('(prefers-reduced-motion: reduce)');
        this.reducedMotion = this.motion.matches;
        this.graphBase = new GraphBase(containerId, {
            isDarkMode: this.readDarkMode(),
            showArrows: true,
            arrowSize: 9,
            scaleExtent: [0.002, 4],
            onBackgroundClick: () => this.invokeDotNet('OnSvgBackgroundClick'),
            onZoom: event => this.handleZoom(event)
        });
        this.container = this.graphBase.container;
        this.isDarkMode = this.readDarkMode();
        this.forceManager = new ForceLayoutManager(this.graphBase.width, this.graphBase.height, {
            linkDistance: this.forceDistance, chargeStrength: this.forceStrength, keepFixed: false,
            onDragEnd: () => {
                if (!this.disposed && this.reducedMotion) this.settleForce();
            }
        });
        this.layoutAlgorithms = createLayoutAlgorithms(this.graphBase.width, this.graphBase.height);
        this.cardRenderer = createBuildingBlockCardRenderer(this.isDarkMode, { minWidth: 220, maxWidth: 360 });
        this.graphBase.svg.attr('aria-hidden', 'true');
        this.graphBase.zoom.on('start.building-block-owner', event => {
            if (event.sourceEvent) this.cancelPendingFit();
        });
        this.applyTypography();
        this.installObservers();
    }

    readDarkMode() {
        const container = document.getElementById(this.containerId);
        const theme = container?.closest('[data-theme]')?.getAttribute('data-theme')
            || document.documentElement.getAttribute('data-theme');
        return theme ? theme.endsWith('-dark') : Boolean(this.isDarkMode);
    }

    installObservers() {
        this.resizeObserver = new ResizeObserver(() => this.scheduleMeasure());
        this.resizeObserver.observe(this.container);
        this.themeObserver = new MutationObserver(() => this.scheduleMeasure(true));
        this.themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
        const owner = this.container.closest('[data-theme]');
        if (owner && owner !== document.documentElement) {
            this.themeObserver.observe(owner, { attributes: true, attributeFilter: ['data-theme'] });
        }
        this.onMotionChange = event => {
            if (this.disposed) return;
            this.reducedMotion = event.matches;
            this.cancelPendingFit();
            this.interruptTransitions();
            if (this.currentLayout === 'force' && this.nodes.length) this.settleForce();
            else this.renderPositions();
        };
        this.motion.addEventListener('change', this.onMotionChange);
        this.onFontsReady = () => this.scheduleMeasure(true);
        document.fonts?.addEventListener('loadingdone', this.onFontsReady);
        document.fonts?.ready.then(() => {
            if (!this.disposed) this.scheduleMeasure(true);
        }).catch(() => {});
        this.removalObserver = new MutationObserver(() => {
            if (!this.container.isConnected) {
                this.dispose().catch(error => console.error('Building block graph resource shutdown failed.', error));
            }
        });
        this.removalObserver.observe(document.body, { childList: true, subtree: true });
        this.scheduleMeasure();
    }

    scheduleMeasure(paint = false) {
        if (this.disposed) return;
        this.paintPending ||= paint;
        if (this.frame !== null) return;
        this.frame = requestAnimationFrame(() => {
            this.frame = null;
            if (this.disposed || !this.container.isConnected) return;
            const width = this.container.clientWidth;
            const height = this.container.clientHeight;
            if (width > 0 && height > 0) {
                const changed = width !== this.graphBase.width || height !== this.graphBase.height;
                this.graphBase.width = width;
                this.graphBase.height = height;
                this.graphBase.svg.attr('width', width).attr('height', height).attr('viewBox', [0, 0, width, height]);
                this.layoutAlgorithms.updateDimensions(width, height);
                this.forceManager.width = width;
                this.forceManager.height = height;
                this.forceManager.config.centerX = width / 2;
                this.forceManager.config.centerY = height / 2;
                this.forceManager.simulation.force('center').x(width / 2).y(height / 2);
                if (changed && this.autoFitPending && this.nodes.length) this.fitView(false);
            }
            if (this.paintPending) {
                this.paintPending = false;
                this.isDarkMode = this.readDarkMode();
                this.cardRenderer.updateTheme(this.isDarkMode);
                this.applyTypography();
                this.drawNodes();
                this.renderPositions();
                this.restoreHighlight();
            }
        });
    }

    applyTypography() {
        const family = getComputedStyle(this.graphBase.container).fontFamily;
        this.cardRenderer.setFontFamily(family);
        this.graphBase.svg.style('font-family', family);
    }

    updateGraph(data, options = {}) {
        if (this.disposed) return;
        this.cancelPendingFit();
        this.interruptTransitions();
        this.forceManager.stop();
        const previous = new Map(this.nodes.map(node => [node.id, node]));
        this.nodes = data.nodes.map(node => {
            const old = previous.get(node.id);
            if (old) for (const name of ['x', 'y', 'vx', 'vy', 'fx', 'fy']) node[name] = old[name];
            return node;
        });
        this.links = data.links;
        this.currentLayout = options.layout || this.currentLayout;
        this.forceDistance = options.forceDistance ?? this.forceDistance;
        this.forceStrength = options.forceStrength ?? this.forceStrength;
        this.selectedId = options.selectedId ?? null;
        this.graphBase.mainGroup.selectAll('.links, .nodes').remove();
        this.linkSelection = this.graphBase.mainGroup.append('g').attr('class', 'links').selectAll('path')
            .data(this.links).join('path').attr('class', 'link modern-link')
            .attr('data-source-id', link => link.source).attr('data-target-id', link => link.target)
            .attr('stroke', LINK_INK).attr('stroke-width', 0.8).attr('stroke-opacity', 1)
            .attr('vector-effect', 'non-scaling-stroke')
            .attr('fill', 'none').attr('marker-end', 'url(#' + this.graphBase.markerIds.markerId + ')')
            .style('pointer-events', 'none');
        this.nodeSelection = this.graphBase.mainGroup.append('g').attr('class', 'nodes').selectAll('g')
            .data(this.nodes, node => node.id).join('g').attr('class', 'node')
            .attr('data-node-id', node => node.id).style('cursor', 'pointer');
        // Large catalogs start with an overview; no identities or edges are discarded.
        this.drawNodes(this.nodes.length < 25 ? 1 : 0.14);
        this.bindNodeEvents();
        this.autoFitPending = this.nodes.length > 0;
        this.applyLayout(this.currentLayout);
    }

    drawNodes(scale = d3.zoomTransform(this.graphBase.svg.node()).k) {
        if (this.disposed || !this.nodeSelection) return;
        const detail = scale >= 0.55 && this.nodes.length <= 80 || scale >= 0.85 || this.nodes.length < 25;
        this.detail = detail;
        this.labelsVisible = detail || scale >= 0.14;
        this.nodeSelection.each((node, index, elements) => {
            const element = d3.select(elements[index]);
            element.selectAll('*').remove();
            element.attr('data-alert-level', node.alertLevel || 'none')
                .attr('data-selected', node.id === this.selectedId ? 'true' : 'false')
                .attr('data-detail', detail ? 'full' : 'overview');
            if (detail && node.isComplex) {
                this.cardRenderer.drawCard(element, node);
            } else {
                const role = ROLE_COLORS[node.colorRole] || ROLE_COLORS.default;
                const shape = node.isComplex
                    ? element.append('rect').attr('class', 'node-core').attr('x', -25).attr('y', -17).attr('width', 50).attr('height', 34).attr('rx', 5)
                    : element.append('circle').attr('class', 'node-core').attr('r', detail ? 25 : 16);
                shape.attr('fill', 'color-mix(in srgb, ' + role + (detail ? ' 12%' : ' 32%') + ', var(--mud-palette-surface))')
                    .attr('stroke', 'color-mix(in srgb, ' + role + ' 40%, var(--mud-palette-text-primary))')
                    .attr('vector-effect', detail ? null : 'non-scaling-stroke')
                    .attr('stroke-width', node.id === this.selectedId ? detail ? 3 : 2 : detail ? 1.8 : 1);
                node._cardSize = node.isComplex ? { width: 50, height: 34 } : null;
                if (detail || scale >= 0.14) {
                    element.append('text').attr('class', 'node-title').attr('y', node.isComplex ? 34 : detail ? 43 : 33)
                        .attr('text-anchor', 'middle').attr('fill', 'var(--mud-palette-text-primary)')
                        .style('font-size', '12px').style('font-weight', '500').style('pointer-events', 'none').text(node.title);
                    if (detail && node.summary) {
                        element.append('text').attr('y', 61).attr('text-anchor', 'middle').attr('fill', MUTED_INK)
                            .style('font-size', '11px').style('pointer-events', 'none').text(node.summary);
                    }
                }
            }
            if (node.alertLevel) {
                const size = node._cardSize;
                element.append('circle').attr('class', 'node-alert')
                    .attr('cx', size ? size.width / 2 : detail ? 22 : 14)
                    .attr('cy', size ? -size.height / 2 : detail ? -22 : -14).attr('r', 4)
                    .attr('fill', ROLE_COLORS[node.alertLevel] || ROLE_COLORS.info)
                    .attr('stroke', 'var(--mud-palette-surface)').attr('stroke-width', 1.5);
            }
            element.append('title').text(node.tooltip || node.title);
        });
        this.updateOverviewGeometry(scale);
        this.updateCollisionRadius();
    }

    updateOverviewGeometry(scale) {
        const cameraScale = Math.max(0.002, scale);
        this.overviewScale = cameraScale;
        const markerSize = this.detail ? 9 : Math.max(9, 3.5 / cameraScale);
        this.graphBase.svg.selectAll('defs marker').attr('markerWidth', markerSize).attr('markerHeight', markerSize);
        if (this.detail || !this.nodeSelection) return;
        // Preserve the complete atlas and its class shapes at fitted device scale.
        this.nodeSelection.each((node, index, elements) => {
            const element = d3.select(elements[index]);
            const halfWidth = Math.max(25, 3 / cameraScale);
            const halfHeight = Math.max(17, 2 / cameraScale);
            const radius = Math.max(16, 2.5 / cameraScale);
            node._overviewRadius = radius;
            if (node.isComplex) {
                element.select('.node-core').attr('x', -halfWidth).attr('y', -halfHeight)
                    .attr('width', halfWidth * 2).attr('height', halfHeight * 2);
                node._cardSize = { width: halfWidth * 2, height: halfHeight * 2 };
            } else element.select('.node-core').attr('r', radius);
            element.select('.node-alert').attr('cx', node.isComplex ? halfWidth : radius * 0.875)
                .attr('cy', node.isComplex ? -halfHeight : -radius * 0.875)
                .attr('r', Math.max(4, 1.5 / cameraScale)).attr('vector-effect', 'non-scaling-stroke');
        });
    }

    bindNodeEvents() {
        this.nodeSelection.on('click', (event, node) => {
            event.stopPropagation();
            if (this.disposed) return;
            if (event.detail === 2 && this.currentLayout === 'force') {
                node.fx = null;
                node.fy = null;
                if (this.reducedMotion) this.settleForce();
                else this.forceManager.simulation.alpha(0.3).restart();
                return;
            }
            this.selectNode(node.id);
            this.invokeDotNet('OnNodeClick', node.id);
        }).on('contextmenu', (event, node) => {
            event.preventDefault();
            event.stopPropagation();
            if (this.disposed) return;
            this.selectNode(node.id);
            this.invokeDotNet('OnNodeRightClick', node.id, event.pageX, event.pageY);
        }).on('mouseenter', (event, node) => {
            if (!this.disposed) this.highlightNode(node.id);
        }).on('mouseleave', () => {
            if (!this.disposed) this.restoreHighlight();
        });
    }

    applyLayout(layout) {
        if (this.disposed || !this.nodeSelection) return;
        this.currentLayout = layout;
        this.nodeSelection.on('.drag', null);
        if (layout === 'force') {
            this.forceManager.releaseAllFixed(this.nodes);
            this.forceManager.setData(this.nodes, this.links);
            this.forceManager.simulation.force('link').distance(this.forceDistance);
            this.forceManager.simulation.force('charge').strength(this.forceStrength);
            this.updateCollisionRadius();
            this.nodeSelection.call(this.guardDragBehavior(this.forceManager.getDragBehavior()));
            if (this.reducedMotion) {
                this.settleForce();
                if (this.autoFitPending) this.fitView(false);
            } else {
                this.forceManager.start(() => this.renderPositions());
                this.forceManager.simulation.tick(60);
                this.renderPositions();
                if (this.autoFitPending) this.fitView(false, true);
                this.forceManager.simulation.on('end.building-block-fit', () => {
                    if (!this.disposed && this.autoFitPending) this.fitView(false);
                });
            }
            return;
        }
        this.forceManager.stop();
        if (layout === 'hierarchy') this.layoutAlgorithms.hierarchicalLayout(this.nodes, this.links);
        else if (layout === 'circular') this.layoutAlgorithms.circularLayout(this.nodes, {
            avgNodeSize: this.detail ? 100 : 45, complexNodeCount: this.nodes.filter(node => node.isComplex).length
        });
        else this.layoutAlgorithms.multiCircularLayout(this.nodes, { nodesPerRing: 15 });
        this.nodeSelection.call(this.guardDragBehavior(createStaticDragBehavior({ updateLinks: () => this.renderLinks() })));
        this.renderPositions();
        if (this.autoFitPending) this.fitView(false);
    }

    updateCollisionRadius() {
        this.forceManager?.simulation?.force('collision')?.radius(node => {
            if (this.detail && node._cardSize) return Math.hypot(node._cardSize.width, node._cardSize.height) / 2 + 12;
            return this.detail ? 42 : 26;
        });
    }

    guardDragBehavior(behavior) {
        const owner = this;
        for (const name of ['start', 'drag', 'end']) {
            const handler = behavior.on(name);
            if (handler) behavior.on(name, function (event, node) {
                if (!owner.disposed) return handler.call(this, event, node);
            });
        }
        return behavior;
    }

    settleForce() {
        if (this.disposed || !this.forceManager.simulation) return;
        this.forceManager.simulation.stop().alpha(0.6).tick(90);
        this.renderPositions();
    }

    renderPositions() {
        if (this.disposed || !this.nodeSelection) return;
        this.nodeSelection.attr('transform', node => 'translate(' + (node.x || 0) + ',' + (node.y || 0) + ')');
        this.renderLinks();
    }

    renderLinks() {
        if (this.disposed || !this.linkSelection) return;
        const nodes = new Map(this.nodes.map(node => [node.id, node]));
        this.linkSelection.attr('d', link => {
            const source = nodes.get(link.source.id || link.source);
            const target = nodes.get(link.target.id || link.target);
            if (!source || !target) return '';
            const dx = target.x - source.x, dy = target.y - source.y;
            const length = Math.hypot(dx, dy);
            if (!length) return '';
            const halfWidth = target._cardSize?.width / 2 || (this.detail ? 25 : target._overviewRadius || 16);
            const halfHeight = target._cardSize?.height / 2 || halfWidth;
            const arrowLength = this.detail ? 9 : Math.max(9, 3.5 / (this.overviewScale || 1));
            const offset = target.isComplex
                ? Math.min(Math.abs(dx) > 0.001 ? halfWidth * length / Math.abs(dx) : Infinity,
                    Math.abs(dy) > 0.001 ? halfHeight * length / Math.abs(dy) : Infinity) + arrowLength
                : halfWidth + arrowLength;
            return 'M' + source.x + ',' + source.y + ' L' + (target.x - dx / length * offset) + ',' + (target.y - dy / length * offset);
        });
    }

    handleZoom(event) {
        if (this.disposed || !this.nodeSelection) return;
        const scale = event.transform.k;
        const detail = scale >= 0.55 && this.nodes.length <= 80 || scale >= 0.85 || this.nodes.length < 25;
        const labels = detail || scale >= 0.14;
        if (detail !== this.detail || labels !== this.labelsVisible) {
            this.labelsVisible = labels;
            this.drawNodes();
            this.renderLinks();
            this.restoreHighlight();
        }
        if (!this.detail) {
            this.updateOverviewGeometry(scale);
            this.renderLinks();
        }
        this.graphBase.svg.attr('data-zoom-scale', scale);
    }

    fitView(animate = true, keepPending = false) {
        if (this.disposed || !this.nodes.length || this.graphBase.width <= 0 || this.graphBase.height <= 0) return;
        this.interruptTransitions();
        const group = this.graphBase.mainGroup.node();
        const padding = Math.min(32, this.graphBase.width / 10, this.graphBase.height / 10);
        const range = this.graphBase.zoom.scaleExtent();
        // Keep the fit presentation stable while measuring actual rendered geometry.
        // Zooming or focusing still reveals every card's complete metadata.
        const fitMaximum = this.nodes.length < 25 ? range[1] : this.nodes.length <= 80 ? 0.549 : 0.849;
        const scaleFor = bounds => Math.max(range[0], Math.min(fitMaximum,
            (this.graphBase.width - padding * 2) / Math.max(bounds.width, 1),
            (this.graphBase.height - padding * 2) / Math.max(bounds.height, 1)));
        this.drawNodes(fitMaximum);
        this.renderPositions();
        let bounds = group.getBBox();
        if (![bounds.x, bounds.y, bounds.width, bounds.height].every(Number.isFinite)) return;
        let scale = scaleFor(bounds);
        if (this.nodes.length >= 25 && scale < 0.14) {
            this.drawNodes(scale);
            this.renderPositions();
            bounds = group.getBBox();
            // Hidden labels must not cause a fit that immediately reveals them again.
            scale = Math.min(0.139, scaleFor(bounds));
        }
        if (!this.detail) {
            // Minimum device-size marks affect real bounds; converge before fitting.
            for (let pass = 0; pass < 6; pass++) {
                this.updateOverviewGeometry(scale);
                this.renderLinks();
                bounds = group.getBBox();
                const nextScale = Math.min(this.labelsVisible ? fitMaximum : 0.139, scaleFor(bounds));
                if (Math.abs(nextScale - scale) < 0.000001) break;
                scale = nextScale;
            }
        }
        const transform = d3.zoomIdentity.translate(this.graphBase.width / 2, this.graphBase.height / 2)
            .scale(scale).translate(-bounds.x - bounds.width / 2, -bounds.y - bounds.height / 2);
        this.graphBase.svg.attr('data-fit-width', bounds.width).attr('data-fit-height', bounds.height);
        this.camera(transform, animate ? 220 : 0);
        if (!keepPending) this.autoFitPending = false;
    }

    camera(transform, duration = 220) {
        const svg = this.graphBase.svg.interrupt();
        if (this.reducedMotion || duration === 0) svg.call(this.graphBase.zoom.transform, transform);
        else svg.transition().duration(duration).call(this.graphBase.zoom.transform, transform);
    }

    cancelPendingFit() {
        this.autoFitPending = false;
    }

    resetView() {
        if (this.disposed) return;
        this.cancelPendingFit();
        this.camera(d3.zoomIdentity);
    }

    zoomIn() { this.zoomBy(1.2); }
    zoomOut() { this.zoomBy(1 / 1.2); }
    zoomBy(amount) {
        if (this.disposed) return;
        this.cancelPendingFit();
        const svg = this.graphBase.svg.interrupt();
        if (this.reducedMotion) svg.call(this.graphBase.zoom.scaleBy, amount);
        else svg.transition().duration(160).call(this.graphBase.zoom.scaleBy, amount);
    }

    setLayout(layout) {
        if (this.disposed) return;
        this.cancelPendingFit();
        this.autoFitPending = this.nodes.length > 0;
        this.applyLayout(layout);
    }

    setForceDistance(distance) {
        if (this.disposed) return;
        this.forceDistance = distance;
        if (this.currentLayout !== 'force') return;
        this.forceManager.updateLinkDistance(distance);
        if (this.reducedMotion && this.currentLayout === 'force') this.settleForce();
    }

    setForceStrength(strength) {
        if (this.disposed) return;
        this.forceStrength = strength;
        if (this.currentLayout !== 'force') return;
        this.forceManager.updateChargeStrength(strength);
        if (this.reducedMotion && this.currentLayout === 'force') this.settleForce();
    }

    selectNode(id) {
        if (this.disposed) return;
        this.selectedId = id;
        this.nodeSelection?.attr('data-selected', node => node.id === id ? 'true' : 'false');
        this.restoreHighlight();
    }

    focusOnNode(id) {
        if (this.disposed) return;
        const node = this.nodes.find(candidate => candidate.id === id);
        if (!node || !Number.isFinite(node.x) || !Number.isFinite(node.y)) return;
        this.cancelPendingFit();
        this.selectNode(id);
        this.camera(d3.zoomIdentity.translate(this.graphBase.width / 2, this.graphBase.height / 2)
            .scale(1.5).translate(-node.x, -node.y));
    }

    highlightNode(id) {
        const related = new Set([id]);
        for (const link of this.links) {
            const source = link.source.id || link.source, target = link.target.id || link.target;
            if (source === id) related.add(target);
            if (target === id) related.add(source);
        }
        this.nodeSelection.interrupt().attr('opacity', node => related.has(node.id) ? 1 : 0.25);
        this.linkSelection.interrupt().attr('stroke', link => (link.source.id || link.source) === id
            ? 'var(--mud-palette-info)' : (link.target.id || link.target) === id ? 'var(--mud-palette-success)' : LINK_INK)
            .attr('stroke-width', link => (link.source.id || link.source) === id || (link.target.id || link.target) === id ? 1.8 : 0.8)
            .attr('stroke-opacity', link => related.has(link.source.id || link.source) && related.has(link.target.id || link.target) ? 1 : 0.25)
            .attr('marker-end', link => 'url(#' + ((link.source.id || link.source) === id ? this.graphBase.markerIds.outgoingMarkerId
                : (link.target.id || link.target) === id ? this.graphBase.markerIds.incomingMarkerId : this.graphBase.markerIds.markerId) + ')');
    }

    restoreHighlight() {
        if (!this.nodeSelection || !this.linkSelection) return;
        this.nodeSelection.interrupt().attr('opacity', 1).each((node, index, elements) => {
            d3.select(elements[index]).select('.node-core, .card-background').attr('stroke-width', node.id === this.selectedId ? this.detail ? 3 : 2 : this.detail ? 1.8 : 1);
        });
        this.linkSelection.interrupt().attr('stroke', LINK_INK).attr('stroke-width', 0.8).attr('stroke-opacity', 1)
            .attr('marker-end', 'url(#' + this.graphBase.markerIds.markerId + ')');
    }

    interruptTransitions() {
        this.graphBase?.svg.interrupt();
        this.graphBase?.mainGroup.selectAll('*').interrupt();
    }

    invokeDotNet(methodName, ...args) {
        const reference = this.dotNetRef;
        if (this.disposed || !reference) return;
        const callback = reference.invokeMethodAsync(methodName, ...args).catch(error => {
            if (!this.disposed && this.dotNetRef) console.error('Building block graph callback ' + methodName + ' failed.', error);
        }).finally(() => this.pendingCallbacks.delete(callback));
        this.pendingCallbacks.add(callback);
    }

    dispose() {
        if (this.disposal) return this.disposal;
        this.disposed = true;
        this.dotNetRef = null;
        this.cancelPendingFit();
        this.resizeObserver?.disconnect();
        this.themeObserver?.disconnect();
        this.removalObserver?.disconnect();
        this.motion.removeEventListener('change', this.onMotionChange);
        document.fonts?.removeEventListener('loadingdone', this.onFontsReady);
        if (this.frame !== null) cancelAnimationFrame(this.frame);
        this.frame = null;
        this.interruptTransitions();
        this.nodeSelection?.on('.drag', null).on('click', null).on('contextmenu', null)
            .on('mouseenter', null).on('mouseleave', null);
        this.graphBase.svg.on('.zoom', null).on('click', null);
        this.graphBase.zoom.on('start.building-block-owner', null);
        this.forceManager.simulation?.on('end.building-block-fit', null);
        this.forceManager.dispose();
        // Stop resources only. Blazor owns removal of the graph DOM; GraphBase.dispose clears it.
        this.disposal = Promise.allSettled([...this.pendingCallbacks]).then(() => this.pendingCallbacks.clear());
        return this.disposal;
    }
}

/** Creates an independently owned controller; callers must await its dispose(). */
export function createGraph(containerId, isDarkMode, dotNetRef) {
    return new BuildingBlockGraph(containerId, isDarkMode, dotNetRef);
}
