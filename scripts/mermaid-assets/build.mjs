import { build } from 'esbuild';
import { readFile, writeFile, mkdir, readdir } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(fileURLToPath(import.meta.url));
const output = process.argv[2] ? resolve(process.argv[2])
    : resolve(root, '../../Monica.UI/wwwroot/lib/mermaid');
await mkdir(output, { recursive: true });
const result = await build({
    absWorkingDir: root,
    stdin: { contents: "export { default } from '@mermaid-js/layout-elk';", resolveDir: root, sourcefile: 'layout-elk-entry.mjs' },
    bundle: true, format: 'esm', platform: 'browser', target: 'es2022', minify: true,
    legalComments: 'eof', metafile: true,
    outfile: join(output, 'mermaid-layout-elk.min.mjs')
});

const packages = new Set();
for (const input of Object.keys(result.metafile.inputs)) {
    const match = input.replaceAll('\\', '/').match(/node_modules\/((?:@[^/]+\/)?[^/]+)/);
    if (match) packages.add(match[1]);
}
const notices = [];
for (const name of [...packages].sort()) {
    const directory = join(root, 'node_modules', name);
    const metadata = JSON.parse(await readFile(join(directory, 'package.json'), 'utf8'));
    const license = (await readdir(directory)).find(file => /^license(?:\.md|\.txt)?$/i.test(file));
    if (!license) throw new Error(`Missing license for bundled package ${name}`);
    notices.push(`${name} ${metadata.version} (${metadata.license})\n${'='.repeat(72)}\n${await readFile(join(directory, license), 'utf8')}`);
}
await writeFile(join(output, 'mermaid-layout-elk.LICENSE.txt'), notices.join('\n\n'));
console.log(`Bundled ${packages.size} packages into ${output}`);
