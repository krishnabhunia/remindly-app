import unittest

from release_notes import implementations, release_text


class NotesTests(unittest.TestCase):
    def test_actual_features_are_kept_but_ci_chatter_is_not(self):
        body = """### Windows UI
- **Google sign-in:** Opens the browser and explains account setup.
- **Cloud status:** Displays last synchronization and error details.
### Validation
- ✅ Test suite passed with 500 checks and a long run identifier.
"""
        changes = implementations(body)
        self.assertEqual(2, len(changes))
        self.assertIn("Google sign-in", release_text("2.17.0-beta001", [{"number": 21, "body": body}]))
        self.assertNotIn("Test suite passed", str(changes))

    def test_empty_generic_release_notes_block_publication(self):
        with self.assertRaisesRegex(ValueError, "no feature-level"):
            release_text("2.17.0", [{"number": 21, "body": "Build artifact uploaded."}])


if __name__ == "__main__":
    unittest.main()
