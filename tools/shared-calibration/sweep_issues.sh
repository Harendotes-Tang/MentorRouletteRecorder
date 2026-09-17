#!/usr/bin/env bash
# 补处理漏掉的共享校准 Issue / Publish the submissions a queued run missed.
#
# Run on a schedule by .github/workflows/publish-calibration.yml, from the repository root:
#
#     bash tools/sweep_issues.sh
#
# The workflow uses one concurrency group so two submissions never race on index.json, and GitHub
# keeps at most ONE pending run per group - a newer pending run cancels the older one. During a
# burst (patch day) some issue runs are therefore cancelled before they start. This sweep publishes
# every open submission without an answer label, one at a time, through tools/publish_issue.sh.
#
# Needs git, python and gh, with GH_TOKEN and GH_REPO set and RUNNER_TEMP pointing at scratch space.
# Issue text is never read here; tools/publish.py lists the numbers and wraps each issue as an event.
set -euo pipefail

py() {
  python -B tools/publish.py "$@"
}

main() {
  local work="${RUNNER_TEMP:?RUNNER_TEMP is not set}/sweep"
  local number failed=0
  rm -rf "$work"
  mkdir -p "$work"
  gh api --paginate "repos/$GH_REPO/issues?state=open&labels=share-calibration&per_page=100" > "$work/open.json"
  py pending --issues "$work/open.json" > "$work/numbers.txt"
  while read -r number; do
    gh api "repos/$GH_REPO/issues/$number" > "$work/issue-$number.json"
    py wrap-event --issue "$work/issue-$number.json" --out "$work/event-$number.json"
    if ! bash tools/publish_issue.sh "$work/event-$number.json"; then
      echo "::error::issue #$number was not published"
      failed=1
    fi
  done < "$work/numbers.txt"
  return "$failed"
}

main "$@"; exit "$?"
