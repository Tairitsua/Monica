import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


CHECKER = Path(__file__).resolve().parents[1] / "check_publication.py"


class PublicationTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.repo = Path(directory.name)
        self.git("init", "--quiet", "--initial-branch=dev")
        self.git("config", "user.name", "Publication Tests")
        self.git("config", "user.email", "publication@example.invalid")
        self.commit_file("README.md")
        self.base = self.git("rev-parse", "HEAD")

    def git(self, *args):
        return subprocess.run(
            ["git", *args], cwd=self.repo, check=True, capture_output=True, text=True,
        ).stdout.strip()

    def commit_file(self, name):
        (self.repo / name).write_text("Synthetic publication test data\n", encoding="utf-8")
        self.git("add", "--", name)
        self.git("commit", "-m", "Add synthetic test file")

    def guard(self, *args, stdin=None):
        return subprocess.run(
            [sys.executable, str(CHECKER), *args], cwd=self.repo,
            input=stdin, capture_output=True, text=True,
        )

    def test_blocks_document_added_and_deleted_in_the_same_push(self):
        name = "test-briefing.DoCx"
        self.commit_file(name)
        self.git("rm", "--", name)
        self.git("commit", "-m", "Remove synthetic test file")
        tip = self.git("rev-parse", "HEAD")

        update = f"refs/heads/dev {tip} refs/heads/dev {self.base}\n"
        result = self.guard("--pre-push", "origin", stdin=update)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("Office documents and PDFs", result.stderr)
        self.assertNotIn(name, result.stderr)

    def test_accepts_removal_of_a_document_already_in_published_history(self):
        name = "old-test-briefing.pdf"
        self.commit_file(name)
        published = self.git("rev-parse", "HEAD")
        self.git("rm", "--", name)
        self.git("commit", "-m", "Remove previously published synthetic test file")

        result = self.guard("--base", published)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_blocks_unrelated_history_even_when_the_merge_tree_is_identical(self):
        self.git("switch", "--orphan", "other")
        self.commit_file("separate-history.md")
        self.git("switch", "dev")
        self.git("merge", "--allow-unrelated-histories", "-s", "ours", "other", "-m", "Merge synthetic branch")
        self.assertEqual(self.git("rev-parse", f"{self.base}^{{tree}}"), self.git("rev-parse", "HEAD^{tree}"))

        result = self.guard("--base", self.base)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("unrelated Git history", result.stderr)

    def test_accepts_an_annotated_tag_based_on_published_dev(self):
        self.git("update-ref", "refs/remotes/origin/dev", self.base)
        self.commit_file("public-changes.md")
        self.git("tag", "-a", "v-test", "-m", "Test release")
        tag = self.git("rev-parse", "v-test")

        update = f"refs/tags/v-test {tag} refs/tags/v-test {'0' * len(tag)}\n"
        result = self.guard("--pre-push", "origin", stdin=update)

        self.assertEqual(0, result.returncode, result.stderr)
