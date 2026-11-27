import { build } from 'esbuild';
import { mkdir, readFile, readdir, unlink, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(fileURLToPath(import.meta.url));
const packages = join(root, 'node_modules');
const repository = resolve(root, '../..');
const uiLib = join(repository, 'Monica.UI/wwwroot/lib');
const mermaidDirectory = join(uiLib, 'mermaid');

const options = {
    absWorkingDir: root,
    bundle: true,
    format: 'esm',
    platform: 'browser',
    target: 'es2022',
    minify: true,
    legalComments: 'eof',
    metafile: true
};

function bundledPackages(result) {
    const directories = new Set();
    for (const output of Object.values(result.metafile.outputs)) {
        for (const [input, { bytesInOutput }] of Object.entries(output.inputs)) {
            if (!bytesInOutput) continue;
            const match = input.replaceAll('\\', '/').match(/^(?:.*\/)?node_modules\/(?:@[^/]+\/)?[^/]+\//);
            if (match) directories.add(resolve(root, match[0].slice(0, -1)));
        }
    }
    return directories;
}

async function writeNotices(destination, directories) {
    const notices = [];
    for (const directory of [...directories].sort()) {
        const { name, version, license } = JSON.parse(await readFile(join(directory, 'package.json'), 'utf8'));
        const files = (await readdir(directory)).filter(file => /^licen[cs]e(?:\.(?:md|txt))?$/i.test(file)).sort();
        if (!files.length) throw new Error('Missing license for bundled package ' + name);
        const terms = await Promise.all(files.map(async file => (await readFile(join(directory, file), 'utf8')).trimEnd()));
        notices.push(name + ' ' + version + ' (' + license + ')\n' + '='.repeat(72) + '\n' + terms.join('\n\n'));
    }
    await writeFile(destination, notices.join('\n\n') + '\n');
}

async function copyBrowserScript(name, source, destination) {
    await mkdir(dirname(destination), { recursive: true });
    const script = await readFile(join(packages, name, source), 'utf8');
    const withoutSourceMap = script.replace(/\r?\n?\/\/# sourceMappingURL=[^\r\n]*\s*$/, '');
    await writeFile(destination, withoutSourceMap.trimEnd() + '\n');
}

const d3 = await build({
    ...options,
    stdin: {
        contents: [
            'export { drag, forceCenter, forceCollide, forceLink, forceManyBody, forceSimulation,',
            '         forceX, forceY, group, max, min, select, zoom, zoomIdentity, zoomTransform } from "d3";'
        ].join('\n'),
        resolveDir: root,
        sourcefile: 'd3-graph.mjs'
    },
    outfile: join(uiLib, 'd3.min.mjs')
});
await writeNotices(join(uiLib, 'd3.LICENSE.txt'), bundledPackages(d3));

const mermaid = await build({
    ...options,
    entryPoints: { 'mermaid.min': 'mermaid' },
    outdir: mermaidDirectory,
    splitting: true,
    chunkNames: 'chunks/[name]-[hash]',
    outExtension: { '.js': '.mjs' }
});
await writeNotices(join(mermaidDirectory, 'THIRD-PARTY-NOTICES.txt'), bundledPackages(mermaid));

// Keep only this build's generated chunks. The directory contains no authored files.
const chunkDirectory = join(mermaidDirectory, 'chunks');
const outputs = new Set(Object.keys(mermaid.metafile.outputs).map(file => resolve(root, file)));
for (const file of await readdir(chunkDirectory)) {
    const path = join(chunkDirectory, file);
    if (file.endsWith('.mjs') && !outputs.has(path)) await unlink(path);
}

const cronDirectory = join(repository, 'Monica.JobScheduler.UI/wwwroot/lib/cronstrue');
await copyBrowserScript('cronstrue', 'dist/cronstrue-i18n.min.js', join(cronDirectory, 'cronstrue-i18n.min.js'));
await writeNotices(join(cronDirectory, 'cronstrue.LICENSE.txt'), [join(packages, 'cronstrue')]);
await copyBrowserScript('@microsoft/signalr', 'dist/browser/signalr.min.js',
    join(repository, 'Monica.SignalR/wwwroot/UISignalR/signalr.min.js'));
