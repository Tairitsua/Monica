# Repository Manifest Schema v2

Create a UTF-8 JSON manifest and pass it to `scripts/scaffold_repository.py`. Unsupported fields are rejected. The scaffold writes the normalized contract as `monica.manifest.json` at the repository root.

## Complete shape

```json
{
  "schemaVersion": 2,
  "repositoryId": "Acme.Monica.AI.OCR",
  "solutionPath": "Acme.Monica.AI.OCR.slnx",
  "version": "0.1.0-alpha.1",
  "authors": "Acme Engineering",
  "nugetOwner": "Acme",
  "repositoryUrl": "https://github.com/acme/acme-monica-ai-ocr",
  "projectUrl": "https://github.com/acme/acme-monica-ai-ocr",
  "supportUrl": "https://github.com/acme/acme-monica-ai-ocr/issues",
  "securityUrl": "https://github.com/acme/acme-monica-ai-ocr/security/policy",
  "source": {
    "available": true,
    "provider": "github",
    "sourceLinkVersion": "10.0.102"
  },
  "distribution": "public",
  "publishing": { "target": "nuget.org" },
  "targetFramework": "net10.0",
  "monicaVersion": "1.0.0-rc.12",
  "license": { "openSource": true, "expression": "MIT" },
  "branding": {
    "icon": { "kind": "compatibility-mark" },
    "showOpenSourceBadge": false
  },
  "packages": [
    {
      "packageId": "Acme.Monica.AI.OCR",
      "projectPath": "src/Acme.Monica.AI.OCR/Acme.Monica.AI.OCR.csproj",
      "description": "Provider-neutral OCR abstractions.",
      "capabilityTags": ["ai", "ocr"],
      "packageDependencies": [],
      "modules": [
        {
          "name": "Ocr",
          "kind": "infrastructure",
          "key": "Acme.Monica.AI.OCR"
        }
      ]
    },
    {
      "packageId": "Acme.Monica.AI.OCR.PaddleOCR",
      "projectPath": "src/Acme.Monica.AI.OCR.PaddleOCR/Acme.Monica.AI.OCR.PaddleOCR.csproj",
      "description": "PaddleOCR provider connector.",
      "capabilityTags": ["ai", "ocr", "paddleocr"],
      "packageDependencies": ["Acme.Monica.AI.OCR"],
      "modules": [
        {
          "name": "PaddleOCR",
          "kind": "provider",
          "key": "Acme.Monica.AI.OCR.PaddleOCR",
          "dependsOn": ["Acme.Monica.AI.OCR"],
          "providerFor": "Acme.Monica.AI.OCR"
        }
      ]
    },
    {
      "packageId": "Acme.Monica.AI.OCR.UI",
      "projectPath": "src/Acme.Monica.AI.OCR.UI/Acme.Monica.AI.OCR.UI.csproj",
      "description": "Interactive OCR UI.",
      "capabilityTags": ["ai", "ocr", "ui"],
      "packageDependencies": ["Acme.Monica.AI.OCR"],
      "modules": [
        {
          "name": "OcrUI",
          "kind": "ui",
          "key": "Acme.Monica.AI.OCR.UI",
          "dependsOn": ["Acme.Monica.AI.OCR"]
        }
      ]
    }
  ],
  "ociImages": [
    {
      "id": "paddleocr-service",
      "repository": "ghcr.io/acme/monica-ai-ocr-paddleocr",
      "companionPackageId": "Acme.Monica.AI.OCR.PaddleOCR",
      "contextPath": "containers/paddleocr",
      "dockerfilePath": "containers/paddleocr/Dockerfile",
      "bakeFilePath": "docker-bake.hcl",
      "releaseGates": {
        "cpuSmokeCommand": "python scripts/smoke_paddleocr.py --accelerator cpu",
        "nvidiaSmokeCommand": "python scripts/smoke_paddleocr.py --accelerator nvidia",
        "managedNvidiaRunnerLabels": ["self-hosted", "linux", "x64", "nvidia"]
      },
      "targets": [
        {
          "bakeTarget": "paddleocr-cpu-amd64",
          "stage": "runtime-cpu",
          "platform": "linux/amd64",
          "accelerator": "cpu",
          "tagSuffix": "cpu-amd64"
        },
        {
          "bakeTarget": "paddleocr-nvidia-cu129-amd64",
          "stage": "runtime-nvidia-cu129",
          "platform": "linux/amd64",
          "accelerator": "nvidia",
          "tagSuffix": "nvidia-cu129-amd64"
        }
      ]
    }
  ]
}
```

## Repository release unit

- `repositoryId` is the durable publisher-owned repository identity and names the `.slnx` file.
- `version` applies to every declared NuGet package and OCI tag in one release.
- A prerelease Monica version requires a prerelease repository version.
- Contacts, source visibility, distribution, publishing, license, and branding are shared. Split repositories when those policies differ.
- Source provider is `github`, `gitlab`, `azure-repos`, or `none`. Source availability independently controls consumer-visible repository metadata and Source Link.
- Publishing target is `nuget.org`, `private-feed`, or `none`. A private feed requires private distribution and an HTTPS `feedUrl`.

## NuGet packages

- `packages` is non-empty. Package IDs and project paths are unique.
- Every project path is exactly `src/<PackageId>/<PackageId>.csproj`.
- All packages use the repository's publisher segment, version, target framework, source, license, and branding policy.
- `capabilityTags` contains package-specific lowercase tags; ecosystem tags remain scaffold-managed `monica-ecosystem-v1` values.
- `packageDependencies` contains full package IDs from the same manifest. It is the authoritative internal NuGet dependency graph.
- Dependency lookup is case-insensitive, but the normalized manifest and generated project paths always use the target package's canonical declared casing.
- A project reference must exist for each declared internal package dependency and no others.

## Monica modules

- Module kind is `infrastructure`, `web`, `ui`, or `provider`.
- Module keys are globally unique ecosystem/distribution identifiers owned by the containing package. They are not Monica runtime identities; the runtime graph is keyed by concrete module `Type`.
- `dependsOn` contains full manifest module keys from the repository. The scaffold resolves each key to a concrete `module.Require<TModule>()` call in `Describe(ModuleDescriptor)`.
- Generated cross-package module type references are namespace-qualified, so different packages may use the same local module name. Typed option contributions use namespace-qualified option types as well.
- Every cross-package runtime dependency must be backed by `packageDependencies`.
- Provider modules require `providerFor`, include that manifest key in `dependsOn`, implement `IModuleProvider`, and return `typeof(TargetModule)` from `ProvidesFor`.
- UI names end with one exact `UI` suffix, UI manifest keys end in `.UI`, navigation category identity removes only that final segment, and the generated strategy implements `IUIModule` explicitly.
- A `web` strategy implements `IWebModule` and `IWebHostRequiredModule`; web capability and host requirement are expressed by CLR markers rather than key conventions.
- Package and module graphs must be acyclic.

## OCI images and targets

- `ociImages` may be empty.
- One entry represents one registry repository. Put CPU and GPU variants under `targets`; do not duplicate the repository entry.
- `companionPackageId` names the package that connects consumers to the service; that package must own at least one `kind: provider` module.
- Context and Dockerfile paths are repository-relative; `bakeFilePath` names a repository-root Bake file.
- `bakeTarget`, runtime `stage`, `platform`, `accelerator`, and `tagSuffix` are explicit and unique.
- Supported accelerators are `cpu` and `nvidia`; NVIDIA targets currently use `linux/amd64`.
- The release tag is `<repository>:<version>-<tagSuffix>`.
- Each Bake file's target collection and `group.default` must exactly equal the targets declared for that file; release automation must not omit or add targets implicitly.
- The scaffold writes the Bake graph but intentionally does not invent a service Dockerfile. Implement the real service before `validate_repository.py` and `validate_oci.py` can pass.
- `releaseGates` is optional while the service is being implemented. If any declared image omits it, the scaffold omits the complete publish workflow so a partial NuGet/OCI release cannot start.
- When present, `releaseGates.cpuSmokeCommand` is required for CPU targets. `nvidiaSmokeCommand` and `managedNvidiaRunnerLabels` are required for NVIDIA targets; labels include `self-hosted` and `nvidia`, and all NVIDIA images in one release use the same label set.
- Smoke commands are single-line repository commands that own container startup/cleanup, call the connector-facing protocol, assert meaningful provider output and the requested accelerator, and return nonzero on failure. Complete gates run after local image load/inspection and before registry login or any external push.

## Generated validation contract

Generated repositories carry repository/package/OCI validators under `scripts/`. The repository validator resolves direct and centrally managed `Monica.*` package versions, including ordinary MSBuild property indirection, and requires every one to equal `monicaVersion`. A release workflow deriving `PackageVersion` from a tag must pass it to both source and artifact validation so a stable override cannot bypass prerelease dependency rules.

The scaffold is a compilable package-graph starting point, not a releasable capability. Replace all sample behavior, implement declared images, run clean consumers, and exercise real provider paths before publishing.
