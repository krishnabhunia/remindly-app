import re
import unittest

from changelog import CHANGELOG, FALLBACK, notes, plain, section

TEXT = """# Changelog

## 2.17.0 — short beta names (05-Nov-2026)

### New
- **Info window** with `F1`.

| SrNo. | x |
|---|---|

## 2.11.0 — first

- First.
"""


class ChangelogTests(unittest.TestCase):
    def test_section_stops_at_the_next_version(self):
        self.assertEqual("- First.", section(TEXT, "2.11.0"))
        self.assertIn("Info window", section(TEXT, "2.17.0"))
        self.assertNotIn("First", section(TEXT, "2.17.0"))
        self.assertIsNone(section(TEXT, "2.1.0"))
        self.assertIsNone(section(TEXT, "2.17"))

    def test_notes_are_plain_text_and_fall_back_when_missing(self):
        self.assertEqual("New\n• Info window with F1.", notes("2.17.0", TEXT))
        self.assertEqual(FALLBACK, notes("9.9.9", TEXT))
        self.assertEqual("• a", plain("* a\n| t |"))

    def test_long_sections_are_trimmed_at_a_line_end(self):
        text = "## 1.0.0\n" + "\n".join(f"- line {i} " + "x" * 40 for i in range(40))
        result = notes("1.0.0", text, limit=200)
        self.assertLessEqual(len(result), 202)
        self.assertTrue(result.endswith("…"))

    def test_the_repository_changelogs_newest_version_has_notes(self):
        text = CHANGELOG.read_text(encoding="utf-8")
        versions = [tuple(map(int, v.split("."))) for v in re.findall(r"(?m)^## (\d+\.\d+\.\d+)\b", text)]
        newest = ".".join(map(str, max(versions)))
        self.assertNotEqual(FALLBACK, notes(newest, text))


if __name__ == "__main__":
    unittest.main()
