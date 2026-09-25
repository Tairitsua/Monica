#!/usr/bin/env python3
"""Export canonical skills as a deterministic, read-only publication artifact.

The catalog owns capability coverage. Consumers render these bytes; they do not
rewrite or become an additional authoring source. Release exports require an
exact clean Git tag. Local exports identify themselves as source previews.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
from pathlib import Path
from typing import Any
from xml.etree import ElementTree

try:
    from agent_skill_references import markdown_documents
except ModuleNotFoundError:  # Supports import-by-path test runners.
    from scripts.agent_skill_references import markdown_documents

ROOT = Path(__file__).resolve().parents[1]


def digest(content: bytes) -> str:
    return "sha256:" + hashlib.sha256(content).hexdigest()


def json_bytes(value: object) -> bytes:
    return (json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + "\n").encode("utf-8")


def git(root: Path, *args: str) -> str:
    return subprocess.check_output(["git", "-C", str(root), *args], text=True, encoding="utf-8").strip()


def metadata(markdown: str) -> tuple[dict[str, str], str]:
    """Read the portable scalar frontmatter used by canonical skill entrypoints."""
    match = re.match(r"\A---\r?\n(.*?)\r?\n---(?:\r?\n|$)", markdown, re.S)
    if not match:
        return {}, markdown
    values = {}
    for line in match[1].splitlines():
        key, separator, value = line.partition(":")
        if separator:
            values[key.strip()] = value.strip().strip("\"'")
    return values, markdown[match.end():]


def collect_skills(root: Path, catalog: dict[str, Any]) -> list[dict[str, Any]]:
    result = []
    modules: set[str] = set()
    for name, entry in sorted(catalog["skills"].items()):
        relative = Path(entry["path"])
        directory = (root / relative).resolve()
        if not directory.is_relative_to(root.resolve()) or relative.as_posix() != f"skills/{name}":
            raise ValueError(f"Invalid canonical skill path: {relative}")
        if not (directory / "SKILL.md").is_file():
            raise ValueError(f"Missing entrypoint for {name}")
        files = []
        for path in markdown_documents(directory):
            if path.is_symlink() or not path.resolve().is_relative_to(directory):
                raise ValueError(f"Skill publication cannot follow a symlink: {path}")
            content = path.read_bytes()
            text = content.decode("utf-8")
            frontmatter, body = metadata(text.lstrip("\ufeff"))
            heading = re.search(r"^#\s+(.+)$", body, re.M)
            title = frontmatter.get("title") or (heading[1] if heading else path.stem.replace("-", " "))
            files.append({
                "path": path.relative_to(directory).as_posix(),
                "title": title,
                "content": text,
                "digest": digest(content),
            })
        skill_metadata, _ = metadata((directory / "SKILL.md").read_text(encoding="utf-8-sig"))
        publication = entry.get("publication", {})
        coverage = publication.get("modules", [])
        duplicates = modules.intersection(coverage)
        if duplicates:
            raise ValueError(f"Multiple skill owners for modules: {sorted(duplicates)}")
        modules.update(coverage)
        result.append({
            "name": name,
            "title": publication.get("title") or next(file["title"] for file in files if file["path"] == "SKILL.md"),
            "description": skill_metadata.get("description", ""),
            "role": entry["role"],
            "modules": coverage,
            "files": files,
        })
    return result


def export(root: Path, release_ref: str | None = None) -> dict[str, Any]:
    root = root.resolve()
    catalog_bytes = (root / ".monica/agent-skill-catalog.json").read_bytes()
    catalog = json.loads(catalog_bytes)
    version = ElementTree.parse(root / "Directory.Build.props").findtext(".//Version")
    commit = git(root, "rev-parse", "HEAD")
    dirty = bool(git(root, "status", "--porcelain", "--untracked-files=all"))
    if release_ref:
        if release_ref != f"v{version}":
            raise ValueError("Release ref must match Directory.Build.props exactly.")
        if git(root, "rev-parse", f"refs/tags/{release_ref}^{{commit}}") != commit or dirty:
            raise ValueError("Release knowledge requires the clean, exact tagged framework checkout.")
    skills = collect_skills(root, catalog)
    return {
        "schemaVersion": 1,
        "frameworkVersion": version,
        "source": {
            "repository": "Tairitsua/Monica",
            "ref": release_ref or commit,
            "commit": commit,
            "mode": "release" if release_ref else "source",
            "dirty": dirty,
            "updatedAt": git(root, "show", "-s", "--format=%cI", "HEAD"),
        },
        "catalogDigest": digest(catalog_bytes),
        "contentDigest": digest(json_bytes(skills)),
        "skills": skills,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--release-ref", help="Exact v<version> tag; fails on a dirty or mismatched checkout.")
    args = parser.parse_args()
    bundle = export(args.root, args.release_ref)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(json_bytes(bundle))
    print(f"Exported {len(bundle['skills'])} canonical skills ({bundle['source']['mode']}) to {args.output}.")


if __name__ == "__main__":
    main()
