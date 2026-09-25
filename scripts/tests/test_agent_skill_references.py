"""Reference navigation follows actual Markdown links and preserves task boundaries."""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from scripts.agent_skill_references import (
    MAX_INFRA_ENTRYPOINT_LINES,
    MAX_INFRA_ENTRYPOINT_WORDS,
    validate_entrypoint_budget,
    validate_reference_navigation,
)


class SkillReferenceNavigationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.workspace = tempfile.TemporaryDirectory()
        self.addCleanup(self.workspace.cleanup)
        self.root = Path(self.workspace.name).resolve()
        self.skill = self.root / "skills" / "monica-infra-example"

    def write(self, path: str, content: str) -> Path:
        destination = self.skill / path
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_text(content, encoding="utf-8", newline="")
        return destination

    def validate(self) -> list[str]:
        return validate_reference_navigation(self.skill, self.root)

    def test_entrypoint_budget_accepts_limits_and_excludes_frontmatter(self) -> None:
        lines = ["word"] * (MAX_INFRA_ENTRYPOINT_LINES - 1)
        lines.append(" ".join(["word"] * (MAX_INFRA_ENTRYPOINT_WORDS - len(lines))))
        entrypoint = self.write(
            "SKILL.md",
            "\ufeff---\r\nname: example\r\ndescription: Select work.\r\n---\r\n\r\n"
            + "\r\n".join(lines) + "\r\n\r\n",
        )
        self.assertEqual([], validate_entrypoint_budget(entrypoint))

    def test_entrypoint_budget_reports_excess_body_lines(self) -> None:
        entrypoint = self.write("SKILL.md", "\n".join(["word"] * (MAX_INFRA_ENTRYPOINT_LINES + 1)))
        errors = validate_entrypoint_budget(entrypoint)
        self.assertEqual(1, len(errors))
        self.assertIn("body lines", errors[0])

    def test_long_paragraph_cannot_evade_the_entrypoint_budget(self) -> None:
        entrypoint = self.write("SKILL.md", " ".join(["word"] * (MAX_INFRA_ENTRYPOINT_WORDS + 1)))
        errors = validate_entrypoint_budget(entrypoint)
        self.assertEqual(1, len(errors))
        self.assertIn("body words", errors[0])

    def test_task_reference_is_uncapped_and_needs_no_overview(self) -> None:
        entrypoint = self.write("SKILL.md", "Read [the operation](references/operation.md).")
        self.write("references/operation.md", "Detailed contract.\n\n" * MAX_INFRA_ENTRYPOINT_WORDS)
        self.assertEqual([], validate_entrypoint_budget(entrypoint))
        self.assertEqual([], self.validate())

    def test_supports_direct_and_transitive_routes_with_cycles(self) -> None:
        self.write("SKILL.md", "| Task | Read |\n| --- | --- |\n| Setup | [Overview](references/overview.md) |\n")
        self.write("references/overview.md", "For a write operation, read [Transactions](transactions.md).")
        self.write("references/transactions.md", "Prerequisites: [Overview](overview.md).")
        self.assertEqual([], self.validate())

    def test_an_isolated_reference_cycle_is_not_discoverable(self) -> None:
        self.write("SKILL.md", "# Skill\n")
        self.write("references/a.md", "[B](b.md)")
        self.write("references/b.md", "[A](a.md)")
        errors = self.validate()
        self.assertEqual(2, len(errors))
        self.assertTrue(all("unreachable" in error for error in errors), errors)

    def test_every_published_markdown_extension_requires_a_route(self) -> None:
        self.write("SKILL.md", "# Skill\n")
        self.write("references/setup.markdown", "# Setup\n")
        self.write("references/advanced.MD", "# Advanced\n")
        errors = self.validate()
        self.assertEqual(2, len(errors))
        self.assertTrue(all("unreachable" in error for error in errors), errors)
        self.write("SKILL.md", "[Setup](references/setup.markdown)\n\n[Advanced](references/advanced.MD)")
        self.assertEqual([], self.validate())

    def test_renaming_a_topic_requires_updating_its_incoming_route(self) -> None:
        self.write("SKILL.md", "[Transaction guide](references/transactions.md)")
        self.write("references/transactions.md", "# Transactions\n").rename(self.skill / "references/operations.md")
        errors = self.validate()
        self.assertTrue(any("missing Markdown target" in error for error in errors), errors)
        self.assertTrue(any("operations.md" in error and "unreachable" in error for error in errors), errors)

    def test_code_examples_and_unused_link_definitions_do_not_create_routes(self) -> None:
        self.write("SKILL.md", """---
name: example
description: '[Overview](references/overview.md)'
---
# Skill
`[Overview](references/overview.md)`

```markdown
[Overview](references/overview.md)
[Missing](missing.md)
```

[unused]: references/overview.md
""")
        self.write("references/overview.md", "# Overview\n")
        errors = self.validate()
        self.assertEqual(1, len(errors))
        self.assertIn("unreachable", errors[0])

    def test_reference_style_links_resolve_encoded_paths_and_ignore_fragments(self) -> None:
        self.write("SKILL.md", "Read [the operation][operation].\n\n[operation]: references/write%20operation.md?mode=local#commit\n")
        self.write("references/write operation.md", "# Commit\n")
        self.assertEqual([], self.validate())

    def test_markdown_parsing_handles_parentheses_and_formatted_labels(self) -> None:
        self.write("SKILL.md", "Read [**advanced** setup](references/setup(advanced).md).")
        self.write("references/setup(advanced).md", "# Setup\n")
        self.assertEqual([], self.validate())

    def test_cross_skill_links_are_checked_but_do_not_hide_local_orphans(self) -> None:
        self.write("SKILL.md", "[Another skill](../monica-infra-other/SKILL.md)")
        self.write("../monica-infra-other/SKILL.md", "[Return](../monica-infra-example/references/orphan.md)")
        self.write("references/orphan.md", "# Orphan\n")
        errors = self.validate()
        self.assertEqual(1, len(errors))
        self.assertIn("unreachable", errors[0])

    def test_missing_cross_skill_topic_is_reported(self) -> None:
        self.write("SKILL.md", "[Outbox](../monica-infra-persistence/references/transactional-events.md)")
        errors = self.validate()
        self.assertEqual(1, len(errors))
        self.assertIn("missing Markdown target", errors[0])

    def test_external_urls_anchors_and_non_markdown_assets_are_not_topic_routes(self) -> None:
        self.write("SKILL.md", """# Skill
[External](https://example.test/guide.md)
[Protocol-relative](//example.test/guide.md)
[Section](#skill)
[Tool](scripts/tool.py)
![Diagram](assets/diagram.svg)
""")
        self.assertEqual([], self.validate())

    def test_link_cannot_escape_the_repository_or_use_an_absolute_path(self) -> None:
        for href in ("../../../outside.md", "/references/overview.md", "C:/missing.md", "D:%5Creferences%5Coverview.md"):
            with self.subTest(href=href):
                self.write("SKILL.md", f"[Guide]({href})")
                errors = self.validate()
                self.assertEqual(1, len(errors))
                self.assertIn("must stay relative", errors[0])


if __name__ == "__main__":
    unittest.main()
