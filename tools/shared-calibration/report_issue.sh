#!/usr/bin/env bash
# 回复一个「报告校准有误」Issue / Answer one report-calibration issue.
#
# Run by .github/workflows/report-calibration.yml from the repository root:
#
#     bash tools/report_issue.sh <event.json>
#
# <event.json> is a GitHub `issues` event. Needs python and gh, with GH_TOKEN and GH_REPO set and
# RUNNER_TEMP pointing at scratch space.
#
# This script never reads issue text: it stays in the event file, and only tools/publish.py reads it.
# Every value used below comes back from publish.py already validated (an issue number, a fixed
# status, the two fixed labels). Nothing here changes a file in the repository, closes the issue or
# takes a calibration out of use: a report is a reason for a maintainer to look, and a count of
# reports must never be able to bring a working calibration down.
set -euo pipefail

py() {
  python -B tools/publish.py "$@"
}

answer() {
  local number="$1" out="$2" event="$3" live="$4"
  local status labels label
  py report --event "$event" --live "$live" --out "$out"
  status="$(py field --out "$out" --name status)"
  if [ "$status" = skipped ]; then
    echo "issue #$number: nothing to do"
    return 0
  fi
  gh issue comment "$number" --body-file "$out/comment.md"
  # A plain assignment, so a failing `py field` trips errexit and nothing is labelled; `for` over a
  # command substitution would swallow the failure and quietly iterate over nothing. The value is
  # one whitelisted constant, so splitting it on spaces is how the two labels arrive. Creating a
  # label that already exists fails harmlessly.
  labels="$(py field --out "$out" --name labels)"
  for label in $labels; do
    gh label create "$label" > /dev/null 2>&1 || true
    gh issue edit "$number" --add-label "$label"
  done
}

main() {
  local event="${1:?usage: bash tools/report_issue.sh <event.json>}"
  local number work
  number="$(py event-field --event "$event" --name number)"
  work="${RUNNER_TEMP:?RUNNER_TEMP is not set}/report-$number"
  rm -rf "$work"
  mkdir -p "$work"

  # The event may be stale or redelivered, and a report is left open rather than closed, so whether
  # it is still open and still unanswered is read from the issue as it is now, never from the event.
  gh issue view "$number" --json state,labels > "$work/live.json"

  answer "$number" "$work/out" "$event" "$work/live.json"
}

main "$@"; exit "$?"
