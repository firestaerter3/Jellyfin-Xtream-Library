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
BETA_WORDS = re.compile(r"\bbeta\b", re.I)
# Wording that says the whole entry is not on stable, whichever version it names.
NOT_STABLE_WORDS = re.compile(r"not (?:yet )?(?:promoted|stable|on stable)", re.I)
# Wording an updated status uses, "on stable since <date>" or "on stable as vX".
ON_STABLE_WORDS = re.compile(r"(?<!not )(?<!not yet )\bon stable (?:since|as)\b", re.I)
UNRELEASED_WORDS = re.compile(r"not released|not yet released|not pushed", re.I)


def parse(version):
    return tuple(int(part) for part in version.split("."))


def stable_versions(fetch):
    """Versions on the stable channel, from origin/main when available, else the working tree."""
    if fetch:
        result = subprocess.run(["git", "-C", str(MANIFEST_REPO), "fetch", "-q"], capture_output=True)
        if result.returncode != 0:
            # Silence means "all matches", so a stale manifest must not pass for a clean one.
            print("check-bugs-status: git fetch in jellyfin-plugin-repo failed; checked the last fetched manifest")
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


def stale_beta_versions(status, stable):
    """Versions the status calls beta or not stable that are on stable after all.

    A label belongs to the version it follows ("v1.56.0.0 (beta), v2.0.2.0 on stable as ..."
    only calls 1.56.0.0 beta). Wording such as "not yet promoted" covers every version named,
    unless the status also says something is on stable; then it only covers its own version.
    """
    matches = list(VERSION.finditer(status))
    # "X and Y, not on stable" is about the whole entry. Once the status also says something
    # is on stable, the wording is per version, so each phrase only covers its own version.
    whole_entry = bool(NOT_STABLE_WORDS.search(status)) and not ON_STABLE_WORDS.search(status)
    stale = []
    for i, m in enumerate(matches):
        # "on stable as v2.0.9.0" names the stable version itself, not one awaiting promotion.
        if re.search(r"on stable as\s*$", status[:m.start()], re.I):
            continue
        end = matches[i + 1].start() if i + 1 < len(matches) else len(status)
        segment = status[m.end():end]
        # "(beta)" right after the version labels it; "on stable as/since" in its own segment
        # says that version (or the line it belongs to) has since been promoted.
        labelled = (BETA_WORDS.search(segment) is not None or NOT_STABLE_WORDS.search(segment) is not None) \
            and not ON_STABLE_WORDS.search(segment)
        if (whole_entry or labelled) and on_stable(m.group(1), stable):
            stale.append(m.group(1))
    return stale


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
        stale = stale_beta_versions(status, stable)
        if stale:
            problems.append(f"{bug}: status says beta / not on stable, but {', '.join(stale)} is on stable")
        problems.extend(unreleased_but_tagged(bug, status))

    if problems:
        print(f"BUGS.md is out of date ({len(problems)}):")
        for line in problems:
            print(f"  - {line}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
