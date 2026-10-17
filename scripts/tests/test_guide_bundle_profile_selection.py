from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("guide_bundle_profiles", ROOT / "scripts" / "build_monica_guide_bundle.py")
assert SPEC is not None and SPEC.loader is not None
BUNDLE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BUNDLE)


class GuideBundleProfileSelectionTests(unittest.TestCase):
    def test_projection_preserves_canonical_profile_conditions(self) -> None:
        catalog = json.loads((ROOT / ".monica" / "agent-skill-catalog.json").read_text(encoding="utf-8"))

        projected = BUNDLE.workspace_projection(catalog)

        self.assertEqual(
            projected["profileSelections"],
            {name: profile["skills"] for name, profile in catalog["profiles"].items()},
        )
        conditions = projected["profileSelections"]["application"]["conditional"]
        microservice = next(condition for condition in conditions if condition["capability"] == "microservice")
        self.assertIn("monica-application-microservice", microservice["skills"])
        application = projected["profileSelections"]["application"]
        self.assertNotIn("monica-infra-ui", application["required"])
        ui = next(condition for condition in conditions if condition["capability"] == "ui")
        self.assertEqual(ui["skills"], ["monica-application-ui", "monica-application-ui-development"])

    def test_projection_keeps_required_dependencies_separate_from_recommendations(self) -> None:
        catalog = {
            "skills": {
                "base": {"role": "router", "dependencies": {"required": ["dependency"], "recommended": ["recommended"]}},
                "dependency": {"role": "capability", "dependencies": {"required": [], "recommended": []}},
                "recommended": {"role": "capability", "dependencies": {"required": [], "recommended": []}},
            },
            "profiles": {
                "application": {"skills": {"required": ["base"], "recommended": [], "conditional": []}},
            },
        }
        with tempfile.TemporaryDirectory(prefix="guide-profile-projection-") as temporary:
            skills = Path(temporary)
            for name in catalog["skills"]:
                root = skills / name
                root.mkdir()
                (root / "SKILL.md").write_text(f"---\nname: {name}\n---\n", encoding="utf-8")

            projected, _ = BUNDLE.project_catalog(catalog, skills)

        base = next(entry for entry in projected["skills"] if entry["name"] == "base")
        self.assertEqual(base["dependencies"], ["dependency"])
        self.assertEqual(projected["profileSelections"]["application"]["required"], ["base"])


if __name__ == "__main__":
    unittest.main()
