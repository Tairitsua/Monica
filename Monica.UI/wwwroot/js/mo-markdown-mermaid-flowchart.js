import layouts from '../lib/mermaid/mermaid-layout-elk.min.mjs';

const rectangularShapes = new Set(['rect', 'squareRect', 'proc', 'process', 'rectangle', 'rounded', 'roundedRect', 'event']);

function padEdgeLabel(label, edge) {
    const background = label.querySelector('rect.background');
    const text = label.querySelector('text');
    if (!edge.label || !background || !text) return;

    const bounds = text.getBBox();
    const height = Math.max(26, bounds.height + 8);
    background.setAttribute('x', bounds.x - 12);
    background.setAttribute('y', bounds.y - (height - bounds.height) / 2);
    background.setAttribute('width', bounds.width + 24);
    background.setAttribute('height', height);
    background.style.removeProperty('stroke');

    // ELK must reserve the padded pill, not just its text, when routing adjacent edges.
    const padded = label.getBBox();
    edge.width = padded.width;
    edge.height = padded.height;
    label.parentElement.setAttribute('transform', `translate(${-padded.x - padded.width / 2}, ${-padded.y - padded.height / 2})`);
}

function refineArrowheads(svg) {
    for (const marker of svg.node().querySelectorAll('marker')) {
        const start = /-pointStart(?:_|$)/.test(marker.id);
        const end = /-pointEnd(?:_|$)/.test(marker.id);
        if (!start && !end) continue;
        marker.setAttribute('viewBox', '-6 -6 12 12');
        marker.setAttribute('markerWidth', '10');
        marker.setAttribute('markerHeight', '10');
        marker.setAttribute('refX', '0');
        marker.setAttribute('refY', '0');
        const path = marker.querySelector('path');
        path.setAttribute('d', start ? 'M 3 -3 L 0 0 L 3 3' : 'M -3 -3 L 0 0 L -3 3');
        path.style.fill = 'none';
        path.style.strokeWidth = '1.2';
        path.style.strokeDasharray = 'none';
    }
}

export async function render(data, svg, helpers, options) {
    const renderer = await layouts[0].loader();
    for (const node of data.nodes) {
        if (node.isGroup || !rectangularShapes.has(node.shape)) continue;
        node.padding = 24;
        node.height = Math.max(node.height ?? 0, 60);
        node.rx = 12;
        node.ry = 12;
    }
    await renderer.render(data, svg, {
        ...helpers,
        async insertEdgeLabel(parent, edge) {
            const label = await helpers.insertEdgeLabel(parent, edge);
            padEdgeLabel(label, edge);
            return label;
        }
    }, options);
    refineArrowheads(svg);
}
