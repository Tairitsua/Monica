#!/usr/bin/env python3
"""Validate a multi-package repository against Monica Ecosystem Standard v1."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import struct
import sys
import xml.etree.ElementTree as ET
from dataclasses import asdict, dataclass
from pathlib import Path


ID_SEGMENT = r"[A-Za-z][A-Za-z0-9]*"
PACKAGE_ID_PATTERN = re.compile(
    rf"^(?P<publisher>{ID_SEGMENT})\.Monica\.{ID_SEGMENT}(?:\.{ID_SEGMENT})*$"
)
MODULE_KEY_PATTERN = PACKAGE_ID_PATTERN
MODULE_NAME_PATTERN = re.compile(r"^[A-Za-z][A-Za-z0-9]*$")
MODULE_KINDS = {"infrastructure", "web", "ui", "provider"}
RUNNER_LABEL_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
MODULE_CLASS_PATTERN = re.compile(
    r'(?:\b(?:public|internal|sealed|abstract|partial)\s+)*'
    r'class\s+(?P<class>Module[A-Za-z_][A-Za-z0-9_]*)'
    r'(?:\s*\([^;{}]*\))?\s*:\s*'
    r'(?:(?:global::)?(?:[A-Za-z_][A-Za-z0-9_]*\.)*)?MonicaModule\s*'
    r'<(?P<option>[^>{}]+)>',
    re.MULTILINE,
)
NAMESPACE_PATTERN = re.compile(r"\bnamespace\s+(?P<namespace>[A-Za-z_][A-Za-z0-9_.]*)\s*[;{]")
CONST_STRING_PATTERN_TEMPLATE = (
    r"\bconst\s+string\s+{name}\s*=\s*\"(?P<route>/[^\"]+)\"\s*;"
)
PROPERTY_PATTERN = re.compile(r"\$\((?P<name>[^)]+)\)")
SEMVER_PATTERN = re.compile(
    r"^(?:0|[1-9][0-9]*)\."
    r"(?:0|[1-9][0-9]*)\."
    r"(?:0|[1-9][0-9]*)"
    r"(?:-(?:[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
    r"(?:\+(?:[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$"
)
SOURCE_LINK_PACKAGES = {
    "Microsoft.SourceLink.GitHub": "github.com",
    "Microsoft.SourceLink.GitLab": "gitlab",
    "Microsoft.SourceLink.AzureRepos.Git": "dev.azure.com",
}
SOURCE_LINK_PROVIDERS = {
    "github": "Microsoft.SourceLink.GitHub",
    "gitlab": "Microsoft.SourceLink.GitLab",
    "azure-repos": "Microsoft.SourceLink.AzureRepos.Git",
}
MANAGED_TAGS = {"monica", "monica-module", "monica-ecosystem-v1", "monica-ui", "extension"}
COMPATIBILITY_MARK_SHA256 = "d7825fce56b42710468b95b76a689c512114e693a4d52d76dbb2ad6c96f27d7b"
COMPATIBILITY_NOTICE = (
    "Monica compatibility is self-attested by the publisher. This community package is "
    "independently maintained and is not affiliated with, endorsed by, or supported by "
    "the Monica project."
)


@dataclass(frozen=True)
class Finding:
    code: str
    message: str
    path: str


@dataclass(frozen=True)
class PackageReference:
    version: str
    private_assets: str


@dataclass(frozen=True)
class ModuleDeclaration:
    class_name: str
    option_type: str
    namespace: str
    path: Path
    implements_provider: bool
    implements_ui: bool
    implements_web: bool
    requires_web_host: bool
    provides_for_type: str | None
    dependencies: tuple[tuple[str, str | None], ...]


@dataclass(frozen=True)
class Invocation:
    method_name: str
    type_arguments: tuple[str, ...]
    arguments: tuple[str, ...]
    start: int
    end: int


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Validate a complete third-party Monica repository and every declared package."
    )
    parser.add_argument("--root", required=True, type=Path, help="Repository root.")
    parser.add_argument(
        "--package-version",
        help="Effective package version, such as a release-tag-derived PackageVersion override.",
    )
    parser.add_argument("--json", action="store_true", help="Emit machine-readable JSON.")
    return parser.parse_args()


def read_xml(path: Path) -> ET.Element | None:
    try:
        return ET.parse(path).getroot()
    except (ET.ParseError, OSError):
        return None


def property_files(root: Path, project: Path) -> list[Path]:
    directories: list[Path] = []
    current = project.parent.resolve()
    root = root.resolve()
    while current == root or root in current.parents:
        directories.append(current)
        if current == root:
            break
        current = current.parent

    files = [directory / "Directory.Build.props" for directory in reversed(directories)]
    files.append(project)
    return [path for path in files if path.is_file()]


def expand(value: str, properties: dict[str, str]) -> str:
    result = value
    for _ in range(10):
        updated = PROPERTY_PATTERN.sub(lambda match: properties.get(match.group("name"), match.group(0)), result)
        if updated == result:
            break
        result = updated
    return result.strip()


def load_properties(root: Path, project: Path) -> dict[str, str]:
    project_name = project.stem
    properties: dict[str, str] = {
        "MSBuildProjectName": project_name,
        "AssemblyName": project_name,
        "RootNamespace": project_name,
        "PackageId": project_name,
    }

    for path in property_files(root, project):
        xml_root = read_xml(path)
        if xml_root is None:
            continue
        for group in xml_root.findall("PropertyGroup"):
            if group.get("Condition"):
                continue
            for element in group:
                if element.get("Condition") or element.text is None:
                    continue
                properties[element.tag] = expand(element.text, properties)

    for key, value in list(properties.items()):
        properties[key] = expand(value, properties)
    return properties


def nearest_file(root: Path, start: Path, filename: str) -> Path | None:
    current = start.resolve()
    root = root.resolve()
    while current == root or root in current.parents:
        candidate = current / filename
        if candidate.is_file():
            return candidate
        if current == root:
            return None
        current = current.parent
    return None


def package_references(root: Path, project: Path) -> dict[str, PackageReference]:
    properties = load_properties(root, project)
    central_versions: dict[str, str] = {}
    central = nearest_file(root, project.parent, "Directory.Packages.props")
    if central:
        xml_root = read_xml(central)
        if xml_root is not None:
            central_properties = dict(properties)
            for group in xml_root.findall("PropertyGroup"):
                if group.get("Condition"):
                    continue
                for element in group:
                    if element.get("Condition") or element.text is None:
                        continue
                    central_properties[element.tag] = expand(element.text, central_properties)
            for key, value in list(central_properties.items()):
                central_properties[key] = expand(value, central_properties)
            for item in xml_root.findall(".//PackageVersion"):
                package = item.get("Include") or item.get("Update")
                version = item.get("Version")
                if package and version:
                    central_versions[package.casefold()] = expand(version, central_properties)

    references: dict[str, PackageReference] = {}
    xml_root = read_xml(project)
    if xml_root is not None:
        for item in xml_root.findall(".//PackageReference"):
            package = item.get("Include") or item.get("Update")
            version = (
                item.get("VersionOverride")
                or (item.findtext("VersionOverride") or "")
                or item.get("Version")
                or (item.findtext("Version") or "")
            )
            private_assets = item.get("PrivateAssets") or (item.findtext("PrivateAssets") or "")
            if package:
                references[package] = PackageReference(
                    version=expand(version or central_versions.get(package.casefold(), ""), properties),
                    private_assets=private_assets,
                )
    return references


def load_repository_contract(root: Path) -> dict[str, object] | None:
    path = root / "monica.manifest.json"
    if not path.is_file():
        return None
    try:
        payload = json.loads(path.read_text(encoding="utf-8-sig"))
    except (json.JSONDecodeError, OSError):
        return None
    return payload if isinstance(payload, dict) else None


def find_packable_projects(root: Path) -> list[Path]:
    projects: list[Path] = []
    for project in sorted(root.rglob("*.csproj")):
        if any(part in {"bin", "obj", ".git"} for part in project.parts):
            continue
        properties = load_properties(root, project)
        if properties.get("IsTestProject", "").lower() == "true":
            continue
        if properties.get("IsPackable", "").lower() == "false":
            continue
        package_id = properties.get("PackageId", project.stem)
        if PACKAGE_ID_PATTERN.fullmatch(package_id):
            projects.append(project)
    return projects


def bool_property(properties: dict[str, str], name: str) -> bool:
    return properties.get(name, "").strip().lower() == "true"


def resolve_artifact(root: Path, project: Path, value: str) -> Path | None:
    if not value or "$" in value:
        return None
    normalized = value.replace("\\", "/")
    candidates = [project.parent / normalized, root / normalized, root / "assets" / normalized]
    return next((candidate.resolve() for candidate in candidates if candidate.is_file()), None)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    digest.update(path.read_bytes())
    return digest.hexdigest()


def png_icon_error(path: Path) -> str | None:
    payload = path.read_bytes()
    if len(payload) < 33 or payload[:8] != b"\x89PNG\r\n\x1a\n" or payload[12:16] != b"IHDR":
        return "PackageIcon must be a valid PNG file."
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
        return f"PackageIcon must be at least 64x64; found {width}x{height}."
    if not has_transparency:
        return "PackageIcon must support transparency."
    return None


def find_module_declarations(project: Path) -> list[ModuleDeclaration]:
    declarations: list[ModuleDeclaration] = []
    for source in project.parent.rglob("*.cs"):
        if any(part in {"bin", "obj"} for part in source.parts):
            continue
        text = source.read_text(encoding="utf-8-sig")
        masked_text = mask_csharp_comments(text)
        namespace_match = NAMESPACE_PATTERN.search(text)
        namespace = namespace_match.group("namespace") if namespace_match else ""
        for match in MODULE_CLASS_PATTERN.finditer(masked_text):
            opening_brace = masked_text.find("{", match.end())
            closing_brace = (
                find_matching_delimiter(masked_text, opening_brace, "{", "}")
                if opening_brace >= 0
                else None
            )
            header_end = opening_brace if opening_brace >= 0 else match.end()
            header = text[match.end():header_end]
            body = (
                text[opening_brace + 1:closing_brace]
                if opening_brace >= 0 and closing_brace is not None
                else ""
            )
            provides_for_match = re.search(
                r"\bProvidesFor\s*=>\s*typeof\s*\(\s*"
                r"(?P<type>(?:global::)?[A-Za-z_][A-Za-z0-9_.:]*)\s*\)\s*;",
                mask_csharp_comments(body),
            )
            describe_body = ""
            searchable_body = mask_csharp_non_code(body)
            describe_match = re.search(r"\bDescribe\s*\(", searchable_body)
            if describe_match:
                opening_parenthesis = searchable_body.find(
                    "(", describe_match.start(), describe_match.end()
                )
                closing_parenthesis = find_matching_delimiter(
                    mask_csharp_comments(body),
                    opening_parenthesis,
                    "(",
                    ")",
                )
                if closing_parenthesis is not None:
                    opening_method_brace = searchable_body.find("{", closing_parenthesis)
                    closing_method_brace = (
                        find_matching_delimiter(
                            mask_csharp_comments(body),
                            opening_method_brace,
                            "{",
                            "}",
                        )
                        if opening_method_brace >= 0
                        else None
                    )
                    if opening_method_brace >= 0 and closing_method_brace is not None:
                        describe_body = body[opening_method_brace + 1:closing_method_brace]
            dependencies = tuple(
                (
                    normalize_csharp_type(invocation.type_arguments[0]),
                    normalize_csharp_type(invocation.type_arguments[1])
                    if len(invocation.type_arguments) == 2
                    else None,
                )
                for invocation in find_generic_invocations(describe_body, "Require")
                if len(invocation.type_arguments) in (1, 2)
            )
            declarations.append(
                ModuleDeclaration(
                    match.group("class"),
                    normalize_csharp_type(match.group("option")),
                    namespace,
                    source,
                    bool(re.search(r"\bIModuleProvider\b", header)),
                    bool(re.search(r"\bIUIModule\b", header)),
                    bool(re.search(r"\bIWebModule\b", header)),
                    bool(re.search(r"\bIWebHostRequiredModule\b", header)),
                    normalize_csharp_type(provides_for_match.group("type"))
                    if provides_for_match
                    else None,
                    dependencies,
                )
            )
    return declarations


def mask_csharp_comments(source: str) -> str:
    """Replace comments with spaces while preserving source offsets and string literals."""
    masked = list(source)
    index = 0
    while index < len(source):
        if source.startswith("//", index):
            end = source.find("\n", index + 2)
            end = len(source) if end < 0 else end
            for position in range(index, end):
                masked[position] = " "
            index = end
            continue
        if source.startswith("/*", index):
            end = source.find("*/", index + 2)
            end = len(source) if end < 0 else end + 2
            for position in range(index, end):
                if masked[position] not in "\r\n":
                    masked[position] = " "
            index = end
            continue
        if source[index] in {'"', "'"}:
            index = csharp_literal_end(source, index)
            continue
        index += 1
    return "".join(masked)


def mask_csharp_non_code(source: str) -> str:
    """Mask comments and literals so invocation names are discovered only in executable code."""
    comments_masked = mask_csharp_comments(source)
    masked = list(comments_masked)
    index = 0
    while index < len(comments_masked):
        if comments_masked[index] not in {'"', "'"}:
            index += 1
            continue
        end = csharp_literal_end(comments_masked, index)
        for position in range(index, end):
            if masked[position] not in "\r\n":
                masked[position] = " "
        index = end
    return "".join(masked)


def csharp_literal_end(source: str, quote_index: int) -> int:
    quote = source[quote_index]
    if quote == '"' and source.startswith('"""', quote_index):
        quote_count = 3
        while quote_index + quote_count < len(source) and source[quote_index + quote_count] == '"':
            quote_count += 1
        delimiter = '"' * quote_count
        end = source.find(delimiter, quote_index + quote_count)
        return len(source) if end < 0 else end + quote_count

    is_verbatim = quote == '"' and quote_index > 0 and source[quote_index - 1] == "@"
    index = quote_index + 1
    while index < len(source):
        if is_verbatim and source.startswith('""', index):
            index += 2
            continue
        if source[index] == quote:
            return index + 1
        if not is_verbatim and source[index] == "\\":
            index += 2
            continue
        index += 1
    return len(source)


def find_matching_delimiter(source: str, opening_index: int, opening: str, closing: str) -> int | None:
    depth = 0
    index = opening_index
    while index < len(source):
        if source[index] in {'"', "'"}:
            index = csharp_literal_end(source, index)
            continue
        if source[index] == opening:
            depth += 1
        elif source[index] == closing:
            depth -= 1
            if depth == 0:
                return index
        index += 1
    return None


def split_top_level(source: str) -> tuple[str, ...]:
    parts: list[str] = []
    stack: list[str] = []
    pair = {')': '(', ']': '[', '}': '{', '>': '<'}
    start = 0
    index = 0
    while index < len(source):
        character = source[index]
        if character in {'"', "'"}:
            index = csharp_literal_end(source, index)
            continue
        if character in "([{<":
            stack.append(character)
        elif character in pair:
            if stack and stack[-1] == pair[character]:
                stack.pop()
        elif character == "," and not stack:
            parts.append(source[start:index].strip())
            start = index + 1
        index += 1
    tail = source[start:].strip()
    if tail:
        parts.append(tail)
    return tuple(parts)


def find_generic_invocations(source: str, method_name: str) -> list[Invocation]:
    delimiters = mask_csharp_comments(source)
    searchable = mask_csharp_non_code(source)
    pattern = re.compile(rf"\b{re.escape(method_name)}\s*<")
    invocations: list[Invocation] = []
    for match in pattern.finditer(searchable):
        opening_angle = searchable.find("<", match.start(), match.end())
        closing_angle = find_matching_delimiter(delimiters, opening_angle, "<", ">")
        if closing_angle is None:
            continue
        opening_parenthesis = closing_angle + 1
        while opening_parenthesis < len(searchable) and searchable[opening_parenthesis].isspace():
            opening_parenthesis += 1
        if opening_parenthesis >= len(searchable) or searchable[opening_parenthesis] != "(":
            continue
        closing_parenthesis = find_matching_delimiter(
            delimiters,
            opening_parenthesis,
            "(",
            ")",
        )
        if closing_parenthesis is None:
            continue
        invocations.append(
            Invocation(
                method_name=method_name,
                type_arguments=split_top_level(source[opening_angle + 1:closing_angle]),
                arguments=split_top_level(source[opening_parenthesis + 1:closing_parenthesis]),
                start=match.start(),
                end=closing_parenthesis + 1,
            )
        )
    return invocations


def find_method_invocations(source: str, method_name: str) -> list[Invocation]:
    delimiters = mask_csharp_comments(source)
    searchable = mask_csharp_non_code(source)
    pattern = re.compile(rf"\b{re.escape(method_name)}\s*\(")
    invocations: list[Invocation] = []
    for match in pattern.finditer(searchable):
        opening_parenthesis = searchable.find("(", match.start(), match.end())
        closing_parenthesis = find_matching_delimiter(
            delimiters,
            opening_parenthesis,
            "(",
            ")",
        )
        if closing_parenthesis is None:
            continue
        invocations.append(
            Invocation(
                method_name=method_name,
                type_arguments=(),
                arguments=split_top_level(source[opening_parenthesis + 1:closing_parenthesis]),
                start=match.start(),
                end=closing_parenthesis + 1,
            )
        )
    return invocations


def invocation_argument(invocation: Invocation, position: int, name: str) -> str | None:
    positional: list[str] = []
    named: dict[str, str] = {}
    for argument in invocation.arguments:
        match = re.match(r"^(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?P<value>.*)$", argument, re.DOTALL)
        if match:
            named[match.group("name")] = match.group("value").strip()
        else:
            positional.append(argument.strip())
    return named.get(name) or (positional[position] if position < len(positional) else None)


def csharp_string_value(expression: str | None) -> str | None:
    if expression is None:
        return None
    value = expression.strip()
    if value.startswith('@"') and value.endswith('"'):
        return value[2:-1].replace('""', '"')
    if not (value.startswith('"') and value.endswith('"')):
        return None
    try:
        decoded = json.loads(value)
    except json.JSONDecodeError:
        return None
    return decoded if isinstance(decoded, str) else None


def normalize_csharp_type(value: str) -> str:
    return re.sub(r"\s+", "", value).removeprefix("global::")


def assigned_identifier(source: str, invocation: Invocation) -> str | None:
    prefix = source[max(0, invocation.start - 240):invocation.start]
    match = re.search(
        r"\b(?:var|NavigationCategoryId)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*"
        r"(?:(?:[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)+$",
        prefix,
    )
    return match.group("name") if match else None


def is_integer_literal(expression: str | None) -> bool:
    return bool(expression and re.fullmatch(r"-?[0-9][0-9_]*", expression.strip()))


def localized_navigation_contract_errors(
    module_text: str,
    expected_category_id: str,
    expected_page_type: str,
) -> list[str]:
    errors: list[str] = []
    blocks = find_method_invocations(module_text, "ConfigureNavigation")
    categories = find_generic_invocations(module_text, "RegisterLocalizedCategory")
    pages = find_generic_invocations(module_text, "RegisterLocalizedPage")

    if len(blocks) != 1:
        errors.append("use exactly one ConfigureNavigation callback")
    if len(categories) != 1:
        errors.append("register exactly one localized navigation category")
    if not pages:
        errors.append("register at least one localized page")
    if len(blocks) == 1:
        block = blocks[0]
        if any(not (block.start < item.start < item.end <= block.end) for item in [*categories, *pages]):
            errors.append("keep category and page registrations inside the ConfigureNavigation callback")

    if len(categories) != 1:
        return errors

    category = categories[0]
    category_resource = (
        normalize_csharp_type(category.type_arguments[0])
        if len(category.type_arguments) == 1
        else None
    )
    if category_resource is None:
        errors.append("give RegisterLocalizedCategory exactly one resource type")
    if csharp_string_value(invocation_argument(category, 0, "categoryId")) != expected_category_id:
        errors.append(
            f"derive category id '{expected_category_id}' from the UI manifest ecosystem key"
        )
    if csharp_string_value(invocation_argument(category, 1, "displayNameKey")) != "Navigation:Category":
        errors.append("use Navigation:Category as the category resource key")
    if not is_integer_literal(invocation_argument(category, 2, "order")):
        errors.append("set an explicit integer category order")

    category_variable = assigned_identifier(module_text, category)
    if category_variable is None:
        errors.append("assign the registered category id to a local variable")

    found_primary_page = False
    for page in pages:
        page_types = tuple(normalize_csharp_type(value) for value in page.type_arguments)
        if len(page_types) != 2:
            errors.append("give RegisterLocalizedPage one page type and one resource type")
            continue
        page_type, page_resource = page_types
        is_primary_page = page_type == expected_page_type
        found_primary_page = found_primary_page or is_primary_page
        if category_resource is not None and page_resource != category_resource:
            errors.append("use the category resource type for every localized page")
        page_key = csharp_string_value(invocation_argument(page, 1, "displayNameKey"))
        if is_primary_page and page_key != "Navigation:Title":
            errors.append("use Navigation:Title for the primary page resource key")
        elif not is_primary_page and (page_key is None or not page_key.startswith("Navigation:")):
            errors.append("use a module-owned Navigation:* resource key for every additional page")
        category_expression = invocation_argument(page, 3, "categoryId")
        if category_variable is not None and (
            category_expression is None or category_expression.strip() != category_variable
        ):
            errors.append("pass the registered category variable through categoryId")
        add_to_nav = (invocation_argument(page, 4, "addToNav") or "").strip().casefold()
        nav_order = invocation_argument(page, 5, "navOrder")
        if is_primary_page:
            if add_to_nav != "true":
                errors.append("set addToNav to true for the primary page")
            if not is_integer_literal(nav_order):
                errors.append("set an explicit integer navigation order for the primary page")
        elif add_to_nav not in {"", "false", "true"}:
            errors.append("set addToNav to a boolean literal for every additional page")
        elif add_to_nav == "true" and not is_integer_literal(nav_order):
            errors.append("set an explicit integer navigation order for every additional navigation page")

    if pages and not found_primary_page:
        errors.append(f"register primary page type {expected_page_type}")

    return list(dict.fromkeys(errors))


def resolve_route_argument(
    project: Path,
    argument: str | None,
) -> tuple[str | None, str | None]:
    literal = csharp_string_value(argument)
    if literal is not None:
        return literal, None
    if argument is None or re.fullmatch(
        r"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+",
        argument,
    ) is None:
        return None, "route argument is neither a literal nor a qualified const string"

    qualifier, constant_name = argument.rsplit(".", 1)
    type_name = qualifier.rsplit(".", 1)[-1]
    constant_pattern = re.compile(
        CONST_STRING_PATTERN_TEMPLATE.format(name=re.escape(constant_name))
    )
    routes: set[str] = set()
    for suffix in ("*.cs", "*.razor"):
        for candidate in project.parent.rglob(suffix):
            if any(part in {"bin", "obj"} for part in candidate.parts):
                continue
            if not (
                candidate.name == f"{type_name}.razor"
                or candidate.name == f"{type_name}.cs"
                or candidate.name.startswith(f"{type_name}.")
            ):
                continue
            text = candidate.read_text(encoding="utf-8-sig")
            routes.update(item.group("route") for item in constant_pattern.finditer(text))
    if len(routes) == 1:
        return routes.pop(), None
    if not routes:
        return None, f"could not resolve constant route argument '{argument}' to a literal const string"
    return None, f"constant route argument '{argument}' resolves to multiple values: {', '.join(sorted(routes))}"


def resolve_registered_routes(
    project: Path,
    module_source: Path,
) -> list[tuple[str | None, str | None]]:
    module_text = module_source.read_text(encoding="utf-8-sig")
    invocations = find_generic_invocations(module_text, "RegisterLocalizedPage")
    return [
        resolve_route_argument(project, invocation_argument(invocation, 0, "route"))
        for invocation in invocations
    ]


def resolve_registered_route(project: Path, module_source: Path) -> tuple[str | None, str | None]:
    routes = resolve_registered_routes(project, module_source)
    return routes[0] if routes else (None, "localized route registration was not found")


def is_valid_semver(value: str) -> bool:
    if SEMVER_PATTERN.fullmatch(value) is None:
        return False
    prerelease = value.partition("-")[2].partition("+")[0]
    return not any(
        identifier.isdigit() and len(identifier) > 1 and identifier.startswith("0")
        for identifier in prerelease.split(".")
        if identifier
    )


def kebab_case(value: str) -> str:
    tokens = re.findall(r"[A-Z]+(?=[A-Z][a-z]|[0-9]|$)|[A-Z]?[a-z]+|[0-9]+", value)
    return "-".join(token.casefold() for token in tokens)


def package_route_prefix(package_id: str) -> str:
    package_segments = package_id.split(".")[2:]
    if len(package_segments) > 1 and package_segments[-1].casefold() == "ui":
        package_segments = package_segments[:-1]
    segments = [kebab_case(segment) for segment in package_segments]
    return "/" + "-".join(segments)


def validate_project(
    root: Path,
    project: Path,
    effective_package_version: str | None,
) -> list[Finding]:
    findings: list[Finding] = []
    properties = load_properties(root, project)
    relative_project = str(project.relative_to(root))
    package_id = properties.get("PackageId", project.stem)

    def add(code: str, message: str, path: Path = project) -> None:
        findings.append(Finding(code, message, str(path.relative_to(root))))

    match = PACKAGE_ID_PATTERN.fullmatch(package_id)
    if not match or len(package_id) > 100:
        add("MTP001", f"PackageId '{package_id}' does not match <Publisher>.Monica.<Package>[.<Variant>] or exceeds 100 characters.")
    elif match.group("publisher").casefold() == "monica":
        add("MTP002", "Publisher segment 'Monica' is reserved for first-party packages.")

    for property_name in ("AssemblyName", "RootNamespace"):
        if properties.get(property_name, project.stem) != package_id:
            add("MTP003", f"{property_name} must exactly match PackageId '{package_id}'.")
    if project.stem != package_id:
        add("MTP004", f"Project file must be named '{package_id}.csproj'.")

    required = {
        "Version": "Version is required.",
        "Authors": "Authors is required and must identify the independent publisher.",
        "NuGetOwner": "NuGetOwner is required and must identify the publishing account or organization.",
        "PackageDescription": "PackageDescription is required.",
        "PackageProjectUrl": "PackageProjectUrl is required.",
        "PackageReadmeFile": "PackageReadmeFile is required.",
        "PackageIcon": "PackageIcon is required.",
    }
    for property_name, message in required.items():
        if not properties.get(property_name):
            add("MTP005", message)

    repository_url = properties.get("RepositoryUrl", "")
    repository_type = properties.get("RepositoryType", "")
    if repository_url and not repository_url.startswith("https://"):
        add("MTP006", "RepositoryUrl must use HTTPS.")
    if repository_url and repository_type.lower() != "git":
        add("MTP007", "RepositoryType must be 'git'.")
    if not repository_url and repository_type:
        add("MTP007", "RepositoryType must be omitted when RepositoryUrl is omitted.")
    project_url = properties.get("PackageProjectUrl", "")
    if project_url and not project_url.startswith("https://"):
        add("MTP006", "PackageProjectUrl must use HTTPS.")

    contract = load_repository_contract(root)
    source_available: bool | None = None
    source_provider: str | None = None
    contract_repository_url: str | None = None
    contract_monica_version: str | None = None
    manifest_modules_by_class: dict[str, dict[str, object]] = {}
    if contract is not None:
        source = contract.get("source")
        if isinstance(source, dict):
            available = source.get("available")
            provider = source.get("provider")
            source_available = available if isinstance(available, bool) else None
            source_provider = provider.casefold() if isinstance(provider, str) else None
        repository = contract.get("repositoryUrl")
        contract_repository_url = repository if isinstance(repository, str) else None
        monica_version = contract.get("monicaVersion")
        contract_monica_version = monica_version if isinstance(monica_version, str) else None
        packages = contract.get("packages")
        if isinstance(packages, list):
            for package in packages:
                if not isinstance(package, dict) or package.get("packageId") != package_id:
                    continue
                modules = package.get("modules")
                if not isinstance(modules, list):
                    break
                manifest_modules_by_class = {
                    f"module{module.get('name')}".casefold(): module
                    for module in modules
                    if isinstance(module, dict) and isinstance(module.get("name"), str)
                }
                break

    if source_available is True:
        if not repository_url:
            add("MTP029", "Source-available packages must emit RepositoryUrl package metadata.")
        elif contract_repository_url and repository_url != contract_repository_url:
            add("MTP029", "RepositoryUrl package metadata must match monica.manifest.json.")
    elif source_available is False and (repository_url or repository_type):
        add(
            "MTP029",
            "Source-unavailable packages must omit consumer-visible RepositoryUrl and RepositoryType metadata.",
        )

    license_expression = properties.get("PackageLicenseExpression", "")
    license_file = properties.get("PackageLicenseFile", "")
    if bool(license_expression) == bool(license_file):
        add("MTP008", "Configure exactly one of PackageLicenseExpression or PackageLicenseFile.")
    if license_file and resolve_artifact(root, project, license_file) is None:
        add("MTP009", f"PackageLicenseFile '{license_file}' does not exist.")

    readme = resolve_artifact(root, project, properties.get("PackageReadmeFile", ""))
    if properties.get("PackageReadmeFile") and readme is None:
        add("MTP010", f"PackageReadmeFile '{properties['PackageReadmeFile']}' does not exist.")
    icon = resolve_artifact(root, project, properties.get("PackageIcon", ""))
    if properties.get("PackageIcon") and icon is None:
        add("MTP011", f"PackageIcon '{properties['PackageIcon']}' does not exist.")
    elif icon is not None:
        icon_error = png_icon_error(icon)
        if icon_error:
            add("MTP011", icon_error, icon)

    uses_compatibility_mark = False
    if icon is not None:
        icon_matches_canonical_mark = sha256(icon) == COMPATIBILITY_MARK_SHA256
        claims_compatibility_mark = icon.name.casefold() == "monica-compatibility-mark.png"
        if claims_compatibility_mark and not icon_matches_canonical_mark:
            add(
                "MTP032",
                "monica-compatibility-mark.png must match the canonical Monica Compatibility Mark unchanged.",
                icon,
            )
        uses_compatibility_mark = icon_matches_canonical_mark
    if uses_compatibility_mark and readme is not None:
        readme_text = readme.read_text(encoding="utf-8-sig")
        if COMPATIBILITY_NOTICE not in readme_text:
            add(
                "MTP012",
                "README must include the Monica Compatibility Mark self-attestation and independence notice.",
                readme,
            )

    tags = {
        tag.casefold()
        for tag in re.split(r"[;,\s]+", properties.get("PackageTags", ""))
        if tag
    }
    for tag in ("monica", "monica-module", "monica-ecosystem-v1"):
        if tag not in tags:
            add("MTP013", f"PackageTags must include '{tag}'.")

    for property_name in ("GenerateDocumentationFile", "TreatWarningsAsErrors", "IncludeSymbols"):
        if not bool_property(properties, property_name):
            add("MTP014", f"{property_name} must be true.")
    if properties.get("SymbolPackageFormat", "").casefold() != "snupkg":
        add("MTP015", "SymbolPackageFormat must be 'snupkg'.")

    references = package_references(root, project)
    source_link_references = {
        package: reference
        for package, reference in references.items()
        if package in SOURCE_LINK_PACKAGES
    }
    if len(source_link_references) > 1:
        add("MTP016", "Reference only the Source Link provider that owns RepositoryUrl.")
    for package, reference in source_link_references.items():
        private_assets = {
            item.casefold()
            for item in re.split(r"[;,\s]+", reference.private_assets)
            if item
        }
        if "all" not in private_assets:
            add("MTP016", f"{package} must set PrivateAssets=All.")
        if not bool_property(properties, "PublishRepositoryUrl"):
            add("MTP014", "PublishRepositoryUrl must be true when Source Link is enabled.")
        if not bool_property(properties, "EmbedUntrackedSources"):
            add("MTP014", "EmbedUntrackedSources must be true when Source Link is enabled.")
        expected_host = SOURCE_LINK_PACKAGES[package]
        if expected_host not in repository_url.casefold():
            add("MTP016", f"{package} does not match RepositoryUrl '{repository_url}'.")
    if not source_link_references and (
        bool_property(properties, "PublishRepositoryUrl") or bool_property(properties, "EmbedUntrackedSources")
    ):
        add("MTP016", "Source Link properties are enabled without a supported Source Link package reference.")
    if source_available is True:
        expected_source_link = SOURCE_LINK_PROVIDERS.get(source_provider or "")
        if expected_source_link is None:
            add("MTP029", "A source-available package must declare a supported source provider in monica.manifest.json.")
        elif expected_source_link not in source_link_references:
            add("MTP016", f"Source provider '{source_provider}' requires {expected_source_link}.")
    elif source_available is False:
        if source_link_references:
            add("MTP029", "Source-unavailable packages must not reference Source Link packages.")
        if bool_property(properties, "PublishRepositoryUrl") or bool_property(properties, "EmbedUntrackedSources"):
            add("MTP029", "Source-unavailable packages must not publish repository source metadata.")

    declared_package_version = properties.get("PackageVersion") or properties.get("Version", "")
    package_version = effective_package_version or declared_package_version
    if not is_valid_semver(package_version):
        add("MTP023", f"Effective package version '{package_version}' is not valid three-part SemVer.")
    for package, reference in references.items():
        if package.casefold().startswith("monica.") and not is_valid_semver(reference.version):
            add("MTP023", f"Monica dependency version '{package} {reference.version}' is not valid three-part SemVer.")
        if (
            package.casefold().startswith("monica.")
            and contract_monica_version is not None
            and reference.version != contract_monica_version
        ):
            add(
                "MTP033",
                f"Monica dependency '{package}' must use manifest monicaVersion '{contract_monica_version}', found '{reference.version}'.",
            )
    prerelease_monica = [
        (package, reference.version)
        for package, reference in references.items()
        if package.casefold().startswith("monica.") and "-" in reference.version
    ]
    if prerelease_monica and "-" not in package_version:
        dependency_list = ", ".join(f"{package} {version}" for package, version in prerelease_monica)
        add("MTP017", f"Stable package version '{package_version}' cannot depend on prerelease Monica packages: {dependency_list}.")

    declarations = find_module_declarations(project)
    if not declarations:
        add("MTP018", "No third-party MonicaModule<TOptions> declaration was found.")

    legacy_registration_sources: set[Path] = set()
    for source in project.parent.rglob("*.cs"):
        if any(part in {"bin", "obj"} for part in source.parts):
            continue
        source_text = source.read_text(encoding="utf-8-sig")
        if find_generic_invocations(source_text, "RegisterLocalizedComponent"):
            legacy_registration_sources.add(source)
            add(
                "MTP030",
                "RegisterLocalizedComponent is a legacy navigation API; register a stable localized category "
                "and use RegisterLocalizedPage instead.",
                source,
            )

    package_prefix = package_id.casefold() + "."
    expected_namespace = f"{package_id}.Modules"
    has_ui_module = False
    for declaration in declarations:
        class_name = declaration.class_name
        source = declaration.path
        if declaration.namespace != expected_namespace:
            add(
                "MTP024",
                f"Third-party module registrations must use namespace '{expected_namespace}', not '{declaration.namespace or '<missing>'}'.",
                source,
            )
        manifest_module = manifest_modules_by_class.get(class_name.casefold())
        if manifest_module is None:
            add(
                "MTP021",
                f"Module class '{class_name}' must have one matching entry in monica.manifest.json.",
                source,
            )
            continue
        key = manifest_module.get("key")
        if not isinstance(key, str):
            add("MTP019", f"Manifest entry for '{class_name}' must declare an ecosystem key.", source)
            continue
        if not MODULE_KEY_PATTERN.fullmatch(key) or len(key) > 100:
            add("MTP019", f"Manifest module key '{key}' is not a valid ecosystem key.", source)
            continue
        folded = key.casefold()
        if folded != package_id.casefold() and not folded.startswith(package_prefix):
            add(
                "MTP020",
                f"Manifest module key '{key}' must equal PackageId or start with '{package_id}.'",
                source,
            )
        is_ui_key = folded.endswith(".ui")
        class_suffix = class_name.removeprefix("Module")
        is_exact_ui_class = class_suffix.endswith("UI")
        has_ui_module = has_ui_module or is_ui_key
        if is_ui_key:
            base_name = class_suffix[:-2] if is_exact_ui_class else ""
            if not is_exact_ui_class or not base_name or base_name.casefold().endswith("ui"):
                add(
                    "MTP022",
                    f"UI identity mismatch: {class_name} must end with one exact 'UI' suffix and have a non-empty base name.",
                    source,
                )
            if source in legacy_registration_sources:
                continue

            module_text = source.read_text(encoding="utf-8-sig")
            category_id = key[:-3]
            navigation_errors = localized_navigation_contract_errors(
                module_text,
                category_id,
                f"UI{base_name}Page",
            )
            if navigation_errors:
                add(
                    "MTP031",
                    f"{class_name} does not follow the stable localized navigation contract: "
                    + "; ".join(navigation_errors)
                    + ".",
                    source,
                )
            route_prefix = package_route_prefix(package_id)
            registered_routes = resolve_registered_routes(project, source)
            if not registered_routes:
                add("MTP028", f"Could not verify {class_name} UI route: localized route registration was not found.", source)
            for registered_route, route_error in registered_routes:
                if registered_route is None:
                    add("MTP028", f"Could not verify {class_name} UI route: {route_error}.", source)
                elif not (
                    registered_route == route_prefix
                    or registered_route.startswith(route_prefix + "-")
                ):
                    add(
                        "MTP027",
                        f"UI route '{registered_route}' must equal '{route_prefix}' or start with '{route_prefix}-'.",
                        source,
                    )
        elif class_suffix.casefold().endswith("ui"):
            add("MTP022", f"UI identity mismatch: {class_name} must use a final '.UI' key segment, and non-UI modules must not.", source)

    if has_ui_module and "monica-ui" not in tags:
        add("MTP025", "PackageTags must include 'monica-ui' when the package contains a UI module.")
    if not (tags - MANAGED_TAGS):
        add("MTP026", "PackageTags must include at least one package-specific capability or provider tag.")

    if findings:
        return findings

    return [Finding("OK", f"{package_id} complies with Monica Ecosystem Standard v1.", relative_project)]


def project_reference_ids(
    root: Path,
    project: Path,
    package_by_project: dict[str, str],
) -> set[str]:
    xml_root = read_xml(project)
    if xml_root is None:
        return set()
    result: set[str] = set()
    for item in xml_root.findall(".//ProjectReference"):
        include = item.get("Include")
        if not include:
            continue
        candidate = (project.parent / include.replace("\\", "/")).resolve()
        try:
            relative = candidate.relative_to(root).as_posix().casefold()
        except ValueError:
            continue
        package_id = package_by_project.get(relative)
        if package_id:
            result.add(package_id.casefold())
    return result


def graph_cycle(nodes: dict[str, set[str]]) -> list[str] | None:
    visiting: list[str] = []
    visited: set[str] = set()

    def visit(node: str) -> list[str] | None:
        if node in visited:
            return None
        if node in visiting:
            return visiting[visiting.index(node):] + [node]
        visiting.append(node)
        for dependency in nodes.get(node, set()):
            cycle = visit(dependency)
            if cycle:
                return cycle
        visiting.pop()
        visited.add(node)
        return None

    for node in nodes:
        cycle = visit(node)
        if cycle:
            return cycle
    return None


def validate_repository_contract(
    root: Path,
    contract: dict[str, object] | None,
    effective_package_version: str | None,
) -> list[Finding]:
    path = root / "monica.manifest.json"
    findings: list[Finding] = []

    def add(code: str, message: str, finding_path: Path = path) -> None:
        findings.append(Finding(code, message, str(finding_path.relative_to(root))))

    def reject_unknown(value: dict[str, object], allowed: set[str], context: str) -> None:
        unknown = sorted(set(value) - allowed)
        if unknown:
            add("MTR032", f"{context} contains unsupported fields: {', '.join(unknown)}.")

    if contract is None:
        add("MTR001", "monica.manifest.json must contain a valid JSON object.")
        return findings
    if contract.get("schemaVersion") != 2:
        add("MTR002", "Only repository manifest schemaVersion 2 is supported.")
        return findings
    reject_unknown(
        contract,
        {
            "schemaVersion", "repositoryId", "solutionPath", "version", "authors", "nugetOwner",
            "repositoryUrl", "projectUrl", "supportUrl", "securityUrl", "source", "distribution",
            "publishing", "targetFramework", "monicaVersion", "license", "branding", "packages", "ociImages",
        },
        "Manifest",
    )
    nested_contracts = {
        "source": {"available", "provider", "sourceLinkVersion"},
        "publishing": {"target", "feedUrl"},
        "license": {"openSource", "expression", "file"},
        "branding": {"icon", "showOpenSourceBadge"},
    }
    for field, allowed in nested_contracts.items():
        value = contract.get(field)
        if not isinstance(value, dict):
            add("MTR033", f"Manifest field '{field}' must be an object.")
        else:
            reject_unknown(value, allowed, field)
    branding = contract.get("branding")
    if isinstance(branding, dict):
        icon = branding.get("icon")
        if not isinstance(icon, dict):
            add("MTR033", "branding.icon must be an object.")
        else:
            reject_unknown(icon, {"kind", "file"}, "branding.icon")

    repository_id = contract.get("repositoryId")
    solution_path = contract.get("solutionPath")
    declared_version = contract.get("version")
    if not isinstance(repository_id, str) or PACKAGE_ID_PATTERN.fullmatch(repository_id) is None:
        add("MTR003", "repositoryId must follow <Publisher>.Monica.<Family>[.<Variant>].")
    if not isinstance(solution_path, str) or not solution_path.endswith(".slnx"):
        add("MTR004", "solutionPath must be a repository-relative .slnx path.")
    elif not (root / solution_path).is_file():
        add("MTR004", f"Declared solutionPath does not exist: {solution_path}.")
    if not isinstance(declared_version, str) or not is_valid_semver(declared_version):
        add("MTR005", "Manifest version must be valid three-part SemVer.")
    monica_version = contract.get("monicaVersion")
    if not isinstance(monica_version, str) or not is_valid_semver(monica_version):
        add("MTR037", "Manifest monicaVersion must be valid three-part SemVer.")
    package_version = effective_package_version or (declared_version if isinstance(declared_version, str) else "")

    raw_packages = contract.get("packages")
    if not isinstance(raw_packages, list) or not raw_packages:
        add("MTR006", "packages must contain at least one package declaration.")
        return findings

    package_entries: dict[str, tuple[dict[str, object], Path]] = {}
    package_by_project: dict[str, str] = {}
    package_graph: dict[str, set[str]] = {}
    module_entries: dict[str, tuple[str, dict[str, object]]] = {}
    repository_publisher = (
        repository_id.split(".", 1)[0].casefold()
        if isinstance(repository_id, str) and "." in repository_id
        else None
    )
    for raw in raw_packages:
        if not isinstance(raw, dict):
            add("MTR006", "Every package declaration must be an object.")
            continue
        reject_unknown(
            raw,
            {"packageId", "projectPath", "description", "capabilityTags", "packageDependencies", "modules"},
            "Package declaration",
        )
        package_id = raw.get("packageId")
        project_value = raw.get("projectPath")
        if not isinstance(package_id, str) or PACKAGE_ID_PATTERN.fullmatch(package_id) is None:
            add("MTR007", f"Invalid packageId in manifest: {package_id!r}.")
            continue
        folded_package = package_id.casefold()
        if repository_publisher and package_id.split(".", 1)[0].casefold() != repository_publisher:
            add("MTR007", f"Package '{package_id}' does not use repository publisher '{repository_publisher}'.")
        if folded_package in package_entries:
            add("MTR008", f"Duplicate packageId in manifest: {package_id}.")
            continue
        if not isinstance(project_value, str):
            add("MTR009", f"Package '{package_id}' requires projectPath.")
            continue
        normalized_project = project_value.replace("\\", "/")
        expected_project = f"src/{package_id}/{package_id}.csproj"
        if normalized_project != expected_project:
            add("MTR009", f"Package '{package_id}' projectPath must be '{expected_project}'.")
        project = (root / normalized_project).resolve()
        if not project.is_file():
            add("MTR009", f"Declared package project does not exist: {normalized_project}.")
        package_entries[folded_package] = (raw, project)
        package_by_project[normalized_project.casefold()] = package_id

        raw_dependencies = raw.get("packageDependencies", [])
        if not isinstance(raw_dependencies, list) or not all(isinstance(item, str) for item in raw_dependencies):
            add("MTR010", f"Package '{package_id}' packageDependencies must be a string array.")
            dependencies: set[str] = set()
        else:
            dependencies = {item.casefold() for item in raw_dependencies}
            if len(dependencies) != len(raw_dependencies):
                add("MTR010", f"Package '{package_id}' contains duplicate packageDependencies.")
        if folded_package in dependencies:
            add("MTR010", f"Package '{package_id}' cannot depend on itself.")
        package_graph[folded_package] = dependencies

        raw_modules = raw.get("modules")
        if not isinstance(raw_modules, list) or not raw_modules:
            add("MTR011", f"Package '{package_id}' must declare at least one module.")
            continue
        for module in raw_modules:
            if not isinstance(module, dict):
                add("MTR011", f"Package '{package_id}' contains a non-object module declaration.")
                continue
            reject_unknown(module, {"name", "kind", "key", "dependsOn", "providerFor"}, "Module declaration")
            key = module.get("key")
            name = module.get("name")
            kind = module.get("kind")
            if not isinstance(name, str) or MODULE_NAME_PATTERN.fullmatch(name) is None:
                add("MTR011", f"Module name {name!r} must begin with an ASCII letter and contain only letters or digits.")
            if not isinstance(kind, str) or kind not in MODULE_KINDS:
                add("MTR011", f"Module '{key}' kind must be one of: {', '.join(sorted(MODULE_KINDS))}.")
            if not isinstance(key, str) or MODULE_KEY_PATTERN.fullmatch(key) is None:
                add(
                    "MTR012",
                    f"Package '{package_id}' contains invalid manifest module key {key!r}.",
                )
                continue
            folded_key = key.casefold()
            if folded_key != folded_package and not folded_key.startswith(folded_package + "."):
                add(
                    "MTR012",
                    f"Manifest module key '{key}' is not owned by package '{package_id}'.",
                )
            if folded_key in module_entries:
                add("MTR013", f"Duplicate manifest module key in repository contract: {key}.")
            module_entries[folded_key] = (folded_package, module)
            is_ui_name = isinstance(name, str) and name.endswith("UI") and not name[:-2].casefold().endswith("ui")
            is_ui_key = folded_key.endswith(".ui")
            if kind == "ui" and (not is_ui_name or not is_ui_key):
                add("MTR011", f"UI module '{key}' must use one exact UI name suffix and a final .UI key segment.")
            elif kind != "ui" and (is_ui_name or is_ui_key):
                add("MTR011", f"Non-UI module '{key}' must not use UI identity suffixes.")

    declared_projects = set(package_by_project)
    actual_projects = {
        project.relative_to(root).as_posix().casefold()
        for project in find_packable_projects(root)
    }
    for missing in sorted(declared_projects - actual_projects):
        add("MTR014", f"Declared packable project was not discovered: {missing}.")
    for extra in sorted(actual_projects - declared_projects):
        add("MTR014", f"Undeclared packable project was discovered: {extra}.")

    for package_id, dependencies in package_graph.items():
        unknown = sorted(dependencies - set(package_entries))
        if unknown:
            add("MTR015", f"Package '{package_id}' depends on undeclared packages: {', '.join(unknown)}.")
        raw_entry = package_entries.get(package_id)
        if raw_entry is not None:
            for dependency in raw_entry[0].get("packageDependencies", []):
                canonical_entry = package_entries.get(str(dependency).casefold())
                canonical_id = canonical_entry[0].get("packageId") if canonical_entry else None
                if isinstance(canonical_id, str) and dependency != canonical_id:
                    add(
                        "MTR010",
                        f"Package dependency '{dependency}' must preserve canonical casing '{canonical_id}'.",
                    )
    cycle = graph_cycle(package_graph)
    if cycle:
        add("MTR016", "Package dependency cycle: " + " -> ".join(cycle) + ".")

    module_graph: dict[str, set[str]] = {}
    for module_key, (owner_id, module) in module_entries.items():
        raw_dependencies = module.get("dependsOn", [])
        if not isinstance(raw_dependencies, list) or not all(isinstance(item, str) for item in raw_dependencies):
            add(
                "MTR017",
                f"Module '{module_key}' dependsOn must contain full manifest module keys.",
            )
            dependencies = set()
        else:
            dependencies = {item.casefold() for item in raw_dependencies}
            if len(dependencies) != len(raw_dependencies):
                add("MTR017", f"Module '{module_key}' contains duplicate dependencies.")
        unknown = sorted(dependencies - set(module_entries))
        if unknown:
            add("MTR017", f"Module '{module_key}' depends on undeclared keys: {', '.join(unknown)}.")
        if isinstance(raw_dependencies, list):
            for dependency in raw_dependencies:
                canonical_entry = module_entries.get(str(dependency).casefold())
                canonical_key = canonical_entry[1].get("key") if canonical_entry else None
                if isinstance(canonical_key, str) and dependency != canonical_key:
                    add(
                        "MTR017",
                        f"Module dependency '{dependency}' must preserve canonical casing '{canonical_key}'.",
                    )
        for dependency in dependencies & set(module_entries):
            dependency_owner, _ = module_entries[dependency]
            if dependency_owner != owner_id and dependency_owner not in package_graph.get(owner_id, set()):
                add("MTR018", f"Module '{module_key}' crosses into '{dependency_owner}' without a package dependency.")
        kind = module.get("kind")
        provider_for = module.get("providerFor")
        if kind == "provider":
            if not isinstance(provider_for, str) or provider_for.casefold() not in dependencies:
                add("MTR019", f"Provider module '{module_key}' must declare providerFor and include it in dependsOn.")
            elif provider_for.casefold() not in module_entries:
                add("MTR019", f"Provider module '{module_key}' targets an undeclared module.")
            else:
                canonical_provider_key = module_entries[provider_for.casefold()][1].get("key")
                if provider_for != canonical_provider_key:
                    add(
                        "MTR019",
                        f"Provider module '{module_key}' providerFor must preserve canonical casing '{canonical_provider_key}'.",
                    )
        elif provider_for is not None:
            add("MTR019", f"Non-provider module '{module_key}' must not declare providerFor.")
        module_graph[module_key] = dependencies
    module_cycle = graph_cycle(module_graph)
    if module_cycle:
        add("MTR020", "Module dependency cycle: " + " -> ".join(module_cycle) + ".")

    full_module_type_to_key: dict[str, str] = {}
    full_option_type_to_key: dict[str, str] = {}
    simple_module_type_to_keys: dict[str, set[str]] = {}
    simple_option_type_to_keys: dict[str, set[str]] = {}
    repository_module_namespaces: set[str] = set()
    for module_key, (owner_id, module) in module_entries.items():
        module_name = module.get("name")
        owner_entry = package_entries.get(owner_id)
        if not isinstance(module_name, str) or owner_entry is None:
            continue
        owner_package_id = owner_entry[0].get("packageId")
        if not isinstance(owner_package_id, str):
            continue
        simple_module_type = f"Module{module_name}"
        simple_option_type = f"{simple_module_type}Option"
        module_namespace = f"{owner_package_id}.Modules"
        full_module_type = f"{module_namespace}.{simple_module_type}"
        full_option_type = f"{module_namespace}.{simple_option_type}"
        repository_module_namespaces.add(module_namespace.casefold())
        full_module_type_to_key[full_module_type.casefold()] = module_key
        full_option_type_to_key[full_option_type.casefold()] = module_key
        simple_module_type_to_keys.setdefault(simple_module_type.casefold(), set()).add(module_key)
        simple_option_type_to_keys.setdefault(simple_option_type.casefold(), set()).add(module_key)

    def resolve_repository_type(
        owner_id: str,
        type_name: str,
        full_type_to_key: dict[str, str],
        simple_type_to_keys: dict[str, set[str]],
    ) -> tuple[str | None, bool]:
        normalized = normalize_csharp_type(type_name)
        if "." in normalized:
            key = full_type_to_key.get(normalized.casefold())
            namespace = normalized.rpartition(".")[0].casefold()
            return key, key is None and namespace in repository_module_namespaces
        owner_entry = package_entries.get(owner_id)
        owner_package_id = owner_entry[0].get("packageId") if owner_entry else None
        if isinstance(owner_package_id, str):
            local = full_type_to_key.get(
                f"{owner_package_id}.Modules.{normalized}".casefold()
            )
            if local is not None:
                return local, False
        candidates = simple_type_to_keys.get(normalized.casefold(), set())
        return None, bool(candidates)

    for package_id, (raw, project) in package_entries.items():
        if not project.is_file():
            continue
        actual_dependencies = project_reference_ids(root, project, package_by_project)
        expected_dependencies = package_graph.get(package_id, set())
        if actual_dependencies != expected_dependencies:
            add(
                "MTR021",
                f"Project references for '{package_id}' must match packageDependencies; "
                f"expected {sorted(expected_dependencies)}, found {sorted(actual_dependencies)}.",
                project,
            )
        manifest_modules_by_class = {
            f"Module{module.get('name')}": module
            for module in raw.get("modules", [])
            if isinstance(module, dict) and isinstance(module.get("key"), str)
        }
        declarations = find_module_declarations(project)
        source_modules = {declaration.class_name for declaration in declarations}
        if set(manifest_modules_by_class) != source_modules:
            add(
                "MTR022",
                f"Source module declarations for '{package_id}' do not match monica.manifest.json.",
                project,
            )
        expected_options = {
            class_name: f"{class_name}Option"
            for class_name in manifest_modules_by_class
        }
        for declaration in declarations:
            expected_option = expected_options.get(declaration.class_name)
            if expected_option is None:
                continue
            normalized_option = declaration.option_type.rpartition(".")[2]
            if normalized_option != expected_option:
                add(
                    "MTR022",
                    f"Module '{declaration.class_name}' must derive from "
                    f"MonicaModule<{expected_option}>, found MonicaModule<{declaration.option_type}>.",
                    declaration.path,
                )
        for declaration in declarations:
            manifest_module = manifest_modules_by_class.get(declaration.class_name)
            if manifest_module is None:
                continue
            manifest_key = manifest_module.get("key")
            if not isinstance(manifest_key, str):
                continue
            expected_dependencies = {
                dependency.casefold()
                for dependency in manifest_module.get("dependsOn", [])
                if isinstance(dependency, str)
            }
            actual_dependencies: set[str] = set()
            invalid_dependencies: list[str] = []
            for module_type, option_type in declaration.dependencies:
                dependency_key, invalid_module_type = resolve_repository_type(
                    package_id,
                    module_type,
                    full_module_type_to_key,
                    simple_module_type_to_keys,
                )
                option_key, invalid_option_type = dependency_key, False
                if option_type is not None:
                    option_key, invalid_option_type = resolve_repository_type(
                        package_id,
                        option_type,
                        full_option_type_to_key,
                        simple_option_type_to_keys,
                    )
                if dependency_key is not None and dependency_key == option_key:
                    actual_dependencies.add(dependency_key)
                elif (
                    dependency_key is not None
                    or option_key is not None
                    or invalid_module_type
                    or invalid_option_type
                ):
                    invalid_dependencies.append(
                        f"{module_type}, {option_type}" if option_type is not None else module_type
                    )
            if actual_dependencies != expected_dependencies or invalid_dependencies:
                detail = (
                    f"expected {sorted(expected_dependencies)}, found {sorted(actual_dependencies)}"
                )
                if invalid_dependencies:
                    detail += (
                        "; unresolved module types or mismatched option types: "
                        f"{sorted(invalid_dependencies)}"
                    )
                add(
                    "MTR034",
                    f"Source module dependencies for '{manifest_key}' do not match "
                    f"monica.manifest.json: {detail}.",
                    declaration.path,
                )

            kind = manifest_module.get("kind")
            provider_for = manifest_module.get("providerFor")
            if kind == "ui":
                if not declaration.implements_ui:
                    add(
                        "MTR036",
                        f"UI module '{manifest_key}' must implement IUIModule.",
                        declaration.path,
                    )
            elif declaration.implements_ui:
                add(
                    "MTR036",
                    f"Non-UI module '{manifest_key}' must not implement IUIModule.",
                    declaration.path,
                )
            if kind == "web":
                if not declaration.implements_web or not declaration.requires_web_host:
                    add(
                        "MTR036",
                        f"Web module '{manifest_key}' must implement IWebModule and "
                        "IWebHostRequiredModule.",
                        declaration.path,
                    )
            elif declaration.implements_web or declaration.requires_web_host:
                add(
                    "MTR036",
                    f"Non-web module '{manifest_key}' must not declare web-host markers.",
                    declaration.path,
                )
            if kind == "provider":
                if not declaration.implements_provider:
                    add(
                        "MTR035",
                        f"Provider module '{manifest_key}' must implement IModuleProvider.",
                        declaration.path,
                    )
                provider_entry = (
                    module_entries.get(provider_for.casefold())
                    if isinstance(provider_for, str)
                    else None
                )
                expected_provider_type = None
                if provider_entry is not None:
                    target_owner_id, target_module = provider_entry
                    target_owner_entry = package_entries.get(target_owner_id)
                    target_package_id = (
                        target_owner_entry[0].get("packageId")
                        if target_owner_entry is not None
                        else None
                    )
                    target_name = target_module.get("name")
                    if isinstance(target_package_id, str) and isinstance(target_name, str):
                        expected_provider_type = (
                            f"{target_package_id}.Modules.Module{target_name}"
                        )
                provider_type_key = None
                invalid_provider_type = False
                if declaration.provides_for_type is not None:
                    provider_type_key, invalid_provider_type = resolve_repository_type(
                        package_id,
                        declaration.provides_for_type,
                        full_module_type_to_key,
                        simple_module_type_to_keys,
                    )
                expected_provider_key = (
                    provider_for.casefold() if isinstance(provider_for, str) else None
                )
                if (
                    provider_type_key != expected_provider_key
                    or invalid_provider_type
                ):
                    add(
                        "MTR035",
                        f"Provider module '{manifest_key}' ProvidesFor must return "
                        f"typeof({expected_provider_type}), found {declaration.provides_for_type!r}.",
                        declaration.path,
                    )
            elif declaration.implements_provider or declaration.provides_for_type is not None:
                add(
                    "MTR035",
                    f"Non-provider module '{manifest_key}' must not implement the provider contract.",
                    declaration.path,
                )
        properties = load_properties(root, project)
        if properties.get("Version") != declared_version:
            add("MTR023", f"Project Version for '{package_id}' must match repository version '{declared_version}'.", project)
        if properties.get("PackageDescription") != raw.get("description"):
            add("MTR024", f"PackageDescription for '{package_id}' must match its manifest description.", project)

    for candidate in [root / "Directory.Build.props", *root.rglob("*.csproj")]:
        if candidate.is_file() and "MonicaSourceRoot" in candidate.read_text(encoding="utf-8-sig"):
            add("MTR025", "Repository builds must consume versioned Monica NuGet packages, not MonicaSourceRoot project switches.", candidate)

    raw_images = contract.get("ociImages", [])
    if not isinstance(raw_images, list):
        add("MTR026", "ociImages must be an array.")
    else:
        image_ids: set[str] = set()
        repositories: set[str] = set()
        bake_targets: set[str] = set()
        resulting_tags: set[str] = set()
        nvidia_runner_sets: set[frozenset[str]] = set()
        for raw_image in raw_images:
            if not isinstance(raw_image, dict):
                add("MTR026", "Every OCI image declaration must be an object.")
                continue
            reject_unknown(
                raw_image,
                {
                    "id", "repository", "companionPackageId", "contextPath", "dockerfilePath",
                    "bakeFilePath", "targets", "releaseGates",
                },
                "OCI image declaration",
            )
            image_id = raw_image.get("id")
            repository = raw_image.get("repository")
            companion = raw_image.get("companionPackageId")
            if not isinstance(image_id, str) or not isinstance(repository, str):
                add("MTR026", "Every OCI image requires id and repository.")
                continue
            if image_id.casefold() in image_ids or repository.casefold() in repositories:
                add("MTR027", f"Duplicate OCI image identity or repository: {image_id} / {repository}.")
            image_ids.add(image_id.casefold())
            repositories.add(repository.casefold())
            companion_entry = package_entries.get(companion.casefold()) if isinstance(companion, str) else None
            if companion_entry is None:
                add("MTR028", f"OCI image '{image_id}' companionPackageId is undeclared.")
            elif not any(
                isinstance(module, dict) and module.get("kind") == "provider"
                for module in companion_entry[0].get("modules", [])
            ):
                add(
                    "MTR028",
                    f"OCI image '{image_id}' companionPackageId must name a package that owns a provider module.",
                )
            for field in ("contextPath", "dockerfilePath", "bakeFilePath"):
                value = raw_image.get(field)
                if not isinstance(value, str) or not (root / value).exists():
                    add("MTR029", f"OCI image '{image_id}' declared {field} does not exist: {value!r}.")
                elif field == "bakeFilePath" and Path(value).parent != Path("."):
                    add("MTR029", f"OCI image '{image_id}' bakeFilePath must be at the repository root.")
            raw_targets = raw_image.get("targets")
            if not isinstance(raw_targets, list) or not raw_targets:
                add("MTR030", f"OCI image '{image_id}' requires at least one target.")
                continue
            target_accelerators: set[str] = set()
            for target in raw_targets:
                if not isinstance(target, dict):
                    add("MTR030", f"OCI image '{image_id}' contains a non-object target.")
                    continue
                reject_unknown(target, {"bakeTarget", "stage", "platform", "accelerator", "tagSuffix"}, "OCI target")
                bake_target = target.get("bakeTarget")
                tag_suffix = target.get("tagSuffix")
                stage = target.get("stage")
                platform = target.get("platform")
                accelerator = target.get("accelerator")
                if not all(isinstance(value, str) and value for value in (bake_target, tag_suffix, stage, platform, accelerator)):
                    add("MTR030", f"OCI image '{image_id}' target requires bakeTarget, stage, platform, accelerator, and tagSuffix.")
                    continue
                if platform not in {"linux/amd64", "linux/arm64"} or accelerator not in {"cpu", "nvidia"}:
                    add("MTR030", f"OCI target '{bake_target}' has unsupported platform or accelerator.")
                if accelerator == "nvidia" and platform != "linux/amd64":
                    add("MTR030", f"NVIDIA OCI target '{bake_target}' must use linux/amd64.")
                target_accelerators.add(accelerator)
                resulting_tag = f"{repository}:{package_version}-{tag_suffix}".casefold()
                if bake_target.casefold() in bake_targets or resulting_tag in resulting_tags:
                    add("MTR031", f"Duplicate OCI bake target or resulting tag: {bake_target}.")
                bake_targets.add(bake_target.casefold())
                resulting_tags.add(resulting_tag)

            release_gates = raw_image.get("releaseGates")
            if release_gates is None:
                continue
            if not isinstance(release_gates, dict):
                add("MTR036", f"OCI image '{image_id}' releaseGates must be an object.")
                continue
            reject_unknown(
                release_gates,
                {"cpuSmokeCommand", "nvidiaSmokeCommand", "managedNvidiaRunnerLabels"},
                "OCI release gates",
            )
            cpu_command = release_gates.get("cpuSmokeCommand")
            nvidia_command = release_gates.get("nvidiaSmokeCommand")
            labels = release_gates.get("managedNvidiaRunnerLabels", [])
            if "cpu" in target_accelerators:
                if not isinstance(cpu_command, str) or not cpu_command.strip() or any(
                    marker in cpu_command for marker in ("\n", "\r", "\0")
                ):
                    add("MTR036", f"OCI image '{image_id}' CPU targets require a single-line cpuSmokeCommand.")
            elif cpu_command is not None:
                add("MTR036", f"OCI image '{image_id}' has a CPU smoke command but no CPU target.")
            if "nvidia" in target_accelerators:
                if not isinstance(nvidia_command, str) or not nvidia_command.strip() or any(
                    marker in nvidia_command for marker in ("\n", "\r", "\0")
                ):
                    add("MTR036", f"OCI image '{image_id}' NVIDIA targets require a single-line nvidiaSmokeCommand.")
                labels_are_valid = isinstance(labels, list) and all(
                    isinstance(label, str) and RUNNER_LABEL_PATTERN.fullmatch(label)
                    for label in labels
                )
                folded_labels = [label.casefold() for label in labels] if labels_are_valid else []
                if not labels_are_valid:
                    add("MTR036", f"OCI image '{image_id}' managedNvidiaRunnerLabels are invalid.")
                elif "self-hosted" not in folded_labels or "nvidia" not in folded_labels:
                    add(
                        "MTR036",
                        f"OCI image '{image_id}' NVIDIA gates require self-hosted and nvidia runner labels.",
                    )
                elif len(set(folded_labels)) != len(folded_labels):
                    add("MTR036", f"OCI image '{image_id}' managedNvidiaRunnerLabels contain duplicates.")
                else:
                    nvidia_runner_sets.add(frozenset(folded_labels))
            elif nvidia_command is not None or "managedNvidiaRunnerLabels" in release_gates:
                add("MTR036", f"OCI image '{image_id}' has NVIDIA release gates but no NVIDIA target.")
        if len(nvidia_runner_sets) > 1:
            add("MTR036", "All NVIDIA OCI release gates must use the same managed runner labels.")

    return findings


def main() -> int:
    args = parse_args()
    root = args.root.resolve()
    if not root.is_dir():
        print(f"Repository root does not exist: {root}", file=sys.stderr)
        return 2
    if args.package_version is not None and not is_valid_semver(args.package_version):
        print(f"Invalid --package-version: {args.package_version}", file=sys.stderr)
        return 2

    projects = find_packable_projects(root)

    missing = [project for project in projects if not project.is_file()]
    if missing:
        for project in missing:
            print(f"Project does not exist: {project}", file=sys.stderr)
        return 2
    if not projects:
        print("No packable third-party Monica project was found.", file=sys.stderr)
        return 2

    contract = load_repository_contract(root)
    results = validate_repository_contract(root, contract, args.package_version)
    results.extend(
        finding
        for project in projects
        for finding in validate_project(root, project, args.package_version)
    )
    failures = [finding for finding in results if finding.code != "OK"]

    if args.json:
        print(json.dumps({"status": "failed" if failures else "passed", "findings": [asdict(item) for item in results]}, indent=2))
    else:
        for finding in results:
            prefix = "PASS" if finding.code == "OK" else "ERROR"
            print(f"[{prefix}] {finding.code} {finding.path}: {finding.message}")
        print(f"\nValidated {len(projects)} package project(s): {len(failures)} error(s).")

    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
