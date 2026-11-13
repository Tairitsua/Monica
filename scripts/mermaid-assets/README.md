# Offline Mermaid layout assets

The shared Markdown renderer keeps Mermaid 11.12.3 and lazily loads ELK for flowcharts. This directory owns the pinned npm inputs and reproducible bundle generation; it is not part of the .NET build or the runtime installation.

From this directory, run:

```sh
npm ci --ignore-scripts --legacy-peer-deps
npm run build
```

`--legacy-peer-deps` avoids installing a second Mermaid runtime for the layout package's peer dependency. The application already ships that runtime. The lockfile pins ELK, D3 and the bundler; `build.mjs` produces `mermaid-layout-elk.min.mjs` and collects the original licenses of all bundled packages into `mermaid-layout-elk.LICENSE.txt`. Commit both generated assets when updating dependencies. Runtime requests remain local and do not require Node, npm, a CDN or a worker service.

`Monica.UI/wwwroot/js/mo-markdown-mermaid-flowchart.js` adapts the upstream renderer's node and label measurements. It preserves authored semantic shapes, edge directions and node IDs. The existing Markdown renderer owns theme configuration, strict sanitization, render serialization and stale-render cancellation; the immersive reader continues to clone the rendered SVG.
