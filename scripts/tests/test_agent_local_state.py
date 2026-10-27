from __future__ import annotations

import io
import subprocess
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

from scripts import sync_agent_skills as sync
from scripts import validate_agent_skills as validator


class LocalAgentStateTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.git("init", "--quiet")
        self.write(".gitignore", ".agents/\n.claude/\n")
        # Keep the fixture independent of the developer's global ignore rules.
        self.write(".git/test-excludes", "")
        self.git("config", "core.excludesFile", str(self.root / ".git/test-excludes"))

    def git(self, *arguments: str) -> str:
        return subprocess.run(
            ["git", "-C", str(self.root), *arguments],
            capture_output=True,
            text=True,
            check=True,
        ).stdout

    def write(self, relative: str, content: str) -> Path:
        path = self.root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        return path

    def errors(self) -> list[str]:
        validation = validator.Validation()
        validator.validate_local_agent_state(validation, self.root)
        return validation.errors

    def test_fresh_checkout_and_generated_local_skills_both_pass(self) -> None:
        self.assertFalse((self.root / ".agents").exists())
        self.assertFalse((self.root / ".claude").exists())
        self.assertEqual([], self.errors())

        canonical = self.write("skills/monica-example/SKILL.md", "canonical\n").parent
        roots = {"monica-example": canonical}
        expected = sync.build_manifest(roots)
        projections = tuple(self.root / name / "skills" for name in validator.LOCAL_AGENT_ROOTS)
        settings = self.write(".claude/settings.json", "{}\n")
        command = self.write(".agents/commands/personal.md", "personal\n")
        with redirect_stdout(io.StringIO()):
            sync.write_projections(roots, expected, set(), projection_paths=projections)
            self.assertTrue(sync.check_projections(expected, set(roots), set(), projection_paths=projections))

        self.git("add", "--all")
        self.assertEqual("", self.git("ls-files", "--", ".agents", ".claude"))
        self.assertEqual([], self.errors())
        self.assertEqual("{}\n", settings.read_text(encoding="utf-8"))
        self.assertEqual("personal\n", command.read_text(encoding="utf-8"))

    def test_force_added_or_committed_agent_files_fail_until_removed_from_index(self) -> None:
        self.git("config", "user.name", "Local state test")
        self.git("config", "user.email", "local-state@example.invalid")
        self.git("config", "commit.gpgSign", "false")
        self.git("config", "core.hooksPath", str(self.root / ".git/no-hooks"))
        for root_name in validator.LOCAL_AGENT_ROOTS:
            for suffix in ("skills/monica-example/SKILL.md", "commands/personal.md", "settings.json"):
                relative = f"{root_name}/{suffix}"
                with self.subTest(path=relative):
                    path = self.write(relative, "canonical bytes\n")
                    self.git("add", "--force", "--", relative)
                    self.assertTrue(any(relative in error and "must not be tracked" in error for error in self.errors()))
                    self.git("commit", "--quiet", "-m", "Restore agent file from another branch")
                    self.assertTrue(any(relative in error and "must not be tracked" in error for error in self.errors()))
                    self.git("rm", "--cached", "--", relative)
                    self.assertEqual("canonical bytes\n", path.read_text(encoding="utf-8"))
                    self.assertEqual([], self.errors())

    def test_removed_partial_or_negated_ignore_rules_fail(self) -> None:
        for root_name in validator.LOCAL_AGENT_ROOTS:
            other = next(name for name in validator.LOCAL_AGENT_ROOTS if name != root_name)
            for rule in ("", f"{root_name}/skills/", f"{root_name}/*", f"{root_name}/\n!{root_name}/"):
                with self.subTest(root=root_name, rule=rule):
                    self.write(".gitignore", f"{other}/\n{rule}\n")
                    self.assertTrue(any(f"entire {root_name}/" in error for error in self.errors()))

    def test_machine_local_exclusions_cannot_replace_repository_rules(self) -> None:
        self.write(".gitignore", "")
        for exclusion in (".git/info/exclude", ".git/test-excludes"):
            with self.subTest(exclusion=exclusion):
                self.write(exclusion, ".agents/\n.claude/\n")
                self.assertEqual(2, len(self.errors()))
                self.write(exclusion, "")

    def test_anchored_whole_root_rules_are_supported(self) -> None:
        self.write(".gitignore", "/.agents/\n/.claude/\n")
        self.assertEqual([], self.errors())

    def test_git_failure_is_reported_instead_of_skipping_the_gate(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            validation = validator.Validation()
            validator.validate_local_agent_state(validation, Path(directory))
            self.assertTrue(any("Cannot verify local agent state" in error for error in validation.errors))

    def test_main_includes_the_local_state_gate(self) -> None:
        self.write(".agents/skills/monica-example/SKILL.md", "canonical bytes\n")
        self.git("add", "--force", "--", ".agents")
        with mock.patch.object(validator, "REPOSITORY_ROOT", self.root), mock.patch.object(
            validator, "parse_args", return_value=SimpleNamespace(json=True)
        ), mock.patch.object(validator, "load_json", return_value={}), mock.patch.object(
            validator, "validate_schema"
        ), mock.patch.object(validator, "validate_catalog"), mock.patch.object(
            validator, "validate_index"
        ), redirect_stdout(io.StringIO()) as output:
            self.assertEqual(1, validator.main())
        self.assertIn("must not be tracked", output.getvalue())


if __name__ == "__main__":
    unittest.main()
