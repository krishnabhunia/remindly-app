import unittest

from beta_tag import ReservationError, beta_name, reserve, short_tags

HEAD = "a" * 40
OTHER = "b" * 40


class FakeApi:
    """In-memory tags; `race` tags appear between the listing and the create (another run won)."""

    def __init__(self, tags=None, annotated=None, race=None):
        self.tags = dict(tags or {})          # tag name -> commit sha (lightweight)
        self.annotated = dict(annotated or {})  # tag name -> commit sha (through a tag object)
        self.race = list(race or [])
        self.created = []

    def refs(self, prefix):
        refs = [{"ref": f"refs/tags/{n}", "object": {"type": "commit", "sha": s}} for n, s in self.tags.items()]
        refs += [{"ref": f"refs/tags/{n}", "object": {"type": "tag", "sha": "tag:" + s}} for n, s in self.annotated.items()]
        return [r for r in refs if r["ref"].startswith(f"refs/tags/{prefix}")]

    def commit_of(self, obj):
        return obj["sha"][4:] if obj["type"] == "tag" else obj["sha"]

    def create_tag(self, tag, sha):
        if self.race:
            name, owner = self.race.pop(0)
            self.tags[name] = owner
        if tag in self.tags or tag in self.annotated:
            return False
        self.tags[tag] = sha
        self.created.append(tag)
        return True


class BetaTagTests(unittest.TestCase):
    def test_first_beta_of_a_version_is_001_and_creates_its_tag(self):
        api = FakeApi({"v2.16.0-beta.18.29": OTHER, "v2.16.0": OTHER})
        result = reserve(api, "2.17.0", "v", HEAD)
        self.assertEqual({"number": 1, "version": "2.17.0-beta001", "tag": "v2.17.0-beta001", "reused": False}, result)
        self.assertEqual(["v2.17.0-beta001"], api.created)

    def test_next_number_is_max_plus_one_across_prs_and_ignores_old_long_tags(self):
        api = FakeApi({"v2.17.0-beta001": OTHER, "v2.17.0-beta004": OTHER, "v2.17.0-beta.19.40": OTHER, "v2.17.0-beta0001": OTHER})
        self.assertEqual("2.17.0-beta005", reserve(api, "2.17.0", "v", HEAD)["version"])

    def test_rerun_or_other_platform_of_the_same_commit_reuses_its_number(self):
        api = FakeApi({"v2.17.0-beta001": OTHER, "v2.17.0-beta002": HEAD, "v2.17.0-beta003": OTHER})
        result = reserve(api, "2.17.0", "v", HEAD)
        self.assertEqual((2, True), (result["number"], result["reused"]))
        self.assertEqual([], api.created)

    def test_annotated_tags_are_peeled_to_their_commit(self):
        api = FakeApi(annotated={"v2.17.0-beta003": HEAD})
        self.assertEqual("v2.17.0-beta003", reserve(api, "2.17.0", "v", HEAD)["tag"])

    def test_conflict_lists_again_and_takes_the_next_free_number(self):
        api = FakeApi({"v2.17.0-beta001": OTHER}, race=[("v2.17.0-beta002", OTHER)])
        self.assertEqual("v2.17.0-beta003", reserve(api, "2.17.0", "v", HEAD)["tag"])

    def test_conflict_won_by_another_platform_of_our_commit_is_reused(self):
        api = FakeApi(race=[("v2.17.0-beta001", HEAD)])
        result = reserve(api, "2.17.0", "v", HEAD)
        self.assertEqual(("v2.17.0-beta001", True), (result["tag"], result["reused"]))

    def test_gives_up_after_ten_attempts(self):
        api = FakeApi(race=[(f"v2.17.0-beta{n:03d}", OTHER) for n in range(1, 30)])
        with self.assertRaises(ReservationError):
            reserve(api, "2.17.0", "v", HEAD)

    def test_999_is_the_last_number_then_a_version_bump_is_required(self):
        api = FakeApi({"v2.17.0-beta998": OTHER})
        self.assertEqual("2.17.0-beta999", reserve(api, "2.17.0", "v", HEAD)["version"])
        with self.assertRaisesRegex(ReservationError, "Bump the version"):
            reserve(api, "2.17.0", "v", "c" * 40)

    def test_names_are_three_digits_and_need_a_full_head_sha(self):
        self.assertEqual("15.0.0-beta007", beta_name("15.0.0", 7))
        with self.assertRaises(ReservationError):
            beta_name("15.0.0", 1000)
        with self.assertRaises(ReservationError):
            reserve(FakeApi(), "2.17.0", "v", "abc123")
        refs = [{"ref": "refs/tags/win-v4.2.0-beta001", "object": {}}, {"ref": "refs/tags/v4.2.0-beta001", "object": {}}]
        self.assertEqual([1], list(short_tags(refs, "win-v", "4.2.0")))


if __name__ == "__main__":
    unittest.main()
