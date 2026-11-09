"""Plan semantic versions and verify the single, cross-platform distribution ZIP."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import zipfile

from beta_tag import GitHubApi, beta_name, reserve

SEMVER = re.compile(r"^(?:win-)?v?(\d+)\.(\d+)(?:\.(\d+))?$")
BETA_TAG = re.compile(r"^v(?P<core>\d+\.\d+\.\d+)-beta(?P<serial>\d{3})$")


def parse_stable(tag):
    m = SEMVER.fullmatch(tag.strip())
    return tuple(int(x or 0) for x in m.groups()) if m else None


def bump(version, kind):
    major, minor, patch = version
    return (major + 1, 0, 0) if kind == "major" else (major, minor + 1, 0) if kind == "minor" else (major, minor, patch + 1)


def change_kind(event, messages):
    labels = {x["name"].lower() for x in event.get("pull_request", {}).get("labels", [])}
    for kind in ("major", "minor", "patch"):
        if f"release:{kind}" in labels:
            return kind
    text = event.get("pull_request", {}).get("title", "") + "\n" + messages
    if re.search(r"(?m)^(?:\w+)(?:\([^\n]*\))?!:|^BREAKING CHANGE:", text):
        return "major"
    return "minor" if re.search(r"(?mi)^feat(?:\([^\n]*\))?:", text) else "patch"


def android_code(base, beta_build=None):
    major, minor, patch = base
    if not (0 <= minor < 100 and 0 <= patch < 100):
        raise ValueError("Android version encoding requires minor and patch below 100")
    serial = 9999 if beta_build is None else int(beta_build)
    if not (1 <= serial <= 9999) or (beta_build is not None and serial == 9999):
        raise ValueError("Beta build must be 1..9998; 9999 is reserved for stable")
    code = major * 100_000_000 + minor * 1_000_000 + patch * 10_000 + serial
    if not 0 < code <= 2_147_483_647:
        raise ValueError("Version is outside Android's versionCode range")
    return code


def next_beta_serial(tags, core):
    """The next short beta of x.y.z from a list of tag names (local dry runs; CI reserves it with beta_tag.py).

    Historical PR/run tags stay valid but do not consume three-digit serials.
    The GitHub Actions beta concurrency group serializes planning through
    publication, so two pull requests cannot allocate the same serial.
    """
    used = set()
    for tag in tags:
        match = BETA_TAG.fullmatch(tag.strip())
        if match and match["core"] == core:
            number = int(match["serial"])
            if number == 0:
                raise ValueError("Beta 000 is reserved")
            used.add(number)
    serial = max(used, default=0) + 1
    if serial > 999:
        raise ValueError("Three-digit beta series is exhausted; plan the next core version")
    return serial


def target_version(event, tags, messages, floor, already_released=None):
    """The stable x.y.z this build releases (a PR's beta is a test build of it)."""
    published = [parse_stable(t) for t in tags]
    base = max([v for v in published if v] or [(2, 11, 0)])
    target = max(bump(base, change_kind(event, messages)), parse_stable(floor))
    if already_released and not event.get("pull_request"):
        target = parse_stable(already_released)
        if target is None:
            raise ValueError("Published tag is not a stable version")
    return target


def plan(event, tags, messages, floor, run_number, already_released=None, beta_number=None):
    """PR builds are <x.y.z>-beta<NNN> (BT1 A); NNN is reserved as a tag by beta_tag.py (BT2).

    Android's hidden versionCode uses the beta number as its serial (since 2.17.0), so a re-run of the
    same commit keeps its code and a later commit's beta always has a higher one; stable uses 9999."""
    target = target_version(event, tags, messages, floor, already_released)
    stable = ".".join(map(str, target))
    beta = event.get("pull_request") is not None
    if beta and beta_number is None:
        beta_number = next_beta_serial(tags, stable)  # local dry runs; CI reserves the tag (tools/beta_tag.py)
    serial = int(beta_number) if beta else None
    name = beta_name(stable, serial) if beta else stable
    return {"base": stable, "version": name, "tag": f"v{name}", "channel": "beta" if beta else "stable",
            "code": android_code(target, serial), "zip": f"Remindly_{name}.zip",
            "kind": change_kind(event, messages), "beta": f"{serial:03d}" if beta else ""}


def expected_entries(version):
    return {f"portable/Remindly_{version}.exe", f"windows-x64/Remindly_{version}.exe",
            f"Android/Remindly_{version}.apk", "macOS/"}


def verify_package(path, version):
    with zipfile.ZipFile(path) as z:
        names = z.namelist()
        if len(names) != len(set(names)) or set(names) != expected_entries(version):
            raise ValueError(f"Unexpected ZIP layout: {names}")
        for info in z.infolist():
            if not info.is_dir() and info.file_size == 0:
                raise ValueError(f"Empty app binary: {info.filename}")
        if z.testzip() is not None:
            raise ValueError("ZIP integrity check failed")


def package(version, android, portable, installer, out):
    out = Path(out)
    out.mkdir(parents=True, exist_ok=True)
    archive = out / f"Remindly_{version}.zip"
    inputs = [(Path(android), f"Android/Remindly_{version}.apk"),
              (Path(portable), f"portable/Remindly_{version}.exe"),
              (Path(installer), f"windows-x64/Remindly_{version}.exe")]
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
        for source, name in inputs:
            if not source.is_file() or source.stat().st_size == 0:
                raise ValueError(f"Missing build: {source}")
            z.write(source, name)
        z.writestr("macOS/", "")  # Reserved, empty: no macOS app has been requested yet.
    verify_package(archive, version)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix(".zip.sha256").write_text(f"{digest}  {archive.name}\n", encoding="ascii")
    return archive


def main():
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="action", required=True)
    v = sub.add_parser("plan")
    v.add_argument("--event", required=True)
    v.add_argument("--tags", required=True)
    v.add_argument("--messages")
    v.add_argument("--run", type=int, required=True)
    v.add_argument("--output", default="release-plan.json")
    v.add_argument("--already-released")
    v.add_argument("--beta-number", type=int, help="Use this beta number instead of reserving one (local dry runs)")
    q = sub.add_parser("package")
    for key in ("version", "android", "portable", "installer", "out"):
        q.add_argument(f"--{key}", required=True)
    a = p.parse_args()
    if a.action == "plan":
        tags = Path(a.tags).read_text().splitlines()
        if a.messages:
            messages = Path(a.messages).read_text()
        else:
            stable_tags = [(parse_stable(t), t) for t in tags if parse_stable(t)]
            latest = max(stable_tags)[1] if stable_tags else None
            args = ["git", "log", f"{latest}..HEAD", "--format=%B"] if latest else ["git", "log", "-1", "--format=%B"]
            messages = subprocess.check_output(args, text=True)
        event = json.loads(Path(a.event).read_text())
        floor = Path("VERSION").read_text().strip()
        number = None
        if event.get("pull_request"):
            if a.beta_number is not None:
                number = a.beta_number
            else:
                core = ".".join(map(str, target_version(event, tags, messages, floor)))
                head = event["pull_request"]["head"]["sha"]  # never the synthetic merge commit
                token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
                reserved = reserve(GitHubApi(os.environ["GITHUB_REPOSITORY"], token), core, "v", head)
                print(f"Beta number {reserved['number']:03d} {'reused' if reserved['reused'] else 'reserved'}: {reserved['tag']} -> {head}")
                number = reserved["number"]
        result = plan(event, tags, messages, floor, a.run, a.already_released, number)
        Path(a.output).write_text(json.dumps(result, indent=2) + "\n")
        if os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a") as f:
                for key, value in result.items():
                    f.write(f"{key}={value}\n")
        print(json.dumps(result))
    else:
        print(package(a.version, a.android, a.portable, a.installer, a.out))


if __name__ == "__main__":
    main()
