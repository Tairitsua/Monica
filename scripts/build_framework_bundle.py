"""Build a verified Framework skill bundle, independently of the Monica Guide client.

The Framework repository owns canonical skills and their immutable manifest.
The standalone Guide downloads and installs this data-only product bundle.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile
from datetime import datetime, timezone
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
CATALOG_PATH = REPOSITORY_ROOT / ".monica" / "agent-skill-catalog.json"

PRODUCT_ID = "Tairitsua.Monica"
PRODUCT_NAME = "Monica"
SUPPORTED_HOSTS = ["claude", "codex"]

# Platform slices match the public product catalog.
PLATFORMS = ("win-x64", "linux-x64", "osx-arm64")

TAG_PATTERN = re.compile(r"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z.-]+)?$")


class BundleError(Exception):
    """Raised when the Framework bundle cannot be assembled or verified."""


def fail(message: str) -> None:
    raise BundleError(message)


def sha256_bytes(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


def json_bytes(payload: object) -> bytes:
    return (json.dumps(payload, indent=2, sort_keys=False, ensure_ascii=False) + "\n").encode("utf-8")


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def engine_tree_digest(entries: list[tuple[str, bytes]]) -> str:
    """Engine catalog digest: sorted ``relativePath \\0 content \\0`` records."""

    digest = hashlib.sha256()
    for relative_path, content in sorted(entries, key=lambda entry: entry[0]):
        digest.update(relative_path.encode("utf-8"))
        digest.update(b"\0")
        digest.update(content)
        digest.update(b"\0")
    return digest.hexdigest()


def require_clean_worktree(allow_dirty: bool) -> str:
    completed = subprocess.run(
        ["git", "status", "--porcelain"],
        cwd=REPOSITORY_ROOT,
        check=False,
        capture_output=True,
        text=True,
    )
    if completed.returncode != 0:
        fail("Could not inspect the repository worktree.")
    dirty = [
        line
        for line in completed.stdout.splitlines()
        if "artifacts/" not in line
    ]
    if dirty and not allow_dirty:
        fail("The canonical skill tree and catalog must be committed before packaging.")
    completed = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=REPOSITORY_ROOT,
        check=False,
        capture_output=True,
        text=True,
    )
    commit = completed.stdout.strip()
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        fail("Could not resolve the current commit.")
    return commit


def collect_skill_files(skill_root: Path) -> list[tuple[str, bytes]]:
    """Every file below one canonical skill tree, POSIX-relative and ordinal-sorted."""

    if not skill_root.is_dir():
        fail(f"Catalog skill directory is missing: {skill_root}")
    entries = [
        (path.relative_to(skill_root).as_posix(), path.read_bytes())
        for path in skill_root.rglob("*")
        if path.is_file() and not path.is_symlink()
    ]
    # Ordinal string order, never the platform collation: the engine requires it and
    # digests are order-sensitive.
    entries.sort(key=lambda entry: entry[0])
    if not entries:
        fail(f"Catalog skill directory has no files: {skill_root}")
    return entries


def profile_membership(catalog: dict) -> dict[str, list[str]]:
    """Project default profile membership; capability roots stay in profileSelections."""

    membership: dict[str, list[str]] = {name: [] for name in catalog["skills"]}
    for profile_name, profile in sorted(catalog.get("profiles", {}).items()):
        skills = profile.get("skills", {})
        for bucket in ("required", "recommended"):
            for skill in skills.get(bucket, []):
                if skill not in membership:
                    fail(f"Profile {profile_name} references unknown skill {skill}")
                membership[skill].append(profile_name)
    return {skill: sorted(set(profiles)) for skill, profiles in membership.items()}


def project_catalog(catalog: dict, staged_skills: Path) -> tuple[dict, bytes]:
    """Project the canonical catalog into the engine installer catalog over staged bytes."""

    skills = []
    aggregate = hashlib.sha256()
    memberships = profile_membership(catalog)
    for skill_name, entry in sorted(catalog["skills"].items()):
        staged_root = staged_skills / skill_name
        files = collect_skill_files(staged_root)
        tree_digest = engine_tree_digest(files)
        dependencies = sorted(
            set(entry.get("dependencies", {}).get("required", []))
        )
        skill_entry = {
            "name": skill_name,
            "path": f"skills/{skill_name}",
            "role": entry["role"],
            "files": [relative for relative, _ in files],
            "dependencies": dependencies,
            "treeDigest": tree_digest,
        }
        profiles = memberships.get(skill_name, [])
        if profiles:
            skill_entry["profiles"] = profiles
        skills.append(skill_entry)
        aggregate.update(skill_name.encode("utf-8"))
        aggregate.update(b"\0")
        aggregate.update(tree_digest.encode("utf-8"))
        aggregate.update(b"\0")

    projected = {
        "schemaVersion": 1,
        "skillCount": len(skills),
        "treeDigest": aggregate.hexdigest(),
        "skills": skills,
    }
    projected.update(workspace_projection(catalog))
    payload = json_bytes(projected)
    (staged_skills / "catalog.json").write_bytes(payload)
    return projected, payload


def workspace_projection(catalog: dict) -> dict:
    """Project the workspace-facing sections the guide engine consumes.

    managedInstructions drives workspace init instruction blocks, sourceRepositories declares
    the first-party repositories source bindings accept, and aliases names retired skill
    directories the doctor can diagnose. Every value is copied unchanged from the canonical
    catalog; nothing is authored here.
    """

    projection: dict = {}
    profiles = catalog.get("profiles") or {}
    if profiles:
        projection["profileSelections"] = {
            name: profile["skills"]
            for name, profile in sorted(profiles.items())
        }
    instructions = catalog.get("managedInstructions") or {}
    if instructions:
        projection["managedInstructions"] = {
            "version": instructions["version"],
            "markers": instructions["markers"],
            "templates": instructions.get("templates", {}),
        }
    repositories = catalog.get("sourceRepositories") or {}
    if repositories:
        projection["sourceRepositories"] = {
            name: {"repository": name, "aliases": entry.get("aliases", [])}
            for name, entry in sorted(repositories.items())
        }
    aliases = catalog.get("aliases") or {}
    if aliases:
        projection["aliases"] = dict(sorted(aliases.items()))
    return projection


def release_files(bundle: Path) -> list[dict]:
    records = []
    for path in sorted(bundle.rglob("*")):
        if path.is_symlink():
            fail(f"Release payload contains a redirected entry: {path}")
        if not path.is_file():
            continue
        content = path.read_bytes()
        records.append(
            {
                "relativePath": path.relative_to(bundle).as_posix(),
                "length": len(content),
                "sha256": sha256_bytes(content),
            }
        )
    return records


def verify_bundle(bundle: Path, manifest: dict, projected: dict, entry_point: str | None) -> None:
    """Recompute every recorded digest from the staged bytes before zipping."""

    catalog_path = bundle / "skills" / "catalog.json"
    if sha256_bytes(catalog_path.read_bytes()) != manifest["skillCatalogDigest"]:
        fail("Staged catalog bytes do not match the manifest skillCatalogDigest")

    for record in manifest["files"]:
        path = bundle / record["relativePath"]
        content = path.read_bytes()
        if len(content) != record["length"] or sha256_bytes(content) != record["sha256"]:
            fail(f"Staged file digest mismatch: {record['relativePath']}")

    listed = {record["relativePath"] for record in manifest["files"]}
    # The engine excludes the manifest itself when comparing the payload file set.
    actual = {
        path.relative_to(bundle).as_posix()
        for path in bundle.rglob("*")
        if path.is_file() and path.name != "release-manifest.json"
    }
    if listed != actual:
        fail(
            "Manifest files do not exactly match the staged bundle "
            f"(missing={sorted(listed - actual)}, extra={sorted(actual - listed)})"
        )

    def projection(skill: dict, path_key: str) -> dict:
        return {
            "name": skill["name"],
            "relativePath": skill[path_key],
            "treeDigest": skill["treeDigest"],
            "files": skill["files"],
            "dependencies": skill["dependencies"],
        }

    if {skill["name"]: projection(skill, "relativePath") for skill in manifest["skills"]} != {
        skill["name"]: projection(skill, "path") for skill in projected["skills"]
    }:
        fail("Manifest skill projection does not exactly match the packaged catalog")

    if manifest["programFiles"] or manifest["programEntryPoint"] is not None:
        fail("A Framework skill bundle must not embed an executable")


def deterministic_zip(bundle: Path, archive: Path, epoch: int, executable_entries: set[str]) -> None:
    """Write a byte-deterministic zip: fixed timestamps, ordinal order, explicit modes."""

    timestamp = datetime.fromtimestamp(epoch, tz=timezone.utc)
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as zipped:
        for path in sorted(bundle.rglob("*"), key=lambda item: item.relative_to(bundle).as_posix()):
            if not path.is_file():
                continue
            relative = path.relative_to(bundle).as_posix()
            info = zipfile.ZipInfo(
                relative,
                date_time=(
                    timestamp.year, timestamp.month, timestamp.day,
                    timestamp.hour, timestamp.minute, timestamp.second,
                ),
            )
            info.compress_type = zipfile.ZIP_DEFLATED
            # Entry executables carry the executable bit so native Unix archives stay
            # runnable after extraction; every other file stays a plain 0644.
            info.external_attr = ((0o755 if relative in executable_entries else 0o644) << 16) | 0o100000
            zipped.writestr(info, path.read_bytes())


def command_build(args: argparse.Namespace) -> None:
    match = TAG_PATTERN.fullmatch(args.tag)
    if not match:
        fail(f"Release tag must be SemVer with a v prefix: {args.tag!r}")
    if args.rid not in PLATFORMS:
        fail(f"--rid must be one of: {', '.join(PLATFORMS)}")
    entry_point = None
    version = args.tag[1:]
    output = Path(args.output_dir).resolve()
    if output == REPOSITORY_ROOT or REPOSITORY_ROOT not in output.parents:
        fail("--output must be a dedicated directory inside the repository.")
    output.mkdir(parents=True, exist_ok=True)
    commit = require_clean_worktree(args.allow_dirty)

    catalog = load_json(CATALOG_PATH)
    staging = output / f".monica-framework-{args.tag}-{args.rid}-staging"
    if staging.exists():
        shutil.rmtree(staging)
    bundle = staging / "bundle"
    bundle.mkdir(parents=True)
    for skill_name in sorted(catalog["skills"]):
        shutil.copytree(
            REPOSITORY_ROOT / "skills" / skill_name,
            bundle / "skills" / skill_name,
            # Local Python cache must never reach the bundle: validate_agent_skills
            # rejects it at the gate, and silent inclusion would skew tree digests.
            ignore=shutil.ignore_patterns("__pycache__", "*.pyc"),
        )

    projected, catalog_payload = project_catalog(catalog, bundle / "skills")
    files = release_files(bundle)
    manifest = {
        "schemaVersion": 1,
        "productId": PRODUCT_ID,
        "productName": PRODUCT_NAME,
        "productVersion": version,
        "assemblyVersion": None,
        "tag": args.tag,
        "sourceCommit": commit,
        "dotnetSdk": args.dotnet_sdk,
        "runtimeIdentifier": args.rid,
        "distributionKind": f"portable-{args.rid}",
        "selfContained": False,
        "requiredRuntime": None,
        "trimmed": False,
        "publishSingleFile": False,
        "programEntryPoint": entry_point,
        "programFiles": [],
        "supportedHosts": list(SUPPORTED_HOSTS),
        "supportedRoutes": [],
        "defaultRoutes": {},
        "defaultJourneys": {},
        "mcpToolSchemaDigest": "",
        "skillCatalogDigest": sha256_bytes(catalog_payload),
        "skills": [
            {
                "name": skill["name"],
                "relativePath": skill["path"],
                "treeDigest": skill["treeDigest"],
                "role": skill["role"],
                "files": skill["files"],
                "dependencies": skill["dependencies"],
                **({"profiles": skill["profiles"]} if "profiles" in skill else {}),
            }
            for skill in projected["skills"]
        ],
        "files": files,
    }
    (bundle / "release-manifest.json").write_bytes(json_bytes(manifest))
    verify_bundle(bundle, manifest, projected, entry_point)

    archive = output / f"monica-framework-{args.tag}-{args.rid}.zip"
    epoch = int(os.environ.get("SOURCE_DATE_EPOCH") or 0)
    if not epoch:
        completed = subprocess.run(
            ["git", "show", "-s", "--format=%ct", "HEAD"],
            cwd=REPOSITORY_ROOT,
            check=False,
            capture_output=True,
            text=True,
        )
        epoch = int(completed.stdout.strip())
    deterministic_zip(bundle, archive, epoch, executable_entries=set())
    shutil.rmtree(staging)
    print(archive)
    print(f"{sha256_bytes(archive.read_bytes())}  {archive.name}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    build = subparsers.add_parser("build", help="Assemble and verify one Framework skill bundle")
    build.add_argument("--tag", required=True, help="Release tag, for example v1.0.0-rc.12")
    build.add_argument(
        "--rid",
        required=True,
        choices=list(PLATFORMS),
        help="Target platform of the publish directory",
    )
    build.add_argument(
        "--output-dir",
        required=True,
        help="Dedicated output directory inside the repository",
    )
    build.add_argument(
        "--dotnet-sdk",
        required=True,
        help="Pinned .NET SDK version used for the build, for example 10.0.101",
    )
    build.add_argument(
        "--allow-dirty",
        action="store_true",
        help="Development only: package from a worktree with uncommitted changes.",
    )
    build.set_defaults(handler=command_build)

    args = parser.parse_args(argv)
    try:
        args.handler(args)
    except BundleError as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
