/**
 * D3.js node interaction module
 * Provides interactive functions such as node hovering, clicking, and highlighting.
 * 
 * @module d3-node-interaction
 */

import * as d3 from '../../lib/d3.min.mjs';
import { getModernLinkStyle } from './d3-graph-base.js';

/**
 * Node Highlight Manager
 */
export class NodeHighlightManager {
    constructor(options = {}) {
        this.highlightedNodes = new Set();
        this.highlightedLinks = new Set();
        this.fadeOpacity = options.fadeOpacity || 0.2;
        this.normalOpacity = options.normalOpacity || 1;
        this.highlightStrokeWidth = options.highlightStrokeWidth || 3;
        this.normalStrokeWidth = options.normalStrokeWidth || 2;
        this.isDarkMode = options.isDarkMode || false;
        
        // Save marker IDs
        this.markerIds = options.markerIds || null;
        
        // Get modern style configuration and pass in marker IDs
        this.normalLinkStyle = getModernLinkStyle(this.isDarkMode, false, this.markerIds);
        this.highlightLinkStyle = getModernLinkStyle(this.isDarkMode, true, this.markerIds);
    }
    
    /**
     * Highlight nodes and their associated connections
     * @param {string} nodeId - node ID
     * @param {Array} nodes - all nodes
     * @param {Array} links - all links
     * @param {Object} nodeSelection - D3 node selection
     * @param {Object} linkSelection - D3 connection selection
     */
    highlightNode(nodeId, nodes, links, nodeSelection, linkSelection) {
        // Clear previous highlights
        this.clearHighlight(nodeSelection, linkSelection);
        
        // Find relevant nodes and connections and record directions
        const relatedNodes = new Set([nodeId]);  // 包含当前节点
        const outgoingLinks = new Set(); // 出边（当前节点 -> 其他节点）
        const incomingLinks = new Set(); // 入边（其他节点 -> 当前节点）
        
        links.forEach(link => {
            const sourceId = link.source.id || link.source;
            const targetId = link.target.id || link.target;
            
            if (sourceId === nodeId) {
                relatedNodes.add(targetId);
                outgoingLinks.add(link); // 出边
            } else if (targetId === nodeId) {
                relatedNodes.add(sourceId);
                incomingLinks.add(link); // 入边
            }
        });
        
        // Apply highlight effects to convey directional information
        this.applyHighlight(relatedNodes, outgoingLinks, incomingLinks, nodeSelection, linkSelection);
    }
    
    /**
     * Apply highlight effect
     */
    applyHighlight(relatedNodes, outgoingLinks, incomingLinks, nodeSelection, linkSelection) {
        const self = this;
        
        // Highlight nodes - use the same opacity for all related nodes
        nodeSelection.each(function(d) {
            const node = d3.select(this);
            const isRelated = relatedNodes.has(d.id);
            const primaryShape = self.getPrimaryShapeSelection(node);
            
            node
                .transition()
                .duration(200)
                .attr('opacity', isRelated ? 1 : self.fadeOpacity);

            // Control node body element border
            primaryShape
                .transition()
                .duration(200)
                .attr('stroke-width', isRelated ? self.highlightStrokeWidth : self.normalStrokeWidth);
            
            // For complex nodes, enhance the shadow effect
            if (isRelated && d.isComplex) {
                const shadowFilter = node.select('filter feDropShadow');
                if (!shadowFilter.empty()) {
                    shadowFilter
                        .transition()
                        .duration(200)
                        .attr('stdDeviation', 5)
                        .attr('flood-opacity', 0.25);
                }
            }
            
            // Control the transparency of all text elements (including titles, dependent numbers, chip text, etc.)
        });
        
        // Highlight connections - use different colors depending on direction
        linkSelection.each(function(d) {
            const link = d3.select(this);
            const isOutgoing = outgoingLinks.has(d);
            const isIncoming = incomingLinks.has(d);
            const isRelated = isOutgoing || isIncoming;
            
            if (isRelated) {
                const style = self.highlightLinkStyle;
                // Using MudBlazor color system variables
                // The outgoing edge uses the Info color system and the incoming edge uses the Success color system.
                const strokeColor = isOutgoing ? 
                    (self.isDarkMode ? 'var(--mud-palette-info-lighten)' : 'var(--mud-palette-info)') :
                    (self.isDarkMode ? 'var(--mud-palette-success-lighten)' : 'var(--mud-palette-success)');
                
                link.transition()
                    .duration(200)
                    .style('opacity', style.strokeOpacity)
                    .attr('stroke-width', style.strokeWidth)
                    .attr('stroke', strokeColor)
                    .attr('marker-end', isOutgoing ? 
                        `url(#${self.markerIds?.outgoingMarkerId || 'arrow-highlight-outgoing'})` : 
                        `url(#${self.markerIds?.incomingMarkerId || 'arrow-highlight-incoming'})`)
                    .style('filter', style.filter);
            } else {
                link.transition()
                    .duration(200)
                    .style('opacity', self.fadeOpacity)
                    .attr('stroke-width', function() { return self.getLinkBaseAttribute(this, 'data-base-stroke-width', self.normalLinkStyle.strokeWidth); })
                    .attr('stroke', function() { return self.getLinkBaseAttribute(this, 'data-base-stroke', self.normalLinkStyle.stroke); })
                    .attr('marker-end', function() { return self.getLinkBaseAttribute(this, 'data-base-marker-end', self.normalLinkStyle.markerEnd); })
                    .style('filter', function() { return self.getLinkBaseAttribute(this, 'data-base-filter', self.normalLinkStyle.filter) || null; });
            }
        });
        
        // Merge outgoing and incoming edges
        const allRelatedLinks = new Set([...outgoingLinks, ...incomingLinks]);
        this.highlightedNodes = relatedNodes;
        this.highlightedLinks = allRelatedLinks;
    }
    
    /**
     * clear highlight
     */
    clearHighlight(nodeSelection, linkSelection) {
        const self = this;
        
        // Restoring Nodes - Ensures all nodes are restored to full opacity
        nodeSelection.each(function(d) {
            const node = d3.select(this);
            const primaryShape = self.getPrimaryShapeSelection(node);
            
            node
                .transition()
                .duration(200)
                .attr('opacity', 1);  // 恢复为完全不透明

            primaryShape
                .transition()
                .duration(200)
                .attr('stroke-width', self.normalStrokeWidth);
            
            // For complex nodes, restore normal shading
            if (d && d.isComplex) {
                const shadowFilter = node.select('filter feDropShadow');
                if (!shadowFilter.empty()) {
                    shadowFilter
                        .transition()
                        .duration(200)
                        .attr('stdDeviation', 3)
                        .attr('flood-opacity', 0.15);
                }
            }
            
        });
        
        // Restore your connection - restore with a modern style
        linkSelection
            .transition()
            .duration(200)
            .style('opacity', function() { return self.getLinkBaseAttribute(this, 'data-base-opacity', self.normalLinkStyle.strokeOpacity); })
            .attr('stroke-width', function() { return self.getLinkBaseAttribute(this, 'data-base-stroke-width', self.normalLinkStyle.strokeWidth); })
            .attr('stroke', function() { return self.getLinkBaseAttribute(this, 'data-base-stroke', self.normalLinkStyle.stroke); })
            .attr('marker-end', function() { return self.getLinkBaseAttribute(this, 'data-base-marker-end', self.normalLinkStyle.markerEnd); })
            .style('filter', function() { return self.getLinkBaseAttribute(this, 'data-base-filter', self.normalLinkStyle.filter) || null; });
        
        this.highlightedNodes.clear();
        this.highlightedLinks.clear();
    }

    /**
     * Gets the primary drawable element for a node.
     * Supports grouped nodes and direct shape selections.
     * @param {Object} nodeSelection - D3 selection for the node root element
     * @returns {Object} D3 selection for the primary node shape
     */
    getPrimaryShapeSelection(nodeSelection) {
        const preferredShape = nodeSelection.select('.node-core, rect.card-background, circle, rect');
        return preferredShape.empty() ? nodeSelection : preferredShape;
    }

    getLinkBaseAttribute(linkElement, attributeName, fallbackValue) {
        const value = linkElement.getAttribute(attributeName);
        return value === null || value === '' ? fallbackValue : value;
    }
}

/**
 * node interaction handler
 */
export class NodeInteractionHandler {
    constructor(options = {}) {
        this.onClick = options.onClick;
        this.onRightClick = options.onRightClick;
        this.onHover = options.onHover;
        this.onHoverOut = options.onHoverOut;
        this.onDoubleClick = options.onDoubleClick;
        // Pass isDarkMode and markerIds to highlightManager
        const highlightOptions = options.highlightOptions || {};
        highlightOptions.isDarkMode = options.isDarkMode;
        highlightOptions.markerIds = options.markerIds; // 传递marker IDs
        this.highlightManager = new NodeHighlightManager(highlightOptions);
    }
    
    /**
     * Bind node interaction events
     * @param {Object} nodeSelection - D3 node selection
     * @param {Object} context - context object, including nodes, links, linkSelection
     */
    bindNodeEvents(nodeSelection, context) {
        const self = this;
        
        nodeSelection
            .on('click', function(event, d) {
                event.stopPropagation();
                
                // Double click processing
                if (event.detail === 2) {
                    if (self.onDoubleClick) {
                        self.onDoubleClick.call(this, event, d);
                    }
                } else if (self.onClick) {
                    self.onClick.call(this, event, d);
                }
            })
            .on('contextmenu', function(event, d) {
                event.preventDefault();
                event.stopPropagation();
                
                if (self.onRightClick) {
                    // Get the actual position of the node in the page
                    const transform = d3.select(this).attr('transform');
                    const matrix = this.getCTM();
                    const pt = this.ownerSVGElement.createSVGPoint();
                    pt.x = d.x || 0;
                    pt.y = d.y || 0;
                    const screenPt = pt.matrixTransform(matrix);
                    
                    // Use native event objects or D3 event objects
                    const nativeEvent = event.sourceEvent || event;
                    
                    self.onRightClick.call(this, event, d, {
                        x: screenPt.x,
                        y: screenPt.y,
                        pageX: nativeEvent.pageX,
                        pageY: nativeEvent.pageY,
                        clientX: nativeEvent.clientX,
                        clientY: nativeEvent.clientY
                    });
                }
            })
            .on('mouseenter', function(event, d) {
                // Highlight relevant nodes and connections
                if (context && context.nodes && context.links && context.linkSelection) {
                    self.highlightManager.highlightNode(
                        d.id,
                        context.nodes,
                        context.links,
                        nodeSelection,
                        context.linkSelection
                    );
                }
                
                if (self.onHover) {
                    self.onHover.call(this, event, d);
                }
            })
            .on('mouseleave', function(event, d) {
                // clear highlight
                if (context && context.linkSelection) {
                    self.highlightManager.clearHighlight(
                        nodeSelection,
                        context.linkSelection
                    );
                }
                
                if (self.onHoverOut) {
                    self.onHoverOut.call(this, event, d);
                }
            });
    }
    
    /**
     * Clear all highlights
     */
    clearAllHighlights(nodeSelection, linkSelection) {
        this.highlightManager.clearHighlight(nodeSelection, linkSelection);
    }
}

/**
 * Create universal drag behavior (for non-force-directed layouts)
 */
export function createStaticDragBehavior(options = {}) {
    let startX, startY;
    
    return d3.drag()
        .on('start', function(event, d) {
            startX = d.x;
            startY = d.y;
            
            d3.select(this).raise(); // 将节点提到最前
            
            if (options.onStart) {
                options.onStart.call(this, event, d);
            }
        })
        .on('drag', function(event, d) {
            d.x = event.x;
            d.y = event.y;
            
            // Update node location
            d3.select(this)
                .attr('transform', `translate(${d.x},${d.y})`);
            
            // Update related connections
            if (options.updateLinks) {
                options.updateLinks(d);
            }
            
            if (options.onDrag) {
                options.onDrag.call(this, event, d);
            }
        })
        .on('end', function(event, d) {
            // Optional: Add the ability to snap to grid
            if (options.snapToGrid) {
                const gridSize = options.gridSize || 10;
                d.x = Math.round(d.x / gridSize) * gridSize;
                d.y = Math.round(d.y / gridSize) * gridSize;
                
                d3.select(this)
                    .transition()
                    .duration(200)
                    .attr('transform', `translate(${d.x},${d.y})`);
            }
            
            if (options.onEnd) {
                options.onEnd.call(this, event, d);
            }
        });
}
