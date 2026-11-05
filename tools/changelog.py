"""Read one version's section of windows/CHANGELOG.md (the update feed notes and the app's Info → What's new)."""
import re
from pathlib import Path

CHANGELOG = Path(__file__).resolve().parent.parent / "windows" / "CHANGELOG.md"
FALLBACK = "See what's new on GitHub Releases."


def section(text, version):
    """The body under '## <version>' (to the next '## ' heading), or None."""
    lines = text.splitlines()
    head = re.compile(r"^##\s+" + re.escape(version) + r"(?:\s|$)")
    for i, line in enumerate(lines):
        if head.match(line):
            body = []
            for rest in lines[i + 1:]:
                if rest.startswith("## "):
                    break
                body.append(rest)
            return "\n".join(body).strip()
    return None


def plain(markdown):
    """Markdown → short plain text: no tables, emphasis or code marks; bullets become '• ';
    a hard-wrapped line is joined to the bullet or paragraph it continues."""
    out = []
    joinable = False
    for raw in markdown.splitlines():
        line = raw.strip()
        if not line or line.startswith("|"):
            joinable = False
            continue
        heading = line.startswith("#")
        bullet = re.match(r"^[-*]\s+", line) is not None
        line = re.sub(r"^#+\s*", "", line)
        line = re.sub(r"^[-*]\s+", "• ", line)
        line = re.sub(r"\*\*(.+?)\*\*", r"\1", line)
        line = line.replace("`", "")
        if joinable and not heading and not bullet:
            out[-1] += " " + line
        else:
            out.append(line)
        joinable = not heading
    return "\n".join(out)


def notes(version, text=None, limit=600):
    """Feed notes for x.y.z: that section as plain text (trimmed at a line end), else a fallback line."""
    if text is None:
        text = CHANGELOG.read_text(encoding="utf-8") if CHANGELOG.exists() else ""
    body = section(text, version)
    if not body:
        return FALLBACK
    result = ""
    for line in plain(body).splitlines():
        candidate = (result + "\n" + line) if result else line
        if len(candidate) > limit:
            return (result + "\n…") if result else line[: limit - 1] + "…"
        result = candidate
    return result or FALLBACK
