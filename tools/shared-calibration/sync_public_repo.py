#!/usr/bin/env python3
"""Assemble the public calibration repository's file set.

    python tools/shared-calibration/sync_public_repo.py --out <new or empty directory>

Everything comes from this repository; nothing here creates a repository, pushes, or talks to GitHub.

  index.json, submissions.json         an empty index and ledger, written by index.py
  README.md, LICENSE.md, .github/...   from tools/shared-calibration/public-repo/
  .gitattributes, .gitignore           from public-repo/dot-gitattributes and dot-gitignore
  LICENSES/GPL-3.0-or-later.txt        this repository's LICENSE
  templates/<profile id>.json          every shipped VERIFIED profile with a calibration section, byte for byte
  tools/                               the runtime files of tools/shared-calibration/ (no tests, no generators)

Text files other than templates are written with LF line endings, so a Windows checkout of this
repository cannot hand bash a CRLF script. The result is read back with index.py and rebuild.py.

Exit codes: 0 written and verified; 1 the directory was not empty or the result did not verify.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import index as repo_index
import rebuild
import sharecode

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
PUBLIC_DIRECTORY = HERE / "public-repo"
RUNTIME_FILES = (
    "sharecode.py", "rebuild.py", "index.py", "issue.py", "publish.py", "publish_issue.sh", "sweep_issues.sh",
)
RENAMED = {"dot-gitattributes": ".gitattributes", "dot-gitignore": ".gitignore"}
PROFILE_REGIONS = ("cn", "global")
LICENSE_TARGET = "LICENSES/GPL-3.0-or-later.txt"


class SyncError(Exception):
    """The file set cannot be assembled, or what was written does not read back."""


def _lf(data: bytes) -> bytes:
    return data.replace(b"\r\n", b"\n")


def shipped_templates(repo: Path = REPO) -> dict:
    """``{"templates/<profile id>.json": bytes}`` for every shipped profile that can serve as a template."""
    found = {}
    for region in PROFILE_REGIONS:
        for path in sorted((Path(repo) / "protocol-profiles" / region).glob("*.json")):
            data = path.read_bytes()
            parsed, document = sharecode.strict_json(data, max_depth=64)
            if not parsed or not isinstance(document, dict) or "calibration" not in document:
                continue
            if document.get("compatibility_status") != "VERIFIED":
                continue
            try:
                template = rebuild.load_template(data, path.name)
            except rebuild.TemplateError as error:
                raise SyncError("a shipped template cannot be used: %s" % error) from error
            name = "templates/%s.json" % template.profile_id
            if name in found:
                raise SyncError("two shipped templates share the profile id " + template.profile_id)
            found[name] = data
    if not found:
        raise SyncError("no shipped VERIFIED profile carries a calibration section")
    return found


def file_set(repo: Path = REPO) -> dict:
    """``{relative path: bytes}`` of the whole public repository."""
    files = {}
    for path in sorted(PUBLIC_DIRECTORY.rglob("*")):
        if path.is_file() and "__pycache__" not in path.parts:
            relative = path.relative_to(PUBLIC_DIRECTORY).as_posix()
            files[RENAMED.get(relative, relative)] = _lf(path.read_bytes())
    for name in RUNTIME_FILES:
        files["tools/" + name] = _lf((HERE / name).read_bytes())
    files[LICENSE_TARGET] = _lf((Path(repo) / "LICENSE").read_bytes())
    files.update(shipped_templates(repo))
    files.update(repo_index.dump(repo_index.empty_index()))
    return files


def verify(root: Path) -> None:
    """Reads a synced tree back the way the Action will; raises SyncError when it does not hold up."""
    try:
        state = repo_index.load(root)
        templates = rebuild.load_templates(Path(root) / "templates")
    except (repo_index.IndexCorrupt, rebuild.TemplateError) as error:
        raise SyncError("the synced repository does not read back: %s" % error) from error
    if state.entries or state.submissions:
        raise SyncError("a new public repository starts with an empty index")
    if not templates:
        raise SyncError("the synced repository has no template")
    missing = [name for name in RUNTIME_FILES if not (Path(root) / "tools" / name).is_file()]
    if missing:
        raise SyncError("tools missing: " + ", ".join(missing))


def sync(out: Path, repo: Path = REPO) -> list:
    """Writes the file set into ``out`` (new or empty), verifies it, and returns the relative paths."""
    out = Path(out)
    if out.exists() and (not out.is_dir() or any(out.iterdir())):
        raise SyncError("the output directory must be new or empty: %s" % out)
    files = file_set(repo)
    for name, data in files.items():
        target = out / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    verify(out)
    return sorted(files)


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(description="Assemble the public calibration repository's files.")
    parser.add_argument("--out", required=True, help="new or empty directory to write into")
    args = parser.parse_args(argv)
    try:
        names = sync(Path(args.out))
    except SyncError as error:
        print("sync_public_repo.py: " + str(error), file=sys.stderr)
        return 1
    for name in names:
        print(name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
