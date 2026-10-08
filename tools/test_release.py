import tempfile
from pathlib import Path
import unittest
import zipfile
from release import android_code, change_kind, expected_entries, package, plan, verify_package


class ReleaseTests(unittest.TestCase):
    def test_pr_build_is_beta_and_main_is_stable(self):
        pr = {"number": 7, "pull_request": {"title": "feat: unified releases", "labels": []}}
        beta = plan(pr, ["v2.11", "win-v2.11.0", "v9.0.0-beta.1.1"], "", "2.12.0", 15)
        stable = plan({}, ["v2.11"], "feat: unified releases", "2.12.0", 16)
        self.assertEqual("2.12.0-beta.7.15", beta["version"])
        self.assertEqual("2.12.0", stable["version"])
        self.assertGreater(stable["code"], beta["code"])
        self.assertGreater(beta["code"], 2011000)

    def test_feature_fix_and_breaking_versions_are_automatic(self):
        for message, expected in [("fix: update checksum", "2.12.1"), ("feat: new lists", "2.13.0"), ("feat!: schema replacement", "3.0.0")]:
            self.assertEqual(expected, plan({}, ["v2.12.0"], message, "2.12.0", 5)["version"])
        self.assertEqual("major", change_kind({"pull_request": {"labels": [{"name": "release:major"}]}}, "fix: something"))

    def test_versions_advance_across_channels(self):
        self.assertLess(android_code((2, 12, 0), 20), android_code((2, 12, 0), 21))
        self.assertLess(android_code((2, 12, 0)), android_code((2, 12, 1), 1))
        with self.assertRaises(ValueError):
            android_code((100, 0, 0))

    def test_rerun_does_not_create_another_stable_version(self):
        result = plan({}, ["v2.12.0"], "feat: unified releases", "2.12.0", 20, already_released="v2.12.0")
        self.assertEqual("2.12.0", result["version"])

    def test_zip_opens_directly_to_requested_folders_and_empty_mac(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parent) as tmp:
            root = Path(tmp)
            self.assertEqual(Path(__file__).resolve().parent, root.resolve().parent)
            binaries = [root / name for name in ("app.apk", "portable.exe", "setup.exe")]
            for source in binaries:
                source.write_bytes(b"test build")
            archive = package("2.12.0-beta.7.1", *binaries, root / "dist")
            with zipfile.ZipFile(archive) as z:
                self.assertEqual(expected_entries("2.12.0-beta.7.1"), set(z.namelist()))
                self.assertTrue(z.getinfo("macOS/").is_dir())
                self.assertEqual(b"test build", z.read("portable/Remindly_2.12.0-beta.7.1.exe"))
            with zipfile.ZipFile(archive, "a") as z:
                z.writestr("macOS/fake.dmg", "not a Mac app")
            with self.assertRaises(ValueError):
                verify_package(archive, "2.12.0-beta.7.1")


if __name__ == "__main__":
    unittest.main()
