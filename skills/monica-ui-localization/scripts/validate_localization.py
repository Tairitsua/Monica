#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Validate Monica localization resources without merging resource identities.

The validator treats ``(resource marker, culture, key)`` as the definition
identity. This is important for mixed packages: two resources may intentionally
reuse a key, but a definition or usage in one resource must never satisfy the
other resource.
"""

import argparse
import json
import os
import re
import sys
from collections import defaultdict
from pathlib import Path
from typing import DefaultDict, Dict, Iterable, List, Optional, Set, Tuple

if sys.platform == 'win32':
    import io

    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
    sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8')


Location = Tuple[str, int]
ResourceKey = Tuple[str, str]
RegistrationLocation = Tuple[str, int, str]

EXCLUDED_RELATIVE_DIRECTORIES = {
    '.evidence',
    '.git',
    '.pending',
    '.tmp',
    '.vs',
    'artifacts',
    'bin',
    'node_modules',
    'obj',
    'testresults',
}
COMMENT_PATTERNS = (
    re.compile(r'@\*.*?\*@', re.DOTALL),
    re.compile(r'<!--.*?-->', re.DOTALL),
    re.compile(r'/\*.*?\*/', re.DOTALL),
    re.compile(r'(?m)^[ \t]*//.*$'),
)
LOCALIZATION_KEY_PATTERN = re.compile(
    r'^[A-Z][A-Za-z0-9]*(?::[A-Z][A-Za-z0-9]*)+$',
)
LOCALIZER_DECLARATION_PATTERN = re.compile(
    r'IStringLocalizer\s*<\s*'
    r'(?P<resource>(?:global::)?[A-Za-z_][A-Za-z0-9_]*(?:(?:::|\.)[A-Za-z_][A-Za-z0-9_]*)*)'
    r'\s*>\s*\??\s+(?P<variable>@?[A-Za-z_][A-Za-z0-9_]*)',
)
LOCALIZER_ACCESS_PATTERN = re.compile(
    r'(?P<variable>@?[A-Za-z_][A-Za-z0-9_]*)\s*\[\s*'
    r'(?P<prefix>\$@|@\$|\$|@)?"(?P<value>(?:\\.|[^"\\])*)"',
    re.DOTALL,
)
DIRECT_KEY_PATTERN = re.compile(
    r'["\'](?P<key>[A-Z][A-Za-z0-9]*(?::[A-Z][A-Za-z0-9]*)+)["\']',
)
INTERPOLATED_LITERAL_PATTERN = re.compile(
    r'(?P<prefix>\$@|@\$|\$)"(?P<value>(?:\\.|[^"\\])*)"',
    re.DOTALL,
)
INTERPOLATION_PATTERN = re.compile(r'\{[^{}]+\}')
TEST_PROJECT_PATTERN = re.compile(
    r'<IsTestProject(?:\s+[^>]*)?>\s*true\s*</IsTestProject>',
    re.IGNORECASE,
)
UI_MODULE_MARKER_PATTERN = re.compile(r'\bIUIModule\b')
ADD_RESOURCE_PATTERN = re.compile(
    r'\bAddResource\s*<\s*'
    r'(?P<resource>(?:global::)?[A-Za-z_][A-Za-z0-9_]*(?:(?:::|\.)[A-Za-z_][A-Za-z0-9_]*)*)'
    r'\s*>',
)


class Colors:
    """ANSI color codes for terminal output."""

    RED = '\033[91m'
    YELLOW = '\033[93m'
    GREEN = '\033[92m'
    BOLD = '\033[1m'
    END = '\033[0m'


class LocalizationValidator:
    """Validate source usages, registry usages, resources, and cultures."""

    def __init__(self, root_path: Path, languages: List[str]):
        self.root_path = root_path.resolve()
        self.languages = languages
        self.ui_projects = self._discover_ui_projects()
        self.localization_resources = self._discover_localization_resources()

        self.resource_paths: Set[str] = set()
        self.project_resources: DefaultDict[Path, List[str]] = defaultdict(list)
        self.resources_by_name: DefaultDict[str, List[str]] = defaultdict(list)
        for resource_dir in self.localization_resources:
            resource_path = str(resource_dir.resolve())
            project_path = resource_dir.parent.parent.resolve()
            self.resource_paths.add(resource_path)
            self.project_resources[project_path].append(resource_path)
            self.resources_by_name[resource_dir.name].append(resource_path)

        # Resource-aware source state.
        self.used_keys: DefaultDict[ResourceKey, List[Location]] = defaultdict(list)
        self.unresolved_used_keys: DefaultDict[ResourceKey, List[Location]] = defaultdict(list)
        self.ambiguous_used_keys: DefaultDict[
            Tuple[str, Tuple[str, ...]], List[Location]
        ] = defaultdict(list)
        self.all_defined_keys: Set[str] = set()
        self.resource_defined_keys: Dict[str, Dict[str, Set[str]]] = {}
        self.missing_keys: DefaultDict[ResourceKey, List[Location]] = defaultdict(list)
        self.unused_keys: Set[ResourceKey] = set()
        self.sync_issues: Dict[ResourceKey, Dict[str, bool]] = {}

        # Resource-aware registry state.
        self.module_resource_used_keys: DefaultDict[
            ResourceKey, List[RegistrationLocation]
        ] = defaultdict(list)
        self.module_resource_missing_keys: DefaultDict[
            ResourceKey, List[RegistrationLocation]
        ] = defaultdict(list)

        self.json_errors: List[Tuple[Path, str]] = []
        self.missing_language_files: Set[Tuple[str, str]] = set()
        self.resource_resolution_errors: Set[str] = set()
        self.discovery_errors: List[str] = []
        self.discovery_warnings: List[str] = []
        self._initialize_discovery_diagnostics()

    def _walk_files(self, base_path: Path, suffixes: Tuple[str, ...]) -> Iterable[Path]:
        """Yield source files while pruning generated and stale roots relative to the request root."""
        if not base_path.exists() or self._is_excluded_path(base_path):
            return

        for current_dir, directory_names, file_names in os.walk(base_path):
            current_path = Path(current_dir)
            directory_names[:] = [
                directory_name
                for directory_name in directory_names
                if directory_name.casefold() not in EXCLUDED_RELATIVE_DIRECTORIES
                and not self._is_excluded_path(current_path / directory_name)
            ]
            for file_name in file_names:
                if file_name.endswith(suffixes):
                    yield current_path / file_name

    def _is_excluded_path(self, path: Path) -> bool:
        """Check exclusions only below the requested root, not in its parent path."""
        try:
            relative_path = path.resolve().relative_to(self.root_path)
        except ValueError:
            return True

        relative_parts = tuple(part.casefold() for part in relative_path.parts)
        return (
            any(part in EXCLUDED_RELATIVE_DIRECTORIES for part in relative_parts)
            or (
                '.ui-design' in relative_parts
                and 'evidence' in relative_parts[relative_parts.index('.ui-design') + 1:]
            )
        )

    def _discover_ui_projects(self) -> List[Path]:
        """Discover UI/localized projects from content rather than one naming convention."""
        projects: Set[Path] = set()
        for project_file in self._walk_files(self.root_path, ('.csproj',)):
            try:
                project_text = project_file.read_text(encoding='utf-8-sig')
            except OSError:
                continue

            if TEST_PROJECT_PATTERN.search(project_text):
                continue

            project_path = project_file.parent.resolve()
            if self._has_ui_or_localization_evidence(project_file, project_text):
                projects.add(project_path)

        return sorted(projects)

    def _has_ui_or_localization_evidence(self, project_file: Path, project_text: str) -> bool:
        """Recognize suffix-free mixed UI projects and resource-owning projects."""
        project_path = project_file.parent
        if project_file.stem.endswith('.UI'):
            return True
        if 'Microsoft.NET.Sdk.Razor' in project_text:
            return True
        if any(self._walk_files(project_path, ('.razor',))):
            return True

        localization_dir = project_path / 'Localization'
        if any(self._walk_files(localization_dir, ('.json',))):
            return True

        modules_dir = project_path / 'Modules'
        for module_file in self._walk_files(modules_dir, ('.cs',)):
            if module_file.stem.endswith('UI'):
                return True
            try:
                module_text = module_file.read_text(encoding='utf-8')
            except OSError:
                continue
            if (
                'RegisterLocalizedPage<' in module_text
                or 'RegisterLocalizedComponent<' in module_text
                or UI_MODULE_MARKER_PATTERN.search(module_text)
            ):
                return True

        return False

    def _discover_localization_resources(self) -> List[Path]:
        """Discover direct ``Localization/<Resource>`` directories in eligible projects."""
        resources: List[Path] = []
        for project_path in self.ui_projects:
            localization_dir = project_path / 'Localization'
            if not localization_dir.exists():
                continue
            for resource_dir in localization_dir.iterdir():
                if self._is_excluded_path(resource_dir) or not resource_dir.is_dir():
                    continue
                if any(self._walk_files(resource_dir, ('.json',))):
                    resources.append(resource_dir.resolve())
        return sorted(resources)

    def _initialize_discovery_diagnostics(self) -> None:
        if not self.ui_projects:
            self.discovery_errors.append(
                'No eligible UI or localized project was found under the requested root. '
                'Razor SDK, Razor files, localization resources, localized registrations, '
                'or a final .UI module key are accepted evidence.'
            )
            return

        if not self.localization_resources:
            self.discovery_errors.append(
                'Eligible UI projects were found, but no Localization/<Resource>/*.json '
                'resource was found. The validator cannot prove localization correctness.'
            )

        has_source_files = any(
            any(self._walk_files(project_path, ('.razor', '.cs')))
            for project_path in self.ui_projects
        )
        if not has_source_files:
            self.discovery_warnings.append(
                'Localization resources were found, but no eligible Razor or C# source file '
                'was found to establish key usage.'
            )

    def scan_razor_files(self) -> None:
        """Extract resource-owned localization usages from Razor files."""
        self._scan_source_files('.razor')

    def scan_cs_files(self) -> None:
        """Extract resource-owned localization usages from C# files."""
        self._scan_source_files('.cs')

    def _scan_source_files(self, suffix: str) -> None:
        for project_path in self.ui_projects:
            for source_file in self._walk_files(project_path, (suffix,)):
                self._scan_source_file(project_path, source_file)

    def _scan_source_file(self, project_path: Path, source_file: Path) -> None:
        try:
            content = source_file.read_text(encoding='utf-8')
        except OSError as exc:
            print(f'Warning: Could not read {source_file}: {exc}', file=sys.stderr)
            return

        masked_content = self._mask_comments(content)
        relative_path = self._relative_path(source_file)
        declarations: Dict[str, Tuple[str, Optional[str]]] = {}
        for declaration in LOCALIZER_DECLARATION_PATTERN.finditer(masked_content):
            resource_type = self._simple_type_name(declaration.group('resource'))
            declarations[declaration.group('variable').lstrip('@')] = (
                resource_type,
                self._resolve_resource_type(project_path, resource_type),
            )

        declared_resources = {
            resource_path
            for _, resource_path in declarations.values()
            if resource_path is not None
        }
        inferred_resource = self._infer_file_resource(project_path, source_file)

        localizer_accesses = list(LOCALIZER_ACCESS_PATTERN.finditer(masked_content))
        for access in localizer_accesses:
            line_number = masked_content.count('\n', 0, access.start()) + 1
            location = (relative_path, line_number)
            variable = access.group('variable').lstrip('@')
            prefix = access.group('prefix') or ''
            value = access.group('value')

            declaration = declarations.get(variable)
            if declaration is not None:
                resource_type, resource_path = declaration
                if resource_path is None:
                    self._record_unresolved_resource_usage(
                        resource_type,
                        value,
                        prefix,
                        location,
                    )
                else:
                    self._record_resource_usage(resource_path, value, prefix, location)
                continue

            if len(declared_resources) == 1:
                self._record_resource_usage(
                    next(iter(declared_resources)),
                    value,
                    prefix,
                    location,
                )
                continue

            if inferred_resource is not None:
                self._record_resource_usage(
                    inferred_resource,
                    value,
                    prefix,
                    location,
                )
                continue

            self._record_untyped_usage(project_path, value, prefix, location)

        context_resources = set(declared_resources)
        if not context_resources and inferred_resource is not None:
            context_resources.add(inferred_resource)
        if not context_resources:
            context_resources.update(self.project_resources.get(project_path.resolve(), []))

        registration_ranges = [
            (start, end)
            for marker in (
                'RegisterLocalizedPage<',
                'RegisterLocalizedCategory<',
                'RegisterLocalizedComponent<',
            )
            for start, _, _, end in self._extract_generic_calls(masked_content, marker)
        ]
        ignored_ranges = registration_ranges + [access.span() for access in localizer_accesses]
        self._scan_indirect_key_literals(
            masked_content,
            relative_path,
            context_resources,
            ignored_ranges,
        )

    @staticmethod
    def _mask_comments(content: str) -> str:
        """Mask comments while preserving diagnostic line numbers."""
        masked_content = content
        for pattern in COMMENT_PATTERNS:
            masked_content = pattern.sub(
                lambda match: re.sub(r'[^\n]', ' ', match.group(0)),
                masked_content,
            )
        return masked_content

    def _record_resource_usage(
        self,
        resource_path: str,
        value: str,
        prefix: str,
        location: Location,
    ) -> None:
        for key in self._extract_literal_keys(resource_path, value, prefix):
            self.used_keys[(resource_path, key)].append(location)

    def _record_unresolved_resource_usage(
        self,
        resource_type: str,
        value: str,
        prefix: str,
        location: Location,
    ) -> None:
        keys = self._extract_literal_keys(None, value, prefix)
        if not keys:
            return
        resource_label = f'<unresolved:{resource_type}>'
        self.resource_resolution_errors.add(
            f"Could not resolve IStringLocalizer<{resource_type}> at {location[0]}:{location[1]} "
            'to a discovered Localization/<Resource> directory.'
        )
        for key in keys:
            self.unresolved_used_keys[(resource_label, key)].append(location)

    def _record_untyped_usage(
        self,
        project_path: Path,
        value: str,
        prefix: str,
        location: Location,
    ) -> None:
        project_resource_paths = self.project_resources.get(project_path.resolve(), [])
        if len(project_resource_paths) == 1:
            self._record_resource_usage(project_resource_paths[0], value, prefix, location)
            return

        candidate_keys = self._extract_literal_keys(None, value, prefix)
        for key in candidate_keys:
            candidates = [
                resource_path
                for resource_path in project_resource_paths
                if key in self._resource_key_union(resource_path)
            ]
            if not candidates and not project_resource_paths:
                candidates = [
                    resource_path
                    for resource_path in self.resource_defined_keys
                    if key in self._resource_key_union(resource_path)
                ]

            if len(candidates) == 1:
                self.used_keys[(candidates[0], key)].append(location)
            elif len(candidates) > 1:
                self.ambiguous_used_keys[(key, tuple(sorted(candidates)))].append(location)
            else:
                self.unresolved_used_keys[('<unresolved>', key)].append(location)

    def _scan_indirect_key_literals(
        self,
        content: str,
        relative_path: str,
        context_resources: Set[str],
        ignored_ranges: List[Tuple[int, int]],
    ) -> None:
        """Account for keys stored in constants, lookup tables, and helper arguments."""
        if not context_resources:
            return

        for match in DIRECT_KEY_PATTERN.finditer(content):
            if self._position_is_ignored(match.start(), ignored_ranges):
                continue
            location = (
                relative_path,
                content.count('\n', 0, match.start()) + 1,
            )
            self._record_context_key(match.group('key'), context_resources, location)

        for match in INTERPOLATED_LITERAL_PATTERN.finditer(content):
            if self._position_is_ignored(match.start(), ignored_ranges):
                continue
            location = (
                relative_path,
                content.count('\n', 0, match.start()) + 1,
            )
            matched_resources: DefaultDict[str, Set[str]] = defaultdict(set)
            for resource_path in context_resources:
                for key in self._extract_interpolated_keys(
                    match.group('value'),
                    self._resource_key_union(resource_path),
                ):
                    matched_resources[resource_path].add(key)
            for resource_path, keys in matched_resources.items():
                for key in keys:
                    self.used_keys[(resource_path, key)].append(location)

    def _record_context_key(
        self,
        key: str,
        context_resources: Set[str],
        location: Location,
    ) -> None:
        if len(context_resources) == 1:
            resource_path = next(iter(context_resources))
            self.used_keys[(resource_path, key)].append(location)
            return

        candidates = [
            resource_path
            for resource_path in context_resources
            if key in self._resource_key_union(resource_path)
        ]
        if len(candidates) == 1:
            self.used_keys[(candidates[0], key)].append(location)
        elif len(candidates) > 1:
            self.ambiguous_used_keys[(key, tuple(sorted(candidates)))].append(location)
        else:
            self.ambiguous_used_keys[(key, tuple(sorted(context_resources)))].append(location)

    @staticmethod
    def _position_is_ignored(
        position: int,
        ignored_ranges: List[Tuple[int, int]],
    ) -> bool:
        return any(start <= position < end for start, end in ignored_ranges)

    def _infer_file_resource(
        self,
        project_path: Path,
        source_file: Path,
    ) -> Optional[str]:
        """Infer resource ownership from an exact resource stem embedded in a file name."""
        source_stem = source_file.stem.lower()
        candidates: List[str] = []
        for resource_path in self.project_resources.get(project_path.resolve(), []):
            resource_name = Path(resource_path).name
            base_name = resource_name
            if base_name.endswith('UIResource'):
                base_name = base_name[:-len('UIResource')]
            elif base_name.endswith('Resource'):
                base_name = base_name[:-len('Resource')]
            if base_name and base_name.lower() in source_stem:
                candidates.append(resource_path)
        return candidates[0] if len(candidates) == 1 else None

    def _extract_literal_keys(
        self,
        resource_path: Optional[str],
        value: str,
        prefix: str,
    ) -> Set[str]:
        if '$' in prefix:
            candidate_keys = (
                self._resource_key_union(resource_path)
                if resource_path is not None
                else self.all_defined_keys
            )
            return self._extract_interpolated_keys(value, candidate_keys)
        return {value} if LOCALIZATION_KEY_PATTERN.fullmatch(value) else set()

    def _extract_interpolated_keys(self, template: str, candidate_keys: Set[str]) -> Set[str]:
        """Resolve a simple interpolated template within one resource's keys."""
        if not candidate_keys or '{' not in template or '}' not in template:
            return set()

        pattern = self._build_interpolated_key_pattern(template)
        if pattern is None:
            return set()
        return {key for key in candidate_keys if pattern.fullmatch(key)}

    @staticmethod
    def _build_interpolated_key_pattern(template: str) -> Optional[re.Pattern[str]]:
        if ':' not in template or not template[:1].isupper():
            return None

        parts: List[str] = []
        last_index = 0
        placeholder_count = 0
        for match in INTERPOLATION_PATTERN.finditer(template):
            parts.append(re.escape(template[last_index:match.start()]))
            parts.append(r'[A-Za-z0-9]+')
            last_index = match.end()
            placeholder_count += 1

        if placeholder_count == 0:
            return None
        parts.append(re.escape(template[last_index:]))
        try:
            return re.compile(f"^{''.join(parts)}$")
        except re.error:
            return None

    def scan_navigation_registration_keys(self) -> None:
        """Route page and category registration keys to their explicit resource markers."""
        for project_path in self.ui_projects:
            registered_resource_types = self._discover_registered_resource_types(project_path)
            for cs_file in self._walk_files(project_path, ('.cs',)):
                try:
                    content = cs_file.read_text(encoding='utf-8')
                except OSError as exc:
                    print(f'Warning: Could not read {cs_file}: {exc}', file=sys.stderr)
                    continue

                masked_content = self._mask_comments(content)
                relative_path = self._relative_path(cs_file)
                legacy_index = masked_content.find('RegisterLocalizedComponent<')
                if legacy_index >= 0:
                    line_number = masked_content.count('\n', 0, legacy_index) + 1
                    self.resource_resolution_errors.add(
                        'Legacy RegisterLocalizedComponent usage is not allowed at '
                        f'{relative_path}:{line_number}. Use RegisterLocalizedPage<TPage, TResource> '
                        'and a stable navigation category identifier.'
                    )

                page_calls = self._extract_generic_calls(masked_content, 'RegisterLocalizedPage<')
                for start_index, generic_arguments, argument_block, _ in page_calls:
                    line_number = masked_content.count('\n', 0, start_index) + 1
                    if len(generic_arguments) != 2:
                        self.resource_resolution_errors.add(
                            'RegisterLocalizedPage must declare both TPage and TResource at '
                            f'{relative_path}:{line_number}.'
                        )
                        continue

                    resource_type = self._simple_type_name(generic_arguments[1])
                    args = self._split_top_level_args(argument_block)
                    display_name = self._find_string_argument(args, 1, 'displayNameKey')

                    if display_name:
                        self._record_registration_key(
                            project_path,
                            resource_type,
                            display_name,
                            relative_path,
                            line_number,
                            'displayNameKey',
                            registered_resource_types,
                        )
                    description = self._find_string_argument(args, 7, 'descriptionKey')
                    if description:
                        self._record_registration_key(
                            project_path,
                            resource_type,
                            description,
                            relative_path,
                            line_number,
                            'descriptionKey',
                            registered_resource_types,
                        )

                category_calls = self._extract_generic_calls(
                    masked_content,
                    'RegisterLocalizedCategory<',
                )
                for start_index, generic_arguments, argument_block, _ in category_calls:
                    line_number = masked_content.count('\n', 0, start_index) + 1
                    if len(generic_arguments) != 1:
                        self.resource_resolution_errors.add(
                            'RegisterLocalizedCategory must declare exactly one TResource at '
                            f'{relative_path}:{line_number}.'
                        )
                        continue

                    resource_type = self._simple_type_name(generic_arguments[0])
                    args = self._split_top_level_args(argument_block)
                    display_name = self._find_string_argument(args, 1, 'displayNameKey')
                    if display_name:
                        self._record_registration_key(
                            project_path,
                            resource_type,
                            display_name,
                            relative_path,
                            line_number,
                            'categoryDisplayNameKey',
                            registered_resource_types,
                        )
                    description = self._find_string_argument(args, 5, 'descriptionKey')
                    if description:
                        self._record_registration_key(
                            project_path,
                            resource_type,
                            description,
                            relative_path,
                            line_number,
                            'categoryDescriptionKey',
                            registered_resource_types,
                        )

    def _discover_registered_resource_types(self, project_path: Path) -> Set[str]:
        registered: Set[str] = set()
        for source_file in self._walk_files(project_path, ('.cs',)):
            try:
                content = self._mask_comments(source_file.read_text(encoding='utf-8'))
            except OSError:
                continue
            registered.update(
                self._simple_type_name(match.group('resource'))
                for match in ADD_RESOURCE_PATTERN.finditer(content)
            )
        return registered

    def _record_registration_key(
        self,
        project_path: Path,
        resource_type: str,
        key: str,
        relative_path: str,
        line_number: int,
        role: str,
        registered_resource_types: Set[str],
    ) -> None:
        location = (relative_path, line_number, role)
        resource_path = self._resolve_resource_type(project_path, resource_type)
        if resource_path is None:
            resource_path = f'<unresolved:{resource_type}>'
            self.resource_resolution_errors.add(
                f'Could not resolve registration resource {resource_type} at '
                f'{relative_path}:{line_number}.'
            )
        if resource_type not in registered_resource_types:
            self.resource_resolution_errors.add(
                f'Navigation resource {resource_type} is not registered through '
                f'AddResource<{resource_type}>() in project {project_path.name}; '
                f'used at {relative_path}:{line_number}.'
            )
        self.module_resource_used_keys[(resource_path, key)].append(
            (relative_path, line_number, f'{resource_type}.{role}')
        )

    def _resolve_resource_type(self, project_path: Path, resource_type: str) -> Optional[str]:
        simple_name = self._simple_type_name(resource_type)
        project_candidate = (
            project_path.resolve() / 'Localization' / simple_name
        ).resolve()
        project_candidate_text = str(project_candidate)
        if project_candidate_text in self.resource_defined_keys or project_candidate_text in self.resource_paths:
            return project_candidate_text

        candidates = self.resources_by_name.get(simple_name, [])
        return candidates[0] if len(candidates) == 1 else None

    @staticmethod
    def _simple_type_name(type_name: str) -> str:
        return type_name.replace('global::', '').replace('::', '.').split('.')[-1].strip()

    def _extract_generic_calls(
        self,
        content: str,
        marker: str,
    ) -> List[Tuple[int, List[str], str, int]]:
        results: List[Tuple[int, List[str], str, int]] = []
        index = 0
        while True:
            start = content.find(marker, index)
            if start == -1:
                break

            generic_open = start + len(marker) - 1
            generic_close = self._find_matching_angle_bracket(content, generic_open)
            if generic_close == -1:
                break

            generic_arguments = self._split_top_level_args(
                content[generic_open + 1:generic_close]
            )
            open_parenthesis = content.find('(', generic_close)
            if open_parenthesis == -1:
                break
            close_parenthesis = self._find_matching_parenthesis(content, open_parenthesis)
            if close_parenthesis == -1:
                break

            results.append(
                (
                    start,
                    generic_arguments,
                    content[open_parenthesis + 1:close_parenthesis],
                    close_parenthesis + 1,
                )
            )
            index = close_parenthesis + 1
        return results

    @staticmethod
    def _find_matching_angle_bracket(content: str, start_index: int) -> int:
        depth = 0
        for index in range(start_index, len(content)):
            if content[index] == '<':
                depth += 1
            elif content[index] == '>':
                depth -= 1
                if depth == 0:
                    return index
        return -1

    @staticmethod
    def _find_matching_parenthesis(content: str, start_index: int) -> int:
        depth = 0
        in_string = False
        escape = False
        for index in range(start_index, len(content)):
            char = content[index]
            if in_string:
                if escape:
                    escape = False
                elif char == '\\':
                    escape = True
                elif char == '"':
                    in_string = False
                continue
            if char == '"':
                in_string = True
            elif char == '(':
                depth += 1
            elif char == ')':
                depth -= 1
                if depth == 0:
                    return index
        return -1

    @staticmethod
    def _split_top_level_args(argument_block: str) -> List[str]:
        args: List[str] = []
        current: List[str] = []
        in_string = False
        escape = False
        depths = {'(': 0, '{': 0, '[': 0, '<': 0}
        closing_to_opening = {')': '(', '}': '{', ']': '[', '>': '<'}

        for char in argument_block:
            if in_string:
                current.append(char)
                if escape:
                    escape = False
                elif char == '\\':
                    escape = True
                elif char == '"':
                    in_string = False
                continue
            if char == '"':
                in_string = True
                current.append(char)
                continue
            if char in depths:
                depths[char] += 1
            elif char in closing_to_opening:
                opening = closing_to_opening[char]
                depths[opening] = max(0, depths[opening] - 1)

            if char == ',' and all(depth == 0 for depth in depths.values()):
                args.append(''.join(current).strip())
                current = []
            else:
                current.append(char)

        tail = ''.join(current).strip()
        if tail:
            args.append(tail)
        return args

    @classmethod
    def _find_string_argument(
        cls,
        arguments: List[str],
        position: int,
        name: str,
    ) -> Optional[str]:
        for argument in arguments:
            if re.match(rf'^{re.escape(name)}\s*:', argument.strip()):
                return cls._extract_argument_string(argument, name)
        if position >= len(arguments):
            return None
        return cls._extract_argument_string(arguments[position])

    @staticmethod
    def _extract_argument_string(
        argument: str,
        expected_name: Optional[str] = None,
    ) -> Optional[str]:
        text = argument.strip()
        if expected_name:
            named_match = re.match(rf'^{re.escape(expected_name)}\s*:\s*(.*)$', text, re.DOTALL)
            if not named_match:
                return None
            text = named_match.group(1).strip()
        if text.startswith('"') and text.endswith('"') and len(text) >= 2:
            return text[1:-1]
        return None

    def validate_json_integrity(self) -> bool:
        """Validate JSON syntax and leaf structure for every requested culture file."""
        self.json_errors.clear()
        self.missing_language_files.clear()
        for resource_dir in self.localization_resources:
            resource_path = str(resource_dir.resolve())
            for language in self.languages:
                json_file = resource_dir / f'{language}.json'
                if not json_file.exists():
                    self.missing_language_files.add((resource_path, language))
                    continue
                try:
                    data = json.loads(json_file.read_text(encoding='utf-8'))
                    if not isinstance(data, dict):
                        self.json_errors.append(
                            (json_file, 'Root element must be a JSON object, not an array or primitive')
                        )
                        continue
                    if self._has_empty_keys(data):
                        self.json_errors.append((json_file, 'Contains empty string keys'))
                    invalid_values = self._find_non_string_values(data)
                    if invalid_values:
                        preview = ', '.join(invalid_values[:5])
                        if len(invalid_values) > 5:
                            preview += f' (and {len(invalid_values) - 5} more)'
                        self.json_errors.append(
                            (json_file, f'Contains non-string leaf values: {preview}')
                        )
                except json.JSONDecodeError as exc:
                    self.json_errors.append((json_file, f'JSON syntax error: {exc}'))
                except OSError as exc:
                    self.json_errors.append((json_file, f'Error reading file: {exc}'))
        return not self.json_errors

    def _has_empty_keys(self, obj: dict) -> bool:
        return any(
            key == '' or (isinstance(value, dict) and self._has_empty_keys(value))
            for key, value in obj.items()
        )

    def _find_non_string_values(self, obj: dict, path: str = '') -> List[str]:
        invalid_values: List[str] = []
        for key, value in obj.items():
            full_path = f'{path}:{key}' if path else key
            if isinstance(value, dict):
                invalid_values.extend(self._find_non_string_values(value, full_path))
            elif not isinstance(value, str):
                invalid_values.append(f'{full_path} ({type(value).__name__})')
        return invalid_values

    def load_json_keys(self) -> None:
        """Load definitions by resource and culture, retaining global views only for discovery."""
        self.all_defined_keys = set()
        self.resource_defined_keys = {}

        for resource_dir in self.localization_resources:
            resource_path = str(resource_dir.resolve())
            culture_keys: Dict[str, Set[str]] = {}
            for language in self.languages:
                json_file = resource_dir / f'{language}.json'
                keys: Set[str] = set()
                if json_file.exists():
                    data = json.loads(json_file.read_text(encoding='utf-8'))
                    if isinstance(data, dict):
                        keys = self._flatten_json(data)
                culture_keys[language] = keys
                self.all_defined_keys.update(keys)
            self.resource_defined_keys[resource_path] = culture_keys

    def _flatten_json(self, obj: dict, parent_key: str = '') -> Set[str]:
        keys: Set[str] = set()
        for key, value in obj.items():
            full_key = f'{parent_key}:{key}' if parent_key else key
            if isinstance(value, dict):
                keys.update(self._flatten_json(value, full_key))
            else:
                keys.add(full_key)
        return keys

    def _resource_key_union(self, resource_path: Optional[str]) -> Set[str]:
        if resource_path is None:
            return set()
        culture_keys = self.resource_defined_keys.get(resource_path, {})
        return set().union(*culture_keys.values()) if culture_keys else set()

    def validate_bidirectional(self) -> None:
        """Validate source usages and compute unused keys per resource."""
        self.missing_keys.clear()
        for (resource_path, key), locations in self.used_keys.items():
            if key not in self._resource_key_union(resource_path):
                self.missing_keys[(resource_path, key)].extend(locations)
        for resource_key, locations in self.unresolved_used_keys.items():
            self.missing_keys[resource_key].extend(locations)

        used_resource_keys = set(self.used_keys)
        used_resource_keys.update(
            resource_key
            for resource_key in self.module_resource_used_keys
            if resource_key[0] in self.resource_defined_keys
        )

        self.unused_keys = {
            (resource_path, key)
            for resource_path in self.resource_defined_keys
            for key in self._resource_key_union(resource_path)
            if (resource_path, key) not in used_resource_keys
        }

    def validate_language_sync(self) -> None:
        """Compare requested cultures independently for every resource."""
        self.sync_issues.clear()
        if len(self.languages) < 2:
            return
        for resource_path in self.resource_defined_keys:
            for key in self._resource_key_union(resource_path):
                presence = {
                    language: key in self.resource_defined_keys[resource_path].get(language, set())
                    for language in self.languages
                }
                if not all(presence.values()):
                    self.sync_issues[(resource_path, key)] = presence

    def validate_navigation_registration_usage(self) -> None:
        """Ensure registration keys exist in the registration's exact resource."""
        self.module_resource_missing_keys.clear()
        for resource_key, locations in self.module_resource_used_keys.items():
            resource_path, key = resource_key
            if key not in self._resource_key_union(resource_path):
                self.module_resource_missing_keys[resource_key].extend(locations)

    def generate_report(
        self,
        output_format: str = 'console',
        summary_only: bool = False,
    ) -> Dict:
        """Generate a resource-explicit report suitable for humans or automation."""
        hard_error = any(
            (
                self.discovery_errors,
                self.json_errors,
                self.missing_language_files,
                self.missing_keys,
                self.sync_issues,
                self.module_resource_missing_keys,
                self.resource_resolution_errors,
                self.ambiguous_used_keys,
            )
        )
        report = {
            'discovery_errors': list(self.discovery_errors),
            'discovery_warnings': list(self.discovery_warnings),
            'json_errors': [
                {'file': str(path), 'message': message}
                for path, message in self.json_errors
            ],
            'missing_language_files': [
                {'resource': self._resource_label(resource), 'language': language}
                for resource, language in sorted(self.missing_language_files)
            ],
            'missing_keys': [
                {
                    'resource': self._resource_label(resource),
                    'key': key,
                    'locations': self._locations(locations),
                }
                for (resource, key), locations in sorted(self.missing_keys.items())
            ],
            'ambiguous_used_keys': [
                {
                    'key': key,
                    'candidate_resources': [self._resource_label(path) for path in candidates],
                    'locations': self._locations(locations),
                }
                for (key, candidates), locations in sorted(self.ambiguous_used_keys.items())
            ],
            'unused_keys': [
                {
                    'resource': self._resource_label(resource),
                    'key': key,
                    'defined_in': [
                        language
                        for language in self.languages
                        if key in self.resource_defined_keys[resource].get(language, set())
                    ],
                }
                for resource, key in sorted(self.unused_keys)
            ],
            'sync_issues': [
                {
                    'resource': self._resource_label(resource),
                    'key': key,
                    'presence': presence,
                }
                for (resource, key), presence in sorted(self.sync_issues.items())
            ],
            'module_resource_missing_keys': [
                {
                    'resource': self._resource_label(resource),
                    'key': key,
                    'locations': self._registration_locations(locations),
                }
                for (resource, key), locations in sorted(
                    self.module_resource_missing_keys.items()
                )
            ],
            'resource_resolution_errors': sorted(self.resource_resolution_errors),
            'summary': {
                'projects_count': len(self.ui_projects),
                'resources_count': len(self.localization_resources),
                'total_keys_defined': sum(
                    len(self._resource_key_union(resource))
                    for resource in self.resource_defined_keys
                ),
                'total_keys_used': len(self.used_keys)
                + len(self.module_resource_used_keys),
                'discovery_errors_count': len(self.discovery_errors),
                'discovery_warnings_count': len(self.discovery_warnings),
                'json_errors_count': len(self.json_errors),
                'missing_language_files_count': len(self.missing_language_files),
                'missing_count': len(self.missing_keys),
                'ambiguous_count': len(self.ambiguous_used_keys),
                'unused_count': len(self.unused_keys),
                'sync_issues_count': len(self.sync_issues),
                'module_resource_missing_count': len(self.module_resource_missing_keys),
                'resource_resolution_errors_count': len(self.resource_resolution_errors),
                'status': 'FAILED' if hard_error else 'PASSED',
            },
        }

        if output_format == 'json':
            print(json.dumps(report, indent=2, ensure_ascii=False))
        elif output_format == 'console':
            self._print_console_report(report, summary_only)
        return report

    @staticmethod
    def _locations(locations: List[Location]) -> List[Dict[str, object]]:
        return [{'file': file_path, 'line': line_number} for file_path, line_number in locations]

    @staticmethod
    def _registration_locations(
        locations: List[RegistrationLocation],
    ) -> List[Dict[str, object]]:
        return [
            {'file': file_path, 'line': line_number, 'role': role}
            for file_path, line_number, role in locations
        ]

    def _resource_label(self, resource_path: str) -> str:
        if resource_path.startswith('<'):
            return resource_path
        try:
            return str(Path(resource_path).resolve().relative_to(self.root_path))
        except ValueError:
            return resource_path

    def _relative_path(self, path: Path) -> str:
        try:
            return str(path.resolve().relative_to(self.root_path))
        except ValueError:
            return str(path)

    def _print_console_report(self, report: Dict, summary_only: bool) -> None:
        print(f'\n{Colors.BOLD}=== Localization Validation Report ==={Colors.END}\n')
        if not summary_only:
            self._print_diagnostics()

        summary = report['summary']
        print(f'{Colors.BOLD}Summary:{Colors.END}')
        print(f"  Projects discovered: {summary['projects_count']}")
        print(f"  Resources discovered: {summary['resources_count']}")
        print(f"  Resource-qualified keys defined: {summary['total_keys_defined']}")
        print(f"  Resource-qualified keys used: {summary['total_keys_used']}")
        self._print_count('Discovery errors', summary['discovery_errors_count'], True)
        self._print_count('Discovery warnings', summary['discovery_warnings_count'], False)
        self._print_count('JSON integrity errors', summary['json_errors_count'], True)
        self._print_count('Missing culture files', summary['missing_language_files_count'], True)
        self._print_count('Missing source keys', summary['missing_count'], True)
        self._print_count('Ambiguous source keys', summary['ambiguous_count'], True)
        self._print_count('Invalid navigation resource keys', summary['module_resource_missing_count'], True)
        self._print_count(
            'Resource resolution errors',
            summary['resource_resolution_errors_count'],
            True,
        )
        self._print_count('Unused keys', summary['unused_count'], False)
        self._print_count('Language sync issues', summary['sync_issues_count'], True)
        status_color = Colors.GREEN if summary['status'] == 'PASSED' else Colors.RED
        print(f"  Status: {status_color}{Colors.BOLD}{summary['status']}{Colors.END}\n")

    @staticmethod
    def _print_count(label: str, count: int, is_error: bool) -> None:
        color = Colors.RED if count and is_error else Colors.YELLOW if count else Colors.GREEN
        print(f'  {label}: {color}{count}{Colors.END}')

    def _print_diagnostics(self) -> None:
        for message in self.discovery_errors:
            print(f'{Colors.RED}[ERROR] Discovery: {message}{Colors.END}')
        for message in self.discovery_warnings:
            print(f'{Colors.YELLOW}[WARNING] Discovery: {message}{Colors.END}')
        for json_file, message in self.json_errors:
            print(f'{Colors.RED}[ERROR] {json_file}: {message}{Colors.END}')
        for resource, language in sorted(self.missing_language_files):
            print(
                f'{Colors.RED}[ERROR] Missing {language}.json in '
                f'{self._resource_label(resource)}{Colors.END}'
            )
        for (resource, key), locations in sorted(self.missing_keys.items()):
            print(
                f'{Colors.RED}[ERROR] Missing key {key} in '
                f'{self._resource_label(resource)}{Colors.END}'
            )
            for file_path, line_number in locations:
                print(f'  Used in: {file_path}:{line_number}')
        for (key, candidates), locations in sorted(self.ambiguous_used_keys.items()):
            labels = ', '.join(self._resource_label(path) for path in candidates)
            print(f'{Colors.RED}[ERROR] Ambiguous key {key}; candidates: {labels}{Colors.END}')
            for file_path, line_number in locations:
                print(f'  Used in: {file_path}:{line_number}')
        for (resource, key), locations in sorted(self.module_resource_missing_keys.items()):
            print(
                f'{Colors.RED}[ERROR] Missing registry key {key} in '
                f'{self._resource_label(resource)}{Colors.END}'
            )
            for file_path, line_number, role in locations:
                print(f'  Used in: {file_path}:{line_number} ({role})')
        for message in sorted(self.resource_resolution_errors):
            print(f'{Colors.RED}[ERROR] {message}{Colors.END}')
        for resource, key in sorted(self.unused_keys):
            print(
                f'{Colors.YELLOW}[WARNING] Unused key {key} in '
                f'{self._resource_label(resource)}{Colors.END}'
            )
        for (resource, key), presence in sorted(self.sync_issues.items()):
            missing = ', '.join(
                language for language, exists in presence.items() if not exists
            )
            print(
                f'{Colors.RED}[ERROR] Key {key} in {self._resource_label(resource)} '
                f'is missing from: {missing}{Colors.END}'
            )
        if any(
            (
                self.discovery_errors,
                self.discovery_warnings,
                self.json_errors,
                self.missing_language_files,
                self.missing_keys,
                self.ambiguous_used_keys,
                self.module_resource_missing_keys,
                self.resource_resolution_errors,
                self.unused_keys,
                self.sync_issues,
            )
        ):
            print()


def run_validation(
    validator: LocalizationValidator,
    output_format: str = 'none',
    summary_only: bool = False,
) -> Dict:
    """Run the full validation pipeline; shared by the CLI and fixture tests."""
    if validator.validate_json_integrity():
        validator.load_json_keys()
        validator.scan_razor_files()
        validator.scan_cs_files()
        validator.scan_navigation_registration_keys()
        validator.validate_bidirectional()
        validator.validate_language_sync()
        validator.validate_navigation_registration_usage()
    return validator.generate_report(output_format, summary_only)


def main() -> None:
    parser = argparse.ArgumentParser(description='Validate Monica localization keys')
    parser.add_argument('--strict', action='store_true', help='Treat warnings as errors')
    parser.add_argument('--json', action='store_true', help='Output JSON')
    parser.add_argument('--summary', action='store_true', help='Show summary only')
    parser.add_argument(
        '--languages',
        type=str,
        default='zh-CN,en-US',
        help='Comma-separated cultures',
    )
    parser.add_argument('--root', type=str, default='.', help='Repository or project root path')
    args = parser.parse_args()

    root_path = Path(args.root).resolve()
    languages = [language.strip() for language in args.languages.split(',') if language.strip()]
    if not root_path.exists():
        print(f'Error: Root path does not exist: {root_path}', file=sys.stderr)
        sys.exit(1)
    if not any(any(root_path.glob(pattern)) for pattern in ('*.slnx', '*.sln', '*.csproj')):
        print(
            f'{Colors.RED}Error: Use a root containing a .slnx, .sln, or .csproj file.{Colors.END}',
            file=sys.stderr,
        )
        sys.exit(1)
    if not languages:
        print('Error: At least one culture must be provided.', file=sys.stderr)
        sys.exit(1)

    validator = LocalizationValidator(root_path, languages)
    try:
        output_format = 'json' if args.json else 'console'
        report = run_validation(validator, output_format, args.summary)
        summary = report['summary']
        if summary['status'] == 'FAILED':
            sys.exit(1)
        if args.strict and (
            summary['unused_count'] > 0 or summary['discovery_warnings_count'] > 0
        ):
            sys.exit(2)
        sys.exit(0)
    except KeyboardInterrupt:
        print('\nValidation interrupted by user', file=sys.stderr)
        sys.exit(130)
    except Exception as exc:
        print(f'Error: {exc}', file=sys.stderr)
        sys.exit(1)


if __name__ == '__main__':
    main()
