#!/usr/bin/env python3
"""Focused fixtures for resource-aware Monica localization validation."""

import importlib.util
import json
import tempfile
import unittest
from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path
from unittest.mock import patch


SCRIPT_PATH = (
    Path(__file__).resolve().parents[1]
    / 'scripts'
    / 'validate_localization.py'
)
SPEC = importlib.util.spec_from_file_location('validate_localization', SCRIPT_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f'Could not load validator from {SCRIPT_PATH}')
VALIDATOR_MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR_MODULE)
LocalizationValidator = VALIDATOR_MODULE.LocalizationValidator
run_validation = VALIDATOR_MODULE.run_validation


class LocalizationValidatorTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary_directory.name) / 'repository'
        self.root.mkdir(parents=True)
        self._write('Fixture.slnx', '<Solution />')

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def test_overlapping_keys_do_not_hide_resource_specific_missing_or_unused_keys(self) -> None:
        self._add_project(
            'MixedPackage',
            resources={
                'ResourceA': {
                    'en-US': {'Shared': {'Title': 'A'}, 'OnlyA': {'Title': 'A only'}},
                    'zh-CN': {'Shared': {'Title': '甲'}, 'OnlyA': {'Title': '仅甲'}},
                },
                'ResourceB': {
                    'en-US': {'Shared': {'Title': 'B'}},
                    'zh-CN': {'Shared': {'Title': '乙'}},
                },
            },
            sources={
                'Pages/PageA.razor': (
                    '@inject IStringLocalizer<ResourceA> A\n'
                    '<h1>@A["Shared:Title"]</h1>\n'
                    '<p>@A["OnlyA:Title"]</p>\n'
                ),
                'Pages/PageB.razor': (
                    '@inject IStringLocalizer<ResourceB> B\n'
                    '<h1>@B["OnlyA:Title"]</h1>\n'
                ),
            },
        )

        validator, report = self._validate()
        resource_a = self._resource_path('MixedPackage', 'ResourceA')
        resource_b = self._resource_path('MixedPackage', 'ResourceB')

        self.assertIn((resource_b, 'OnlyA:Title'), validator.missing_keys)
        self.assertNotIn((resource_a, 'OnlyA:Title'), validator.missing_keys)
        self.assertIn((resource_b, 'Shared:Title'), validator.unused_keys)
        self.assertNotIn((resource_a, 'Shared:Title'), validator.unused_keys)
        self.assertEqual(1, report['summary']['missing_count'])

    def test_language_sync_is_scoped_to_each_resource(self) -> None:
        self._add_project(
            'MixedPackage',
            resources={
                'ResourceA': {
                    'en-US': {'Shared': {'Title': 'A'}},
                    'zh-CN': {'Shared': {'Title': '甲'}},
                },
                'ResourceB': {
                    'en-US': {'Shared': {'Title': 'B'}},
                    'zh-CN': {},
                },
            },
            sources={
                'Pages/Page.razor': (
                    '@inject IStringLocalizer<ResourceB> L\n'
                    '<h1>@L["Shared:Title"]</h1>\n'
                ),
            },
        )

        validator, _ = self._validate()
        resource_a = self._resource_path('MixedPackage', 'ResourceA')
        resource_b = self._resource_path('MixedPackage', 'ResourceB')

        self.assertNotIn((resource_a, 'Shared:Title'), validator.sync_issues)
        self.assertEqual(
            {'zh-CN': False, 'en-US': True},
            validator.sync_issues[(resource_b, 'Shared:Title')],
        )

    def test_navigation_keys_are_checked_against_explicit_registered_resource(self) -> None:
        self._add_project(
            'MixedPackage',
            resources={
                'ResourceA': {
                    'en-US': {
                        'Navigation': {'Page': 'Page', 'Category': 'Category'},
                    },
                    'zh-CN': {
                        'Navigation': {'Page': '页面', 'Category': '分类'},
                    },
                },
                'ResourceB': {
                    'en-US': {'Other': {'Key': 'Other'}},
                    'zh-CN': {'Other': {'Key': '其他'}},
                },
            },
            sources={
                'Modules/ModuleDashboard.cs': (
                    'public sealed class ModuleDashboard : IUIModule\n'
                    '{\n'
                    '    void Register(INavigationRegistryBuilder registry)\n'
                    '    {\n'
                    '        localization.AddResource<ResourceB>();\n'
                    '        var category = registry.RegisterLocalizedCategory<ResourceB>(\n'
                    '            "Acme.Monica.MixedPackage",\n'
                    '            "Navigation:Category");\n'
                    '        registry.RegisterLocalizedPage<Page, ResourceB>(\n'
                    '            "/page",\n'
                    '            "Navigation:Page",\n'
                    '            categoryId: category);\n'
                    '    }\n'
                    '    ModuleRegistration<ModuleLocalization, ModuleLocalizationOption> localization = default!;\n'
                    '}\n'
                ),
            },
            razor_sdk=False,
        )

        validator, _ = self._validate()
        resource_b = self._resource_path('MixedPackage', 'ResourceB')

        self.assertIn(
            (resource_b, 'Navigation:Page'),
            validator.module_resource_missing_keys,
        )
        self.assertIn(
            (resource_b, 'Navigation:Category'),
            validator.module_resource_missing_keys,
        )

    def test_navigation_descriptions_support_named_and_positional_resource_keys(self) -> None:
        navigation = {
            'Page': 'Page',
            'Category': 'Category',
            'CategoryNamed': 'Category explanation',
            'PagePositional': 'Page purpose',
            'CategoryPositional': 'Another category explanation',
            'PageNamed': 'Another page purpose',
        }
        self._add_project(
            'MixedPackage',
            resources={
                'ResourceA': {
                    'en-US': {'Navigation': navigation},
                    'zh-CN': {'Navigation': navigation},
                },
            },
            sources={
                'Modules/ModuleDashboard.cs': (
                    'public sealed class ModuleDashboard : IUIModule\n'
                    '{\n'
                    '    void Register(INavigationRegistryBuilder registry)\n'
                    '    {\n'
                    '        localization.AddResource<ResourceA>();\n'
                    '        registry.RegisterLocalizedCategory<ResourceA>(\n'
                    '            "Acme.Named", "Navigation:Category",\n'
                    '            descriptionKey: "Navigation:CategoryNamed");\n'
                    '        registry.RegisterLocalizedPage<Page, ResourceA>(\n'
                    '            "/positional", "Navigation:Page", null, null, true, 0, null,\n'
                    '            "Navigation:PagePositional");\n'
                    '        registry.RegisterLocalizedCategory<ResourceA>(\n'
                    '            "Acme.Positional", "Navigation:Category", 0, null, null,\n'
                    '            "Navigation:CategoryPositional");\n'
                    '        registry.RegisterLocalizedPage<Page, ResourceA>(\n'
                    '            "/named", "Navigation:Page",\n'
                    '            descriptionKey: "Navigation:PageNamed");\n'
                    '    }\n'
                    '    ModuleRegistration<ModuleLocalization, ModuleLocalizationOption> localization = default!;\n'
                    '}\n'
                ),
            },
            razor_sdk=False,
        )

        validator, report = self._validate()
        resource = self._resource_path('MixedPackage', 'ResourceA')

        for key in navigation:
            identity = (resource, f'Navigation:{key}')
            self.assertIn(identity, validator.module_resource_used_keys)
            self.assertNotIn(identity, validator.unused_keys)
        self.assertEqual(0, report['summary']['missing_count'])
        self.assertEqual([], report['resource_resolution_errors'])

    def test_navigation_descriptions_cannot_resolve_from_another_resource(self) -> None:
        self._add_project(
            'MixedPackage',
            resources={
                'ResourceA': {
                    'en-US': {'Navigation': {'PageDescription': 'Purpose', 'CategoryDescription': 'Capabilities'}},
                    'zh-CN': {'Navigation': {'PageDescription': '用途', 'CategoryDescription': '能力'}},
                },
                'ResourceB': {
                    'en-US': {'Navigation': {'Page': 'Page', 'Category': 'Category'}},
                    'zh-CN': {'Navigation': {'Page': '页面', 'Category': '分类'}},
                },
            },
            sources={
                'Modules/ModuleDashboard.cs': (
                    'public sealed class ModuleDashboard : IUIModule\n'
                    '{\n'
                    '    void Register(INavigationRegistryBuilder registry)\n'
                    '    {\n'
                    '        localization.AddResource<ResourceB>();\n'
                    '        registry.RegisterLocalizedCategory<ResourceB>(\n'
                    '            "Acme.Diagnostics", "Navigation:Category",\n'
                    '            descriptionKey: "Navigation:CategoryDescription");\n'
                    '        registry.RegisterLocalizedPage<Page, ResourceB>(\n'
                    '            "/page", "Navigation:Page",\n'
                    '            descriptionKey: "Navigation:PageDescription");\n'
                    '    }\n'
                    '    ModuleRegistration<ModuleLocalization, ModuleLocalizationOption> localization = default!;\n'
                    '}\n'
                ),
            },
            razor_sdk=False,
        )

        validator, _ = self._validate()
        resource = self._resource_path('MixedPackage', 'ResourceB')

        self.assertIn((resource, 'Navigation:PageDescription'), validator.module_resource_missing_keys)
        self.assertIn((resource, 'Navigation:CategoryDescription'), validator.module_resource_missing_keys)

    def test_localized_page_without_explicit_resource_is_rejected(self) -> None:
        self._add_project(
            'MixedPackage',
            resources={
                'ResourceA': {
                    'en-US': {'Navigation': {'Page': 'Page'}},
                    'zh-CN': {'Navigation': {'Page': '页面'}},
                },
            },
            sources={
                'Modules/ModuleDashboard.cs': (
                    'public sealed class ModuleDashboard : IUIModule\n'
                    '{\n'
                    '    void Register(INavigationRegistryBuilder registry)\n'
                    '    {\n'
                    '        registry.RegisterLocalizedPage<Page>("/page", "Navigation:Page");\n'
                    '    }\n'
                    '}\n'
                ),
            },
            razor_sdk=False,
        )

        _, report = self._validate()

        self.assertTrue(any(
            'must declare both TPage and TResource' in message
            for message in report['resource_resolution_errors']
        ))

    def test_navigation_resource_must_be_registered_through_add_resource(self) -> None:
        self._add_project(
            'MixedPackage',
            resources={
                'ResourceA': {
                    'en-US': {'Navigation': {'Page': 'Page'}},
                    'zh-CN': {'Navigation': {'Page': '页面'}},
                },
            },
            sources={
                'Modules/ModuleDashboard.cs': (
                    'public sealed class ModuleDashboard : IUIModule\n'
                    '{\n'
                    '    void Register(INavigationRegistryBuilder registry)\n'
                    '    {\n'
                    '        registry.RegisterLocalizedPage<Page, ResourceA>(\n'
                    '            "/page", "Navigation:Page");\n'
                    '    }\n'
                    '}\n'
                ),
            },
            razor_sdk=False,
        )

        _, report = self._validate()

        self.assertTrue(any(
            'is not registered through AddResource<ResourceA>()' in message
            for message in report['resource_resolution_errors']
        ))

    def test_legacy_localized_component_registration_is_rejected(self) -> None:
        self._add_project(
            'MixedPackage',
            resources={
                'ResourceA': {
                    'en-US': {'Navigation': {'Page': 'Page'}},
                    'zh-CN': {'Navigation': {'Page': '页面'}},
                },
            },
            sources={
                'Modules/ModuleDashboard.cs': (
                    'public sealed class ModuleDashboard : IUIModule\n'
                    '{\n'
                    '    void Register(INavigationRegistryBuilder registry)\n'
                    '    {\n'
                    '        registry.RegisterLocalizedComponent<Page, ResourceA>(\n'
                    '            "/page", "Navigation:Page");\n'
                    '    }\n'
                    '}\n'
                ),
            },
            razor_sdk=False,
        )

        _, report = self._validate()

        self.assertTrue(any(
            'Legacy RegisterLocalizedComponent usage is not allowed' in message
            for message in report['resource_resolution_errors']
        ))

    def test_mixed_ui_project_does_not_require_ui_project_suffix(self) -> None:
        project = self._add_project(
            'Acme.MixedPackage',
            resources={
                'MixedResource': {
                    'en-US': {'Page': {'Title': 'Title'}},
                    'zh-CN': {'Page': {'Title': '标题'}},
                },
            },
            sources={
                'Modules/ModuleDashboard.cs': (
                    'public sealed class ModuleDashboard : IUIModule;\n'
                ),
            },
            razor_sdk=False,
        )

        validator = LocalizationValidator(self.root, ['zh-CN', 'en-US'])

        self.assertIn(project.resolve(), validator.ui_projects)

    def test_exclusions_are_relative_to_explicit_root_inside_parent_tmp(self) -> None:
        outer = Path(self.temporary_directory.name)
        self.root = outer / '.tmp' / 'explicit-root'
        self.root.mkdir(parents=True)
        self._write('Fixture.slnx', '<Solution />')
        included = self._add_project(
            'IncludedProject',
            resources={
                'IncludedResource': {
                    'en-US': {'Page': {'Title': 'Title'}},
                    'zh-CN': {'Page': {'Title': '标题'}},
                },
            },
            sources={
                'Page.razor': (
                    '@inject IStringLocalizer<IncludedResource> L\n'
                    '@L["Page:Title"]\n'
                ),
            },
        )
        stale = self._add_project(
            '.tmp/StaleProject',
            resources={
                'StaleResource': {
                    'en-US': {'Page': {'Title': 'Stale'}},
                    'zh-CN': {'Page': {'Title': '过期'}},
                },
            },
            sources={'Page.razor': '@inject IStringLocalizer<StaleResource> L\n'},
        )

        validator = LocalizationValidator(self.root, ['zh-CN', 'en-US'])

        self.assertIn(included.resolve(), validator.ui_projects)
        self.assertNotIn(stale.resolve(), validator.ui_projects)

    def test_pending_input_copies_do_not_shadow_shared_resources(self) -> None:
        for prefix in ('Live', '.pending/baseline'):
            self._add_project(
                f'{prefix}/Shell',
                resources={'SharedResource': {
                    'en-US': {'Page': {'Title': 'Title'}},
                    'zh-CN': {'Page': {'Title': '标题'}},
                }},
                sources={'Page.razor': '@inject IStringLocalizer<SharedResource> L\n@L["Page:Title"]\n'},
            )

        validator, report = self._validate()

        self.assertEqual('PASSED', report['summary']['status'])
        self.assertEqual(1, len(validator.resources_by_name['SharedResource']))

    def test_empty_discovery_cannot_report_success(self) -> None:
        self._write(
            'EmptyUi/EmptyUi.csproj',
            '<Project Sdk="Microsoft.NET.Sdk.Razor" />',
        )

        _, report = self._validate()

        self.assertEqual('FAILED', report['summary']['status'])
        self.assertEqual(1, report['summary']['discovery_errors_count'])
        self.assertEqual(1, report['summary']['discovery_warnings_count'])

    def test_evidence_input_copies_do_not_shadow_resources_or_hide_live_design_bridge_errors(self) -> None:
        self._add_project(
            'Shell',
            resources={
                'SharedResource': {
                    'en-US': {'Navigation': {'Title': 'Navigation'}},
                    'zh-CN': {'Navigation': {'Title': '导航'}},
                },
            },
            sources={'Page.razor': '@inject IStringLocalizer<SharedResource> L\n@L["Navigation:Title"]\n'},
        )
        bridge = self._add_project(
            '.ui-design/review/bridge/ReviewHost',
            resources={
                'BridgeResource': {
                    'en-US': {'Page': {'Title': 'Review'}},
                    'zh-CN': {'Page': {'Title': '预览'}},
                },
            },
            sources={
                'Page.razor': (
                    '@inject IStringLocalizer<BridgeResource> B\n'
                    '@inject IStringLocalizer<SharedResource> L\n'
                    '@B["Page:Title"]\n'
                    '@L["Navigation:Title"]\n'
                    '@L["Navigation:Missing"]\n'
                ),
            },
        )
        archive = self._add_project(
            '.ui-design/review/.evidence/before/inputs/Shell',
            resources={
                'SharedResource': {
                    'en-US': {'Navigation': {'Title': 'Old title', 'Missing': 'Archived only'}},
                    'zh-CN': {'Navigation': {'Title': '旧标题', 'Missing': '仅归档'}},
                },
            },
            sources={'Page.razor': '@inject IStringLocalizer<SharedResource> L\n@L["Old:Missing"]\n'},
        )

        validator, report = self._validate()
        shared = self._resource_path('Shell', 'SharedResource')

        self.assertIn(bridge.resolve(), validator.ui_projects)
        self.assertNotIn(archive.resolve(), validator.ui_projects)
        self.assertEqual([shared], validator.resources_by_name['SharedResource'])
        self.assertEqual([], report['resource_resolution_errors'])
        self.assertEqual(0, report['summary']['unused_count'])
        self.assertEqual(1, report['summary']['missing_count'])
        self.assertEqual('FAILED', report['summary']['status'])
        self.assertEqual(
            [(str((bridge / 'Page.razor').relative_to(self.root)), 5)],
            validator.missing_keys[(shared, 'Navigation:Missing')],
        )

    def test_only_exact_evidence_directory_is_excluded_and_live_duplicate_resource_remains_unresolved(self) -> None:
        resources = {
            'SharedResource': {
                'en-US': {'Page': {'Title': 'Title'}},
                'zh-CN': {'Page': {'Title': '标题'}},
            },
        }
        self._add_project(
            'Shell', resources=resources,
            sources={'Page.razor': '@inject IStringLocalizer<SharedResource> L\n@L["Page:Title"]\n'},
        )
        similarly_named = self._add_project(
            '.ui-design/review/.evidence-copy/OtherLiveOwner', resources=resources,
            sources={'Page.razor': '@inject IStringLocalizer<SharedResource> L\n@L["Page:Title"]\n'},
        )
        self._add_project(
            '.ui-design/review/bridge/ReviewHost',
            resources={
                'BridgeResource': {
                    'en-US': {'Page': {'Title': 'Review'}},
                    'zh-CN': {'Page': {'Title': '预览'}},
                },
            },
            sources={
                'Page.razor': (
                    '@inject IStringLocalizer<BridgeResource> B\n'
                    '@inject IStringLocalizer<SharedResource> L\n'
                    '@B["Page:Title"]\n@L["Page:Title"]\n'
                ),
            },
        )

        validator, report = self._validate()

        self.assertIn(similarly_named.resolve(), validator.ui_projects)
        self.assertEqual(2, len(validator.resources_by_name['SharedResource']))
        self.assertIn(('<unresolved:SharedResource>', 'Page:Title'), validator.unresolved_used_keys)
        self.assertTrue(report['resource_resolution_errors'])
        self.assertEqual('FAILED', report['summary']['status'])

    def test_scoped_design_archives_cannot_pollute_repository_validation(self) -> None:
        for archive_kind in ('duplicate', 'malformed', 'unused'):
            with self.subTest(archive_kind=archive_kind):
                self._write(f'{archive_kind}/Fixture.slnx', '<Solution />')
                shell = self._add_project(
                    f'{archive_kind}/Shell',
                    resources={
                        'SharedResource': {
                            'en-US': {'Page': {'Title': 'Title'}},
                            'zh-CN': {'Page': {'Title': '标题'}},
                        },
                    },
                    sources={'Page.razor': '@inject IStringLocalizer<SharedResource> L\n@L["Page:Title"]\n'},
                )
                bridge = self._add_project(
                    f'{archive_kind}/.ui-design/review/bridge/ReviewHost',
                    resources={
                        'BridgeResource': {
                            'en-US': {'Page': {'Title': 'Review'}},
                            'zh-CN': {'Page': {'Title': '预览'}},
                        },
                    },
                    sources={
                        'Page.razor': (
                            '@inject IStringLocalizer<BridgeResource> B\n'
                            '@inject IStringLocalizer<SharedResource> L\n'
                            '@B["Page:Title"]\n@L["Page:Title"]\n'
                        ),
                    },
                )
                archive_name = f'{archive_kind}/.ui-design/review/evidence/bridge-preparation/Archived'
                resource = 'SharedResource' if archive_kind == 'duplicate' else 'ArchivedResource'
                key = 'Unused' if archive_kind == 'unused' else 'Title'
                archive = self._add_project(
                    archive_name,
                    resources={
                        resource: {
                            'en-US': {'Page': {key: 'Archived'}},
                            'zh-CN': {'Page': {key: '归档'}},
                        },
                    },
                    sources={
                        'Page.razor': (
                            f'@inject IStringLocalizer<{resource}> L\n'
                            + ('@L["Page:Title"]\n' if archive_kind != 'unused' else '')
                        ),
                    },
                )
                if archive_kind == 'malformed':
                    self._write(f'{archive_name}/Localization/{resource}/en-US.json', '{ invalid JSON')

                validator = LocalizationValidator(self.root / archive_kind, ['zh-CN', 'en-US'])
                report = run_validation(validator)
                shared = self._resource_path(f'{archive_kind}/Shell', 'SharedResource')

                self.assertEqual({shell.resolve(), bridge.resolve()}, set(validator.ui_projects))
                self.assertNotIn(archive.resolve(), validator.ui_projects)
                self.assertEqual([shared], validator.resources_by_name['SharedResource'])
                self.assertEqual([], report['json_errors'])
                self.assertEqual([], report['resource_resolution_errors'])
                self.assertEqual(0, report['summary']['missing_count'])
                self.assertEqual(0, report['summary']['unused_count'])
                self.assertEqual('PASSED', report['summary']['status'])

    def test_scoped_archive_rule_keeps_similar_names_and_production_evidence_live(self) -> None:
        projects = (
            ('Production/Evidence/Owner', 'ProductionResource'),
            ('.ui-design/review/evidence-copy/Owner', 'CopyResource'),
            ('.ui-design/review/.evidence-copy/Owner', 'DotCopyResource'),
            ('.ui-design/review/bridge/ReviewHost', 'BridgeResource'),
        )
        expected_projects = set()
        expected_missing = set()
        expected_unused = set()
        for project_name, resource in projects:
            project = self._add_project(
                project_name,
                resources={
                    resource: {
                        'en-US': {'Page': {'Title': 'Unused title'}},
                        'zh-CN': {'Page': {'Title': '未使用标题'}},
                    },
                },
                sources={
                    'Page.razor': f'@inject IStringLocalizer<{resource}> L\n@L["Page:Missing"]\n',
                },
            )
            expected_projects.add(project.resolve())
            resource_path = self._resource_path(project_name, resource)
            expected_missing.add((resource_path, 'Page:Missing'))
            expected_unused.add((resource_path, 'Page:Title'))

        validator, report = self._validate()

        self.assertEqual(expected_projects, set(validator.ui_projects))
        self.assertEqual(expected_missing, set(validator.missing_keys))
        self.assertEqual(expected_unused, validator.unused_keys)
        self.assertEqual('FAILED', report['summary']['status'])

    def test_explicit_project_root_below_design_archive_is_validated_by_cli(self) -> None:
        project_name = '.ui-design/review/evidence/bridge-preparation/ReviewHost'
        project = self._add_project(
            project_name,
            resources={
                'BridgeResource': {
                    'en-US': {'Page': {'Title': 'Unused title'}},
                    'zh-CN': {'Page': {'Title': '未使用标题'}},
                },
            },
            sources={'Page.razor': '@inject IStringLocalizer<BridgeResource> L\n@L["Page:Missing"]\n'},
        )
        nested_archive_name = f'{project_name}/.ui-design/review/evidence/NestedCopy'
        self._add_project(
            nested_archive_name,
            resources={'BridgeResource': {'en-US': {}, 'zh-CN': {}}},
            sources={'Page.razor': '@inject IStringLocalizer<BridgeResource> L\n'},
        )
        self._write(f'{nested_archive_name}/Localization/BridgeResource/en-US.json', '{ invalid JSON')
        output = StringIO()

        with patch.object(
            VALIDATOR_MODULE.sys, 'argv',
            ['validate_localization.py', '--root', str(project), '--strict', '--json'],
        ), redirect_stdout(output):
            with self.assertRaises(SystemExit) as exit_result:
                VALIDATOR_MODULE.main()

        report = json.loads(output.getvalue())
        self.assertEqual(1, exit_result.exception.code)
        self.assertEqual(1, report['summary']['projects_count'])
        self.assertEqual(1, report['summary']['resources_count'])
        self.assertEqual(0, report['summary']['json_errors_count'])
        self.assertEqual(1, report['summary']['missing_count'])
        self.assertEqual(1, report['summary']['unused_count'])
        self.assertEqual('Page:Missing', report['missing_keys'][0]['key'])
        self.assertEqual('FAILED', report['summary']['status'])

    def test_cli_root_without_solution_or_project_is_rejected(self) -> None:
        self._write('EmptyRoot/placeholder.txt', '')
        error = StringIO()

        with patch.object(
            VALIDATOR_MODULE.sys, 'argv',
            ['validate_localization.py', '--root', str(self.root / 'EmptyRoot'), '--json'],
        ), redirect_stdout(StringIO()), patch.object(VALIDATOR_MODULE.sys, 'stderr', error):
            with self.assertRaises(SystemExit) as exit_result:
                VALIDATOR_MODULE.main()

        self.assertEqual(1, exit_result.exception.code)
        self.assertIn('.csproj', error.getvalue())

    def _validate(self):
        validator = LocalizationValidator(self.root, ['zh-CN', 'en-US'])
        report = run_validation(validator)
        return validator, report

    def _add_project(
        self,
        name: str,
        resources: dict,
        sources: dict,
        razor_sdk: bool = True,
    ) -> Path:
        project_path = self.root / name
        sdk = 'Microsoft.NET.Sdk.Razor' if razor_sdk else 'Microsoft.NET.Sdk'
        self._write(f'{name}/{Path(name).name}.csproj', f'<Project Sdk="{sdk}" />')
        for resource_name, cultures in resources.items():
            for culture, content in cultures.items():
                self._write(
                    f'{name}/Localization/{resource_name}/{culture}.json',
                    json.dumps(content, ensure_ascii=False),
                )
        for relative_path, content in sources.items():
            self._write(f'{name}/{relative_path}', content)
        return project_path

    def _resource_path(self, project: str, resource: str) -> str:
        return str((self.root / project / 'Localization' / resource).resolve())

    def _write(self, relative_path: str, content: str) -> None:
        target = self.root / relative_path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding='utf-8')


if __name__ == '__main__':
    unittest.main()
