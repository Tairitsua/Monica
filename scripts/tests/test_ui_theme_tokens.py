"""Regression checks for source color-role validation (no UI rendering)."""
import importlib.util
from pathlib import Path
import tempfile
import unittest


REPOSITORY = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "ui_theme_tokens", REPOSITORY / "scripts" / "validate_ui_theme_tokens.py"
)
VALIDATOR = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VALIDATOR)


class ThemeRoleValidationTests(unittest.TestCase):
    def scan(self, source):
        with tempfile.TemporaryDirectory(prefix="theme-role-check-", dir=REPOSITORY) as directory:
            path = Path(directory) / "component.css"
            path.write_text(source, encoding="utf-8")
            return VALIDATOR.scan_file(path)

    def test_whitespace_property_is_not_a_named_white_color(self):
        self.assertEqual([], self.scan(".reader { background: var(--surface); white-space: pre; }"))

    def test_named_colors_still_require_theme_roles(self):
        for declaration in ["fill:white!important", "background: black", "border-color: WHITE"]:
            with self.subTest(declaration=declaration):
                self.assertEqual(1, len(self.scan(f".reader {{ {declaration}; }}")))

    def test_color_role_identifiers_are_not_color_literals(self):
        self.assertEqual([], self.scan(".reader { color: var(--caption-black); background: var(--paper-white); }"))



if __name__ == "__main__":
    unittest.main()
