import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const source = await readFile(new URL('../../../Monica.UI/wwwroot/js/mo-markdown-mermaid.js', import.meta.url), 'utf8');
const { renderMermaid } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function diagramSurface(bounds, viewBox) {
    const attributes = new Map([['viewBox', viewBox], ['height', '2000']]);
    let inserted = false;
    const svg = {
        style: {},
        getBBox() {
            assert.ok(inserted, 'Geometry must be measured after the SVG enters its final container');
            return bounds;
        },
        setAttribute: (name, value) => attributes.set(name, value),
        removeAttribute: name => attributes.delete(name)
    };
    const element = {
        set innerHTML(value) { inserted = value.length > 0; },
        querySelector: () => svg
    };
    return { element, svg, attributes };
}

function deferred() {
    let resolve;
    const promise = new Promise(complete => { resolve = complete; });
    return { promise, resolve };
}

function unwritableSurface() {
    return {
        set innerHTML(_) { assert.fail('A retired render must not replace the existing diagram'); },
        querySelector() { assert.fail('A retired render must not measure or bind the diagram'); },
        replaceChildren() { assert.fail('A retired render must not clear the existing diagram'); }
    };
}

test('rendered diagrams fit their content when staging bounds are oversized or clip nodes', async () => {
    let config;
    let boundElement;
    const mermaid = {
        initialize(value) { config = value; },
        async render() { return { svg: '<svg></svg>', bindFunctions: element => { boundElement = element; } }; }
    };
    const previousWindow = globalThis.window;
    globalThis.window = { mermaid };
    try {
        for (const initialViewBox of ['-138 -47 2146 2055', '-134 -42 1003 92']) {
            const bounds = { x: 8, y: 8, width: 1105, height: 68 };
            const { element, svg, attributes } = diagramSurface(bounds, initialViewBox);
            await renderMermaid(element, 'flowchart LR\nA-->B', { fontFamily: ['sans-serif'] });

            const [x, y, width, height] = attributes.get('viewBox').split(' ').map(Number);
            assert.ok(x < bounds.x && y < bounds.y);
            assert.ok(x + width > bounds.x + bounds.width && y + height > bounds.y + bounds.height);
            assert.ok(width < bounds.width + 40 && height < bounds.height + 40, 'Canvas must not retain the temporary blank area');
            assert.equal(attributes.get('width'), '100%');
            assert.equal(attributes.has('height'), false);
            assert.equal(Number.parseFloat(svg.style.maxWidth), width);
            assert.equal(config.htmlLabels, false);
            assert.equal(config.flowchart.htmlLabels, false);
            assert.equal(config.securityLevel, 'strict');
            assert.equal(boundElement, element);
        }
    } finally {
        globalThis.window = previousWindow;
    }
});

test('an empty or invalid measurement does not replace the renderer viewport', async () => {
    const previousWindow = globalThis.window;
    globalThis.window = { mermaid: { initialize() {}, async render() { return { svg: '<svg></svg>' }; } } };
    try {
        for (const bounds of [{ x: 0, y: 0, width: 0, height: 0 }, { x: 0, y: 0, width: NaN, height: 50 }]) {
            const { element, attributes } = diagramSurface(bounds, '0 0 100 50');
            await renderMermaid(element, 'flowchart LR\nA-->B', { fontFamily: ['sans-serif'] });
            assert.equal(attributes.get('viewBox'), '0 0 100 50');
        }
    } finally {
        globalThis.window = previousWindow;
    }
});

test('retiring an in-flight render leaves the existing diagram and handlers untouched', async () => {
    const started = deferred();
    const completed = deferred();
    let current = true;
    let bound = false;
    const previousWindow = globalThis.window;
    globalThis.window = { mermaid: {
        initialize() {},
        async render() {
            started.resolve();
            await completed.promise;
            return { svg: '<svg></svg>', bindFunctions() { bound = true; } };
        }
    } };
    const pending = renderMermaid(unwritableSurface(), 'flowchart LR\nA-->B',
        { fontFamily: ['sans-serif'] }, () => current);
    try {
        await started.promise;
        current = false;
        completed.resolve();

        assert.equal(await pending, false);
        assert.equal(bound, false);
    } finally {
        completed.resolve();
        await pending.catch(() => {});
        globalThis.window = previousWindow;
    }
});

test('a retired queued request skips Mermaid without blocking following diagrams', async () => {
    const started = deferred();
    const completed = deferred();
    const calls = [];
    let current = true;
    const previousWindow = globalThis.window;
    globalThis.window = { mermaid: {
        initialize() {},
        async render(_, definition) {
            calls.push(definition);
            if (definition === 'first') {
                started.resolve();
                await completed.promise;
            }
            return { svg: '<svg></svg>' };
        }
    } };
    const theme = { fontFamily: ['sans-serif'] };
    const surface = () => diagramSurface({ x: 0, y: 0, width: 100, height: 50 }, '0 0 100 50').element;
    const first = renderMermaid(surface(), 'first', theme);
    const pending = [first];
    try {
        await started.promise;
        pending.push(renderMermaid(unwritableSurface(), 'retired', theme, () => current));
        pending.push(renderMermaid(surface(), 'following', theme));
        current = false;
        completed.resolve();

        assert.deepEqual(await Promise.all(pending), [true, false, true]);
        assert.deepEqual(calls, ['first', 'following']);
    } finally {
        completed.resolve();
        await Promise.allSettled(pending);
        globalThis.window = previousWindow;
    }
});

test('each async diagram keeps its global theme and a failed render does not poison the queue', async () => {
    const started = deferred();
    const completed = deferred();
    const colors = [];
    const failure = new Error('Invalid first diagram');
    let activeColor;
    let rendering = false;
    const previousWindow = globalThis.window;
    globalThis.window = { mermaid: {
        initialize(config) {
            assert.equal(rendering, false, 'Another request must not reconfigure an in-flight diagram');
            activeColor = config.themeVariables.primaryColor;
        },
        async render(_, definition) {
            rendering = true;
            const initialColor = activeColor;
            try {
                if (definition === 'invalid') {
                    started.resolve();
                    await completed.promise;
                }
                colors.push([definition, initialColor, activeColor]);
                if (definition === 'invalid') throw failure;
                return { svg: '<svg></svg>' };
            } finally {
                rendering = false;
            }
        }
    } };
    const theme = primary => ({ primary, fontFamily: ['sans-serif'] });
    const failed = renderMermaid(unwritableSurface(), 'invalid', theme('first-color'));
    const rejected = assert.rejects(failed, error => error === failure);
    let following;
    try {
        await started.promise;
        const { element } = diagramSurface({ x: 0, y: 0, width: 100, height: 50 }, '0 0 100 50');
        following = renderMermaid(element, 'valid', theme('second-color'));
        completed.resolve();

        await rejected;
        assert.equal(await following, true);
        assert.deepEqual(colors, [
            ['invalid', 'first-color', 'first-color'],
            ['valid', 'second-color', 'second-color']
        ]);
    } finally {
        completed.resolve();
        await Promise.allSettled([failed, rejected, following]);
        globalThis.window = previousWindow;
    }
});
