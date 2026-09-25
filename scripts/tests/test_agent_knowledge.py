"""Publication preserves canonical bytes and rejects ambiguous content ownership."""

import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location("knowledge", Path(__file__).parents[1] / "export_agent_knowledge.py")
knowledge = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(knowledge)


class KnowledgePublicationTests(unittest.TestCase):
    def setUp(self):
        self.workspace = tempfile.TemporaryDirectory()
        self.addCleanup(self.workspace.cleanup)
        self.root = Path(self.workspace.name)
        self.catalog = {"skills": {}}

    def add_skill(self, name, modules):
        directory = self.root / "skills" / name
        directory.mkdir(parents=True)
        content = f"---\nname: {name}\ndescription: Perform a useful task.\n---\n\n# Example\n\nUse the verified contract.\n"
        (directory / "SKILL.md").write_bytes(content.encode("utf-8"))
        self.catalog["skills"][name] = {"path": f"skills/{name}", "role": "capability", "publication": {"modules": modules}}
        return directory, content

    def git(self, *arguments):
        return subprocess.run(
            ["git", "-C", str(self.root), *arguments],
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()

    def tagged_source(self):
        self.add_skill("monica-infra-example", ["example"])
        catalog_path = self.root / ".monica" / "agent-skill-catalog.json"
        catalog_path.parent.mkdir()
        catalog_path.write_text(json.dumps(self.catalog), encoding="utf-8")
        (self.root / "Directory.Build.props").write_text(
            "<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>",
            encoding="utf-8",
        )
        self.git("init", "-q")
        self.git("config", "user.name", "Knowledge Test")
        self.git("config", "user.email", "knowledge@example.test")
        self.git("config", "core.autocrlf", "false")
        self.git("add", ".")
        self.git("commit", "-qm", "release source")
        self.git("tag", "v1.2.3")

    def test_preserves_entrypoint_and_reference_content_with_digests(self):
        directory, original = self.add_skill("monica-infra-example", ["example"])
        (directory / "references").mkdir()
        (directory / "references/contract.md").write_text("# Contract\n\nMeaningful details.\n", encoding="utf-8")
        first = knowledge.collect_skills(self.root, self.catalog)
        self.assertEqual(first, knowledge.collect_skills(self.root, self.catalog))
        entry = next(file for file in first[0]["files"] if file["path"] == "SKILL.md")
        self.assertEqual(original, entry["content"])
        self.assertEqual(knowledge.digest(original.encode()), entry["digest"])
        self.assertEqual(2, len(first[0]["files"]))

    def test_rejects_multiple_authoritative_owners_for_one_module(self):
        self.add_skill("monica-infra-a", ["example"])
        self.add_skill("monica-infra-b", ["example"])
        with self.assertRaisesRegex(ValueError, "Multiple skill owners"):
            knowledge.collect_skills(self.root, self.catalog)

    def test_publishes_every_supported_markdown_reference_extension(self):
        directory, _ = self.add_skill("monica-infra-example", ["example"])
        references = directory / "references"
        references.mkdir()
        for name in ("setup.md", "operations.markdown", "advanced.MD"):
            (references / name).write_text(f"# {name}\n", encoding="utf-8")
        paths = {file["path"] for file in knowledge.collect_skills(self.root, self.catalog)[0]["files"]}
        self.assertEqual({"SKILL.md", "references/setup.md", "references/operations.markdown", "references/advanced.MD"}, paths)

    def test_preserves_bom_and_windows_line_endings_in_resource_digests(self):
        directory, _ = self.add_skill("monica-infra-example", ["example"])
        content = b"\xef\xbb\xbf# Reference\r\n\r\nExact source bytes.\r\n"
        (directory / "reference.md").write_bytes(content)
        resource = next(file for file in knowledge.collect_skills(self.root, self.catalog)[0]["files"] if file["path"] == "reference.md")
        self.assertEqual(content, resource["content"].encode("utf-8"))
        self.assertEqual(knowledge.digest(content), resource["digest"])

    def test_rejects_catalog_path_escaping_the_source(self):
        self.catalog["skills"]["escape"] = {"path": "../other", "role": "capability"}
        with self.assertRaisesRegex(ValueError, "Invalid canonical"):
            knowledge.collect_skills(self.root, self.catalog)

    def test_release_export_identifies_clean_matching_tag(self):
        self.tagged_source()

        bundle = knowledge.export(self.root, "v1.2.3")

        self.assertEqual("1.2.3", bundle["frameworkVersion"])
        self.assertEqual("release", bundle["source"]["mode"])
        self.assertEqual("v1.2.3", bundle["source"]["ref"])
        self.assertEqual(self.git("rev-parse", "HEAD"), bundle["source"]["commit"])
        self.assertFalse(bundle["source"]["dirty"])
        self.assertEqual(knowledge.digest(knowledge.json_bytes(bundle["skills"])), bundle["contentDigest"])

    def test_release_export_rejects_dirty_tagged_checkout(self):
        self.tagged_source()
        skill = self.root / "skills" / "monica-infra-example" / "SKILL.md"
        skill.write_text(skill.read_text(encoding="utf-8") + "\nLocal edit.\n", encoding="utf-8")

        with self.assertRaisesRegex(ValueError, "clean, exact tagged"):
            knowledge.export(self.root, "v1.2.3")

    def test_release_export_rejects_mismatched_version_or_tag_commit(self):
        self.tagged_source()
        with self.assertRaisesRegex(ValueError, "match Directory.Build.props"):
            knowledge.export(self.root, "v1.2.4")

        skill = self.root / "skills" / "monica-infra-example" / "SKILL.md"
        skill.write_text(skill.read_text(encoding="utf-8") + "\nCommitted edit.\n", encoding="utf-8")
        self.git("add", ".")
        self.git("commit", "-qm", "post-tag change")
        with self.assertRaisesRegex(ValueError, "clean, exact tagged"):
            knowledge.export(self.root, "v1.2.3")


if __name__ == "__main__":
    unittest.main()
