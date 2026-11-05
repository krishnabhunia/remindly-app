"""Check docs/FEATURES.md (embedded into the Windows app's Info → What's implemented).

* The file must parse: one table with the exact header, SrNo. 1..n in order, every Since an x.y.z
  no later than the version being built.
* A PR that adds a feature must also change docs/FEATURES.md. Remindly's version rule (docs/RELEASE-POLICY.md)
  marks a feature as a minor (feat:) or major (breaking) bump, so those PRs must touch the file.
"""
import argparse
from pathlib import Path
import re
import sys

HEADER = "| SrNo. | Area | What it does | Since |"
PATH = "docs/FEATURES.md"
VERSION = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")


def cells(line):
    return [c.strip() for c in line.strip().strip("|").split("|")]


def parse(text):
    """Rows as (srno, area, what, since); raises ValueError with a plain message."""
    lines = text.splitlines()
    starts = [i for i, line in enumerate(lines) if line.strip() == HEADER]
    if len(starts) != 1:
        raise ValueError(f"{PATH} must contain exactly one table with the header '{HEADER}'")
    i = starts[0] + 1
    if i >= len(lines) or not re.fullmatch(r"\|(\s*:?-{3,}:?\s*\|){4}", lines[i].strip()):
        raise ValueError(f"{PATH}: the line after the header must be the table divider '|---|---|---|---|'")
    rows = []
    for line in lines[i + 1:]:
        if not line.strip().startswith("|"):
            break
        parts = cells(line)
        if len(parts) != 4:
            raise ValueError(f"{PATH}: row '{line.strip()}' must have 4 cells")
        srno, area, what, since = parts
        expected = len(rows) + 1
        if srno != str(expected):
            raise ValueError(f"{PATH}: SrNo. must run 1..n in order; found '{srno}' where {expected} was expected")
        if not area or not what:
            raise ValueError(f"{PATH}: row {srno} needs an Area and a What it does")
        if not VERSION.fullmatch(since):
            raise ValueError(f"{PATH}: row {srno} Since '{since}' must be a version like 2.17.0")
        rows.append((int(srno), area, what, since))
    if not rows:
        raise ValueError(f"{PATH}: the table has no rows")
    return rows


def as_tuple(version):
    return tuple(int(x) for x in VERSION.fullmatch(version).groups())


def problems(text, current, kind=None, changed=None):
    """Every problem found (empty = OK). kind/changed are given for pull requests only."""
    found = []
    try:
        for srno, _, _, since in parse(text):
            if as_tuple(since) > as_tuple(current):
                found.append(f"{PATH}: row {srno} says Since {since}, later than the version being built ({current})")
    except ValueError as error:
        found.append(str(error))
    if kind in ("minor", "major") and changed is not None and PATH not in changed:
        found.append(f"This PR adds a feature (a {kind} version bump), so it must also add or update its row in {PATH}.")
    return found


def main():
    p = argparse.ArgumentParser(description="Check docs/FEATURES.md")
    p.add_argument("--file", default=PATH)
    p.add_argument("--version", required=True, help="x.y.z being built")
    p.add_argument("--kind", choices=("major", "minor", "patch"), help="the PR's version bump")
    p.add_argument("--changed", help="file listing the paths the PR changes, one per line")
    a = p.parse_args()
    changed = None
    if a.changed:
        changed = {line.strip() for line in Path(a.changed).read_text().splitlines() if line.strip()}
    found = problems(Path(a.file).read_text(encoding="utf-8"), a.version, a.kind, changed)
    for line in found:
        print(f"::error file={PATH}::{line}")
    if found:
        sys.exit(1)
    print(f"{PATH} OK")


if __name__ == "__main__":
    main()
