/**
 * D3.js force-directed layout module
 * Provides layout algorithms and configurations for force-directed graphs
 * 
 * @module d3-force-layout
 */

import * as d3 from '../../lib/d3.min.mjs';

/**
 * Force-directed layout configuration
 */
export class ForceLayoutConfig {
    constructor(options = {}) {
        this.linkDistance = options.linkDistance || 150;
        this.chargeStrength = options.chargeStrength || -300;
        this.collisionRadius = options.collisionRadius || 50;
        this.centerX = options.centerX || 0;
        this.centerY = options.centerY || 0;
        this.alphaDecay = options.alphaDecay || 0.0228; // 默认值
        this.velocityDecay = options.velocityDecay || 0.4; // 默认值
    }
}

/**
 * Create a force-directed simulator
 * @param {Object} config - force-directed configuration
 * @returns {Object} D3 force-directed simulator
 */
export function createForceSimulation(config = new ForceLayoutConfig()) {
    const simulation = d3.forceSimulation()
        .alphaDecay(config.alphaDecay)
        .velocityDecay(config.velocityDecay);
    
    // Set various forces
    simulation
        .force('link', d3.forceLink()
            .id(d => d.id)
            .distance(config.linkDistance))
        .force('charge', d3.forceManyBody()
            .strength(config.chargeStrength))
        .force('center', d3.forceCenter(config.centerX, config.centerY))
        .force('collision', d3.forceCollide()
            .radius(config.collisionRadius));
    
    return simulation;
}

/**
 * Update force steering parameters
 * @param {Object} simulation - force-directed simulator
 * @param {string} forceName - the name of the force
 * @param {Object} params - parameter configuration
 */
export function updateForceParameter(simulation, forceName, params) {
    const force = simulation.force(forceName);
    if (!force) return;
    
    switch (forceName) {
        case 'link':
            if (params.distance !== undefined) {
                force.distance(params.distance);
            }
            break;
        case 'charge':
            if (params.strength !== undefined) {
                force.strength(params.strength);
            }
            break;
        case 'collision':
            if (params.radius !== undefined) {
                force.radius(params.radius);
            }
            break;
        case 'center':
            if (params.x !== undefined && params.y !== undefined) {
                simulation.force('center', d3.forceCenter(params.x, params.y));
            }
            break;
    }
    
    // Reheat simulation
    simulation.alpha(0.3).restart();
}

/**
 * Force-directed drag behavior
 */
export class ForceDragBehavior {
    constructor(simulation, options = {}) {
        this.simulation = simulation;
        this.options = options;
        this.keepFixed = options.keepFixed || false; // 是否保持固定
    }
    
    createDrag() {
        const self = this;
        
        return d3.drag()
            .on('start', function(event, d) {
                if (!event.active) {
                    self.simulation.alphaTarget(0.3).restart();
                }
                // Double click to release pin
                if (event.sourceEvent && event.sourceEvent.detail === 2) {
                    d.fx = null;
                    d.fy = null;
                } else {
                    d.fx = d.x;
                    d.fy = d.y;
                }
                
                if (self.options.onStart) {
                    self.options.onStart.call(this, event, d);
                }
            })
            .on('drag', function(event, d) {
                d.fx = event.x;
                d.fy = event.y;
                
                if (self.options.onDrag) {
                    self.options.onDrag.call(this, event, d);
                }
            })
            .on('end', function(event, d) {
                if (!event.active) {
                    self.simulation.alphaTarget(0);
                }
                
                // If not held fixed, release the node
                if (!self.keepFixed) {
                    d.fx = null;
                    d.fy = null;
                }
                
                if (self.options.onEnd) {
                    self.options.onEnd.call(this, event, d);
                }
            });
    }
}

/**
 * Force-directed layout manager
 */
export class ForceLayoutManager {
    constructor(width, height, options = {}) {
        this.width = width;
        this.height = height;
        
        // Create configuration
        this.config = new ForceLayoutConfig({
            centerX: width / 2,
            centerY: height / 2,
            ...options
        });
        
        // Create emulator
        this.simulation = createForceSimulation(this.config);
        
        // Create drag behavior
        this.dragBehavior = new ForceDragBehavior(this.simulation, {
            keepFixed: options.keepFixed || false,
            onStart: options.onDragStart,
            onDrag: options.onDrag,
            onEnd: options.onDragEnd
        });
    }
    
    /**
     * Set data
     */
    setData(nodes, links) {
        this.simulation.nodes(nodes);
        if (this.simulation.force('link')) {
            this.simulation.force('link').links(links);
        }
        return this;
    }
    
    /**
     * Update connection distance
     */
    updateLinkDistance(distance) {
        this.config.linkDistance = distance;
        updateForceParameter(this.simulation, 'link', { distance });
        return this;
    }
    
    /**
     * Update repulsion strength
     */
    updateChargeStrength(strength) {
        this.config.chargeStrength = strength;
        updateForceParameter(this.simulation, 'charge', { strength });
        return this;
    }
    
    /**
     * Set whether to keep nodes fixed
     */
    setKeepFixed(keepFixed) {
        this.dragBehavior.keepFixed = keepFixed;
        return this;
    }
    
    /**
     * Release all pinned nodes
     */
    releaseAllFixed(nodes) {
        nodes.forEach(node => {
            node.fx = null;
            node.fy = null;
        });
        this.simulation.alpha(0.3).restart();
        return this;
    }
    
    /**
     * Start simulation
     */
    start(onTick) {
        this.simulation.on('tick', onTick);
        this.simulation.alpha(1).restart();
        return this;
    }
    
    /**
     * Stop simulation
     */
    stop() {
        this.simulation.stop();
        return this;
    }
    
    /**
     * Get drag behavior
     */
    getDragBehavior() {
        return this.dragBehavior.createDrag();
    }
    
    /**
     * destroy
     */
    dispose() {
        this.stop();
        this.simulation = null;
    }
}