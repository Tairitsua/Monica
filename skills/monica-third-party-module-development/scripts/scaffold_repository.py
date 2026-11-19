#!/usr/bin/env python3
"""Scaffold a multi-package Monica ecosystem repository from a validated manifest."""

from __future__ import annotations

import argparse
import json
import re
import shutil
import struct
import sys
import textwrap
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit


SEGMENT_PATTERN = re.compile(r"^[A-Za-z][A-Za-z0-9]*$")
PACKAGE_PATTERN = re.compile(
    r"^(?P<publisher>[A-Za-z][A-Za-z0-9]*)\.Monica\."
    r"[A-Za-z][A-Za-z0-9]*(?:\.[A-Za-z][A-Za-z0-9]*)*$"
)
SEMVER_PATTERN = re.compile(
    r"^(?:0|[1-9][0-9]*)\."
    r"(?:0|[1-9][0-9]*)\."
    r"(?:0|[1-9][0-9]*)"
    r"(?:-(?:[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
    r"(?:\+(?:[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$"
)
TAG_PATTERN = re.compile(r"^[a-z0-9][a-z0-9-]*$")
SPDX_EXPRESSION_PATTERN = re.compile(r"^[A-Za-z0-9().+\-\s]+$")
SOURCE_LINK_PACKAGES = {
    "github": "Microsoft.SourceLink.GitHub",
    "gitlab": "Microsoft.SourceLink.GitLab",
    "azure-repos": "Microsoft.SourceLink.AzureRepos.Git",
}
SOURCE_PROVIDERS = {*SOURCE_LINK_PACKAGES, "none"}
PUBLISH_TARGETS = {"nuget.org", "private-feed", "none"}
ICON_KINDS = {"compatibility-mark", "publisher"}
MODULE_KINDS = {"infrastructure", "web", "ui", "provider"}
ACCELERATORS = {"cpu", "nvidia"}
OCI_REPOSITORY_PATTERN = re.compile(
    r"^[a-z0-9][a-z0-9.-]*(?::[0-9]+)?/[a-z0-9]+(?:[._/-][a-z0-9]+)*$"
)
OCI_NAME_PATTERN = re.compile(r"^[a-z0-9][a-z0-9-]*$")
RUNNER_LABEL_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
UI_CATEGORY_ORDER_BASE = 450
UI_NAV_ORDER_BASE = 80
COMPATIBILITY_NOTICE = (
    "Monica compatibility is self-attested by the publisher. This community package is "
    "independently maintained and is not affiliated with, endorsed by, or supported by "
    "the Monica project."
)


@dataclass(frozen=True)
class ModuleSpec:
    name: str
    kind: str
    key: str
    depends_on: tuple[str, ...]
    provider_for: str | None

    @property
    def is_ui(self) -> bool:
        return self.kind == "ui"

    @property
    def is_provider(self) -> bool:
        return self.kind == "provider"

    @property
    def base_name(self) -> str:
        return self.name[:-2] if self.is_ui and self.name.endswith("UI") else self.name

    @property
    def navigation_category_id(self) -> str:
        if not self.is_ui or not self.key.casefold().endswith(".ui"):
            raise ValueError(f"Module '{self.name}' is not a UI module with a final .UI key segment.")
        return self.key[:-3]


@dataclass(frozen=True)
class PackageSpec:
    package_id: str
    project_path: str
    description: str
    capability_tags: tuple[str, ...]
    package_dependencies: tuple[str, ...]
    modules: tuple[ModuleSpec, ...]

    @property
    def has_ui(self) -> bool:
        return any(module.is_ui for module in self.modules)

    @property
    def has_web(self) -> bool:
        return any(module.kind == "web" for module in self.modules)

    @property
    def package_tags(self) -> tuple[str, ...]:
        tags = ["monica", "monica-module", "monica-ecosystem-v1", "extension"]
        if self.has_ui:
            tags.append("monica-ui")
        tags.extend(self.capability_tags)
        return tuple(dict.fromkeys(tags))


@dataclass(frozen=True)
class OciTargetSpec:
    bake_target: str
    stage: str
    platform: str
    accelerator: str
    tag_suffix: str


@dataclass(frozen=True)
class OciReleaseGates:
    cpu_smoke_command: str | None
    nvidia_smoke_command: str | None
    managed_nvidia_runner_labels: tuple[str, ...]


@dataclass(frozen=True)
class OciImageSpec:
    image_id: str
    repository: str
    companion_package_id: str
    context_path: str
    dockerfile_path: str
    bake_file_path: str
    targets: tuple[OciTargetSpec, ...]
    release_gates: OciReleaseGates | None


@dataclass(frozen=True)
class Manifest:
    source_path: Path
    repository_id: str
    solution_path: str
    version: str
    authors: str
    nuget_owner: str
    repository_url: str | None
    project_url: str
    support_url: str
    security_url: str
    target_framework: str
    monica_version: str
    source_available: bool
    source_provider: str
    source_link_version: str | None
    distribution: str
    publish_target: str
    publish_feed_url: str | None
    open_source: bool
    license_expression: str | None
    license_file: Path | None
    icon_kind: str
    icon_file: Path | None
    show_open_source_badge: bool
    packages: tuple[PackageSpec, ...]
    oci_images: tuple[OciImageSpec, ...]

    @property
    def has_ui(self) -> bool:
        return any(package.has_ui for package in self.packages)

    @property
    def has_web(self) -> bool:
        return any(package.has_web for package in self.packages)

    @property
    def source_link_package(self) -> str | None:
        if not self.source_available:
            return None
        return SOURCE_LINK_PACKAGES[self.source_provider]

    @property
    def has_complete_oci_release_gates(self) -> bool:
        return all(image.release_gates is not None for image in self.oci_images)

    @property
    def release_runner_labels(self) -> tuple[str, ...]:
        for image in self.oci_images:
            if image.release_gates and image.release_gates.managed_nvidia_runner_labels:
                return image.release_gates.managed_nvidia_runner_labels
        return ()

    @property
    def icon_name(self) -> str:
        if self.icon_kind == "compatibility-mark":
            return "monica-compatibility-mark.png"
        assert self.icon_file is not None
        return self.icon_file.name


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Create a Monica ecosystem multi-package repository.")
    parser.add_argument("--manifest", required=True, type=Path, help="JSON manifest path.")
    parser.add_argument("--output", required=True, type=Path, help="New or empty output directory.")
    return parser.parse_args()


def require_text(data: dict[str, Any], name: str) -> str:
    value = data.get(name)
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"Manifest field '{name}' must be a non-empty string.")
    return value.strip()


def require_single_line_command(data: dict[str, Any], name: str) -> str:
    value = require_text(data, name)
    if "\n" in value or "\r" in value or "\0" in value:
        raise ValueError(f"Manifest field '{name}' must be a single-line command.")
    return value


def require_boolean(data: dict[str, Any], name: str) -> bool:
    value = data.get(name)
    if not isinstance(value, bool):
        raise ValueError(f"Manifest field '{name}' must be a boolean.")
    return value


def require_https_url(data: dict[str, Any], name: str) -> str:
    value = require_text(data, name)
    parsed = urlsplit(value)
    if parsed.scheme != "https" or not parsed.netloc or parsed.username or parsed.password:
        raise ValueError(
            f"Manifest field '{name}' must be an absolute HTTPS URL without embedded credentials."
        )
    return value


def require_object(data: dict[str, Any], name: str) -> dict[str, Any]:
    value = data.get(name)
    if not isinstance(value, dict):
        raise ValueError(f"Manifest field '{name}' must be an object.")
    return value


def validate_semver(value: str, name: str) -> str:
    if SEMVER_PATTERN.fullmatch(value) is None:
        raise ValueError(f"{name} must be a valid three-part SemVer value.")
    prerelease = value.partition("-")[2].partition("+")[0]
    if prerelease:
        for identifier in prerelease.split("."):
            if identifier.isdigit() and len(identifier) > 1 and identifier.startswith("0"):
                raise ValueError(f"{name} has a numeric prerelease identifier with a leading zero.")
    return value


def reject_unknown_fields(data: dict[str, Any], allowed: set[str], context: str) -> None:
    unknown = sorted(set(data) - allowed)
    if unknown:
        raise ValueError(f"{context} contains unsupported fields: {', '.join(unknown)}")


def resolve_manifest_file(manifest_path: Path, value: Any, field_name: str) -> Path:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{field_name} must be a non-empty relative file path.")
    relative = Path(value)
    if relative.is_absolute() or ".." in relative.parts:
        raise ValueError(f"{field_name} must stay within the manifest directory.")
    resolved = (manifest_path.parent / relative).resolve()
    if not resolved.is_file():
        raise ValueError(f"{field_name} does not exist: {resolved}")
    return resolved


def require_relative_path(value: Any, field_name: str, *, suffix: str | None = None) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{field_name} must be a non-empty relative path.")
    normalized = value.strip().replace("\\", "/")
    relative = Path(normalized)
    if relative.is_absolute() or ".." in relative.parts or normalized.startswith("/"):
        raise ValueError(f"{field_name} must stay within the repository.")
    if suffix and not normalized.casefold().endswith(suffix.casefold()):
        raise ValueError(f"{field_name} must end with '{suffix}'.")
    return normalized


def validate_png_icon(path: Path) -> None:
    payload = path.read_bytes()
    if len(payload) < 33 or payload[:8] != b"\x89PNG\r\n\x1a\n" or payload[12:16] != b"IHDR":
        raise ValueError(f"Package icon is not a valid PNG file: {path}")
    width, height = struct.unpack(">II", payload[16:24])
    color_type = payload[25]
    has_transparency = color_type in {4, 6}
    offset = 8
    while offset + 12 <= len(payload):
        chunk_length = struct.unpack(">I", payload[offset:offset + 4])[0]
        chunk_type = payload[offset + 4:offset + 8]
        if chunk_type == b"tRNS":
            has_transparency = True
        offset += 12 + chunk_length
        if chunk_type == b"IEND":
            break
    if width < 64 or height < 64:
        raise ValueError("Package icon must be at least 64 by 64 pixels.")
    if not has_transparency:
        raise ValueError("Package icon must support transparency.")


def validate_graph(nodes: dict[str, tuple[str, ...]], description: str) -> None:
    visiting: list[str] = []
    visited: set[str] = set()

    def visit(name: str) -> None:
        if name in visited:
            return
        if name in visiting:
            cycle = visiting[visiting.index(name):] + [name]
            raise ValueError(f"{description} cycle detected: {' -> '.join(cycle)}")
        visiting.append(name)
        for dependency in nodes[name]:
            visit(dependency)
        visiting.pop()
        visited.add(name)

    for node in nodes:
        visit(node)


def load_manifest(path: Path) -> Manifest:
    path = path.resolve()
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(data, dict):
        raise ValueError("Manifest root must be a JSON object.")
    if data.get("schemaVersion") != 2:
        raise ValueError("Only repository manifest schemaVersion 2 is supported.")
    reject_unknown_fields(
        data,
        {
            "schemaVersion", "repositoryId", "solutionPath", "version", "authors",
            "nugetOwner", "repositoryUrl", "projectUrl", "supportUrl", "securityUrl",
            "targetFramework", "monicaVersion", "source", "distribution", "publishing",
            "license", "branding", "packages", "ociImages",
        },
        "Manifest",
    )

    repository_id = require_text(data, "repositoryId")
    if PACKAGE_PATTERN.fullmatch(repository_id) is None or len(repository_id) > 100:
        raise ValueError("repositoryId must follow <Publisher>.Monica.<Family>[.<Variant>] and be at most 100 characters.")
    solution_path = require_relative_path(data.get("solutionPath"), "solutionPath", suffix=".slnx")
    if Path(solution_path).name != f"{repository_id}.slnx":
        raise ValueError("solutionPath file name must exactly match repositoryId with a .slnx suffix.")

    version = validate_semver(require_text(data, "version"), "version")
    monica_version = validate_semver(require_text(data, "monicaVersion"), "monicaVersion")
    if "-" in monica_version and "-" not in version:
        raise ValueError("A repository depending on prerelease Monica must itself use a prerelease version.")

    project_url = require_https_url(data, "projectUrl")
    support_url = require_https_url(data, "supportUrl")
    security_url = require_https_url(data, "securityUrl")

    source_data = require_object(data, "source")
    reject_unknown_fields(source_data, {"available", "provider", "sourceLinkVersion"}, "source")
    source_available = require_boolean(source_data, "available")
    source_provider = require_text(source_data, "provider").casefold()
    if source_provider not in SOURCE_PROVIDERS:
        raise ValueError(f"source.provider must be one of: {', '.join(sorted(SOURCE_PROVIDERS))}.")
    if source_available and source_provider == "none":
        raise ValueError("source.provider cannot be 'none' when source.available is true.")
    repository_url = None
    if source_provider != "none":
        repository_url = require_https_url(data, "repositoryUrl")
    elif "repositoryUrl" in data:
        raise ValueError("repositoryUrl must be omitted when source.provider is 'none'.")
    source_link_version_value = source_data.get("sourceLinkVersion")
    source_link_version = None
    if source_available:
        if not isinstance(source_link_version_value, str):
            raise ValueError("source.sourceLinkVersion is required when source.available is true.")
        source_link_version = validate_semver(source_link_version_value.strip(), "source.sourceLinkVersion")
    elif source_link_version_value is not None:
        raise ValueError("source.sourceLinkVersion is only valid when source.available is true.")

    distribution = require_text(data, "distribution").casefold()
    if distribution not in {"public", "private"}:
        raise ValueError("distribution must be 'public' or 'private'.")
    publishing_data = require_object(data, "publishing")
    reject_unknown_fields(publishing_data, {"target", "feedUrl"}, "publishing")
    publish_target = require_text(publishing_data, "target").casefold()
    if publish_target not in PUBLISH_TARGETS:
        raise ValueError(f"publishing.target must be one of: {', '.join(sorted(PUBLISH_TARGETS))}.")
    feed_value = publishing_data.get("feedUrl")
    publish_feed_url = None
    if publish_target == "private-feed":
        if not isinstance(feed_value, str) or not feed_value.startswith("https://"):
            raise ValueError("publishing.feedUrl must be an HTTPS URL for a private-feed target.")
        publish_feed_url = feed_value.strip()
    elif feed_value is not None:
        raise ValueError("publishing.feedUrl is only valid when publishing.target is 'private-feed'.")
    if publish_target == "nuget.org" and distribution != "public":
        raise ValueError("publishing.target 'nuget.org' requires distribution 'public'.")
    if publish_target == "private-feed" and distribution != "private":
        raise ValueError("publishing.target 'private-feed' requires distribution 'private'.")

    license_data = require_object(data, "license")
    reject_unknown_fields(license_data, {"openSource", "expression", "file"}, "license")
    open_source = require_boolean(license_data, "openSource")
    expression = license_data.get("expression")
    license_file_value = license_data.get("file")
    if (expression is None) == (license_file_value is None):
        raise ValueError("license must contain exactly one of expression or file.")
    license_expression = None
    license_file = None
    if expression is not None:
        if not isinstance(expression, str) or not expression.strip():
            raise ValueError("license.expression must be a non-empty string.")
        license_expression = expression.strip()
        if SPDX_EXPRESSION_PATTERN.fullmatch(license_expression) is None:
            raise ValueError("license.expression contains characters that are not valid in an SPDX expression.")
    else:
        license_file = resolve_manifest_file(path, license_file_value, "license.file")
    if open_source and license_expression is None:
        raise ValueError("license.openSource requires a NuGet SPDX license expression.")

    branding_data = require_object(data, "branding")
    reject_unknown_fields(branding_data, {"icon", "showOpenSourceBadge"}, "branding")
    show_open_source_badge = require_boolean(branding_data, "showOpenSourceBadge")
    if show_open_source_badge and not (open_source and license_expression):
        raise ValueError("branding.showOpenSourceBadge requires license.openSource=true and a license expression.")
    icon_data = require_object(branding_data, "icon")
    reject_unknown_fields(icon_data, {"kind", "file"}, "branding.icon")
    icon_kind = require_text(icon_data, "kind").casefold()
    if icon_kind not in ICON_KINDS:
        raise ValueError(f"branding.icon.kind must be one of: {', '.join(sorted(ICON_KINDS))}.")
    icon_value = icon_data.get("file")
    icon_file = None
    if icon_kind == "publisher":
        icon_file = resolve_manifest_file(path, icon_value, "branding.icon.file")
        if icon_file.suffix.casefold() != ".png":
            raise ValueError("branding.icon.file must be a PNG file.")
        if icon_file.name.casefold() in {
            "readme.md", "license", "monica.manifest.json", "monica-open-source-badge.svg"
        }:
            raise ValueError("branding.icon.file uses a reserved scaffold file name.")
        if license_file and icon_file.name.casefold() == license_file.name.casefold():
            raise ValueError("branding.icon.file and license.file must use different file names.")
        validate_png_icon(icon_file)
    elif icon_value is not None:
        raise ValueError("branding.icon.file is only valid for a publisher icon.")

    raw_packages = data.get("packages")
    if not isinstance(raw_packages, list) or not raw_packages:
        raise ValueError("packages must contain at least one package definition.")

    packages: list[PackageSpec] = []
    package_ids: set[str] = set()
    project_paths: set[str] = set()
    module_keys: set[str] = set()
    publisher: str | None = None
    for raw_package in raw_packages:
        if not isinstance(raw_package, dict):
            raise ValueError("Every package definition must be an object.")
        reject_unknown_fields(
            raw_package,
            {"packageId", "projectPath", "description", "capabilityTags", "packageDependencies", "modules"},
            "Package definition",
        )
        package_id = require_text(raw_package, "packageId")
        match = PACKAGE_PATTERN.fullmatch(package_id)
        if match is None or len(package_id) > 100:
            raise ValueError(f"PackageId '{package_id}' does not follow the Monica ecosystem grammar.")
        if match.group("publisher").casefold() == "monica":
            raise ValueError("The Monica publisher segment is reserved for first-party packages.")
        publisher = publisher or match.group("publisher").casefold()
        if match.group("publisher").casefold() != publisher:
            raise ValueError("Every package in one repository manifest must use the same publisher segment.")
        if package_id.casefold() in package_ids:
            raise ValueError(f"Duplicate packageId: {package_id}")

        project_path = require_relative_path(raw_package.get("projectPath"), f"{package_id}.projectPath", suffix=".csproj")
        expected_path = f"src/{package_id}/{package_id}.csproj"
        if project_path != expected_path:
            raise ValueError(f"Package '{package_id}' projectPath must be '{expected_path}'.")
        if project_path.casefold() in project_paths:
            raise ValueError(f"Duplicate projectPath: {project_path}")

        raw_tags = raw_package.get("capabilityTags")
        if not isinstance(raw_tags, list) or not raw_tags:
            raise ValueError(f"Package '{package_id}' capabilityTags must contain at least one tag.")
        if not all(isinstance(tag, str) and TAG_PATTERN.fullmatch(tag) for tag in raw_tags):
            raise ValueError(f"Package '{package_id}' capabilityTags must use lowercase ASCII letters, digits, and hyphens.")
        folded_tags = [tag.casefold() for tag in raw_tags]
        if len(folded_tags) != len(set(folded_tags)):
            raise ValueError(f"Package '{package_id}' capabilityTags must not contain duplicates.")
        reserved_tags = {"monica", "monica-module", "monica-ecosystem-v1", "monica-ui", "extension"}
        if reserved_tags.intersection(folded_tags):
            raise ValueError(f"Package '{package_id}' capabilityTags must not repeat scaffold-managed tags.")

        raw_package_dependencies = raw_package.get("packageDependencies", [])
        if not isinstance(raw_package_dependencies, list) or not all(isinstance(item, str) for item in raw_package_dependencies):
            raise ValueError(f"Package '{package_id}' packageDependencies must be a string array.")
        folded_package_dependencies = [item.casefold() for item in raw_package_dependencies]
        if len(folded_package_dependencies) != len(set(folded_package_dependencies)):
            raise ValueError(f"Package '{package_id}' contains duplicate packageDependencies.")

        raw_modules = raw_package.get("modules")
        if not isinstance(raw_modules, list) or not raw_modules:
            raise ValueError(f"Package '{package_id}' modules must contain at least one module.")
        modules: list[ModuleSpec] = []
        local_names: set[str] = set()
        package_prefix = package_id.casefold() + "."
        for raw_module in raw_modules:
            if not isinstance(raw_module, dict):
                raise ValueError("Every module definition must be an object.")
            reject_unknown_fields(raw_module, {"name", "kind", "key", "dependsOn", "providerFor"}, "Module definition")
            name = require_text(raw_module, "name")
            if SEGMENT_PATTERN.fullmatch(name) is None:
                raise ValueError(f"Module name '{name}' must begin with an ASCII letter and contain only letters or digits.")
            kind = require_text(raw_module, "kind").casefold()
            if kind not in MODULE_KINDS:
                raise ValueError(f"Module '{name}' kind must be one of: {', '.join(sorted(MODULE_KINDS))}.")
            if kind == "ui":
                base_name = name[:-2] if name.endswith("UI") else ""
                if not base_name or SEGMENT_PATTERN.fullmatch(base_name) is None or base_name.casefold().endswith("ui"):
                    raise ValueError(f"UI module '{name}' must end with one exact UI suffix and have a non-UI base name.")
            elif name.casefold().endswith("ui"):
                raise ValueError(f"Non-UI module '{name}' must not end in UI.")
            key = require_text(raw_module, "key")
            if PACKAGE_PATTERN.fullmatch(key) is None or len(key) > 100:
                raise ValueError(f"Manifest module key '{key}' does not follow the Monica ecosystem grammar.")
            folded_key = key.casefold()
            if folded_key != package_id.casefold() and not folded_key.startswith(package_prefix):
                raise ValueError(
                    f"Manifest module key '{key}' must equal packageId or begin with '{package_id}.'."
                )
            if (kind == "ui") != folded_key.endswith(".ui"):
                raise ValueError(f"Module '{name}' and key '{key}' disagree about the final .UI identity segment.")
            if name.casefold() in local_names:
                raise ValueError(f"Duplicate module name in '{package_id}': {name}")
            if folded_key in module_keys:
                raise ValueError(f"Duplicate manifest module key: {key}")

            raw_dependencies = raw_module.get("dependsOn", [])
            if not isinstance(raw_dependencies, list) or not all(isinstance(item, str) for item in raw_dependencies):
                raise ValueError(
                    f"Module '{name}' dependsOn must be a string array of full manifest module keys."
                )
            folded_dependencies = [item.casefold() for item in raw_dependencies]
            if len(folded_dependencies) != len(set(folded_dependencies)):
                raise ValueError(f"Module '{name}' contains duplicate dependsOn entries.")
            provider_for_value = raw_module.get("providerFor")
            provider_for = provider_for_value.strip() if isinstance(provider_for_value, str) else None
            if kind == "provider" and not provider_for:
                raise ValueError(f"Provider module '{name}' requires providerFor.")
            if kind != "provider" and provider_for_value is not None:
                raise ValueError(f"Only provider modules may declare providerFor; found '{name}'.")
            if provider_for and provider_for.casefold() not in folded_dependencies:
                raise ValueError(f"Provider module '{name}' must include providerFor in dependsOn.")

            local_names.add(name.casefold())
            module_keys.add(folded_key)
            modules.append(ModuleSpec(name, kind, key, tuple(raw_dependencies), provider_for))

        package_ids.add(package_id.casefold())
        project_paths.add(project_path.casefold())
        packages.append(
            PackageSpec(
                package_id,
                project_path,
                require_text(raw_package, "description"),
                tuple(folded_tags),
                tuple(raw_package_dependencies),
                tuple(modules),
            )
        )

    repository_publisher = repository_id.split(".", 1)[0].casefold()
    if publisher != repository_publisher:
        raise ValueError(
            f"repositoryId publisher '{repository_id.split('.', 1)[0]}' must match the package publisher."
        )

    packages_by_id = {package.package_id.casefold(): package for package in packages}
    modules_by_key = {
        module.key.casefold(): (package, module)
        for package in packages
        for module in package.modules
    }
    package_graph: dict[str, tuple[str, ...]] = {}
    module_graph: dict[str, tuple[str, ...]] = {}
    for package in packages:
        unknown_packages = [value for value in package.package_dependencies if value.casefold() not in packages_by_id]
        if unknown_packages:
            raise ValueError(f"Package '{package.package_id}' depends on undeclared packages: {', '.join(unknown_packages)}")
        if package.package_id.casefold() in {value.casefold() for value in package.package_dependencies}:
            raise ValueError(f"Package '{package.package_id}' cannot depend on itself.")
        package_graph[package.package_id.casefold()] = tuple(value.casefold() for value in package.package_dependencies)
        declared_package_dependencies = {value.casefold() for value in package.package_dependencies}
        for module in package.modules:
            dependency_keys = tuple(value.casefold() for value in module.depends_on)
            unknown_modules = [value for value in module.depends_on if value.casefold() not in modules_by_key]
            if unknown_modules:
                raise ValueError(
                    f"Module '{module.name}' depends on undeclared manifest module keys: "
                    f"{', '.join(unknown_modules)}"
                )
            if module.key.casefold() in dependency_keys:
                raise ValueError(f"Module '{module.name}' cannot depend on itself.")
            for dependency_key in dependency_keys:
                dependency_package, _ = modules_by_key[dependency_key]
                if dependency_package.package_id.casefold() != package.package_id.casefold() and dependency_package.package_id.casefold() not in declared_package_dependencies:
                    raise ValueError(
                        f"Module '{module.name}' crosses into '{dependency_package.package_id}' without a packageDependencies edge."
                    )
            if module.provider_for and module.provider_for.casefold() not in modules_by_key:
                raise ValueError(f"Provider module '{module.name}' providerFor target is undeclared: {module.provider_for}")
            module_graph[module.key.casefold()] = dependency_keys
    validate_graph(package_graph, "Package dependency")
    validate_graph(module_graph, "Module dependency")

    # Preserve the canonical casing declared by each package/module owner. NuGet identity
    # comparison is case-insensitive, but generated project paths are consumed on Linux CI.
    canonical_packages: list[PackageSpec] = []
    for package in packages:
        canonical_modules = tuple(
            ModuleSpec(
                module.name,
                module.kind,
                module.key,
                tuple(modules_by_key[value.casefold()][1].key for value in module.depends_on),
                (
                    modules_by_key[module.provider_for.casefold()][1].key
                    if module.provider_for is not None
                    else None
                ),
            )
            for module in package.modules
        )
        canonical_packages.append(
            PackageSpec(
                package.package_id,
                package.project_path,
                package.description,
                package.capability_tags,
                tuple(
                    packages_by_id[value.casefold()].package_id
                    for value in package.package_dependencies
                ),
                canonical_modules,
            )
        )
    packages = canonical_packages
    packages_by_id = {package.package_id.casefold(): package for package in packages}
    modules_by_key = {
        module.key.casefold(): (package, module)
        for package in packages
        for module in package.modules
    }

    raw_images = data.get("ociImages", [])
    if not isinstance(raw_images, list):
        raise ValueError("ociImages must be an array.")
    images: list[OciImageSpec] = []
    image_ids: set[str] = set()
    repositories: set[str] = set()
    bake_targets: set[str] = set()
    resulting_tags: set[str] = set()
    for raw_image in raw_images:
        if not isinstance(raw_image, dict):
            raise ValueError("Every OCI image definition must be an object.")
        reject_unknown_fields(
            raw_image,
            {
                "id", "repository", "companionPackageId", "contextPath", "dockerfilePath",
                "bakeFilePath", "targets", "releaseGates",
            },
            "OCI image definition",
        )
        image_id = require_text(raw_image, "id").casefold()
        repository = require_text(raw_image, "repository").casefold()
        companion = require_text(raw_image, "companionPackageId")
        if OCI_NAME_PATTERN.fullmatch(image_id) is None:
            raise ValueError(f"OCI image id '{image_id}' must use lowercase letters, digits, and hyphens.")
        if OCI_REPOSITORY_PATTERN.fullmatch(repository) is None:
            raise ValueError(f"OCI repository '{repository}' is not a valid lowercase registry/repository path.")
        if image_id in image_ids or repository in repositories:
            raise ValueError(f"Duplicate OCI image identity or repository: {image_id} / {repository}")
        if companion.casefold() not in packages_by_id:
            raise ValueError(f"OCI image '{image_id}' companionPackageId is undeclared: {companion}")
        companion_package = packages_by_id[companion.casefold()]
        if not any(module.is_provider for module in companion_package.modules):
            raise ValueError(
                f"OCI image '{image_id}' companionPackageId must name a package that owns a provider module."
            )
        companion = companion_package.package_id
        context_path = require_relative_path(raw_image.get("contextPath"), f"ociImages.{image_id}.contextPath")
        dockerfile_path = require_relative_path(raw_image.get("dockerfilePath"), f"ociImages.{image_id}.dockerfilePath")
        bake_file_path = require_relative_path(raw_image.get("bakeFilePath"), f"ociImages.{image_id}.bakeFilePath")
        if Path(bake_file_path).parent != Path("."):
            raise ValueError("OCI bakeFilePath must name a repository-root Bake file.")
        raw_targets = raw_image.get("targets")
        if not isinstance(raw_targets, list) or not raw_targets:
            raise ValueError(f"OCI image '{image_id}' targets must contain at least one target.")
        targets: list[OciTargetSpec] = []
        for raw_target in raw_targets:
            if not isinstance(raw_target, dict):
                raise ValueError("Every OCI target must be an object.")
            reject_unknown_fields(raw_target, {"bakeTarget", "stage", "platform", "accelerator", "tagSuffix"}, "OCI target")
            bake_target = require_text(raw_target, "bakeTarget").casefold()
            stage = require_text(raw_target, "stage")
            platform = require_text(raw_target, "platform").casefold()
            accelerator = require_text(raw_target, "accelerator").casefold()
            tag_suffix = require_text(raw_target, "tagSuffix").casefold()
            if OCI_NAME_PATTERN.fullmatch(bake_target) is None or OCI_NAME_PATTERN.fullmatch(stage) is None:
                raise ValueError("OCI bakeTarget and stage must use lowercase letters, digits, and hyphens.")
            if platform not in {"linux/amd64", "linux/arm64"}:
                raise ValueError("OCI platform must be linux/amd64 or linux/arm64.")
            if accelerator not in ACCELERATORS:
                raise ValueError(f"OCI accelerator must be one of: {', '.join(sorted(ACCELERATORS))}.")
            if accelerator == "nvidia" and platform != "linux/amd64":
                raise ValueError("NVIDIA targets currently require linux/amd64.")
            if OCI_NAME_PATTERN.fullmatch(tag_suffix) is None:
                raise ValueError("OCI tagSuffix must use lowercase letters, digits, and hyphens.")
            resulting_tag = f"{repository}:{version}-{tag_suffix}"
            if bake_target in bake_targets or resulting_tag in resulting_tags:
                raise ValueError(f"Duplicate OCI bake target or resulting tag: {bake_target}")
            bake_targets.add(bake_target)
            resulting_tags.add(resulting_tag)
            targets.append(OciTargetSpec(bake_target, stage, platform, accelerator, tag_suffix))
        image_ids.add(image_id)
        repositories.add(repository)
        accelerators = {target.accelerator for target in targets}
        raw_release_gates = raw_image.get("releaseGates")
        release_gates = None
        if raw_release_gates is not None:
            if not isinstance(raw_release_gates, dict):
                raise ValueError(f"OCI image '{image_id}' releaseGates must be an object.")
            reject_unknown_fields(
                raw_release_gates,
                {"cpuSmokeCommand", "nvidiaSmokeCommand", "managedNvidiaRunnerLabels"},
                "OCI release gates",
            )
            cpu_smoke_command = (
                require_single_line_command(raw_release_gates, "cpuSmokeCommand")
                if "cpu" in accelerators
                else None
            )
            nvidia_smoke_command = (
                require_single_line_command(raw_release_gates, "nvidiaSmokeCommand")
                if "nvidia" in accelerators
                else None
            )
            if "cpu" not in accelerators and "cpuSmokeCommand" in raw_release_gates:
                raise ValueError(f"OCI image '{image_id}' has a CPU smoke command but no CPU target.")
            if "nvidia" not in accelerators and (
                "nvidiaSmokeCommand" in raw_release_gates
                or "managedNvidiaRunnerLabels" in raw_release_gates
            ):
                raise ValueError(f"OCI image '{image_id}' has NVIDIA release gates but no NVIDIA target.")
            labels_value = raw_release_gates.get("managedNvidiaRunnerLabels", [])
            if not isinstance(labels_value, list) or not all(
                isinstance(label, str) and RUNNER_LABEL_PATTERN.fullmatch(label)
                for label in labels_value
            ):
                raise ValueError(
                    f"OCI image '{image_id}' managedNvidiaRunnerLabels must be an array of runner labels."
                )
            labels = tuple(labels_value)
            folded_labels = [label.casefold() for label in labels]
            if len(folded_labels) != len(set(folded_labels)):
                raise ValueError(
                    f"OCI image '{image_id}' managedNvidiaRunnerLabels must not contain duplicates."
                )
            if "nvidia" in accelerators and (
                "self-hosted" not in folded_labels
                or "nvidia" not in folded_labels
            ):
                raise ValueError(
                    f"OCI image '{image_id}' NVIDIA release gates require managedNvidiaRunnerLabels containing self-hosted and nvidia."
                )
            release_gates = OciReleaseGates(
                cpu_smoke_command,
                nvidia_smoke_command,
                labels,
            )
        images.append(
            OciImageSpec(
                image_id,
                repository,
                companion,
                context_path,
                dockerfile_path,
                bake_file_path,
                tuple(targets),
                release_gates,
            )
        )

    nvidia_runner_sets = {
        frozenset(label.casefold() for label in image.release_gates.managed_nvidia_runner_labels)
        for image in images
        if image.release_gates and image.release_gates.managed_nvidia_runner_labels
    }
    if len(nvidia_runner_sets) > 1:
        raise ValueError("All NVIDIA OCI release gates must use the same managed runner labels.")

    return Manifest(
        source_path=path,
        repository_id=repository_id,
        solution_path=solution_path,
        version=version,
        authors=require_text(data, "authors"),
        nuget_owner=require_text(data, "nugetOwner"),
        repository_url=repository_url,
        project_url=project_url,
        support_url=support_url,
        security_url=security_url,
        target_framework=require_text(data, "targetFramework"),
        monica_version=monica_version,
        source_available=source_available,
        source_provider=source_provider,
        source_link_version=source_link_version,
        distribution=distribution,
        publish_target=publish_target,
        publish_feed_url=publish_feed_url,
        open_source=open_source,
        license_expression=license_expression,
        license_file=license_file,
        icon_kind=icon_kind,
        icon_file=icon_file,
        show_open_source_badge=show_open_source_badge,
        packages=tuple(packages),
        oci_images=tuple(images),
    )


def write(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    normalized = textwrap.dedent(content).strip() + "\n"
    path.write_text(normalized, encoding="utf-8", newline="\n")


def xml_escape(value: str) -> str:
    return value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


def csharp_escape(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"')


def kebab_case(value: str) -> str:
    tokens = re.findall(r"[A-Z]+(?=[A-Z][a-z]|[0-9]|$)|[A-Z]?[a-z]+|[0-9]+", value)
    return "-".join(token.casefold() for token in tokens)


def package_route_segments(package_id: str) -> list[str]:
    segments = package_id.split(".")[2:]
    if len(segments) > 1 and segments[-1].casefold() == "ui":
        segments = segments[:-1]
    return segments


def ui_route(package: PackageSpec, module: ModuleSpec) -> str:
    package_segments = package_route_segments(package.package_id)
    if not any(segment.casefold() == module.base_name.casefold() for segment in package_segments):
        package_segments.append(module.base_name)
    return "/" + "-".join(kebab_case(segment) for segment in package_segments)


def create_directory_build_props(root: Path, manifest: Manifest) -> None:
    write(
        root / "Directory.Build.props",
        f"""
        <Project>
          <PropertyGroup>
            <TargetFramework>{xml_escape(manifest.target_framework)}</TargetFramework>
            <LangVersion>preview</LangVersion>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
            <GenerateDocumentationFile>true</GenerateDocumentationFile>
            <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
          </PropertyGroup>
        </Project>
        """,
    )


def create_directory_packages_props(root: Path, manifest: Manifest) -> None:
    versions = [
        ("Monica.Core", manifest.monica_version),
        *(([("Monica.UI", manifest.monica_version)]) if manifest.has_ui else []),
        ("Monica.Testing", manifest.monica_version),
        ("AwesomeAssertions", "9.4.0"),
        ("coverlet.collector", "8.0.1"),
        ("Microsoft.NET.Test.Sdk", "18.4.0"),
        *(([(manifest.source_link_package, manifest.source_link_version)]) if manifest.source_link_package else []),
        ("xunit.runner.visualstudio", "3.1.5"),
        ("xunit.v3", "3.2.2"),
    ]
    lines = [
        "<Project>",
        "  <PropertyGroup>",
        "    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>",
        "  </PropertyGroup>",
        "  <ItemGroup>",
        *[
            f'    <PackageVersion Include="{package}" Version="{xml_escape(version)}" />'
            for package, version in versions
        ],
        "  </ItemGroup>",
        "</Project>",
    ]
    write(root / "Directory.Packages.props", "\n".join(lines))


def create_project(root: Path, manifest: Manifest, package: PackageSpec) -> Path:
    project_path = root / package.project_path
    project_dir = project_path.parent
    sdk = "Microsoft.NET.Sdk.Razor" if package.has_ui else "Microsoft.NET.Sdk"
    monica_reference = "Monica.UI" if package.has_ui else "Monica.Core"
    license_property = (
        f"<PackageLicenseExpression>{xml_escape(manifest.license_expression or '')}</PackageLicenseExpression>"
        if manifest.license_expression
        else f"<PackageLicenseFile>{xml_escape(manifest.license_file.name)}</PackageLicenseFile>"
    )
    package_items = [
        '..\\..\\README.md',
        f'..\\..\\{xml_escape(manifest.icon_name)}',
    ]
    if manifest.show_open_source_badge:
        package_items.append('..\\..\\monica-open-source-badge.svg')
    if manifest.icon_kind == "compatibility-mark":
        package_items.append('..\\..\\monica-compatibility-mark.svg')
    if manifest.license_file:
        package_items.append(f'..\\..\\{xml_escape(manifest.license_file.name)}')

    repository_properties: list[str] = []
    if manifest.source_available:
        assert manifest.repository_url is not None
        repository_properties = [
            f"    <RepositoryUrl>{xml_escape(manifest.repository_url)}</RepositoryUrl>",
            "    <RepositoryType>git</RepositoryType>",
        ]

    lines = [
        f'<Project Sdk="{sdk}">',
        "  <PropertyGroup>",
        f"    <PackageId>{package.package_id}</PackageId>",
        f"    <AssemblyName>{package.package_id}</AssemblyName>",
        f"    <RootNamespace>{package.package_id}</RootNamespace>",
        f"    <Version>{manifest.version}</Version>",
        f"    <Authors>{xml_escape(manifest.authors)}</Authors>",
        f"    <PackageDescription>{xml_escape(package.description)}</PackageDescription>",
        f"    <PackageProjectUrl>{xml_escape(manifest.project_url)}</PackageProjectUrl>",
        *repository_properties,
        f"    {license_property}",
        f"    <PackageIcon>{xml_escape(manifest.icon_name)}</PackageIcon>",
        "    <PackageReadmeFile>README.md</PackageReadmeFile>",
        f"    <PackageTags>{';'.join(package.package_tags)}</PackageTags>",
        "    <IncludeSymbols>true</IncludeSymbols>",
        "    <SymbolPackageFormat>snupkg</SymbolPackageFormat>",
        f"    <NuGetOwner>{xml_escape(manifest.nuget_owner)}</NuGetOwner>",
    ]
    if manifest.source_link_package:
        lines.extend(
            [
                "    <PublishRepositoryUrl>true</PublishRepositoryUrl>",
                "    <EmbedUntrackedSources>true</EmbedUntrackedSources>",
            ]
        )
    lines.append("  </PropertyGroup>")
    if manifest.source_link_package or package.has_web:
        lines.append("  <ItemGroup>")
        if manifest.source_link_package:
            lines.append(
                f'    <PackageReference Include="{manifest.source_link_package}" PrivateAssets="All" />'
            )
        if package.has_web:
            lines.append('    <FrameworkReference Include="Microsoft.AspNetCore.App" />')
        lines.append("  </ItemGroup>")
    lines.extend(
        [
            "  <ItemGroup>",
            f'    <PackageReference Include="{monica_reference}" />',
            *[
                f'    <ProjectReference Include="..\\{dependency}\\{dependency}.csproj" />'
                for dependency in package.package_dependencies
            ],
            "  </ItemGroup>",
            "  <ItemGroup>",
            *[
                f'    <None Include="{item}" Pack="true" PackagePath="" />'
                for item in package_items
            ],
            "  </ItemGroup>",
        ]
    )
    if package.has_ui:
        lines.extend(
            [
                "  <ItemGroup>",
                '    <EmbeddedResource Include="Localization\\**\\*.json" />',
                "  </ItemGroup>",
            ]
        )
    lines.append("</Project>")
    write(project_path, "\n".join(lines))
    return project_dir


def module_type_reference(
    owner: PackageSpec,
    module: ModuleSpec,
    *,
    fully_qualified: bool,
) -> str:
    prefix = f"global::{owner.package_id}.Modules." if fully_qualified else ""
    return f"{prefix}Module{module.name}"


def module_dependencies(
    package: PackageSpec,
    module: ModuleSpec,
    modules_by_key: dict[str, tuple[PackageSpec, ModuleSpec]],
) -> list[str]:
    dependencies: list[str] = []
    for key in module.depends_on:
        owner, dependency = modules_by_key[key.casefold()]
        module_type = module_type_reference(
            owner,
            dependency,
            fully_qualified=owner.package_id != package.package_id,
        )
        dependencies.append(f"module.Require<{module_type}>();")
    return dependencies


def create_module(
    project_dir: Path,
    manifest: Manifest,
    package: PackageSpec,
    module: ModuleSpec,
    ui_index: int,
    modules_by_key: dict[str, tuple[PackageSpec, ModuleSpec]],
) -> None:
    dependencies = module_dependencies(package, module, modules_by_key)
    additional_usings: list[str] = []
    module_interfaces: list[str] = []

    if module.kind == "web":
        module_interfaces.extend(["IWebModule", "IWebHostRequiredModule"])
    elif module.is_ui:
        module_interfaces.append("IUIModule")
        resource_name = f"{module.base_name}Resource"
        page_name = f"UI{module.base_name}Page"
        route = ui_route(package, module)
        dependencies.extend(
            [
                "module.Require<global::Monica.Modules.ModuleLocalization, "
                "global::Monica.Modules.ModuleLocalizationOption>(\n"
                f"    static option => option.AddResource<{resource_name}>());",
                "module.Require<global::Monica.Modules.ModuleShellUI, "
                "global::Monica.Modules.ModuleShellUIOption>(\n"
                "    static option => option.ConfigureNavigation(registry =>\n"
                "    {\n"
                f"        var category = registry.RegisterLocalizedCategory<{resource_name}>(\n"
                f"            \"{csharp_escape(module.navigation_category_id)}\",\n"
                "            \"Navigation:Category\",\n"
                f"            order: {UI_CATEGORY_ORDER_BASE + ui_index});\n"
                f"        registry.RegisterLocalizedPage<{page_name}, {resource_name}>(\n"
                f"            \"{route}\",\n"
                "            \"Navigation:Title\",\n"
                "            Icons.Material.Filled.Extension,\n"
                "            categoryId: category,\n"
                "            addToNav: true,\n"
                f"            navOrder: {UI_NAV_ORDER_BASE + ui_index});\n"
                "    }));",
            ]
        )
        additional_usings.extend(
            [
                f"using {package.package_id}.Localization;",
                f"using {package.package_id}.Pages;",
                "using Monica.Modules;",
                "using MudBlazor;",
            ]
        )
        create_ui_files(project_dir, package, module, route)

    dependency_method = ""
    if dependencies:
        statements = "\n\n".join(textwrap.indent(statement, "        ") for statement in dependencies)
        dependency_method = f"""
    /// <inheritdoc />
    public override void Describe(ModuleDescriptor module)
    {{
{statements}
    }}
""".rstrip()

    usings = [
        "using Monica.Core;",
        "using Monica.Core.Modularity;",
        "using Monica.Core.Modularity.Abstractions;",
        "using Monica.Core.Modularity.Models;",
        *additional_usings,
    ]
    if module.is_provider:
        module_interfaces.append("IModuleProvider")
    interface_list = "" if not module_interfaces else ", " + ", ".join(module_interfaces)
    provider_property = ""
    if module.is_provider:
        assert module.provider_for is not None
        target_package, target_module = modules_by_key[module.provider_for.casefold()]
        target_module_type = module_type_reference(
            target_package,
            target_module,
            fully_qualified=True,
        )
        provider_property = (
            "\n    /// <inheritdoc />\n"
            f"    public Type ProvidesFor => typeof({target_module_type});\n"
        )
        target_cref = target_module_type.removeprefix("global::")
        registration_surface = f"""
public static class Module{module.name}BuilderExtensions
{{
    /// <summary>
    /// Selects <see cref="Module{module.name}"/> as the provider for
    /// <see cref="{target_cref}"/>.
    /// </summary>
    /// <param name="target">The target capability registration.</param>
    /// <param name="configure">Optional provider option configuration.</param>
    /// <returns>The host-bound provider registration.</returns>
    public static ModuleRegistration<Module{module.name}, Module{module.name}Option> Use{module.name}Provider(
        this ModuleRegistration<{target_module_type}, {target_module_type}Option> target,
        Action<Module{module.name}Option>? configure = null)
    {{
        return target.Include<Module{module.name}, Module{module.name}Option>(configure);
    }}
}}
"""
    else:
        registration_surface = f"""
public static class Module{module.name}BuilderExtensions
{{
    extension(IMonicaBuilder builder)
    {{
        /// <summary>
        /// Registers <see cref="Module{module.name}"/> and applies optional configuration.
        /// </summary>
        /// <param name="configure">Optional module-options configuration.</param>
        /// <returns>The host-bound module registration.</returns>
        public ModuleRegistration<Module{module.name}, Module{module.name}Option> Add{module.name}(
            Action<Module{module.name}Option>? configure = null)
        {{
            return builder.AddModule<Module{module.name}, Module{module.name}Option>(configure);
        }}
    }}
}}
"""

    code = f"""{chr(10).join(dict.fromkeys(usings))}

namespace {package.package_id}.Modules;

/// <summary>
/// Registers the independently maintained {module.base_name}{' UI' if module.is_ui else ''} capability.
/// </summary>
public sealed class Module{module.name}
    : MonicaModule<Module{module.name}Option>{interface_list}
{{{provider_property}{dependency_method}
}}

/// <summary>
/// Configures <see cref="Module{module.name}"/>.
/// </summary>
public sealed class Module{module.name}Option : ModuleOptions<Module{module.name}>
{{
}}

/// <summary>
/// Adds the {module.base_name}{' UI' if module.is_ui else ''} module to a Monica host.
/// </summary>
{registration_surface}
"""
    write(project_dir / "Modules" / f"Module{module.name}.cs", code)


def create_ui_files(project_dir: Path, package: PackageSpec, module: ModuleSpec, route: str) -> None:
    resource_name = f"{module.base_name}Resource"
    page_name = f"UI{module.base_name}Page"
    write(
        project_dir / "Localization" / f"{resource_name}.cs",
        f"""
        using Monica.Core.Localization.Abstractions;

        namespace {package.package_id}.Localization;

        /// <summary>
        /// Identifies localization resources for the {module.base_name} UI.
        /// </summary>
        public sealed class {resource_name} : ILocalizationResource
        {{
        }}
        """,
    )
    en = {
        "Navigation": {"Title": module.base_name, "Category": "Extensions"},
        "Page": {
            "Eyebrow": "Community extension",
            "Title": module.base_name,
            "Description": package.description,
            "Status": "The module is registered and ready for its package-owned experience.",
        },
    }
    zh = {
        "Navigation": {"Title": module.base_name, "Category": "扩展模块"},
        "Page": {
            "Eyebrow": "社区扩展",
            "Title": module.base_name,
            "Description": "这是一个由独立开发者维护的 Monica 扩展模块。",
            "Status": "模块已注册，可以开始使用该包提供的功能。",
        },
    }
    resource_dir = project_dir / "Localization" / resource_name
    write(resource_dir / "en-US.json", json.dumps(en, ensure_ascii=False, indent=2))
    write(resource_dir / "zh-CN.json", json.dumps(zh, ensure_ascii=False, indent=2))

    write(
        project_dir / "_Imports.razor",
        f"""
        @using Microsoft.Extensions.Localization
        @using MudBlazor
        @using {package.package_id}.Localization
        """,
    )
    write(
        project_dir / "Pages" / f"{page_name}.razor",
        f"""
        @page "{route}"
        @inject IStringLocalizer<{resource_name}> L

        <div class="extension-page">
            <MudPaper Class="extension-hero" Elevation="2">
                <MudStack Spacing="3">
                    <MudChip T="string" Color="Color.Success" Variant="Variant.Outlined" Icon="@Icons.Material.Filled.Extension">
                        @L["Page:Eyebrow"]
                    </MudChip>
                    <MudText Typo="Typo.h3">@L["Page:Title"]</MudText>
                    <MudText Typo="Typo.body1" Color="Color.Secondary">@L["Page:Description"]</MudText>
                    <MudAlert Severity="Severity.Success" Variant="Variant.Filled" Dense="true">
                        @L["Page:Status"]
                    </MudAlert>
                </MudStack>
            </MudPaper>
        </div>
        """,
    )
    write(
        project_dir / "Pages" / f"{page_name}.razor.css",
        """
        .extension-page {
            display: grid;
            min-height: 100%;
            padding: clamp(1rem, 4vw, 3rem);
            place-items: center;
        }

        .extension-page ::deep .extension-hero {
            width: min(100%, 52rem);
            padding: clamp(1.5rem, 5vw, 4rem);
            border: 1px solid var(--mud-palette-lines-default);
            border-radius: var(--mud-default-borderradius);
            background:
                radial-gradient(circle at top right, color-mix(in srgb, var(--mud-palette-success) 18%, transparent), transparent 42%),
                var(--mud-palette-surface);
        }

        @media (max-width: 600px) {
            .extension-page {
                align-items: start;
                padding: 1rem;
            }
        }
        """,
    )


def registration_expression(
    module: ModuleSpec,
    modules_by_key: dict[str, tuple[PackageSpec, ModuleSpec]],
) -> str:
    if module.is_provider:
        assert module.provider_for is not None
        target_owner, target = modules_by_key[module.provider_for.casefold()]
        target_module_type = module_type_reference(
            target_owner,
            target,
            fully_qualified=True,
        )
        return (
            f"builder.AddModule<{target_module_type}, {target_module_type}Option>()"
            f".Use{module.name}Provider();"
        )
    return f"builder.Add{module.name}();"


def create_tests(
    root: Path,
    manifest: Manifest,
    package: PackageSpec,
    modules_by_key: dict[str, tuple[PackageSpec, ModuleSpec]],
) -> None:
    test_name = f"Test.{package.package_id}"
    test_dir = root / "tests" / test_name
    test_sdk = "Microsoft.NET.Sdk.Razor" if package.has_ui else "Microsoft.NET.Sdk"
    project_lines = [
        f'<Project Sdk="{test_sdk}">',
        "  <PropertyGroup>",
        "    <IsPackable>false</IsPackable>",
        "    <IsTestProject>true</IsTestProject>",
        "    <GenerateDocumentationFile>false</GenerateDocumentationFile>",
        f"    <RootNamespace>{test_name}</RootNamespace>",
    ]
    if package.has_ui:
        project_lines.extend(
            [
                "    <AddRazorSupportForMvc>true</AddRazorSupportForMvc>",
                "    <GenerateStaticWebAssetsManifest>true</GenerateStaticWebAssetsManifest>",
            ]
        )
    project_lines.extend(
        [
            "  </PropertyGroup>",
            "  <ItemGroup>",
            f'    <ProjectReference Include="..\\..\\{package.project_path.replace("/", "\\\\")}" />',
            '    <PackageReference Include="AwesomeAssertions" />',
            '    <PackageReference Include="coverlet.collector" PrivateAssets="All" />',
            '    <PackageReference Include="Microsoft.NET.Test.Sdk" />',
            '    <PackageReference Include="xunit.runner.visualstudio" PrivateAssets="All" />',
            '    <PackageReference Include="xunit.v3" />',
            '    <PackageReference Include="Monica.Testing" />',
            "  </ItemGroup>",
            "</Project>",
        ]
    )
    write(test_dir / f"{test_name}.csproj", "\n".join(project_lines))

    depended_on_keys = {
        dependency.casefold()
        for module in package.modules
        for dependency in module.depends_on
        if dependency.casefold() in {item.key.casefold() for item in package.modules}
    }
    entry_modules = [module for module in package.modules if module.key.casefold() not in depended_on_keys]
    registrations = "\n".join(
        f"        {registration_expression(module, modules_by_key)}"
        for module in entry_modules
    )
    all_reachable_keys = {
        module.key.casefold()
        for module in package.modules
    }
    for module in package.modules:
        all_reachable_keys.update(dependency.casefold() for dependency in module.depends_on)
    reachable_modules = [modules_by_key[key] for key in sorted(all_reachable_keys)]
    assertions = "\n".join(
        "        scope.Resolve<IOptions<"
        f"global::{owner.package_id}.Modules.Module{module.name}Option"
        ">>().Value.Should().NotBeNull();"
        for owner, module in reachable_modules
    )
    dependency_usings = [f"using {package.package_id}.Modules;"]

    ui_modules = [module for module in package.modules if module.is_ui]
    ui_usings = ""
    ui_setup = ""
    ui_assertions = ""
    if ui_modules:
        ui_usings = f"""using {package.package_id}.Localization;
using {package.package_id}.Pages;
using Monica.Core.Localization.Abstractions;
using Monica.UI.Shell.Models;
using Monica.UI.Shell.Support;
"""
        ui_setup = """
        var pageCatalog = scope.Resolve<IPageCatalog>();
        var localizationCatalog = scope.Resolve<ILocalizationCatalog>();
""".rstrip()
        blocks: list[str] = []
        for index, module in enumerate(ui_modules):
            route = ui_route(package, module).lstrip("/")
            blocks.append(
                f"""        var categoryId{index} = NavigationCategoryId.Create("{csharp_escape(module.navigation_category_id)}");
        var page{index} = pageCatalog.GetRegisteredPages()
            .Single(page => page.ComponentType == typeof(UI{module.base_name}Page));
        var category{index} = pageCatalog.GetNavigationCategories()
            .Single(category => category.Id == categoryId{index});
        var navigationItem{index} = pageCatalog.GetNavItems()
            .Single(item => item.Href == "{route}");

        page{index}.Route.Should().Be("{route}");
        page{index}.DisplayName.ResourceType.Should().Be(typeof({module.base_name}Resource));
        page{index}.DisplayName.Key.Should().Be("Navigation:Title");
        category{index}.DisplayName.ResourceType.Should().Be(typeof({module.base_name}Resource));
        category{index}.DisplayName.Key.Should().Be("Navigation:Category");
        category{index}.Order.Should().Be({UI_CATEGORY_ORDER_BASE + index});
        navigationItem{index}.CategoryId.Should().Be(categoryId{index});
        navigationItem{index}.Order.Should().Be({UI_NAV_ORDER_BASE + index});
        localizationCatalog.For<{module.base_name}Resource>().Should().NotBeNull();"""
            )
        ui_assertions = "\n\n" + "\n\n".join(blocks)

    anchor = package.modules[0]
    anchor_type = f"global::{package.package_id}.Modules.Module{anchor.name}"
    write(
        test_dir / "Modules" / "ModuleRegistrationTests.cs",
        f"""{chr(10).join(dependency_usings)}
{ui_usings}using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Monica.Core.Modularity.Abstractions;
using Monica.Testing.Hosting;
using Xunit;

namespace {test_name}.Modules;

public sealed class PackageTestApplicationFactory : MonicaTestApplicationFactory<{anchor_type}>
{{
    protected override void ConfigureMonica(IMonicaBuilder builder)
    {{
{registrations}
    }}
}}

public sealed class ModuleRegistrationTests(PackageTestApplicationFactory factory)
    : IClassFixture<PackageTestApplicationFactory>
{{
    [Fact]
    public async Task CreateAsync_WhenEntryModulesAreRegistered_ShouldComposeDeclaredDependencies()
    {{
        await using var application = await factory.CreateAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        await using var scope = application.CreateScope(TestContext.Current.CancellationToken);

{assertions}
{ui_setup}{ui_assertions}
    }}
}}
""",
    )


def create_tests_readme(root: Path, manifest: Manifest) -> None:
    projects = "\n".join(
        f"- `tests/Test.{package.package_id}/Test.{package.package_id}.csproj`"
        for package in manifest.packages
    )
    write(
        root / "tests" / "README.md",
        f"""
        # Tests

        Each independently published package owns a publisher-first test project:

        {projects}

        Registration tests add only graph-entry modules. Runtime dependencies, provider selection,
        localized navigation, and module-owned resource markers must compose transitively.

        Keep test processes sharing build outputs sequential. Use paths understood by the selected SDK.
        """,
    )


def create_readme(root: Path, manifest: Manifest) -> None:
    package_rows = "\n".join(
        f"| `{package.package_id}` | {package.description} | `{'`, `'.join(package.capability_tags)}` |"
        for package in manifest.packages
    )
    module_rows = "\n".join(
        f"| `{package.package_id}` | `Module{module.name}` | `{module.key}` | `{module.kind}` |"
        for package in manifest.packages
        for module in package.modules
    )
    license_text = manifest.license_expression or manifest.license_file.name
    install_lines = "\n".join(
        f"dotnet add package {package.package_id} --version {manifest.version}"
        for package in manifest.packages
    )
    image_lines = "\n".join(
        f"- `{image.repository}`: "
        + ", ".join(f"`{manifest.version}-{target.tag_suffix}`" for target in image.targets)
        for image in manifest.oci_images
    )
    lines = [
        f"# {manifest.repository_id}",
        "",
        f"![Package icon]({manifest.icon_name})",
    ]
    if manifest.show_open_source_badge:
        lines.extend(["", "![Monica Open Source](monica-open-source-badge.svg)"])
    lines.extend(["", "Independent Monica packages released from one validated repository contract.", ""])
    if manifest.icon_kind == "compatibility-mark":
        lines.extend([COMPATIBILITY_NOTICE, ""])
    lines.extend(
        [
            "## Install",
            "",
            "```bash",
            install_lines,
            "```",
            "",
            "## Packages",
            "",
            "| Package | Description | Capability tags |",
            "|---|---|---|",
            package_rows,
            "",
            "## Modules",
            "",
            "| Package | Module | Manifest ecosystem key | Kind |",
            "|---|---|---|---|",
            module_rows,
            "",
            "NuGet dependencies and manifest module declarations are separate explicit graphs; Monica compiles manifest edges into concrete CLR-type runtime dependencies.",
            "",
            "## Repository contract",
            "",
            f"- NuGet owner: `{manifest.nuget_owner}`",
            f"- Distribution: `{manifest.distribution}`",
            f"- Publishing target: `{manifest.publish_target}`",
            f"- Source available: `{'yes' if manifest.source_available else 'no'}`",
            f"- Source provider: `{manifest.source_provider}`",
            f"- Monica minimum version: `{manifest.monica_version}`",
            "- Monica Ecosystem Standard: `v1`",
            f"- License: `{license_text}`",
            "",
            "## Project links",
            "",
            f"- [Project]({manifest.project_url})",
            f"- [Support]({manifest.support_url})",
            f"- [Security policy]({manifest.security_url})",
            "",
            "## Independence",
            "",
            f"These packages are maintained by {manifest.authors} for the NuGet owner {manifest.nuget_owner}. They are independent of the Monica project.",
        ]
    )
    if image_lines:
        contract_index = lines.index("## Repository contract")
        lines[contract_index:contract_index] = ["## Companion OCI images", "", image_lines, ""]
    if manifest.source_available and manifest.repository_url:
        project_link_index = lines.index(f"- [Project]({manifest.project_url})") + 1
        lines.insert(project_link_index, f"- [Repository]({manifest.repository_url})")
    write(root / "README.md", "\n".join(lines))


def create_license(root: Path, manifest: Manifest) -> None:
    if manifest.license_file:
        shutil.copy2(manifest.license_file, root / manifest.license_file.name)
        return
    if manifest.license_expression != "MIT":
        return
    year = datetime.now(UTC).year
    write(
        root / "LICENSE",
        f"""
        MIT License

        Copyright (c) {year} {manifest.authors}

        Permission is hereby granted, free of charge, to any person obtaining a copy
        of this software and associated documentation files (the "Software"), to deal
        in the Software without restriction, including without limitation the rights
        to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
        copies of the Software, and to permit persons to whom the Software is
        furnished to do so, subject to the following conditions:

        The above copyright notice and this permission notice shall be included in all
        copies or substantial portions of the Software.

        THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
        IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
        FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
        AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
        LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
        OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
        SOFTWARE.
        """,
    )


def create_workflows(root: Path, manifest: Manifest) -> None:
    if manifest.source_provider != "github":
        return

    localization_step = (
        "\n              - run: python scripts/validate_localization.py --root . --strict"
        if manifest.has_ui
        else ""
    )
    oci_validation_steps = ""
    oci_build_steps = ""
    if manifest.oci_images:
        oci_validation_steps = """
              - uses: docker/setup-buildx-action@v3
              - run: python scripts/validate_oci.py --root .
""".rstrip()
        bake_files = tuple(dict.fromkeys(image.bake_file_path for image in manifest.oci_images))
        oci_build_steps = "\n".join(
            f"              - run: docker buildx bake --file {bake_file}"
            for bake_file in bake_files
        )
    write(
        root / ".github" / "workflows" / "ci.yml",
        f"""
        name: CI

        on:
          push:
            branches: [main]
          pull_request:

        permissions:
          contents: read

        jobs:
          validate:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v4
              - uses: actions/setup-dotnet@v4
                with:
                  dotnet-version: "10.0.x"
              - run: python scripts/validate_repository.py --root .{localization_step}
{oci_validation_steps}
{oci_build_steps}
              - run: dotnet restore {manifest.solution_path}
              - run: dotnet build {manifest.solution_path} --configuration Release --no-restore
              - run: dotnet test {manifest.solution_path} --configuration Release --no-build
              - run: dotnet pack {manifest.solution_path} --configuration Release --no-build --output artifacts
              - run: python scripts/inspect_packages.py --root . --artifacts artifacts
              - uses: actions/upload-artifact@v4
                with:
                  name: packages
                  path: artifacts/*.*nupkg
        """,
    )
    if manifest.publish_target != "nuget.org":
        return
    if manifest.oci_images and not manifest.has_complete_oci_release_gates:
        return

    publish_permissions = ""
    oci_publish_steps = ""
    release_runner = '"ubuntu-latest"'
    if manifest.oci_images:
        publish_permissions = "\n          packages: write"
        if manifest.release_runner_labels:
            release_runner = json.dumps(list(manifest.release_runner_labels))
        registries = {image.repository.split("/", 1)[0] for image in manifest.oci_images}
        bake_files = tuple(dict.fromkeys(image.bake_file_path for image in manifest.oci_images))
        if registries == {"ghcr.io"}:
            load_commands = "\n".join(
                f'              - run: RELEASE_VERSION="${{{{ steps.version.outputs.value }}}}" docker buildx bake --file {bake_file} --load'
                for bake_file in bake_files
            )
            smoke_steps: list[str] = []
            for image in manifest.oci_images:
                assert image.release_gates is not None
                if image.release_gates.cpu_smoke_command:
                    smoke_steps.extend(
                        [
                            f"              - name: {image.image_id} CPU provider smoke",
                            "                shell: bash",
                            "                run: |",
                            f"                  {image.release_gates.cpu_smoke_command}",
                        ]
                    )
                if image.release_gates.nvidia_smoke_command:
                    smoke_steps.extend(
                        [
                            f"              - name: {image.image_id} NVIDIA provider smoke",
                            "                shell: bash",
                            "                run: |",
                            f"                  {image.release_gates.nvidia_smoke_command}",
                        ]
                    )
            smoke_commands = "\n".join(smoke_steps)
            bake_commands = "\n".join(
                f'              - run: RELEASE_VERSION="${{{{ steps.version.outputs.value }}}}" docker buildx bake --file {bake_file} --push'
                for bake_file in bake_files
            )
            oci_publish_steps = f"""
              - uses: docker/setup-buildx-action@v3
              - run: python scripts/validate_oci.py --root .
                env:
                  RELEASE_VERSION: ${{{{ steps.version.outputs.value }}}}
{load_commands}
              - run: python scripts/inspect_images.py --root .
                env:
                  RELEASE_VERSION: ${{{{ steps.version.outputs.value }}}}
{smoke_commands}
              - uses: docker/login-action@v3
                with:
                  registry: ghcr.io
                  username: ${{{{ github.actor }}}}
                  password: ${{{{ secrets.GITHUB_TOKEN }}}}
{bake_commands}
""".rstrip()
        else:
            oci_publish_steps = """
              - name: Configure declared OCI registry publishing
                run: echo "This scaffold only configures GHCR authentication automatically." >&2 && exit 1
""".rstrip()

    write(
        root / ".github" / "workflows" / "publish.yml",
        f"""
        name: Publish

        on:
          push:
            tags: ["v*"]

        permissions:
          contents: read
          id-token: write{publish_permissions}

        jobs:
          publish:
            runs-on: {release_runner}
            environment: release
            steps:
              - uses: actions/checkout@v4
              - uses: actions/setup-dotnet@v4
                with:
                  dotnet-version: "10.0.x"
              - name: Derive package version
                id: version
                shell: bash
                run: echo "value=${{GITHUB_REF_NAME#v}}" >> "$GITHUB_OUTPUT"
              - run: python scripts/validate_repository.py --root . --package-version "${{{{ steps.version.outputs.value }}}}"
              - run: dotnet restore {manifest.solution_path}
              - run: dotnet build {manifest.solution_path} --configuration Release --no-restore
              - run: dotnet test {manifest.solution_path} --configuration Release --no-build
              - run: dotnet pack {manifest.solution_path} --configuration Release --no-build --output artifacts -p:PackageVersion=${{{{ steps.version.outputs.value }}}}
              - run: python scripts/inspect_packages.py --root . --artifacts artifacts --package-version "${{{{ steps.version.outputs.value }}}}"
{oci_publish_steps}
              - name: Exchange OIDC token for a temporary NuGet key
                uses: NuGet/login@v1
                id: nuget-login
                with:
                  user: ${{{{ secrets.NUGET_USER }}}}
              - name: Publish package
                run: dotnet nuget push artifacts/*.nupkg --api-key "${{{{ steps.nuget-login.outputs.NUGET_API_KEY }}}}" --source https://api.nuget.org/v3/index.json
        """,
    )


def create_solution(root: Path, manifest: Manifest) -> None:
    source_projects = "\n".join(
        f'    <Project Path="{package.project_path}" />'
        for package in manifest.packages
    )
    test_projects = "\n".join(
        f'    <Project Path="tests/Test.{package.package_id}/Test.{package.package_id}.csproj" />'
        for package in manifest.packages
    )
    write(
        root / manifest.solution_path,
        f"""
        <Solution>
          <Folder Name="/src/">
        {source_projects}
          </Folder>
          <Folder Name="/tests/">
        {test_projects}
          </Folder>
        </Solution>
        """,
    )


def create_repository_contract(root: Path, manifest: Manifest) -> None:
    source: dict[str, Any] = {
        "available": manifest.source_available,
        "provider": manifest.source_provider,
    }
    if manifest.source_link_version:
        source["sourceLinkVersion"] = manifest.source_link_version

    publishing: dict[str, Any] = {"target": manifest.publish_target}
    if manifest.publish_feed_url:
        publishing["feedUrl"] = manifest.publish_feed_url

    license_data: dict[str, Any] = {"openSource": manifest.open_source}
    if manifest.license_expression:
        license_data["expression"] = manifest.license_expression
    else:
        assert manifest.license_file is not None
        license_data["file"] = manifest.license_file.name

    icon: dict[str, Any] = {"kind": manifest.icon_kind}
    if manifest.icon_file:
        icon["file"] = manifest.icon_file.name

    payload = {
        "schemaVersion": 2,
        "repositoryId": manifest.repository_id,
        "solutionPath": manifest.solution_path,
        "version": manifest.version,
        "authors": manifest.authors,
        "nugetOwner": manifest.nuget_owner,
        "projectUrl": manifest.project_url,
        "supportUrl": manifest.support_url,
        "securityUrl": manifest.security_url,
        "source": source,
        "distribution": manifest.distribution,
        "publishing": publishing,
        "targetFramework": manifest.target_framework,
        "monicaVersion": manifest.monica_version,
        "license": license_data,
        "branding": {
            "icon": icon,
            "showOpenSourceBadge": manifest.show_open_source_badge,
        },
        "packages": [
            {
                "packageId": package.package_id,
                "projectPath": package.project_path,
                "description": package.description,
                "capabilityTags": list(package.capability_tags),
                "packageDependencies": list(package.package_dependencies),
                "modules": [
                    {
                        "name": module.name,
                        "kind": module.kind,
                        "key": module.key,
                        **({"dependsOn": list(module.depends_on)} if module.depends_on else {}),
                        **({"providerFor": module.provider_for} if module.provider_for else {}),
                    }
                    for module in package.modules
                ],
            }
            for package in manifest.packages
        ],
        "ociImages": [
            {
                "id": image.image_id,
                "repository": image.repository,
                "companionPackageId": image.companion_package_id,
                "contextPath": image.context_path,
                "dockerfilePath": image.dockerfile_path,
                "bakeFilePath": image.bake_file_path,
                **(
                    {
                        "releaseGates": {
                            **(
                                {"cpuSmokeCommand": image.release_gates.cpu_smoke_command}
                                if image.release_gates.cpu_smoke_command
                                else {}
                            ),
                            **(
                                {"nvidiaSmokeCommand": image.release_gates.nvidia_smoke_command}
                                if image.release_gates.nvidia_smoke_command
                                else {}
                            ),
                            **(
                                {
                                    "managedNvidiaRunnerLabels": list(
                                        image.release_gates.managed_nvidia_runner_labels
                                    )
                                }
                                if image.release_gates.managed_nvidia_runner_labels
                                else {}
                            ),
                        }
                    }
                    if image.release_gates
                    else {}
                ),
                "targets": [
                    {
                        "bakeTarget": target.bake_target,
                        "stage": target.stage,
                        "platform": target.platform,
                        "accelerator": target.accelerator,
                        "tagSuffix": target.tag_suffix,
                    }
                    for target in image.targets
                ],
            }
            for image in manifest.oci_images
        ],
    }
    if manifest.repository_url:
        payload["repositoryUrl"] = manifest.repository_url
    write(root / "monica.manifest.json", json.dumps(payload, ensure_ascii=False, indent=2))


def create_oci_contracts(root: Path, manifest: Manifest) -> None:
    by_bake_file: dict[str, list[tuple[OciImageSpec, OciTargetSpec]]] = {}
    for image in manifest.oci_images:
        (root / image.context_path).mkdir(parents=True, exist_ok=True)
        by_bake_file.setdefault(image.bake_file_path, []).extend(
            (image, target) for target in image.targets
        )

    for bake_file, declarations in by_bake_file.items():
        target_names = ", ".join(f'"{target.bake_target}"' for _, target in declarations)
        blocks = [
            'variable "RELEASE_VERSION" {',
            f'  default = "{manifest.version}"',
            '}',
            '',
            'group "default" {',
            f'  targets = [{target_names}]',
            '}',
        ]
        for image, target in declarations:
            try:
                dockerfile_for_bake = Path(image.dockerfile_path).relative_to(Path(image.context_path)).as_posix()
            except ValueError:
                dockerfile_for_bake = image.dockerfile_path
            blocks.extend(
                [
                    '',
                    f'target "{target.bake_target}" {{',
                    f'  context = "{image.context_path}"',
                    f'  dockerfile = "{dockerfile_for_bake}"',
                    f'  target = "{target.stage}"',
                    f'  platforms = ["{target.platform}"]',
                    f'  tags = ["{image.repository}:${{RELEASE_VERSION}}-{target.tag_suffix}"]',
                    '}',
                ]
            )
        write(root / bake_file, "\n".join(blocks))


def create_repository(manifest: Manifest, output: Path) -> None:
    output = output.resolve()
    if output.exists() and any(output.iterdir()):
        raise ValueError(f"Output directory must be new or empty: {output}")
    output.mkdir(parents=True, exist_ok=True)

    create_directory_build_props(output, manifest)
    create_directory_packages_props(output, manifest)
    modules_by_key = {
        module.key.casefold(): (package, module)
        for package in manifest.packages
        for module in package.modules
    }
    for package in manifest.packages:
        project_dir = create_project(output, manifest, package)
        ui_index = 0
        for module in package.modules:
            create_module(project_dir, manifest, package, module, ui_index, modules_by_key)
            if module.is_ui:
                ui_index += 1
        create_tests(output, manifest, package, modules_by_key)
    create_tests_readme(output, manifest)
    create_readme(output, manifest)
    create_license(output, manifest)
    create_workflows(output, manifest)
    create_solution(output, manifest)
    create_repository_contract(output, manifest)
    create_oci_contracts(output, manifest)

    skill_root = Path(__file__).resolve().parent.parent
    skill_assets = skill_root / "assets"
    if manifest.icon_kind == "compatibility-mark":
        shutil.copy2(skill_assets / "monica-compatibility-mark.png", output / "monica-compatibility-mark.png")
        shutil.copy2(skill_assets / "monica-compatibility-mark.svg", output / "monica-compatibility-mark.svg")
    else:
        assert manifest.icon_file is not None
        shutil.copy2(manifest.icon_file, output / manifest.icon_name)
    if manifest.show_open_source_badge:
        shutil.copy2(skill_assets / "monica-open-source-badge.svg", output / "monica-open-source-badge.svg")

    generated_scripts = output / "scripts"
    generated_scripts.mkdir(parents=True, exist_ok=True)
    shutil.copy2(skill_root / "scripts" / "validate_repository.py", generated_scripts / "validate_repository.py")
    shutil.copy2(skill_root / "scripts" / "inspect_packages.py", generated_scripts / "inspect_packages.py")
    if manifest.oci_images:
        shutil.copy2(skill_root / "scripts" / "validate_oci.py", generated_scripts / "validate_oci.py")
        shutil.copy2(skill_root / "scripts" / "inspect_images.py", generated_scripts / "inspect_images.py")
    if manifest.has_ui:
        localization_validator = skill_root.parent / "monica-ui-localization" / "scripts" / "validate_localization.py"
        if not localization_validator.is_file():
            raise ValueError(f"Required companion localization validator was not found: {localization_validator}")
        shutil.copy2(localization_validator, generated_scripts / "validate_localization.py")

    write(
        output / ".gitignore",
        """
        **/bin/
        **/obj/
        .idea/
        .vs/
        .vscode/
        TestResults/
        artifacts/
        *.user
        *.suo
        """,
    )


def main() -> int:
    args = parse_args()
    try:
        manifest = load_manifest(args.manifest)
        create_repository(manifest, args.output)
    except (ValueError, OSError, json.JSONDecodeError) as exc:
        print(f"Scaffold failed: {exc}", file=sys.stderr)
        return 1

    print(f"Created {manifest.repository_id} with {len(manifest.packages)} package(s) at {args.output.resolve()}")
    if manifest.oci_images:
        print("Implement every declared service Dockerfile before running OCI validation; the scaffold does not invent fake services.")
        if not manifest.has_complete_oci_release_gates:
            print("OCI release automation was omitted because provider-specific releaseGates are incomplete.")
    print("Implement the real capability, then validate the repository, build, test, pack, inspect, and run clean consumers.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
