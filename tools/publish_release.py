"""Publish only verified builds; update a channel's feed after its assets exist."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile

from release import verify_package
from release_notes import load_release_pulls, release_text


def command(*args, capture=False, check=True):
    return subprocess.run(args, check=check, text=True, stdout=subprocess.PIPE if capture else None)


def gh(*args, capture=False, check=True):
    return command("gh", *args, "--repo", os.environ["GITHUB_REPOSITORY"], capture=capture, check=check)


def main():
    parser = argparse.ArgumentParser()
    for key in ("plan", "dist", "apk"):
        parser.add_argument(f"--{key}", required=True)
    args = parser.parse_args()
    plan = json.loads(Path(args.plan).read_text())
    archive = Path(args.dist) / plan["zip"]
    verify_package(archive, plan["version"])
    apk = Path(args.apk)
    if apk.name != f"Remindly_{plan['version']}.apk" or not apk.is_file():
        raise ValueError("APK name does not match the verified release plan")
    repo = os.environ["GITHUB_REPOSITORY"]
    tag = plan["tag"]
    sha = os.environ["RELEASE_SHA"]
    beta = plan["channel"] == "beta"
    full_notes = release_text(plan["version"], load_release_pulls(sha))
    notes_path = Path(args.dist) / "release-notes.md"
    notes_path.write_text(full_notes, encoding="utf-8")
    user_summary = " ".join(line[2:] for line in full_notes.splitlines() if line.startswith("- "))[:1300]
    manifest = {"versionCode": plan["code"], "versionName": plan["version"], "apk": apk.name,
                "apkUrl": f"https://github.com/{repo}/releases/download/{tag}/{apk.name}",
                "sha256": hashlib.sha256(apk.read_bytes()).hexdigest(), "sizeBytes": apk.stat().st_size,
                "notes": user_summary}
    manifest_file = Path(args.dist) / "version.json"
    manifest_file.write_text(json.dumps(manifest, indent=2) + "\n")
    info = gh("release", "view", tag, "--json", "isDraft,targetCommitish", capture=True, check=False)
    if info.returncode == 0:
        existing = json.loads(info.stdout)
        if existing["targetCommitish"] != sha:
            raise ValueError("Refusing to overwrite a release made from another commit")
        if existing["isDraft"]:
            gh("release", "upload", tag, str(archive), str(archive) + ".sha256", str(apk), str(manifest_file), "--clobber")
        else:
            # Published assets are immutable. A retry must use THEIR hashes, not a rebuilt APK's hash.
            gh("release", "download", tag, "--pattern", "version.json", "--dir", args.dist, "--clobber")
            manifest = json.loads(manifest_file.read_text())
    else:
        flags = ["--prerelease"] if beta else []
        gh("release", "create", tag, str(archive), str(archive) + ".sha256", str(apk), str(manifest_file),
           "--draft", "--latest=false", "--target", sha, "--title", f"Remindly {plan['version']}",
           "--notes-file", str(notes_path), *flags)
    gh("release", "edit", tag, "--draft=false", f"--prerelease={'true' if beta else 'false'}", f"--latest={'false' if beta else 'true'}")
    if beta:
        # Separate pointer: beta publication never commits to main or changes the stable feed.
        pointer = gh("release", "view", "beta", capture=True, check=False)
        if pointer.returncode == 0:
            with tempfile.TemporaryDirectory() as tmp:
                gh("release", "download", "beta", "--pattern", "version.json", "--dir", tmp)
                old = json.loads((Path(tmp) / "version.json").read_text())
                if old["versionCode"] >= manifest["versionCode"]:
                    return
            gh("release", "upload", "beta", str(manifest_file), "--clobber")
        else:
            gh("release", "create", "beta", str(manifest_file), "--prerelease", "--latest=false", "--target", sha,
               "--title", "Beta channel update manifest", "--notes", "Latest verified beta update metadata. Stable releases use their own update feed.")
        return
    # Keep VERSION and the legacy Android feed synchronized, without overwriting concurrent main work.
    for attempt in range(3):
        command("git", "fetch", "origin", "main")
        command("git", "checkout", "-B", "release-feed", "origin/main")
        feed = Path("releases/version.json")
        current = json.loads(feed.read_text()) if feed.exists() else {}
        if current.get("versionCode", 0) >= manifest["versionCode"]:
            return
        Path("VERSION").write_text(plan["base"] + "\n")
        feed.write_text(json.dumps(manifest, indent=2) + "\n")
        command("git", "config", "user.name", "github-actions[bot]")
        command("git", "config", "user.email", "41898282+github-actions[bot]@users.noreply.github.com")
        command("git", "add", "VERSION", "releases/version.json")
        command("git", "commit", "-m", f"chore: publish update feed for {tag}")
        if command("git", "push", "origin", "HEAD:main", check=False).returncode == 0:
            return
    raise RuntimeError("Release is published, but main advanced repeatedly; rerun to finish the feed update")


if __name__ == "__main__":
    main()
