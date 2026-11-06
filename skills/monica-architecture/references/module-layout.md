# Framework module layout

Use a layer only when it has a clear responsibility. Root layers work for a cohesive module; feature folders work when a project contains distinct capabilities such as `Chat/` and `RAG/`. Each feature can repeat the relevant layers. Shared types stay at project root, and one feature must not reach into another feature's internal `Services/`.

| Folder | Contract |
| --- | --- |
| `Modules/` | Public `Module{Name}`, `Module{Name}Option`, builder and registration extensions; registration only. Keep related types together by default. |
| `Abstractions/` and `Models/` | Public interfaces and data contracts consumed across modules. Put private variants under `Internal/`. |
| `Annotations/` | Public developer-facing attributes. |
| `Facades/` | Public host/UI use cases and result-envelope conversion. |
| `Services/` | Internal implementation and orchestration. `Support/` may contain registries, resolvers, coordinators, policies, and factories. |
| `Providers/{Provider}/` | Internal adapters for vendor SDKs, external systems, storage, or file I/O. |
| `Metrics/` | Meter names, instruments, state, and adapters. Expose only names that hosts must subscribe to. |
| `Events/`, `Exceptions/`, `Extensions/` | Public only when deliberately part of the module contract. |
| `Utils/` | Pure internal utilities; `Tools/` remains reserved for AI tool providers. |

Prefer a scannable flat folder with descriptive, related names. Add subfolders for real ownership or visibility boundaries, especially `Internal/` and `Providers/{Provider}/`, not merely to reduce file counts. A layout review should consider whether movement alone suffices or whether the active task also warrants API and abstraction changes; do not let an old folder template block a coherent redesign.

Facades normalize inputs, coordinate a use case, and map known failures to `Result` or `Result<T>`. They are not cross-module contracts. Services use direct return types and exceptions. Providers implement abstractions and do not return result envelopes or manage UI state. A public type should have useful XML documentation explaining its contract.
