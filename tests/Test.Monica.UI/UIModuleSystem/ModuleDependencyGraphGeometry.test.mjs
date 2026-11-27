import assert from "node:assert/strict";

const {
    calculateDependencyEdgeEndpoints,
    calculateLayeredDependencyLayout,
    calculateRadialDependencyLayout
} = await import("../../../Monica.UI/wwwroot/js/module-dependency-graph.js");

const forward = calculateDependencyEdgeEndpoints(
    { x: 0, y: 0 },
    { x: 100, y: 0 },
    23,
    31);
assert.deepEqual(forward, { x1: 25, y1: 0, x2: 63, y2: 0 });

const reverse = calculateDependencyEdgeEndpoints(
    { x: 100, y: 0 },
    { x: 0, y: 0 },
    31,
    23);
assert.deepEqual(reverse, { x1: 67, y1: 0, x2: 29, y2: 0 });

const diagonal = calculateDependencyEdgeEndpoints(
    { x: 0, y: 0 },
    { x: 30, y: 40 },
    3,
    4);
assert.deepEqual(diagonal, { x1: 3, y1: 4, x2: 24, y2: 32 });

assert.equal(calculateDependencyEdgeEndpoints(
    { x: 4, y: 4 },
    { x: 4, y: 4 },
    23,
    23), null);

const layered = calculateLayeredDependencyLayout([
    { id: "root", label: "Root", depth: 0 },
    { id: "first", label: "First", depth: 1 },
    { id: "second", label: "Second", depth: 1 }
], 800, 500);
const layeredById = new Map(layered.positions.map(position => [position.id, position]));
assert.ok(layeredById.get("root").x < layeredById.get("first").x);
assert.equal(layeredById.get("first").x, layeredById.get("second").x);
assert.notEqual(layeredById.get("first").y, layeredById.get("second").y);

const radial = calculateRadialDependencyLayout([
    { id: "root", label: "Root", depth: 0 },
    { id: "outer", label: "Outer", depth: 2 }
], 720, 480);
const radialById = new Map(radial.positions.map(position => [position.id, position]));
const center = { x: radial.width / 2, y: radial.height / 2 };
assert.deepEqual(radialById.get("root"), { id: "root", ...center });
assert.ok(Math.hypot(
    radialById.get("outer").x - center.x,
    radialById.get("outer").y - center.y) > 200);
