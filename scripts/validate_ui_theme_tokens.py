#!/usr/bin/env python3
from __future__ import annotations

import re
import sys
from collections.abc import Iterator
from os import walk
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]

UI_EXTENSIONS = {".css", ".razor", ".js"}
EXCLUDED_DIR_NAMES = {
    ".git",
    ".gitnexus",
    ".idea",
    ".pending",
    ".playwright",
    ".playwright-cli",
    ".tmp",
    ".ui-design",
    ".vs",
    "bin",
    "obj",
}
THEME_DEFINITION_DIRS = {
    REPO_ROOT / "Monica.UI" / "wwwroot" / "css" / "themes",
    REPO_ROOT / "Monica.UI" / "wwwroot" / "css" / "markdown",
    # Guide is a standalone wizard with its own two-mode theme and no Monica.UI dependency.
    REPO_ROOT / "Monica.Guide.App" / "wwwroot" / "css" / "themes" / "guide",
}
THEME_DEFINITION_FILES = {
    REPO_ROOT / "Monica.UI" / "wwwroot" / "css" / "mo-theme-main.css",
}
ALLOWED_SEMANTIC_TOKENS = {
    "--mo-color-scrollbar-thumb",
    "--mo-color-scrollbar-thumb-hover",
    "--mo-color-scrollbar-thumb-active",
    "--mo-color-brand-background",
    "--mo-color-brand-foreground",
    "--mo-color-brand-accent",
    "--mo-color-navigation-rail-background",
    "--mo-color-navigation-rail-text",
    "--mo-color-navigation-context-background",
    "--mo-color-shell-frame",
    "--mo-color-shell-frame-text",
    "--mo-color-shell-frame-muted",
    "--mo-color-shell-frame-hover",
    "--mo-color-role-third",
    "--mo-color-page-hero-background",
    "--mo-color-page-hero-text",
    "--mo-color-page-hero-action",
    "--mo-color-highlight-background",
    "--mo-color-highlight-text",
    "--mo-color-code-key",
    "--mo-color-code-string",
    "--mo-color-code-number",
    "--mo-color-code-boolean",
    "--mo-color-code-null",
    "--mo-color-state-info-soft-background",
    "--mo-color-state-info-soft-text",
    "--mo-color-state-info-soft-border",
    "--mo-color-state-success-soft-background",
    "--mo-color-state-success-soft-text",
    "--mo-color-state-success-soft-border",
    "--mo-color-state-warning-soft-background",
    "--mo-color-state-warning-soft-text",
    "--mo-color-state-warning-soft-border",
    "--mo-color-state-error-soft-background",
    "--mo-color-state-error-soft-text",
    "--mo-color-state-error-soft-border",
}

PRIVATE_THEME_PATTERN = re.compile(r"--mo-(?:m3|ink|hermes|fresh|vibe|zen|comic)-[\w-]+|--mo-system-info-hero-[\w-]+")
SEMANTIC_TOKEN_PATTERN = re.compile(r"--mo-color-[\w-]+")
HEX_COLOR_PATTERN = re.compile(r"#(?:[0-9a-fA-F]{3,8})\b", re.IGNORECASE)
FUNCTION_COLOR_PATTERN = re.compile(
    r"(?<!-)rgba?\((?!var\()|(?<!-)hsla?\(",
    re.IGNORECASE,
)
NAMED_COLOR_PATTERN = re.compile(
    r"(?:color|background(?:-color)?|border(?:-color)?|fill|stroke|outline(?:-color)?).*?(?<![\w-])(?:white|black)(?![\w-])",
    re.IGNORECASE,
)
COMMENT_ONLY_PATTERN = re.compile(r"^\s*(?://|/\*|\*|\*/|<!--)")
SHADOW_LINE_PATTERN = re.compile(r"\b(?:box-shadow|text-shadow|drop-shadow|filter)\b", re.IGNORECASE)


def is_excluded(path: Path) -> bool:
    return any(part in EXCLUDED_DIR_NAMES for part in path.parts)


def is_theme_definition_file(path: Path) -> bool:
    if path in THEME_DEFINITION_FILES:
        return True
    return any(parent in THEME_DEFINITION_DIRS for parent in path.parents)


def should_scan(path: Path) -> bool:
    if path.suffix not in UI_EXTENSIONS:
        return False
    if is_excluded(path):
        return False
    if path.name.endswith(".min.css") or path.name.endswith(".min.js"):
        return False
    if "wwwroot" in path.parts and "lib" in path.parts:
        return False
    return "Monica." in path.name or any(part.startswith("Monica.") for part in path.parts)


def iter_scan_files(root: Path) -> Iterator[Path]:
    for directory_name, dirnames, filenames in walk(root):
        dirnames[:] = [name for name in dirnames if name not in EXCLUDED_DIR_NAMES]
        directory = Path(directory_name)

        for filename in filenames:
            path = directory / filename
            if should_scan(path):
                yield path


def line_allowed_for_raw_colors(path: Path, line: str) -> bool:
    if is_theme_definition_file(path):
        return True
    if COMMENT_ONLY_PATTERN.match(line):
        return True
    if "--mo-" in line and ":" in line:
        return True
    if "var(--mud-palette-" in line:
        return True
    if SHADOW_LINE_PATTERN.search(line):
        return True
    if "rgba(var(--mud-palette-" in line:
        return True
    if "var(--mud-palette-" in line and "rgb" in line:
        return True
    return False


def scan_file(path: Path) -> list[str]:
    issues: list[str] = []
    text = path.read_text(encoding="utf-8")
    lines = text.splitlines()

    for line_number, line in enumerate(lines, start=1):
        has_raw_color = (
            HEX_COLOR_PATTERN.search(line)
            or FUNCTION_COLOR_PATTERN.search(line)
            or NAMED_COLOR_PATTERN.search(line)
        )

        if has_raw_color:
            if line_allowed_for_raw_colors(path, line):
                continue
            issues.append(f"{path.relative_to(REPO_ROOT)}:{line_number}: raw color literal is not allowed")

        if not is_theme_definition_file(path):
            if PRIVATE_THEME_PATTERN.search(line):
                issues.append(
                    f"{path.relative_to(REPO_ROOT)}:{line_number}: private theme namespaces are not allowed outside theme files"
                )

        for token in SEMANTIC_TOKEN_PATTERN.findall(line):
            if token not in ALLOWED_SEMANTIC_TOKENS:
                issues.append(f"{path.relative_to(REPO_ROOT)}:{line_number}: unapproved semantic token {token}")

    return issues


def main() -> int:
    issues: list[str] = []

    for path in iter_scan_files(REPO_ROOT):
        issues.extend(scan_file(path))

    if issues:
        print("\n".join(sorted(issues)))
        print(f"\nFound {len(issues)} UI theme token issue(s).", file=sys.stderr)
        return 1

    print("UI theme token validation passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
