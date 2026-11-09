"""Reserve the short beta number for a PR head commit (BT2).

A PR test build is named ``<x.y.z>-beta<NNN>`` (three digits, 001-999) and tagged ``v<x.y.z>-beta<NNN>``.
The counter is per x.y.z and shared by every PR. The tag itself is the reservation:

1. list ``<prefix><core>-beta###`` tags (old ``-beta.<PR>.<run>`` tags are ignored), peeling annotated tags;
2. a tag already on the head commit is reused (a re-run, or another platform of the same commit);
3. otherwise create the next number; a 422 means another run took it first, so list again and retry.

A number whose build later fails is simply skipped: gaps in the sequence are expected.
"""
import argparse
import json
import os
import re
import urllib.error
import urllib.parse
import urllib.request

MAX_BETA = 999
ATTEMPTS = 10


class ReservationError(RuntimeError):
    pass


def beta_name(core, number):
    if not 1 <= number <= MAX_BETA:
        raise ReservationError(f"Beta number {number} for {core} is outside 001-{MAX_BETA:03d}")
    return f"{core}-beta{number:03d}"


def short_tags(refs, prefix, core):
    """{number: ref object} for refs named exactly <prefix><core>-beta###."""
    pattern = re.compile(r"^refs/tags/" + re.escape(f"{prefix}{core}-beta") + r"(\d{3})$")
    found = {}
    for ref in refs:
        m = pattern.match(ref.get("ref", ""))
        if m and int(m.group(1)) >= 1:
            found[int(m.group(1))] = ref.get("object", {})
    return found


def next_number(numbers, core):
    n = max(numbers, default=0) + 1
    if n > MAX_BETA:
        raise ReservationError(f"All {MAX_BETA} beta numbers of {core} are used. Bump the version (x.y.z) to build more betas.")
    return n


def tag_commit(api, tag):
    """The commit a tag points at (annotated tags peeled), or None when the tag does not exist."""
    obj = api.ref_object(tag)
    return api.commit_of(obj) if obj else None


def reserve(api, core, prefix, head_sha, attempts=ATTEMPTS):
    """Returns {"number", "version", "tag", "reused"}; api has refs(prefix), commit_of(obj) and create_tag(tag, sha)."""
    if not re.fullmatch(r"[0-9a-f]{40}", head_sha or ""):
        raise ReservationError(f"Head commit SHA is not a full SHA: {head_sha!r}")
    for _ in range(attempts):
        found = short_tags(api.refs(f"{prefix}{core}-beta"), prefix, core)
        for number in sorted(found):
            if api.commit_of(found[number]) == head_sha:
                return {"number": number, "version": beta_name(core, number), "tag": prefix + beta_name(core, number), "reused": True}
        number = next_number(found, core)
        tag = prefix + beta_name(core, number)
        if api.create_tag(tag, head_sha):
            return {"number": number, "version": beta_name(core, number), "tag": tag, "reused": False}
    raise ReservationError(f"Could not reserve a beta number for {core} after {attempts} attempts; re-run the workflow.")


class GitHubApi:
    """The three REST calls the reservation needs, with the workflow's GITHUB_TOKEN."""

    def __init__(self, repo, token, base=None):
        self.repo = repo
        self.token = token
        self.base = (base or os.environ.get("GITHUB_API_URL") or "https://api.github.com").rstrip("/")

    def _call(self, method, path, body=None):
        request = urllib.request.Request(self.base + path, method=method,
                                         data=None if body is None else json.dumps(body).encode())
        request.add_header("Accept", "application/vnd.github+json")
        request.add_header("Authorization", f"Bearer {self.token}")
        request.add_header("X-GitHub-Api-Version", "2022-11-28")
        if body is not None:
            request.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return response.status, json.loads(response.read() or b"null")
        except urllib.error.HTTPError as error:
            return error.code, None

    def refs(self, name_prefix):
        status, data = self._call("GET", f"/repos/{self.repo}/git/matching-refs/tags/{urllib.parse.quote(name_prefix)}")
        if status != 200 or not isinstance(data, list):
            raise ReservationError(f"Listing tags failed (HTTP {status})")
        return data

    def ref_object(self, tag):
        status, data = self._call("GET", f"/repos/{self.repo}/git/ref/tags/{urllib.parse.quote(tag)}")
        if status == 404:
            return None
        if status != 200 or not isinstance(data, dict):
            raise ReservationError(f"Reading tag {tag} failed (HTTP {status})")
        return data.get("object")

    def commit_of(self, obj):
        for _ in range(5):  # annotated tags point at a tag object; peel to the commit
            if obj.get("type") != "tag":
                return obj.get("sha")
            status, data = self._call("GET", f"/repos/{self.repo}/git/tags/{obj['sha']}")
            if status != 200:
                raise ReservationError(f"Reading annotated tag {obj['sha']} failed (HTTP {status})")
            obj = data.get("object", {})
        return None

    def create_tag(self, tag, sha):
        status, _ = self._call("POST", f"/repos/{self.repo}/git/refs", {"ref": f"refs/tags/{tag}", "sha": sha})
        if status == 201:
            return True
        if status == 422:  # already exists: another run reserved it first
            return False
        raise ReservationError(f"Creating tag {tag} failed (HTTP {status}); the job needs 'contents: write'.")


def main():
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--core", required=True, help="x.y.z")
    p.add_argument("--prefix", default="v")
    p.add_argument("--sha", required=True, help="PR head commit (not the merge commit)")
    p.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY"))
    a = p.parse_args()
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    if not a.repo or not token:
        raise SystemExit("GITHUB_REPOSITORY and GH_TOKEN are required")
    print(json.dumps(reserve(GitHubApi(a.repo, token), a.core, a.prefix, a.sha)))


if __name__ == "__main__":
    main()
