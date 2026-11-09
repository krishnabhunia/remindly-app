import tempfile
from pathlib import Path
import unittest
import zipfile
from release import android_code, change_kind, expected_entries, next_beta_serial, package, plan, verify_package


class ReleaseTests(unittest.TestCase):
    def test_pr_build_is_beta_and_main_is_stable(self):
        pr = {"number": 7, "pull_request": {"title": "feat: unified releases", "labels": []}}
        beta = plan(pr, ["v2.11", "win-v2.11.0", "v9.0.0-beta.1.1", "v9.0.0-beta001"], "", "2.12.0", 15, beta_number=7)
        stable = plan({}, ["v2.11"], "feat: unified releases", "2.12.0", 16)
        self.assertEqual("2.12.0-beta007", beta["version"])
        self.assertEqual("v2.12.0-beta007", beta["tag"])
        self.assertEqual("Remindly_2.12.0-beta007.zip", beta["zip"])
        self.assertEqual(("beta", "007", "minor"), (beta["channel"], beta["beta"], beta["kind"]))
        # Without a reserved number (local dry run) the next free short tag is used.
        self.assertEqual("2.12.0-beta001", plan(pr, ["v2.11", "v9.0.0-beta001"], "", "2.12.0", 15)["version"])
        self.assertEqual("2.12.0", stable["version"])
        self.assertGreater(stable["code"], beta["code"])
        self.assertGreater(beta["code"], 2011000)

    def test_android_version_code_follows_the_reserved_beta_number(self):
        pr = {"number": 7, "pull_request": {"title": "feat: x", "labels": []}}
        first = plan(pr, ["v2.17.0"], "", "2.17.0", 31, beta_number=1)
        rerun = plan(pr, ["v2.17.0"], "", "2.17.0", 32, beta_number=1)   # same commit, same number
        later = plan(pr, ["v2.17.0"], "", "2.17.0", 33, beta_number=2)
        self.assertEqual(android_code((2, 18, 0), 1), first["code"])
        self.assertEqual(first["code"], rerun["code"])
        self.assertLess(first["code"], later["code"])
        self.assertLess(later["code"], plan({}, ["v2.17.0"], "feat: x", "2.17.0", 34)["code"])

    def test_feature_fix_and_breaking_versions_are_automatic(self):
        for message, expected in [("fix: update checksum", "2.12.1"), ("feat: new lists", "2.13.0"), ("feat!: schema replacement", "3.0.0")]:
            self.assertEqual(expected, plan({}, ["v2.12.0"], message, "2.12.0", 5)["version"])
        self.assertEqual("major", change_kind({"pull_request": {"labels": [{"name": "release:major"}]}}, "fix: something"))

    def test_squash_and_claude_titled_merges_still_count_as_features(self):
        squash = ("Claude : Feature: short beta names and Info window (BT1 A, IN1, #19) — 2.18.0 (#20)\n\n"
                  "* feat(ci): short three-digit beta versions and in-app Info window\n\n* Merge main into the branch\n")
        self.assertEqual("2.18.0", plan({}, ["v2.17.0", "v2.16.0"], squash, "2.17.0", 50)["version"])
        self.assertEqual("minor", change_kind({}, "Claude : Feature: Info window (#20)"))
        self.assertEqual("minor", change_kind({"pull_request": {"title": "Claude : Feature: x", "labels": []}}, ""))
        self.assertEqual("patch", change_kind({}, "Claude : Fix: wrong label (#30)\n\n* fix(ui): label"))
        self.assertEqual("major", change_kind({}, "Claude : Feature: x (#31)\n\n* feat!: new data format"))
        self.assertEqual("patch", change_kind({}, "Merge pull request #9\n\nNot a feat: line"))

    def test_versions_advance_across_channels(self):
        self.assertLess(android_code((2, 12, 0), 20), android_code((2, 12, 0), 21))
        self.assertLess(android_code((2, 12, 0)), android_code((2, 12, 1), 1))
        with self.assertRaises(ValueError):
            android_code((100, 0, 0))

    def test_beta_serial_is_monotonic_per_core_and_ignores_legacy_tags(self):
        tags = ["v2.12.0-beta.18.29", "v2.12.0-beta001", "v2.12.0-beta003", "v2.11.0-beta099"]
        self.assertEqual(4, next_beta_serial(tags, "2.12.0"))
        self.assertEqual(1, next_beta_serial(tags, "2.13.0"))
        result = plan({"number": 7, "pull_request": {"title": "fix: text", "labels": []}},
                      tags + ["v2.11.0"], "fix: text", "2.12.0", 88)
        self.assertEqual("2.12.0-beta004", result["version"])
        self.assertEqual(android_code((2, 12, 0), 4), result["code"])

    def test_beta_counter_rejects_exhaustion_and_zero(self):
        with self.assertRaisesRegex(ValueError, "exhausted"):
            next_beta_serial(["v2.12.0-beta999"], "2.12.0")
        with self.assertRaisesRegex(ValueError, "reserved"):
            next_beta_serial(["v2.12.0-beta000"], "2.12.0")

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
            archive = package("2.12.0-beta001", *binaries, root / "dist")
            with zipfile.ZipFile(archive) as z:
                self.assertEqual(expected_entries("2.12.0-beta001"), set(z.namelist()))
                self.assertTrue(z.getinfo("macOS/").is_dir())
                self.assertEqual(b"test build", z.read("portable/Remindly_2.12.0-beta001.exe"))
            with zipfile.ZipFile(archive, "a") as z:
                z.writestr("macOS/fake.dmg", "not a Mac app")
            with self.assertRaises(ValueError):
                verify_package(archive, "2.12.0-beta001")


if __name__ == "__main__":
    unittest.main()
