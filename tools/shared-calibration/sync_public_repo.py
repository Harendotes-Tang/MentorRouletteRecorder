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
  tools/sync_refs.py                    the canonical tools/release-mirror/sync_refs.py maintenance entrypoint

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
    "report_issue.sh", "evidence.py", "evidence_workflow.py", "community_classification.py",
)
RENAMED = {"dot-gitattributes": ".gitattributes", "dot-gitignore": ".gitignore"}
EXTERNAL_RUNTIME_FILES = {"tools/sync_refs.py": "tools/release-mirror/sync_refs.py",
                          "schemas/calibration-evidence-v1.schema.json": "contracts/calibration-evidence-v1.schema.json",
                          "schemas/community-content-v1.schema.json": "contracts/community-content-v1.schema.json",
                          "tools/community/publish.py": "tools/shared-calibration/community_publish.py"}
COMMUNITY_SEEDS = {
    "community/v1/index.json": b'{"generation":0,"schema_version":1,"snapshot_commit":null,"snapshot_sha256":null}\n',
    "community/v1/submissions.json": b'{"issues":{},"schema_version":1}\n',
}
COMMUNITY_MANAGED = {
    "community/README.md", "schemas/community-content-v1.schema.json",
    "tools/community/publish.py", "tools/community_classification.py", "tools/publish.py",
    "tools/evidence_workflow.py", ".github/ISSUE_TEMPLATE/share-community-content.yml",
    ".github/workflows/publish-community-content.yml", ".github/workflows/mirror-repository.yml",
    ".github/workflows/evidence-calibration.yml", ".github/workflows/report-calibration.yml",
    ".github/workflows/publish-calibration.yml",
}
PROFILE_REGIONS = ("cn", "global", "tc")
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
    for target, source in EXTERNAL_RUNTIME_FILES.items():
        if target in files:
            raise SyncError("mapped runtime file has a second source: " + target)
        raw = (Path(repo) / source).read_bytes()
        files[target] = raw if target.startswith("schemas/") else _lf(raw)
    files[LICENSE_TARGET] = _lf((Path(repo) / "LICENSE").read_bytes())
    files.update(shipped_templates(repo))
    files.update(repo_index.dump(repo_index.empty_index()))
    files.update(COMMUNITY_SEEDS)
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
    required_tools = ["tools/" + name for name in RUNTIME_FILES] + list(EXTERNAL_RUNTIME_FILES)
    missing = [name for name in required_tools if not (Path(root) / name).is_file()]
    if missing:
        raise SyncError("tools missing: " + ", ".join(missing))


def sync(out: Path, repo: Path = REPO) -> list:
    """Writes the file set into ``out`` (new or empty), verifies it, and returns the relative paths."""
    out = Path(out)
    if out.exists() and (not out.is_dir() or any(out.iterdir())):
        raise SyncError("the output directory must be new or empty: %s" % out)
    files = file_set(repo)
    for name, data in files.items():
        write_managed(out, name, data)
    verify(out)
    return sorted(files)


def write_managed(root: Path, name: str, data: bytes) -> None:
    target = root / name
    if target.resolve().relative_to(root.resolve()).as_posix() != name:
        raise SyncError("managed path must not be redirected: " + name)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)


def refresh_calibration_guard(name: str, current: bytes) -> bytes:
    """Apply the community guard without replacing the deployed calibration implementation."""
    content = current.decode("utf-8")
    newline = "\r\n" if "\r\n" in content else "\n"
    content = content.replace("\r\n", "\n")
    import_line = "from community_classification import classify as classify_community"
    if name == "tools/publish.py":
        replacements = [
            ("import issue as issue_form", "import issue as issue_form\n" + import_line),
            ("    if REPORT_LABEL not in labels:\n        return ReportResult",
             '    if classify_community(live if "body" in live else issue, labels) != "other" or REPORT_LABEL not in labels:\n'
             "        return ReportResult"),
            ('        issue = live\n    user = issue.get("user")',
             '        issue = live\n    if classify_community(issue, labels) != "other":\n'
             '        return None, Result(SKIPPED, NOT_LABELLED, issue=number)\n    user = issue.get("user")'),
        ]
    else:
        replacements = [
            ("import sharecode", "import sharecode\n" + import_line),
            ("        if not labels & LABELS: return",
             '        if not labels & LABELS or classify_community(live, labels) != "other": return'),
        ]
    for before, after in replacements:
        if after in content:
            continue
        if content.count(before) != 1:
            raise SyncError("cannot identify the deployed community guard in " + name)
        content = content.replace(before, after)
    return content.replace("\n", newline).encode("utf-8")


def update_existing(out: Path, repo: Path = REPO) -> list:
    out = Path(out)
    if not (out / "index.json").is_file() or not (out / "submissions.json").is_file():
        raise SyncError("expected a checkout with existing calibration state")
    files = file_set(repo)
    names = set(COMMUNITY_MANAGED)
    seeds_present = {name for name in COMMUNITY_SEEDS if (out / name).exists()}
    if seeds_present and seeds_present != COMMUNITY_SEEDS.keys():
        raise SyncError("incomplete community state; check the existing data before updating")
    if not seeds_present:
        if any((out / "community/v1").glob("*.json")) or any((out / "community/v1").glob("*/*.json")):
            raise SyncError("existing community data has no index or submission ledger")
        names.update(COMMUNITY_SEEDS)
    for name in ("tools/publish.py", "tools/evidence_workflow.py"):
        target = out / name
        if not target.is_file():
            raise SyncError("missing deployed calibration tool: " + name)
        files[name] = refresh_calibration_guard(name, target.read_bytes())
    for name in sorted(names):
        write_managed(out, name, files[name])
    return sorted(names)


def main(argv: list | None = None) -> int:
    parser = argparse.ArgumentParser(description="Assemble the public calibration repository's files.")
    parser.add_argument("--out", required=True, help="new or empty directory to write into")
    parser.add_argument("--update-existing", action="store_true", help="update only community-managed files")
    args = parser.parse_args(argv)
    try:
        names = (update_existing if args.update_existing else sync)(Path(args.out))
    except SyncError as error:
        print("sync_public_repo.py: " + str(error), file=sys.stderr)
        return 1
    for name in names:
        print(name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
