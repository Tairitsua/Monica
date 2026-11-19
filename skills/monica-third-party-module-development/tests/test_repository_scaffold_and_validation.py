from __future__ import annotations

import copy
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch


SKILL_ROOT = Path(__file__).resolve().parents[1]


def load_script(name: str):
    path = SKILL_ROOT / "scripts" / f"{name}.py"
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


scaffold = load_script("scaffold_repository")
validator = load_script("validate_repository")
inspector = load_script("inspect_packages")
oci_validator = load_script("validate_oci")
image_inspector = load_script("inspect_images")


def shared_manifest(repository_id: str = "Acme.Monica.Example") -> dict:
    return {
        "schemaVersion": 2,
        "repositoryId": repository_id,
        "solutionPath": f"{repository_id}.slnx",
        "version": "0.1.0-alpha.1",
        "authors": "Acme Engineering",
        "nugetOwner": "Acme",
        "repositoryUrl": "https://github.com/acme/acme-monica-example",
        "projectUrl": "https://github.com/acme/acme-monica-example",
        "supportUrl": "https://github.com/acme/acme-monica-example/issues",
        "securityUrl": "https://github.com/acme/acme-monica-example/security/policy",
        "source": {
            "available": True,
            "provider": "github",
            "sourceLinkVersion": "10.0.102",
        },
        "distribution": "public",
        "publishing": {"target": "nuget.org"},
        "targetFramework": "net10.0",
        "monicaVersion": "1.0.0-rc.12",
        "license": {"openSource": True, "expression": "MIT"},
        "branding": {
            "icon": {"kind": "compatibility-mark"},
            "showOpenSourceBadge": True,
        },
        "packages": [],
        "ociImages": [],
    }


def one_package_manifest() -> dict:
    payload = shared_manifest()
    payload["packages"] = [
        {
            "packageId": "Acme.Monica.Example",
            "projectPath": "src/Acme.Monica.Example/Acme.Monica.Example.csproj",
            "description": "Example capabilities for Monica.",
            "capabilityTags": ["example"],
            "packageDependencies": [],
            "modules": [
                {
                    "name": "Example",
                    "kind": "infrastructure",
                    "key": "Acme.Monica.Example",
                }
            ],
        }
    ]
    return payload


def ocr_repository_manifest() -> dict:
    payload = shared_manifest("Acme.Monica.AI.OCR")
    payload["packages"] = [
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
                    "key": "Acme.Monica.AI.OCR",
                }
            ],
        },
        {
            "packageId": "Acme.Monica.AI.OCR.PaddleOCR",
            "projectPath": "src/Acme.Monica.AI.OCR.PaddleOCR/Acme.Monica.AI.OCR.PaddleOCR.csproj",
            "description": "PaddleOCR provider.",
            "capabilityTags": ["ai", "ocr", "paddleocr"],
            "packageDependencies": ["Acme.Monica.AI.OCR"],
            "modules": [
                {
                    "name": "PaddleOCR",
                    "kind": "provider",
                    "key": "Acme.Monica.AI.OCR.PaddleOCR",
                    "dependsOn": ["Acme.Monica.AI.OCR"],
                    "providerFor": "Acme.Monica.AI.OCR",
                }
            ],
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
                    "dependsOn": ["Acme.Monica.AI.OCR"],
                }
            ],
        },
    ]
    payload["ociImages"] = [
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
                "managedNvidiaRunnerLabels": ["self-hosted", "linux", "x64", "nvidia"],
            },
            "targets": [
                {
                    "bakeTarget": "paddleocr-cpu-amd64",
                    "stage": "runtime-cpu",
                    "platform": "linux/amd64",
                    "accelerator": "cpu",
                    "tagSuffix": "cpu-amd64",
                },
                {
                    "bakeTarget": "paddleocr-nvidia-cu129-amd64",
                    "stage": "runtime-nvidia-cu129",
                    "platform": "linux/amd64",
                    "accelerator": "nvidia",
                    "tagSuffix": "nvidia-cu129-amd64",
                },
            ],
        }
    ]
    return payload


class RepositorySkillTests(unittest.TestCase):
    def write_manifest(self, root: Path, payload: dict) -> Path:
        path = root / "manifest.json"
        path.write_text(json.dumps(payload), encoding="utf-8")
        return path

    def scaffold(self, root: Path, payload: dict) -> Path:
        manifest = scaffold.load_manifest(self.write_manifest(root, payload))
        output = root / "output"
        scaffold.create_repository(manifest, output)
        return output

    def complete_oci_fixture(self, output: Path) -> None:
        dockerfile = output / "containers/paddleocr/Dockerfile"
        dockerfile.write_text(
            "FROM scratch AS shared\n"
            "FROM shared AS runtime-cpu\n"
            "FROM shared AS runtime-nvidia-cu129\n",
            encoding="utf-8",
        )

    def test_one_package_scaffold_emits_repository_contract_and_validates(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), one_package_manifest())
            contract = json.loads((output / "monica.manifest.json").read_text(encoding="utf-8"))
            project = output / "src/Acme.Monica.Example/Acme.Monica.Example.csproj"

            self.assertEqual(2, contract["schemaVersion"])
            self.assertEqual(["Acme.Monica.Example"], [item["packageId"] for item in contract["packages"]])
            self.assertTrue(project.is_file())
            self.assertNotIn("MonicaSourceRoot", project.read_text(encoding="utf-8"))
            module_text = (
                output / "src/Acme.Monica.Example/Modules/ModuleExample.cs"
            ).read_text(encoding="utf-8")
            test_text = (
                output
                / "tests/Test.Acme.Monica.Example/Modules/ModuleRegistrationTests.cs"
            ).read_text(encoding="utf-8")
            self.assertIn("MonicaModule<ModuleExampleOption>", module_text)
            self.assertIn("ModuleRegistration<ModuleExample, ModuleExampleOption>", module_text)
            self.assertIn("builder.AddModule<ModuleExample, ModuleExampleOption>", module_text)
            self.assertIn("builder.AddExample();", test_text)
            self.assertEqual(
                {"inspect_packages.py", "validate_repository.py"},
                {path.name for path in (output / "scripts").glob("*.py")},
            )
            findings = validator.validate_repository_contract(output, contract, None)
            findings.extend(validator.validate_project(output, project, None))
            self.assertEqual(["OK"], [item.code for item in findings])

    def test_web_scaffold_declares_capability_and_explicit_host_requirement(self) -> None:
        payload = one_package_manifest()
        payload["packages"][0]["modules"][0]["kind"] = "web"

        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), payload)
            contract = json.loads(
                (output / "monica.manifest.json").read_text(encoding="utf-8")
            )
            project = output / "src/Acme.Monica.Example/Acme.Monica.Example.csproj"
            source = (
                output / "src/Acme.Monica.Example/Modules/ModuleExample.cs"
            ).read_text(encoding="utf-8")

            self.assertIn(
                "MonicaModule<ModuleExampleOption>, IWebModule, IWebHostRequiredModule",
                source,
            )
            findings = validator.validate_repository_contract(output, contract, None)
            findings.extend(validator.validate_project(output, project, None))
            self.assertEqual(["OK"], [item.code for item in findings])

            module_path = output / "src/Acme.Monica.Example/Modules/ModuleExample.cs"
            module_path.write_text(
                source.replace(", IWebHostRequiredModule", ""),
                encoding="utf-8",
            )
            self.assertIn(
                "MTR036",
                {
                    finding.code
                    for finding in validator.validate_repository_contract(
                        output,
                        contract,
                        None,
                    )
                },
            )

    def test_three_package_provider_ui_and_oci_scaffold_is_coherent(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), ocr_repository_manifest())
            self.complete_oci_fixture(output)
            contract = json.loads((output / "monica.manifest.json").read_text(encoding="utf-8"))
            provider_project = output / "src/Acme.Monica.AI.OCR.PaddleOCR/Acme.Monica.AI.OCR.PaddleOCR.csproj"
            provider_module = output / "src/Acme.Monica.AI.OCR.PaddleOCR/Modules/ModulePaddleOCR.cs"
            ui_module = output / "src/Acme.Monica.AI.OCR.UI/Modules/ModuleOcrUI.cs"
            bake = (output / "docker-bake.hcl").read_text(encoding="utf-8")
            publish_workflow = (output / ".github/workflows/publish.yml").read_text(encoding="utf-8")

            self.assertEqual(3, len(contract["packages"]))
            self.assertIn("<ProjectReference Include=\"..\\Acme.Monica.AI.OCR\\Acme.Monica.AI.OCR.csproj\" />", provider_project.read_text(encoding="utf-8"))
            provider_text = provider_module.read_text(encoding="utf-8")
            self.assertIn("IModuleProvider", provider_text)
            self.assertIn(
                "ProvidesFor => typeof(global::Acme.Monica.AI.OCR.Modules.ModuleOcr)",
                provider_text,
            )
            self.assertIn("UsePaddleOCRProvider", provider_text)
            ui_text = ui_module.read_text(encoding="utf-8")
            self.assertIn("MonicaModule<ModuleOcrUIOption>, IUIModule", ui_text)
            self.assertIn(
                "module.Require<global::Monica.Modules.ModuleLocalization,",
                ui_text,
            )
            self.assertIn(
                "module.Require<global::Monica.Modules.ModuleShellUI,",
                ui_text,
            )
            self.assertIn("option.AddResource<OcrResource>()", ui_text)
            self.assertIn("option.ConfigureNavigation(registry =>", ui_text)
            self.assertNotIn("registration.Require", ui_text)
            self.assertIn('"/ai-ocr"', ui_text)
            self.assertIn('target "paddleocr-cpu-amd64"', bake)
            self.assertIn('target "paddleocr-nvidia-cu129-amd64"', bake)
            self.assertIn('${RELEASE_VERSION}-nvidia-cu129-amd64', bake)
            self.assertIn("docker/login-action@v3", publish_workflow)
            self.assertIn("docker buildx bake --file docker-bake.hcl --push", publish_workflow)
            self.assertIn("docker buildx bake --file docker-bake.hcl --load", publish_workflow)
            self.assertIn("python scripts/inspect_images.py --root .", publish_workflow)
            self.assertIn("python scripts/smoke_paddleocr.py --accelerator cpu", publish_workflow)
            self.assertIn("python scripts/smoke_paddleocr.py --accelerator nvidia", publish_workflow)
            self.assertIn('runs-on: ["self-hosted", "linux", "x64", "nvidia"]', publish_workflow)
            self.assertIn("packages: write", publish_workflow)
            self.assertLess(
                publish_workflow.index("--accelerator nvidia"),
                publish_workflow.index("docker/login-action@v3"),
            )
            self.assertLess(
                publish_workflow.index("docker/login-action@v3"),
                publish_workflow.index("--push"),
            )

            findings = validator.validate_repository_contract(output, contract, None)
            for package in contract["packages"]:
                findings.extend(validator.validate_project(output, output / package["projectPath"], None))
            self.assertEqual(["OK", "OK", "OK"], [item.code for item in findings])

            ui_module.write_text(ui_text.replace(", IUIModule", ""), encoding="utf-8")
            self.assertIn(
                "MTR036",
                {
                    finding.code
                    for finding in validator.validate_repository_contract(
                        output,
                        contract,
                        None,
                    )
                },
            )

    def test_oci_publish_is_omitted_without_explicit_release_gates(self) -> None:
        payload = ocr_repository_manifest()
        payload["ociImages"][0].pop("releaseGates")
        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), payload)
            self.assertTrue((output / ".github/workflows/ci.yml").is_file())
            self.assertFalse((output / ".github/workflows/publish.yml").exists())

    def test_nvidia_release_gates_require_managed_nvidia_runner(self) -> None:
        payload = ocr_repository_manifest()
        payload["ociImages"][0]["releaseGates"]["managedNvidiaRunnerLabels"] = [
            "self-hosted",
            "linux",
        ]
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(ValueError, "containing self-hosted and nvidia"):
                scaffold.load_manifest(self.write_manifest(Path(temporary), payload))

    def test_oci_companion_package_must_own_a_provider_module(self) -> None:
        payload = ocr_repository_manifest()
        payload["ociImages"][0]["companionPackageId"] = "Acme.Monica.AI.OCR"
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaisesRegex(ValueError, "owns a provider module"):
                scaffold.load_manifest(self.write_manifest(Path(temporary), payload))

        payload = ocr_repository_manifest()
        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), payload)
            contract = json.loads((output / "monica.manifest.json").read_text(encoding="utf-8"))
            contract["ociImages"][0]["companionPackageId"] = "Acme.Monica.AI.OCR.UI"
            self.assertIn(
                "MTR028",
                {
                    item.code
                    for item in validator.validate_repository_contract(output, contract, None)
                },
            )

    def test_validator_enforces_manifest_monica_version_with_central_property_resolution(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), one_package_manifest())
            central = output / "Directory.Packages.props"
            central_text = central.read_text(encoding="utf-8")
            central_text = central_text.replace(
                "    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>",
                "    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n"
                "    <MonicaVersion>1.0.0-rc.8</MonicaVersion>",
            )
            central_text = central_text.replace(
                '<PackageVersion Include="Monica.Core" Version="1.0.0-rc.12" />',
                '<PackageVersion Include="Monica.Core" Version="$(MonicaVersion)" />',
            )
            central.write_text(central_text, encoding="utf-8")
            project = output / "src/Acme.Monica.Example/Acme.Monica.Example.csproj"
            findings = validator.validate_project(output, project, None)
            mismatch = [item for item in findings if item.code == "MTP033"]
            self.assertEqual(1, len(mismatch))
            self.assertIn("found '1.0.0-rc.8'", mismatch[0].message)

    def test_manifest_rejects_cross_package_graph_and_provider_drift(self) -> None:
        cases = []
        missing_package_edge = ocr_repository_manifest()
        missing_package_edge["packages"][1]["packageDependencies"] = []
        cases.append(("missing package edge", missing_package_edge))
        missing_provider_dependency = ocr_repository_manifest()
        missing_provider_dependency["packages"][1]["modules"][0]["dependsOn"] = []
        cases.append(("provider target absent from dependsOn", missing_provider_dependency))
        duplicate_key = ocr_repository_manifest()
        duplicate_key["packages"][2]["modules"][0]["key"] = "Acme.Monica.AI.OCR.PaddleOCR"
        cases.append(("duplicate key", duplicate_key))
        package_cycle = ocr_repository_manifest()
        package_cycle["packages"][0]["packageDependencies"] = ["Acme.Monica.AI.OCR.UI"]
        package_cycle["packages"][0]["modules"][0]["dependsOn"] = ["Acme.Monica.AI.OCR.UI"]
        cases.append(("package cycle", package_cycle))
        unsupported = ocr_repository_manifest()
        unsupported["packages"][0]["implicitMagic"] = True
        cases.append(("unsupported field", unsupported))

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name, payload in cases:
                with self.subTest(name=name):
                    with self.assertRaises(ValueError):
                        scaffold.load_manifest(self.write_manifest(root, payload))

    def test_repository_validator_rejects_extra_project_local_source_and_missing_dockerfile(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), ocr_repository_manifest())
            contract = json.loads((output / "monica.manifest.json").read_text(encoding="utf-8"))
            extra = output / "src/Acme.Monica.Extra/Acme.Monica.Extra.csproj"
            extra.parent.mkdir(parents=True)
            extra.write_text(
                "<Project><PropertyGroup><PackageId>Acme.Monica.Extra</PackageId></PropertyGroup></Project>",
                encoding="utf-8",
            )
            build_props = output / "Directory.Build.props"
            build_props.write_text(build_props.read_text(encoding="utf-8") + "<!-- MonicaSourceRoot -->\n", encoding="utf-8")

            codes = {item.code for item in validator.validate_repository_contract(output, contract, None)}

            self.assertTrue({"MTR014", "MTR025", "MTR029"}.issubset(codes))

    def test_validate_oci_compares_buildx_normalized_graph(self) -> None:
        manifest = ocr_repository_manifest()
        graph = {
            "group": {
                "default": {
                    "targets": [target["bakeTarget"] for target in manifest["ociImages"][0]["targets"]]
                }
            },
            "target": {
                target["bakeTarget"]: {
                    "context": "containers/paddleocr",
                    "dockerfile": "Dockerfile",
                    "target": target["stage"],
                    "platforms": [target["platform"]],
                    "tags": [f"ghcr.io/acme/monica-ai-ocr-paddleocr:0.1.0-alpha.1-{target['tagSuffix']}"],
                }
                for target in manifest["ociImages"][0]["targets"]
            }
        }
        completed = subprocess.CompletedProcess([], 0, stdout=json.dumps(graph), stderr="")
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "docker-bake.hcl").write_text("group \"default\" {}", encoding="utf-8")
            with patch.object(oci_validator.subprocess, "run", return_value=completed):
                self.assertEqual([], oci_validator.validate(root, manifest))

    def test_artifact_inspector_enforces_exact_package_set_and_internal_dependencies(self) -> None:
        version = "0.1.0-alpha.1"
        repository_ids = {
            "acme.monica.ai.ocr",
            "acme.monica.ai.ocr.paddleocr",
            "acme.monica.ai.ocr.ui",
        }
        with tempfile.TemporaryDirectory() as temporary:
            artifacts = Path(temporary)
            package = artifacts / f"Acme.Monica.AI.OCR.PaddleOCR.{version}.nupkg"
            symbols = artifacts / f"Acme.Monica.AI.OCR.PaddleOCR.{version}.snupkg"
            nuspec = f"""<?xml version="1.0" encoding="utf-8"?>
<package><metadata>
  <id>Acme.Monica.AI.OCR.PaddleOCR</id><version>{version}</version>
  <readme>README.md</readme><icon>monica-compatibility-mark.png</icon>
  <license type="expression">MIT</license>
  <dependencies><group targetFramework="net10.0"><dependency id="Acme.Monica.AI.OCR" version="{version}" /></group></dependencies>
</metadata></package>"""
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr("Acme.Monica.AI.OCR.PaddleOCR.nuspec", nuspec)
                archive.writestr("README.md", "![Icon](monica-compatibility-mark.png)")
                archive.writestr("monica-compatibility-mark.png", b"png")
                archive.writestr("lib/net10.0/Acme.Monica.AI.OCR.PaddleOCR.dll", b"assembly")
            with zipfile.ZipFile(symbols, "w") as archive:
                archive.writestr("Acme.Monica.AI.OCR.PaddleOCR.nuspec", nuspec)
                archive.writestr("lib/net10.0/Acme.Monica.AI.OCR.PaddleOCR.pdb", b"pdb")

            self.assertEqual(
                [],
                inspector.inspect_archive(
                    package,
                    version,
                    "Acme.Monica.AI.OCR.PaddleOCR",
                    {"acme.monica.ai.ocr"},
                    repository_ids,
                ),
            )
            with zipfile.ZipFile(package, "a") as archive:
                archive.writestr("lib/net10.0/Acme.Monica.AI.OCR.dll", b"embedded-core")
            findings = inspector.inspect_archive(
                package,
                version,
                "Acme.Monica.AI.OCR.PaddleOCR",
                {"acme.monica.ai.ocr"},
                repository_ids,
            )
            self.assertIn("MTPA023", {item.code for item in findings})

    def test_private_repository_keeps_ci_but_omits_nuget_publish_workflow(self) -> None:
        payload = one_package_manifest()
        payload["source"] = {"available": False, "provider": "github"}
        payload["distribution"] = "private"
        payload["publishing"] = {
            "target": "private-feed",
            "feedUrl": "https://packages.example.test/nuget/v3/index.json",
        }
        payload["license"] = {"openSource": False, "expression": "MIT"}
        payload["branding"]["showOpenSourceBadge"] = False
        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), payload)
            project = output / "src/Acme.Monica.Example/Acme.Monica.Example.csproj"
            self.assertTrue((output / ".github/workflows/ci.yml").is_file())
            self.assertFalse((output / ".github/workflows/publish.yml").exists())
            self.assertNotIn("RepositoryUrl", project.read_text(encoding="utf-8"))

    def test_package_dependency_casing_is_canonicalized_before_emission(self) -> None:
        payload = ocr_repository_manifest()
        payload["packages"][1]["packageDependencies"] = ["acme.monica.ai.ocr"]
        payload["packages"][1]["modules"][0]["dependsOn"] = ["acme.monica.ai.ocr"]
        payload["packages"][1]["modules"][0]["providerFor"] = "acme.monica.ai.ocr"

        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), payload)
            contract = json.loads((output / "monica.manifest.json").read_text(encoding="utf-8"))
            provider = contract["packages"][1]
            project = output / provider["projectPath"]
            module = project.parent / "Modules/ModulePaddleOCR.cs"

            self.assertEqual(["Acme.Monica.AI.OCR"], provider["packageDependencies"])
            self.assertEqual(["Acme.Monica.AI.OCR"], provider["modules"][0]["dependsOn"])
            self.assertEqual("Acme.Monica.AI.OCR", provider["modules"][0]["providerFor"])
            self.assertIn(
                '<ProjectReference Include="..\\Acme.Monica.AI.OCR\\Acme.Monica.AI.OCR.csproj" />',
                project.read_text(encoding="utf-8"),
            )
            self.assertIn(
                "this ModuleRegistration<global::Acme.Monica.AI.OCR.Modules.ModuleOcr, "
                "global::Acme.Monica.AI.OCR.Modules.ModuleOcrOption> target",
                module.read_text(encoding="utf-8"),
            )

    def test_cross_package_dependencies_use_fully_qualified_module_types(self) -> None:
        payload = shared_manifest("Acme.Monica.Bundle")
        payload["packages"] = [
            {
                "packageId": "Acme.Monica.One",
                "projectPath": "src/Acme.Monica.One/Acme.Monica.One.csproj",
                "description": "First shared capability.",
                "capabilityTags": ["one"],
                "packageDependencies": [],
                "modules": [{"name": "Shared", "kind": "infrastructure", "key": "Acme.Monica.One"}],
            },
            {
                "packageId": "Acme.Monica.Two",
                "projectPath": "src/Acme.Monica.Two/Acme.Monica.Two.csproj",
                "description": "Second shared capability.",
                "capabilityTags": ["two"],
                "packageDependencies": [],
                "modules": [{"name": "Shared", "kind": "infrastructure", "key": "Acme.Monica.Two"}],
            },
            {
                "packageId": "Acme.Monica.Bundle",
                "projectPath": "src/Acme.Monica.Bundle/Acme.Monica.Bundle.csproj",
                "description": "Composes both capabilities.",
                "capabilityTags": ["bundle"],
                "packageDependencies": ["Acme.Monica.One", "Acme.Monica.Two"],
                "modules": [
                    {
                        "name": "Bundle",
                        "kind": "infrastructure",
                        "key": "Acme.Monica.Bundle",
                        "dependsOn": ["Acme.Monica.One", "Acme.Monica.Two"],
                    }
                ],
            },
        ]

        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), payload)
            contract = json.loads((output / "monica.manifest.json").read_text(encoding="utf-8"))
            module = output / "src/Acme.Monica.Bundle/Modules/ModuleBundle.cs"
            source = module.read_text(encoding="utf-8")

            self.assertIn(
                "module.Require<global::Acme.Monica.One.Modules.ModuleShared>()",
                source,
            )
            self.assertIn(
                "module.Require<global::Acme.Monica.Two.Modules.ModuleShared>()",
                source,
            )
            module.write_text(
                source.replace(
                    "module.Require<global::Acme.Monica.One.Modules.ModuleShared>();",
                    "module.Require<global::Acme.Monica.One.Modules.ModuleShared, "
                    "global::Acme.Monica.One.Modules.ModuleSharedOption>(static _ => { });",
                ),
                encoding="utf-8",
            )
            findings = validator.validate_repository_contract(output, contract, None)
            for package in contract["packages"]:
                findings.extend(validator.validate_project(output, output / package["projectPath"], None))
            self.assertEqual(["OK", "OK", "OK"], [item.code for item in findings])

    def test_repository_validator_rejects_invalid_kind_and_source_runtime_drift(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), ocr_repository_manifest())
            self.complete_oci_fixture(output)
            contract_path = output / "monica.manifest.json"
            contract = json.loads(contract_path.read_text(encoding="utf-8"))
            invalid_kind = copy.deepcopy(contract)
            invalid_kind["packages"][0]["modules"][0]["kind"] = "banana"
            self.assertIn(
                "MTR011",
                {item.code for item in validator.validate_repository_contract(output, invalid_kind, None)},
            )

            provider_source = output / "src/Acme.Monica.AI.OCR.PaddleOCR/Modules/ModulePaddleOCR.cs"
            provider_text = provider_source.read_text(encoding="utf-8")
            provider_text = provider_text.replace(", IModuleProvider", "")
            provider_text = provider_text.replace(
                "    /// <inheritdoc />\n"
                "    public Type ProvidesFor => "
                "typeof(global::Acme.Monica.AI.OCR.Modules.ModuleOcr);\n",
                "",
            )
            provider_text = provider_text.replace(
                "        module.Require<global::Acme.Monica.AI.OCR.Modules.ModuleOcr>();\n",
                "",
            )
            provider_source.write_text(provider_text, encoding="utf-8")

            codes = {
                item.code
                for item in validator.validate_repository_contract(output, contract, None)
            }
            self.assertTrue({"MTR034", "MTR035"}.issubset(codes))

    def test_validator_checks_every_registered_ui_route(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), ocr_repository_manifest())
            module = output / "src/Acme.Monica.AI.OCR.UI/Modules/ModuleOcrUI.cs"
            source = module.read_text(encoding="utf-8")
            source = source.replace(
                "                navOrder: 80);\n",
                "                navOrder: 80);\n"
                "            registry.RegisterLocalizedPage<UIOcrPage, OcrResource>(\n"
                '                "/settings",\n'
                '                "Navigation:Title",\n'
                "                Icons.Material.Filled.Extension,\n"
                "                categoryId: category,\n"
                "                addToNav: true,\n"
                "                navOrder: 81);\n",
            )
            module.write_text(source, encoding="utf-8")

            findings = validator.validate_project(
                output,
                output / "src/Acme.Monica.AI.OCR.UI/Acme.Monica.AI.OCR.UI.csproj",
                None,
            )
            self.assertIn("MTP027", {item.code for item in findings})

    def test_oci_validator_requires_exact_default_and_target_sets(self) -> None:
        manifest = ocr_repository_manifest()
        targets = manifest["ociImages"][0]["targets"]
        graph_targets = {
            target["bakeTarget"]: {
                "context": "containers/paddleocr",
                "dockerfile": "Dockerfile",
                "target": target["stage"],
                "platforms": [target["platform"]],
                "tags": [
                    "ghcr.io/acme/monica-ai-ocr-paddleocr:"
                    f"0.1.0-alpha.1-{target['tagSuffix']}"
                ],
            }
            for target in targets
        }
        graph_targets["undeclared-release-target"] = {
            "context": "containers/paddleocr",
            "dockerfile": "Dockerfile",
            "target": "runtime-extra",
            "platforms": ["linux/amd64"],
            "tags": ["ghcr.io/acme/monica-ai-ocr-paddleocr:unexpected"],
        }
        graph = {
            "group": {"default": {"targets": []}},
            "target": graph_targets,
        }

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "docker-bake.hcl").write_text("group \"default\" {}", encoding="utf-8")
            with patch.object(oci_validator, "bake_graph", return_value=graph):
                codes = {item.code for item in oci_validator.validate(root, manifest)}
        self.assertTrue({"MTO011", "MTO012"}.issubset(codes))

    def test_image_inspector_rejects_malformed_contract_and_platform_drift(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "monica.manifest.json").write_text(
                json.dumps(
                    {
                        "schemaVersion": 2,
                        "version": "1.0.0",
                        "ociImages": [{"unexpected": True}],
                    }
                ),
                encoding="utf-8",
            )
            self.assertIn("MTI007", {item.code for item in image_inspector.validate(root)})

            manifest = ocr_repository_manifest()
            manifest["ociImages"][0]["targets"] = [manifest["ociImages"][0]["targets"][0]]
            (root / "monica.manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
            inspected = {
                "Os": "linux",
                "Architecture": "amd64",
                "Config": {
                    "User": "10001:10001",
                    "Healthcheck": {"Test": ["CMD", "true"]},
                    "Labels": {
                        "org.opencontainers.image.version": manifest["version"],
                        "org.opencontainers.image.source": manifest["repositoryUrl"],
                        "org.opencontainers.image.revision": "abc123",
                        "io.monica.companion-package": "Acme.Monica.AI.OCR.PaddleOCR",
                        "io.monica.accelerator": "cpu",
                    },
                },
            }
            with patch.object(image_inspector, "docker_inspect", return_value=inspected):
                self.assertEqual([], image_inspector.validate(root))

            inspected["Architecture"] = "arm64"
            with patch.object(image_inspector, "docker_inspect", return_value=inspected):
                self.assertIn("MTI008", {item.code for item in image_inspector.validate(root)})

    def test_branding_validator_requires_notice_and_canonical_mark(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), one_package_manifest())
            project = output / "src/Acme.Monica.Example/Acme.Monica.Example.csproj"
            readme = output / "README.md"
            original_readme = readme.read_text(encoding="utf-8")
            readme.write_text(
                original_readme.replace(
                    "Monica compatibility is self-attested by the publisher. ",
                    "",
                ),
                encoding="utf-8",
            )
            self.assertIn(
                "MTP012",
                {item.code for item in validator.validate_project(output, project, None)},
            )

            readme.write_text(original_readme, encoding="utf-8")
            mark = output / "monica-compatibility-mark.png"
            mark.write_bytes(mark.read_bytes() + b"modified")
            self.assertIn(
                "MTP032",
                {item.code for item in validator.validate_project(output, project, None)},
            )

    def test_constant_and_named_ui_routes_are_parsed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            project_root = Path(temporary) / "src/Acme.Monica.GachaPool"
            module_source = project_root / "Modules/ModuleGachaPoolUI.cs"
            page_source = project_root / "Pages/UIGachaPoolPage.razor"
            module_source.parent.mkdir(parents=True)
            page_source.parent.mkdir(parents=True)
            project = project_root / "Acme.Monica.GachaPool.csproj"
            project.write_text("<Project />", encoding="utf-8")
            module_source.write_text(
                "registry.RegisterLocalizedPage<UIGachaPoolPage, GachaPoolResource>(\n"
                "    route: UIGachaPoolPage.PAGE_URL,\n"
                '    displayNameKey: "Navigation:Title");\n',
                encoding="utf-8",
            )
            page_source.write_text('public const string PAGE_URL = "/gacha-pool";\n', encoding="utf-8")

            self.assertEqual(
                [("/gacha-pool", None)],
                validator.resolve_registered_routes(project, module_source),
            )

        module_text = """
// RegisterLocalizedComponent<LegacyPage, LegacyResource>("/ignored", "Ignored");
shellOption.ConfigureNavigation(registry =>
{
    var category = registry.RegisterLocalizedCategory<AuditResource>(
        categoryId: "Acme.Monica.Toolkit.Audit",
        displayNameKey: "Navigation:Category",
        order: 4_00);
    registry.RegisterLocalizedPage<UIAuditPage, AuditResource>(
        route: "/toolkit-audit",
        displayNameKey: "Navigation:Title",
        icon: null,
        categoryId: category,
        addToNav: true,
        navOrder: 80);
    registry.RegisterLocalizedPage<UIAuditDetailsPage, AuditResource>(
        route: "/toolkit-audit-details",
        displayNameKey: "Navigation:Details",
        categoryId: category);
});
"""
        self.assertEqual(
            [],
            validator.localized_navigation_contract_errors(
                module_text,
                "Acme.Monica.Toolkit.Audit",
                "UIAuditPage",
            ),
        )

    def test_multi_ui_package_keeps_independent_categories_and_routes(self) -> None:
        payload = shared_manifest("Acme.Monica.Toolkit")
        payload["packages"] = [
            {
                "packageId": "Acme.Monica.Toolkit",
                "projectPath": "src/Acme.Monica.Toolkit/Acme.Monica.Toolkit.csproj",
                "description": "A toolkit with independent UI capabilities.",
                "capabilityTags": ["toolkit"],
                "packageDependencies": [],
                "modules": [
                    {"name": "Toolkit", "kind": "infrastructure", "key": "Acme.Monica.Toolkit"},
                    {
                        "name": "AuditUI",
                        "kind": "ui",
                        "key": "Acme.Monica.Toolkit.Audit.UI",
                        "dependsOn": ["Acme.Monica.Toolkit"],
                    },
                    {
                        "name": "ReportsUI",
                        "kind": "ui",
                        "key": "Acme.Monica.Toolkit.Reports.UI",
                        "dependsOn": ["Acme.Monica.Toolkit"],
                    },
                ],
            }
        ]
        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), payload)
            audit = (output / "src/Acme.Monica.Toolkit/Modules/ModuleAuditUI.cs").read_text(encoding="utf-8")
            reports = (output / "src/Acme.Monica.Toolkit/Modules/ModuleReportsUI.cs").read_text(encoding="utf-8")
            self.assertIn('"Acme.Monica.Toolkit.Audit"', audit)
            self.assertIn('"/toolkit-audit"', audit)
            self.assertIn("order: 450", audit)
            self.assertIn('"Acme.Monica.Toolkit.Reports"', reports)
            self.assertIn('"/toolkit-reports"', reports)
            self.assertIn("order: 451", reports)
            findings = validator.validate_project(
                output,
                output / "src/Acme.Monica.Toolkit/Acme.Monica.Toolkit.csproj",
                None,
            )
            self.assertEqual(["OK"], [item.code for item in findings])

    def test_source_link_and_effective_semver_regressions(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), one_package_manifest())
            build_props = output / "Directory.Build.props"
            build_props.write_text(
                build_props.read_text(encoding="utf-8").replace(
                    "<TargetFramework>net10.0</TargetFramework>",
                    "<TargetFramework>net10.0</TargetFramework>\n    <MonicaVersion>1.0.0-rc.12</MonicaVersion>",
                ),
                encoding="utf-8",
            )
            packages = output / "Directory.Packages.props"
            packages.write_text(
                packages.read_text(encoding="utf-8").replace(
                    'Version="1.0.0-rc.12"',
                    'Version="$(MonicaVersion)"',
                ),
                encoding="utf-8",
            )
            project = output / "src/Acme.Monica.Example/Acme.Monica.Example.csproj"
            self.assertEqual("1.0.0-rc.12", validator.package_references(output, project)["Monica.Core"].version)

            project.write_text(
                project.read_text(encoding="utf-8").replace(' PrivateAssets="All"', ""),
                encoding="utf-8",
            )
            module = output / "src/Acme.Monica.Example/Modules/ModuleExample.cs"
            module.write_text(
                module.read_text(encoding="utf-8").replace(
                    "namespace Acme.Monica.Example.Modules;",
                    "namespace Monica.Modules;",
                ),
                encoding="utf-8",
            )
            codes = {
                item.code
                for item in validator.validate_project(output, project, "1.0.0")
            }
            self.assertTrue({"MTP016", "MTP017", "MTP024"}.issubset(codes))

    def test_validator_rejects_legacy_localized_registration(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            output = self.scaffold(Path(temporary), ocr_repository_manifest())
            module = output / "src/Acme.Monica.AI.OCR.UI/Modules/ModuleOcrUI.cs"
            module.write_text(
                module.read_text(encoding="utf-8").replace(
                    "RegisterLocalizedPage<UIOcrPage, OcrResource>",
                    "RegisterLocalizedComponent<UIOcrPage, OcrResource>",
                ),
                encoding="utf-8",
            )
            findings = validator.validate_project(
                output,
                output / "src/Acme.Monica.AI.OCR.UI/Acme.Monica.AI.OCR.UI.csproj",
                None,
            )
            self.assertIn("MTP030", {item.code for item in findings})

    def test_artifact_inspector_preserves_root_metadata_and_symbols(self) -> None:
        version = "0.1.0-alpha.1"
        with tempfile.TemporaryDirectory() as temporary:
            artifacts = Path(temporary)
            package = artifacts / f"Acme.Monica.Example.{version}.nupkg"
            symbols = artifacts / f"Acme.Monica.Example.{version}.snupkg"
            nuspec = f"""<?xml version="1.0" encoding="utf-8"?>
<package><metadata>
  <id>Acme.Monica.Example</id><version>{version}</version>
  <readme>README.md</readme><icon>monica-compatibility-mark.png</icon>
  <license type="expression">MIT</license>
</metadata></package>"""
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr("Acme.Monica.Example.nuspec", nuspec)
                archive.writestr("README.md", "![Icon](monica-compatibility-mark.png)")
                archive.writestr("monica-compatibility-mark.png", b"png")
                archive.writestr("lib/net10.0/Acme.Monica.Example.dll", b"assembly")
            with zipfile.ZipFile(symbols, "w") as archive:
                archive.writestr("Acme.Monica.Example.nuspec", nuspec)
                archive.writestr("lib/net10.0/Acme.Monica.Example.pdb", b"pdb")

            self.assertEqual([], inspector.inspect_archive(package, version))
            self.assertEqual([], inspector.inspect_symbol_archive(symbols))

            invalid = artifacts / f"Acme.Monica.Invalid.{version}.nupkg"
            with zipfile.ZipFile(invalid, "w") as archive:
                archive.writestr(
                    "Acme.Monica.Invalid.nuspec",
                    f'<package><metadata><id>Acme.Monica.Invalid</id><version>{version}</version>'
                    '<license type="expression">MIT</license></metadata></package>',
                )
            codes = {item.code for item in inspector.inspect_archive(invalid, version)}
            self.assertIn("MTPA007", codes)


if __name__ == "__main__":
    unittest.main()
