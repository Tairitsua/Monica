"""Bound infrastructure entrypoints and check navigation without fixing topic layouts."""

from __future__ import annotations

import re
from pathlib import Path
from urllib.parse import unquote, urlsplit

try:
    from markdown_it import MarkdownIt
except ImportError:  # The validator reports the required dependency explicitly.
    MarkdownIt = None


FRONTMATTER = re.compile(r"\A---\r?\n.*?\r?\n---(?:\r?\n|$)", re.DOTALL)
MARKDOWN_SUFFIXES = {".md", ".markdown"}
MAX_INFRA_ENTRYPOINT_LINES = 26
MAX_INFRA_ENTRYPOINT_WORDS = 300


def validate_entrypoint_budget(entrypoint: Path) -> list[str]:
    """Limit activation context, excluding frontmatter and outer blank lines.

    Both dimensions matter: a line ceiling alone allows unlimited text to be
    packed into one paragraph. References are intentionally outside this budget.
    """
    body = FRONTMATTER.sub("", entrypoint.read_text(encoding="utf-8-sig"), count=1).strip()
    errors: list[str] = []
    for unit, actual, limit in (
        ("lines", len(body.splitlines()), MAX_INFRA_ENTRYPOINT_LINES),
        ("words", len(body.split()), MAX_INFRA_ENTRYPOINT_WORDS),
    ):
        if actual > limit:
            errors.append(
                f"{entrypoint}: infrastructure entrypoint has {actual} body {unit} "
                f"(limit {limit}); keep selection guidance here and move execution detail "
                "to its owning reference without compressing the prose"
            )
    return errors


def markdown_documents(root: Path) -> list[Path]:
    """Use the same Markdown file inventory for navigation and publication."""
    return sorted(
        path for path in root.rglob("*")
        if path.is_file() and path.suffix.lower() in MARKDOWN_SUFFIXES
    )


def validate_reference_navigation(skill_root: Path, repository_root: Path) -> list[str]:
    """Require existing relative Markdown targets and locally reachable references.

    Cross-skill and repository-source links are checked for existence, but only
    links within this skill establish reachability from its entrypoint. Markdown
    examples in code blocks and inline code are not navigation. Heading fragments
    and external URLs are outside this file-level check.
    """
    if MarkdownIt is None:
        return ["The markdown-it-py package is required; install scripts/requirements-agent-skills.txt."]

    repository_root = repository_root.resolve()
    skill_root = skill_root.resolve()
    entrypoint = skill_root / "SKILL.md"
    references = set(markdown_documents(skill_root / "references"))
    documents = {entrypoint, *references}
    edges: dict[Path, set[Path]] = {path: set() for path in documents}
    errors: list[str] = []
    parser = MarkdownIt("commonmark").enable("table")

    for source in sorted(documents):
        label = source.relative_to(repository_root).as_posix()
        if not source.resolve().is_relative_to(skill_root) or not source.is_file():
            errors.append(f"{label}: missing or nonlocal skill document")
            continue
        body = FRONTMATTER.sub("", source.read_text(encoding="utf-8-sig"), count=1)
        for block in parser.parse(body):
            for token in block.children or []:
                if token.type != "link_open":
                    continue
                href = token.attrGet("href") or ""
                url = urlsplit(href)
                relative = Path(unquote(url.path))
                if relative.suffix.lower() not in MARKDOWN_SUFFIXES:
                    continue
                if re.match(r"^[a-zA-Z]:[/\\]", unquote(href)) or url.scheme.lower() == "file":
                    errors.append(f"{label}: Markdown link must stay relative to the repository: {href}")
                    continue
                if url.scheme or url.netloc or not url.path:
                    continue
                target = (source.parent / relative).resolve()
                if relative.is_absolute() or not target.is_relative_to(repository_root):
                    errors.append(f"{label}: Markdown link must stay relative to the repository: {href}")
                elif not target.is_file():
                    errors.append(f"{label}: missing Markdown target: {href}")
                elif target in documents:
                    edges[source].add(target)

    reached: set[Path] = set()
    pending = [entrypoint]
    while pending:
        source = pending.pop()
        if source not in reached:
            reached.add(source)
            pending.extend(edges[source] - reached)
    for reference in sorted(references - reached):
        errors.append(
            f"{reference.relative_to(repository_root).as_posix()}: "
            "reference is unreachable from SKILL.md; add a task-specific Markdown link"
        )
    return errors
