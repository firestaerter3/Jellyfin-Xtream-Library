#!/usr/bin/env python3
"""Report BUGS.md entries whose status no longer matches what has shipped.

BUGS.md is gitignored and edited by hand, so nothing else notices when a status goes stale.
This compares it with the stable manifest in ../jellyfin-plugin-repo and with git tags, and
prints one line per entry that needs updating:

- listed under "Open" although the version it shipped in is already on stable;
- a status that says beta or "not yet promoted" for a version that is on stable;
- a status that says "not released" for a commit that is already in a tag.

A version counts as on stable when stable carries that version or a later one of the same
major line (1.x for Jellyfin 10.11, 2.x for Jellyfin 12), because promotion can skip versions.

Read-only. Prints nothing and exits 0 when everything matches, so it can run at session start.
Usage: python3 scripts/check-bugs-status.py [--fetch]
"""

import json
import re
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
BUGS = REPO / "BUGS.md"
MANIFEST_REPO = REPO.parent / "jellyfin-plugin-repo"
PLUGIN_GUID = "63ba5fcd-c8ce-421a-83e8-ba0b11030d53"

VERSION = re.compile(r"\bv?(\d+\.\d+\.\d+\.\d+)\b")
SHA = re.compile(r"[`(]([0-9a-f]{7,40})[`)]")
BETA_WORDS = re.compile(r"\(beta\)|\bbeta\b|not yet promoted|not yet stable|not yet on stable", re.I)
UNRELEASED_WORDS = re.compile(r"not released|not yet released|not pushed", re.I)


def parse(version):
    return tuple(int(part) for part in version.split("."))


def stable_versions(fetch):
    """Versions on the stable channel, from origin/main when available, else the working tree."""
    if fetch:
        subprocess.run(["git", "-C", str(MANIFEST_REPO), "fetch", "-q"], capture_output=True)
    try:
        raw = subprocess.run(
            ["git", "-C", str(MANIFEST_REPO), "show", "origin/main:manifest.json"],
            capture_output=True, text=True, check=True).stdout
    except (subprocess.CalledProcessError, FileNotFoundError):
        raw = (MANIFEST_REPO / "manifest.json").read_text()
    plugin = next(p for p in json.loads(raw) if p["guid"] == PLUGIN_GUID)
    return [parse(v["version"]) for v in plugin["versions"]]


def on_stable(version, stable):
    v = parse(version)
    return any(s[0] == v[0] and s >= v for s in stable)


def in_a_tag(sha):
    result = subprocess.run(["git", "-C", str(REPO), "tag", "--contains", sha],
                            capture_output=True, text=True)
    return result.returncode == 0 and result.stdout.strip() != ""


def unreleased_but_tagged(bug, status):
    if UNRELEASED_WORDS.search(status):
        for sha in SHA.findall(status):
            if in_a_tag(sha):
                return [f"{bug}: status says not released, but commit {sha} is in a tag"]
    return []


def open_rows(text):
    """BUG ids and status cells from the Overview "Open" table."""
    start = text.find("### Open")
    end = text.find("### Closed", start)
    if start < 0 or end < 0:
        return []
    rows = []
    for line in text[start:end].splitlines():
        m = re.match(r"\| \[(BUG-\d+)\]", line)
        if m:
            rows.append((m.group(1), line.strip().strip("|").split("|")[-1].strip()))
    return rows


def section_statuses(text):
    """BUG ids and the Status row of each detail section."""
    out = []
    for m in re.finditer(r"^## (BUG-\d+) ·", text, re.M):
        nxt = re.search(r"^## BUG-\d+ ·", text[m.end():], re.M)
        body = text[m.end():m.end() + nxt.start()] if nxt else text[m.end():]
        status = re.search(r"^\| \*\*Status\*\*\s*\|(.*)\|\s*$", body, re.M)
        if status:
            out.append((m.group(1), status.group(1).strip()))
    return out


def main():
    if not BUGS.exists():
        return 0
    text = BUGS.read_text()
    stable = stable_versions("--fetch" in sys.argv)
    problems = []

    for bug, status in open_rows(text):
        versions = VERSION.findall(status)
        if versions and all(on_stable(v, stable) for v in versions):
            problems.append(f"{bug}: listed under Open, but {', '.join(versions)} is on stable")
        problems.extend(unreleased_but_tagged(bug, status))

    for bug, status in section_statuses(text):
        versions = VERSION.findall(status)
        if BETA_WORDS.search(status) and "on stable" not in status and versions and \
                any(on_stable(v, stable) for v in versions):
            problems.append(f"{bug}: status says beta / not promoted, but {', '.join(versions)} is on stable")
        problems.extend(unreleased_but_tagged(bug, status))

    if problems:
        print(f"BUGS.md is out of date ({len(problems)}):")
        for line in problems:
            print(f"  - {line}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
