# Guide ownership and framework payloads

The standalone Monica.Guide repository owns the private implementation of the free local client: engine, CLI, and Interactive Server UI. It uses the original lightweight Blazor shell and its own paper/ink theme, without Monica Application UI. A Guide domain, application role and published requests retain the engine/presentation boundary. Neither Framework nor product runtimes reference the Guide engine.

This Framework repository owns `skills/monica-guide` and `.monica/agent-skill-catalog.json`. `scripts/build_framework_bundle.py` produces the versioned skills-only `monica-framework-v<version>-<rid>.zip`; it contains no Guide executable. The client is downloaded independently from https://monica.dpdns.org/downloads. Complete Guide downloads embed the default module bundles for offline workspace setup. Later module updates do not require rebuilding the client.

Product descriptors in the client's selected `catalog.json` define identity, release feed, platforms, and runtime capabilities. They are data, not executable extensions. Adding a product does not require changes to common workspace and skill screens. Product download endpoints own access control; installed products remain usable offline.

The engine owns preview/apply plans, digests, exact skill replacement, shared workspace configuration, per-product instruction blocks, and the shared ownership ledger. A workspace document uses schema 2 with a `products` map plus shared `skillTargets` and Claude-import ownership. Removal of one product preserves every other selection. Unsupported old state must be configured again; do not add migration adapters.

Monica.Docs remains developer documentation and the download frontend. It has no Guide source binding or profile. Agent-relevant documentation belongs in canonical Framework skills: architecture and invocation rules in application skills, operational recipes in infrastructure skills, API details in their focused references. Do not clone the Docs source to make installation work.

The UI uses Monica Application UI, theme roles, localization, and published Guide command/query APIs. It never accesses the engine or filesystem directly. The local host retains its random per-run `/s/<token>/` URL and loopback binding. A distribution website only serves files; it cannot manage arbitrary directories on a visitor's computer.

Isolate machine state with `MONICA_GUIDE_DATA_ROOT` and product state with `MONICA_GUIDE_PRODUCT_DATA_ROOT_*` during development. These settings do not redirect agent home directories. Browser checks must review actual targets before applying a plan; never install into the user's global skill roots as a smoke test.

The UI has one workspace setup flow with module checkboxes, shared skill targets and one AGENTS.md preview/apply. It has no product switcher, install-version picker or bundle-path field. Updates and health are separate common pages; runtime controls and source bindings follow declared module capabilities.
