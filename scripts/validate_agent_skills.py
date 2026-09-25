#!/usr/bin/env python3
"""Validate Monica's canonical Agent Skills catalog and release contracts."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
from collections import defaultdict
from pathlib import Path
from typing import Any, Iterable

try:
    import jsonschema
except ImportError:  # pragma: no cover - exercised by dependency-free environments
    jsonschema = None

try:
    from agent_skill_release_contract import (
        ReleaseContractError,
        validate_revision_history,
    )
    from agent_skill_file_manifest import digest_files
    from agent_skill_references import validate_entrypoint_budget, validate_reference_navigation
except ModuleNotFoundError:  # pragma: no cover - supports import-by-path test runners
    from scripts.agent_skill_release_contract import (
        ReleaseContractError,
        validate_revision_history,
    )
    from scripts.agent_skill_file_manifest import digest_files
    from scripts.agent_skill_references import validate_entrypoint_budget, validate_reference_navigation


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
CATALOG_PATH = REPOSITORY_ROOT / ".monica" / "agent-skill-catalog.json"
INDEX_PATH = REPOSITORY_ROOT / ".monica" / "agent-skill-index.json"
CATALOG_SCHEMA_PATH = REPOSITORY_ROOT / ".monica" / "schemas" / "agent-skill-catalog.schema.json"
INDEX_SCHEMA_PATH = REPOSITORY_ROOT / ".monica" / "schemas" / "agent-skill-index.schema.json"
BOOTSTRAP_TOKEN_PATTERN = re.compile(r"\{\{[A-Z0-9_]+\}\}")
SKILL_NAME_PATTERN = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
SKILL_REFERENCE_PATTERN = re.compile(r"\$((?:monica|mo)-[a-z0-9-]+)")
FRONTMATTER_PATTERN = re.compile(r"\A---\r?\n(?P<body>.*?)\r?\n---\r?\n", re.DOTALL)
TOP_LEVEL_YAML_KEY_PATTERN = re.compile(r"^([A-Za-z0-9_-]+):(?:\s|$)")
SHA256_PATTERN = re.compile(r"^sha256:[0-9a-f]{64}$")
TEXT_SOURCE_SUFFIXES = {
    ".bat",
    ".cmd",
    ".cs",
    ".css",
    ".html",
    ".js",
    ".json",
    ".jsx",
    ".md",
    ".mjs",
    ".props",
    ".ps1",
    ".py",
    ".razor",
    ".sh",
    ".slnx",
    ".targets",
    ".toml",
    ".ts",
    ".tsx",
    ".txt",
    ".xml",
    ".yaml",
    ".yml",
}
IGNORED_SOURCE_DIRECTORIES = {
    ".git",
    ".idea",
    ".tmp",
    ".vs",
    ".vscode",
    "bin",
    "node_modules",
    "obj",
}


class Validation:
    def __init__(self) -> None:
        self.errors: list[str] = []

    def check(self, condition: bool, message: str) -> None:
        if not condition:
            self.errors.append(message)


def _reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key {key!r}")
        result[key] = value
    return result


def load_json(path: Path) -> dict[str, Any]:
    payload = json.loads(
        path.read_text(encoding="utf-8"), object_pairs_hook=_reject_duplicate_keys
    )
    if not isinstance(payload, dict):
        raise ValueError(f"{path} must contain a JSON object")
    return payload


def repository_source_files(root: Path) -> list[Path]:
    """Return versioned and untracked source files without build or tool caches."""

    try:
        result = subprocess.run(
            ["git", "-C", str(root), "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
            check=True,
            capture_output=True,
        )
        candidates = [root / item.decode("utf-8") for item in result.stdout.split(b"\0") if item]
    except (FileNotFoundError, subprocess.CalledProcessError, UnicodeDecodeError):
        candidates = [path for path in root.rglob("*") if path.is_file()]

    return sorted(
        path
        for path in candidates
        if path.is_file()
        and path.suffix.lower() in TEXT_SOURCE_SUFFIXES
        and not any(part in IGNORED_SOURCE_DIRECTORIES for part in path.relative_to(root).parts[:-1])
    )


def find_retired_alias_occurrences(
    root: Path,
    aliases: Iterable[str],
    *,
    allowed_files: Iterable[Path] = (),
    allowed_roots: Iterable[Path] = (),
) -> list[str]:
    """Find retired skill names outside their catalog definitions and diagnostic fixtures."""

    alias_list = sorted(set(aliases), key=len, reverse=True)
    if not alias_list:
        return []
    alias_pattern = re.compile(
        rf"(?<![a-z0-9-])({'|'.join(re.escape(alias) for alias in alias_list)})(?![a-z0-9-])"
    )
    allowed_file_set = {path.resolve() for path in allowed_files}
    allowed_root_set = {path.resolve() for path in allowed_roots}
    occurrences: list[str] = []

    for path in repository_source_files(root):
        resolved = path.resolve()
        if resolved in allowed_file_set or any(
            resolved == allowed_root or resolved.is_relative_to(allowed_root)
            for allowed_root in allowed_root_set
        ):
            continue
        relative = path.relative_to(root).as_posix()
        for part in path.relative_to(root).parts:
            if part in alias_list:
                occurrences.append(f"{relative}:path:{part}")
        text = path.read_text(encoding="utf-8", errors="replace")
        for line_number, line in enumerate(text.splitlines(), start=1):
            for match in alias_pattern.finditer(line):
                occurrences.append(f"{relative}:{line_number}:{match.group(1)}")
    return occurrences


def validate_schema(
    validation: Validation, payload: dict[str, Any], schema_path: Path, label: str
) -> None:
    if jsonschema is None:
        validation.errors.append(
            "The jsonschema package is required; install scripts/requirements-agent-skills.txt."
        )
        return
    schema = load_json(schema_path)
    validator = jsonschema.Draft202012Validator(schema, format_checker=jsonschema.FormatChecker())
    for error in sorted(validator.iter_errors(payload), key=lambda item: list(item.absolute_path)):
        location = "/".join(str(segment) for segment in error.absolute_path) or "<root>"
        validation.errors.append(f"{label} schema {location}: {error.message}")


def parse_frontmatter(path: Path, validation: Validation) -> tuple[str | None, str | None]:
    text = path.read_text(encoding="utf-8")
    match = FRONTMATTER_PATTERN.match(text)
    if not match:
        validation.errors.append(f"{path}: missing or malformed YAML frontmatter")
        return None, None

    keys: dict[str, str] = {}
    for line in match.group("body").splitlines():
        key_match = TOP_LEVEL_YAML_KEY_PATTERN.match(line)
        if not key_match:
            validation.errors.append(f"{path}: unsupported multiline or nested frontmatter: {line!r}")
            continue
        key = key_match.group(1)
        if key in keys:
            validation.errors.append(f"{path}: duplicate frontmatter field {key!r}")
            continue
        keys[key] = line.split(":", 1)[1].strip()

    validation.check(
        set(keys) == {"name", "description"},
        f"{path}: portable frontmatter must contain only name and description; found {sorted(keys)}",
    )
    name = keys.get("name", "").strip("'\"") or None
    description = keys.get("description", "").strip("'\"") or None
    if name:
        validation.check(bool(SKILL_NAME_PATTERN.fullmatch(name)), f"{path}: invalid skill name {name!r}")
        validation.check(len(name) <= 64, f"{path}: skill name exceeds 64 characters")
    if description:
        validation.check(len(description) <= 1024, f"{path}: description exceeds 1024 characters")
    return name, description


def parse_openai_yaml(path: Path, skill_name: str, validation: Validation) -> None:
    if not path.is_file():
        validation.errors.append(f"{path}: missing agents/openai.yaml")
        return
    text = path.read_text(encoding="utf-8")
    fields: dict[str, str] = {}
    for field in ("display_name", "short_description", "default_prompt"):
        match = re.search(rf"^\s{{2}}{field}:\s*\"(.*)\"\s*$", text, re.MULTILINE)
        if match:
            fields[field] = match.group(1)
        else:
            validation.errors.append(f"{path}: missing quoted interface.{field}")
    short_description = fields.get("short_description", "")
    if short_description:
        validation.check(
            25 <= len(short_description) <= 64,
            f"{path}: short_description must contain 25-64 characters",
        )
    default_prompt = fields.get("default_prompt", "")
    if default_prompt:
        validation.check(
            f"${skill_name}" in default_prompt,
            f"{path}: default_prompt must explicitly mention ${skill_name}",
        )


def required_closure(skills: dict[str, Any], roots: Iterable[str]) -> set[str]:
    closure: set[str] = set()
    pending = list(roots)
    while pending:
        name = pending.pop()
        if name in closure:
            continue
        closure.add(name)
        entry = skills.get(name)
        if entry:
            pending.extend(entry["dependencies"]["required"])
    return closure


def profile_selection_closure(
    skills: dict[str, Any],
    profile: dict[str, Any],
    *,
    include_recommended: bool = False,
    capabilities: Iterable[str] = (),
) -> set[str]:
    """Resolve explicit profile buckets through required dependency edges only."""

    profile_skills = profile["skills"]
    roots = list(profile_skills["required"])
    if include_recommended:
        roots.extend(profile_skills["recommended"])
    selected_capabilities = set(capabilities)
    for conditional in profile_skills["conditional"]:
        if conditional["capability"] in selected_capabilities:
            roots.extend(conditional["skills"])
    return required_closure(skills, roots)


def find_required_cycles(skills: dict[str, Any]) -> list[list[str]]:
    graph = {
        name: entry["dependencies"]["required"]
        for name, entry in skills.items()
    }
    colors: dict[str, int] = defaultdict(int)
    stack: list[str] = []
    cycles: list[list[str]] = []

    def visit(name: str) -> None:
        colors[name] = 1
        stack.append(name)
        for dependency in graph.get(name, []):
            if colors[dependency] == 0:
                visit(dependency)
            elif colors[dependency] == 1:
                start = stack.index(dependency)
                cycles.append(stack[start:] + [dependency])
        stack.pop()
        colors[name] = 2

    for skill_name in graph:
        if colors[skill_name] == 0:
            visit(skill_name)
    return cycles


def bootstrap_prompt_entries(
    bootstrap: dict[str, Any],
) -> Iterable[tuple[str, str]]:
    """Yield each localized universal prompt from the schema-v3 manifest."""

    for locale, localized in bootstrap.get("locales", {}).items():
        yield locale, localized.get("prompt", "")


def validate_bootstrap_prompts(
    validation: Validation,
    catalog: dict[str, Any],
    bootstrap: dict[str, Any],
    schema_path: Path,
) -> None:
    """Validate executable website prompts against the release catalog contract."""

    validate_schema(validation, bootstrap, schema_path, "bootstrap prompts")
    expected_distribution = dict(catalog["distribution"])
    validation.check(
        bootstrap.get("distribution") == expected_distribution,
        "bootstrap prompts: distribution must exactly match the catalog installer and URL template",
    )
    validation.check(
        bootstrap.get("immutableRef") == "{{MONICA_IMMUTABLE_REF}}",
        "bootstrap prompts: immutable release token is missing",
    )
    validation.check(
        bootstrap.get("catalogDigest") == "{{MONICA_CATALOG_DIGEST}}",
        "bootstrap prompts: catalog digest token is missing",
    )

    validation.check(
        bootstrap.get("verifiedAgentTargets") == ["codex", "claude-code"],
        "bootstrap prompts: verified agent targets must remain informational Codex and Claude Code metadata",
    )

    expected_fallback = {
        "installCommand": "$monica-guide configure --workspace <workspace>",
        "verifyCommand": "$monica-guide status --workspace <workspace>",
    }
    validation.check(
        bootstrap.get("fallback") == expected_fallback,
        "bootstrap prompts: interactive fallback commands must match the catalog-pinned, platform-neutral contract",
    )

    entries = list(bootstrap_prompt_entries(bootstrap))
    validation.check(
        {locale for locale, _ in entries} == {"en-US", "zh-CN"}
        and len(entries) == 2,
        "bootstrap prompts: exactly one universal prompt is required for each supported locale",
    )
    for locale, prompt in entries:
        label = f"bootstrap {locale}"
        validation.check(
            "$monica-guide init --workspace" in prompt
            and "$monica-guide configure --workspace" in prompt,
            f"{label}: must bootstrap the project directory through the unified guide executable",
        )
        validation.check(
            "{rid}" in prompt
            and "Monica.Guide.exe" not in prompt
            and "win-x64" not in prompt,
            f"{label}: must name the platform through the rid placeholder, never a single-platform executable or asset",
        )
        validation.check(
            "--environment" not in prompt and "--target shared" not in prompt,
            f"{label}: must not default to a machine-wide global install",
        )
        validation.check(
            "SHA256SUMS" in prompt and "npx" not in prompt,
            f"{label}: must verify the release digest and avoid ad-hoc CLI installers",
        )
        required_fragments = [
            "$monica-guide",
        ]
        validation.check(
            all(fragment in prompt for fragment in required_fragments),
            f"{label}: toolbox invocation guidance is incomplete",
        )
        validation.check(
            set(BOOTSTRAP_TOKEN_PATTERN.findall(prompt))
            == {"{{MONICA_IMMUTABLE_REF}}"},
            f"{label}: contains an unknown or missing substitution token",
        )
        validation.check(
            not any(
                fragment in prompt
                for fragment in (
                    "--apply",
                    "--agent",
                    "--profile application",
                    "--profile extension",
                    "--profile framework",
                    "--profile docs",
                    "-a codex",
                    "-a claude-code",
                )
            ),
            f"{label}: universal bootstrap must not select a host or concrete profile by itself",
        )
        locale_safety = (
            (
                "The installation stays inside this repository.",
                "restart the host only if discovery still fails",
                "ask what I want to do next",
                "approve its preview",
                "After that installation, these restrictions apply",
                "Do not initialize another repository",
                "bind or unbind source",
                "make any other file changes",
                "remote mutations",
            )
            if locale == "en-US"
            else (
                "只写入本仓库内部",
                "只有发现仍失败时才重启宿主",
                "询问我下一步想做什么",
                "批准其预览",
                "完成这次安装后",
                "不要初始化其他仓库",
                "绑定或解绑源码",
                "再修改其他文件",
                "远程变更",
            )
        )
        validation.check(
            all(fragment in prompt for fragment in locale_safety),
            f"{label}: localized toolbox safety or discovery guidance is missing",
        )


def validate_catalog(validation: Validation, catalog: dict[str, Any]) -> None:
    skills: dict[str, Any] = catalog.get("skills", {})
    external: dict[str, Any] = catalog.get("externalSkills", {})
    aliases: dict[str, Any] = catalog.get("aliases", {})
    source_repositories: dict[str, Any] = catalog.get("sourceRepositories", {})
    canonical_root = REPOSITORY_ROOT / "skills"
    canonical_directories = {
        child.name for child in canonical_root.iterdir() if child.is_dir()
    }
    validation.check(
        canonical_directories == set(skills),
        "skills/ directories must exactly match catalog-managed Monica skills; "
        f"only on disk={sorted(canonical_directories - set(skills))}, "
        f"only in catalog={sorted(set(skills) - canonical_directories)}",
    )
    generated_artifacts = sorted(
        path.relative_to(REPOSITORY_ROOT).as_posix()
        for path in canonical_root.rglob("*")
        if path.name == "__pycache__" or path.suffix in {".pyc", ".pyo"}
    )
    validation.check(
        not generated_artifacts,
        f"generated Python cache artifacts are not allowed in canonical skills: {generated_artifacts}",
    )

    module_owners: dict[str, str] = {}
    for skill_name, entry in skills.items():
        validation.check(entry.get("path") == f"skills/{skill_name}", f"{skill_name}: invalid canonical path")
        publication = entry.get("publication")
        if skill_name.startswith("monica-infra-"):
            validation.check(bool(publication), f"{skill_name}: infrastructure skills must declare publication ownership")
        if publication:
            for module in publication.get("modules", []):
                validation.check(module not in module_owners, f"{skill_name}: module {module!r} already belongs to {module_owners.get(module)}")
                module_owners[module] = skill_name
            for source_path in publication.get("sourcePaths", []):
                source = (REPOSITORY_ROOT / source_path).resolve()
                validation.check(
                    source.is_relative_to(REPOSITORY_ROOT.resolve()) and source.exists(),
                    f"{skill_name}: publication source path must exist inside the repository: {source_path}",
                )
        skill_path = REPOSITORY_ROOT / entry.get("path", "")
        skill_file = skill_path / "SKILL.md"
        if not skill_file.is_file():
            validation.errors.append(f"{skill_name}: missing {skill_file}")
            continue
        frontmatter_name, _ = parse_frontmatter(skill_file, validation)
        validation.check(frontmatter_name == skill_name, f"{skill_file}: name must match directory")
        parse_openai_yaml(skill_path / "agents" / "openai.yaml", skill_name, validation)
        if skill_name.startswith("monica-infra-"):
            validation.errors.extend(validate_entrypoint_budget(skill_file))
            validation.errors.extend(validate_reference_navigation(skill_path, REPOSITORY_ROOT))

        dependencies = entry.get("dependencies", {})
        local_buckets = [
            *dependencies.get("required", []),
            *dependencies.get("recommended", []),
            *(name for item in dependencies.get("conditional", []) for name in item.get("skills", [])),
        ]
        for dependency in local_buckets:
            validation.check(dependency in skills, f"{skill_name}: unknown Monica dependency {dependency!r}")
            validation.check(dependency != skill_name, f"{skill_name}: self dependency is not allowed")
        for dependency in dependencies.get("external", []):
            validation.check(dependency in external, f"{skill_name}: unknown external dependency {dependency!r}")

        required = set(dependencies.get("required", []))
        recommended = set(dependencies.get("recommended", []))
        validation.check(
            not required & recommended,
            f"{skill_name}: required and recommended dependencies overlap: {sorted(required & recommended)}",
        )

        if entry.get("role") == "router":
            routes = set(entry.get("routes", []))
            validation.check(bool(routes), f"{skill_name}: router must declare routes")
            for route in routes:
                validation.check(route in skills, f"{skill_name}: unknown router target {route!r}")
            body_references = {
                match.group(1)
                for match in SKILL_REFERENCE_PATTERN.finditer(skill_file.read_text(encoding="utf-8"))
                if match.group(1) in skills and match.group(1) != skill_name
            }
            validation.check(
                body_references == routes,
                (
                    f"{skill_name}: SKILL.md router targets must exactly match catalog routes; "
                    f"missing from SKILL.md: {sorted(routes - body_references)}, "
                    f"absent from catalog: {sorted(body_references - routes)}"
                ),
            )

        skill_text = skill_file.read_text(encoding="utf-8")
        for alias in aliases:
            validation.check(
                f"${alias}" not in skill_text and f"`{alias}`" not in skill_text,
                f"{skill_file}: retired alias {alias!r} may appear only in diagnostics",
            )

    for alias, alias_entry in aliases.items():
        validation.check(alias not in skills, f"alias {alias!r} must not have a compatibility folder")
        validation.check(alias_entry.get("canonical") in skills, f"alias {alias!r} has unknown canonical target")

    catalog_without_aliases = dict(catalog)
    catalog_without_aliases.pop("aliases", None)
    serialized_catalog = json.dumps(catalog_without_aliases, ensure_ascii=False, sort_keys=True)
    for alias in aliases:
        alias_pattern = re.compile(rf"(?<![a-z0-9-]){re.escape(alias)}(?![a-z0-9-])")
        validation.check(
            not alias_pattern.search(serialized_catalog),
            f"retired alias {alias!r} may appear only in the catalog aliases object",
        )

    retired_occurrences = find_retired_alias_occurrences(
        REPOSITORY_ROOT,
        aliases,
        allowed_files=(CATALOG_PATH,),
        allowed_roots=(),
    )
    validation.check(
        not retired_occurrences,
        "retired skill names may appear only in catalog alias definitions or Monica Guide "
        f"diagnostic fixtures: {retired_occurrences}",
    )

    for cycle in find_required_cycles(skills):
        validation.errors.append("required dependency cycle: " + " -> ".join(cycle))

    expected_source_aliases = {
        "Tairitsua/Monica": ["monica"],
    }
    source_aliases: dict[str, str] = {}
    for repository, source_entry in source_repositories.items():
        validation.check(
            source_entry.get("aliases") == expected_source_aliases.get(repository),
            f"source repository {repository}: canonical alias contract is invalid",
        )
        for alias in source_entry.get("aliases", []):
            validation.check(
                alias not in source_aliases,
                f"source repository alias {alias!r} is assigned more than once",
            )
            source_aliases[alias] = repository

    for profile_name, profile in catalog.get("profiles", {}).items():
        profile_skills = profile.get("skills", {})
        roots = [
            *profile_skills.get("required", []),
            *profile_skills.get("recommended", []),
            *(name for item in profile_skills.get("conditional", []) for name in item.get("skills", [])),
        ]
        for skill_name in roots:
            validation.check(skill_name in skills, f"profile {profile_name}: unknown skill {skill_name!r}")
        required = set(profile_skills.get("required", []))
        recommended = set(profile_skills.get("recommended", []))
        validation.check(
            not required & recommended,
            f"profile {profile_name}: required and recommended skills overlap",
        )
        closure = profile_selection_closure(skills, profile)
        validation.check(
            closure <= required,
            f"profile {profile_name}: required list omits transitive dependencies: {sorted(closure - required)}",
        )
        for selection_kind, selection_roots in (
            ("recommended", profile_skills.get("recommended", [])),
            *(
                (f"conditional:{item.get('capability')}", item.get("skills", []))
                for item in profile_skills.get("conditional", [])
            ),
        ):
            selection_closure = required_closure(skills, [*required, *selection_roots])
            validation.check(
                selection_closure <= set(skills),
                f"profile {profile_name} {selection_kind}: closure contains an unknown skill",
            )
        source_requirements = profile.get("sourceRequirements", [])
        required_repositories: set[str] = set()
        for source_requirement in source_requirements:
            repository = source_requirement.get("repository")
            validation.check(
                repository in source_repositories,
                f"profile {profile_name}: unknown source repository {repository!r}",
            )
            validation.check(
                repository not in required_repositories,
                f"profile {profile_name}: duplicate source requirement for {repository!r}",
            )
            required_repositories.add(repository)

    for template_name, template in catalog.get("managedInstructions", {}).get("templates", {}).items():
        validation.check(template_name in catalog.get("profiles", {}), f"unknown instruction template {template_name}")
        for skill_name in template.get("skills", []):
            validation.check(skill_name in skills, f"instruction template {template_name}: unknown skill {skill_name}")
        rule_ids = [rule.get("id", "") for rule in template.get("rules", [])]
        validation.check(
            all(rule_ids) and len(rule_ids) == len(set(rule_ids)),
            f"instruction template {template_name}: rule ids must be non-empty and unique",
        )
        validation.check(
            all(rule.get("text") for rule in template.get("rules", [])),
            f"instruction template {template_name}: rule texts must be non-empty",
        )

    bootstrap_path = catalog.get("prompts", {}).get("bootstrapAsset")
    bootstrap_schema_path = catalog.get("prompts", {}).get("bootstrapSchema")
    if bootstrap_path and bootstrap_schema_path:
        bootstrap_file = REPOSITORY_ROOT / bootstrap_path
        bootstrap_schema_file = REPOSITORY_ROOT / bootstrap_schema_path
        validation.check(bootstrap_file.is_file(), f"missing bootstrap prompt asset {bootstrap_path}")
        validation.check(
            bootstrap_schema_file.is_file(),
            f"missing bootstrap prompt schema {bootstrap_schema_path}",
        )
        if bootstrap_file.is_file() and bootstrap_schema_file.is_file():
            try:
                bootstrap = load_json(bootstrap_file)
                validate_bootstrap_prompts(
                    validation,
                    catalog,
                    bootstrap,
                    bootstrap_schema_file,
                )
            except (OSError, ValueError, json.JSONDecodeError, KeyError) as exc:
                validation.errors.append(f"invalid bootstrap prompt asset: {exc}")


def validate_index(validation: Validation, index: dict[str, Any]) -> None:
    channels = index.get("channels", {})
    versions = index.get("versions", {})
    releases = index.get("releases", {})
    for channel, release_id in channels.items():
        if release_id is not None:
            validation.check(release_id in releases, f"channel {channel}: unknown release {release_id}")
    for version, release_id in versions.items():
        validation.check(release_id in releases, f"version {version}: unknown release {release_id}")
        if release_id in releases:
            validation.check(
                releases[release_id].get("monicaVersion") == version,
                f"version {version}: release monicaVersion does not match",
            )
    for release_id, release in releases.items():
        validation.check(release.get("tag") == release_id, f"release {release_id}: tag must match key")
        validation.check(
            release_id == f"v{release.get('monicaVersion', '')}",
            f"release {release_id}: tag and Monica version must match",
        )
        for field in ("catalogDigest", "skillTreeDigest", "manifestDigest"):
            validation.check(
                bool(SHA256_PATTERN.fullmatch(release.get(field, ""))),
                f"release {release_id}: invalid {field}",
            )
        asset_base_url = release.get("assetBaseUrl", "")
        validation.check(
            asset_base_url.endswith(f"/{release_id}"),
            f"release {release_id}: assetBaseUrl must end in the immutable tag",
        )
        validation.check(
            release.get("catalogUrl") == f"{asset_base_url}/agent-skill-catalog.json",
            f"release {release_id}: catalogUrl must be deterministic",
        )
        validation.check(
            release.get("manifestUrl") == f"{asset_base_url}/agent-skill-manifest.json",
            f"release {release_id}: manifestUrl must be deterministic",
        )
    try:
        validate_revision_history(index, label="index")
    except ReleaseContractError as exc:
        validation.errors.append(str(exc))


def tree_digest(catalog: dict[str, Any]) -> str:
    files: list[Path] = []
    for skill_name, entry in sorted(catalog["skills"].items()):
        if entry.get("ownership") != "monica" or entry.get("managed") is not True:
            continue
        root = REPOSITORY_ROOT / entry["path"]
        files.extend(path for path in root.rglob("*") if path.is_file())
    return digest_files(files, relative_to=REPOSITORY_ROOT)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", action="store_true", help="Emit a machine-readable result.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    validation = Validation()
    try:
        catalog = load_json(CATALOG_PATH)
        index = load_json(INDEX_PATH)
        validate_schema(validation, catalog, CATALOG_SCHEMA_PATH, "catalog")
        validate_schema(validation, index, INDEX_SCHEMA_PATH, "index")
        validate_catalog(validation, catalog)
        validate_index(validation, index)
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        validation.errors.append(str(exc))
        catalog = {}

    payload = {
        "ok": not validation.errors,
        "errors": validation.errors,
        "catalogDigest": (
            "sha256:" + hashlib.sha256(CATALOG_PATH.read_bytes()).hexdigest()
            if CATALOG_PATH.is_file()
            else None
        ),
        "skillTreeDigest": tree_digest(catalog) if catalog.get("skills") else None,
    }
    if args.json:
        print(json.dumps(payload, ensure_ascii=False, indent=2))
    elif validation.errors:
        print(f"Agent skill validation failed with {len(validation.errors)} error(s):")
        for error in validation.errors:
            print(f"  - {error}")
    else:
        print(f"[ok] catalog {payload['catalogDigest']}")
        print(f"[ok] skill tree {payload['skillTreeDigest']}")
    return 0 if payload["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
