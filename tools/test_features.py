from pathlib import Path
import unittest

from features import HEADER, PATH, parse, problems

GOOD = f"""Intro line.

{HEADER}
|---|---|---|---|
| 1 | Tasks | Quick add. | 2.11.0 |
| 2 | Info window | Shows this build. | 2.17.0 |

Text after the table is ignored.
"""


class FeaturesTests(unittest.TestCase):
    def test_the_repository_file_parses_and_lists_the_info_window(self):
        text = (Path(__file__).resolve().parent.parent / PATH).read_text(encoding="utf-8")
        rows = parse(text)
        self.assertEqual(list(range(1, len(rows) + 1)), [r[0] for r in rows])
        self.assertIn("Info window", [r[1] for r in rows])

    def test_good_table_passes(self):
        self.assertEqual([(1, "Tasks", "Quick add.", "2.11.0"), (2, "Info window", "Shows this build.", "2.17.0")], parse(GOOD))
        self.assertEqual([], problems(GOOD, "2.17.0"))

    def test_bad_tables_fail_with_plain_messages(self):
        cases = {
            GOOD.replace("| SrNo. |", "| No. |"): "exactly one table",
            GOOD.replace("| 2 | Info", "| 3 | Info"): "SrNo. must run 1..n",
            GOOD.replace("| 2.17.0 |", "| 2.17 |"): "must be a version",
            GOOD.replace("| Quick add. |", "|"): "4 cells",
            GOOD.replace("|---|---|---|---|", ""): "divider",
        }
        for text, message in cases.items():
            found = problems(text, "2.17.0")
            self.assertEqual(1, len(found), text)
            self.assertIn(message, found[0])

    def test_since_cannot_be_later_than_the_version_built(self):
        self.assertIn("later than the version being built", problems(GOOD, "2.16.0")[0])
        self.assertEqual([], problems(GOOD, "10.0.0"))

    def test_feature_prs_must_change_the_file(self):
        self.assertIn("must also add or update", problems(GOOD, "2.17.0", "minor", {"tools/release.py"})[0])
        self.assertIn("must also add or update", problems(GOOD, "3.0.0", "major", set())[0])
        self.assertEqual([], problems(GOOD, "2.17.0", "minor", {PATH, "tools/release.py"}))
        self.assertEqual([], problems(GOOD, "2.17.1", "patch", {"tools/release.py"}))
        self.assertEqual([], problems(GOOD, "2.17.0", "minor", None))  # stable builds on main


if __name__ == "__main__":
    unittest.main()
