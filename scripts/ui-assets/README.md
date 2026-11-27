# Offline UI assets

This build locks the third-party browser sources in `package-lock.json`. The generated files ship through Razor static web assets. .NET builds and running hosts consume the generated files directly.

With Node 22 installed, run these commands from this directory.

```sh
npm ci --ignore-scripts
npm run build
```

- D3 builds the graph APIs Monica uses into `Monica.UI/wwwroot/lib/d3.min.mjs`.
- Mermaid builds into `Monica.UI/wwwroot/lib/mermaid/`. Browser imports load the diagram-specific chunks and the built-in ELK layout on demand.
- cRonstrue and SignalR use their published browser builds in JobScheduler.UI and SignalR. The rebuild removes source-map references because the maps are outside the deployed assets.

Commit the generated files and their notices with lockfile changes. The SignalR npm package declares MIT and omits the license text. Its [upstream license](https://github.com/dotnet/aspnetcore/blob/v10.0.11/LICENSE.txt) is included alongside the browser script.
