#!/usr/bin/env bash
# 发布一个共享校准 Issue / Publish one share-calibration issue.
#
# Run by .github/workflows/publish-calibration.yml (and tools/sweep_issues.sh) from the repository root:
#
#     bash tools/publish_issue.sh <event.json>
#
# <event.json> is a GitHub `issues` event, or {"issue": <REST issue>} written by tools/sweep_issues.sh.
# Needs git, python and gh, with GH_TOKEN and GH_REPO set and RUNNER_TEMP pointing at scratch space.
#
# This script never reads issue text. The title, body and submitter's login stay in JSON files and
# only tools/publish.py reads them; every value used below comes back from publish.py already
# validated (an issue number, a login of [A-Za-z0-9-], a fixed status, label or close reason, a code
# path of the repository's own layout, a commit message built from validated values).
#
# Why everything is inside functions and the last line ends with `exit`: `git reset --hard` below can
# replace this very file with a newer version, and bash reads a script while it runs. A function is
# parsed completely before it starts, and `exit` stops bash before it reads anything after that line.
set -euo pipefail

py() {
  python -B tools/publish.py "$@"
}

read_issue() {
  gh api "repos/$GH_REPO/issues/$1" > "$2"
}

still_current() {
  local number="$1" snapshot="$2" current="$3"
  shift 3
  read_issue "$number" "$current"
  py unchanged --issue "$snapshot" --live "$current" "$@"
}

# Decide, commit and push, starting again from the new main whenever someone else pushed first. A
# rebase would not do: index.json pins a new code to the commit that added its file, and a rebase
# rewrites that commit, so every retry recomputes both commits on top of the current main.
publish_loop() {
  local event="$1" account="$2" out="$3" live="$4" number="$5"
  local attempt status file login
  git config user.name 'github-actions[bot]'
  git config user.email '41898282+github-actions[bot]@users.noreply.github.com'
  for attempt in 1 2 3 4 5; do
    git fetch --quiet origin main
    git reset --quiet --hard origin/main
    git clean --quiet -fd
    rm -rf "$out"
    mkdir -p "$out"
    # A queued event and a failed push can both outlive an edit. Every attempt uses the latest author
    # and contents, and a changed snapshot is discarded before it can be pushed.
    read_issue "$number" "$live"
    echo '{}' > "$account"
    if login="$(py event-field --event "$event" --live "$live" --name login)"; then
      gh api "users/$login" > "$account" || echo '{}' > "$account"
    fi
    py check --repo . --event "$event" --account "$account" --live "$live" --out "$out"
    status="$(py field --out "$out" --name status)"
    case "$status" in
      published)
        file="$(py field --out "$out" --name file)"
        git add -- "$file"
        # Normally a new file. If main already holds this code's file, HEAD is a commit that contains it.
        if ! git diff --cached --quiet; then
          git commit --quiet -m "$(py field --out "$out" --name code_commit_message)"
        fi
        py update-index --repo . --event "$event" --account "$account" --live "$live" --out "$out" \
          --expect published --commit "$(git rev-parse HEAD)"
        git add -- index.json submissions.json
        git commit --quiet -m "$(py field --out "$out" --name index_commit_message)"
        ;;
      added)
        py update-index --repo . --event "$event" --account "$account" --live "$live" --out "$out" --expect added
        git add -- index.json submissions.json
        git commit --quiet -m "$(py field --out "$out" --name index_commit_message)"
        ;;
      *)
        return 0
        ;;
    esac
    if ! still_current "$number" "$live" "$live.current"; then
      echo "issue #$number changed before push; reading the latest submission"
      continue
    fi
    if git push --quiet origin HEAD:main; then
      return 0
    fi
    echo "main moved while publishing (attempt $attempt of 5); starting again from the new main"
    sleep "$attempt"
  done
  py push-failed --out "$out"
}

# Comment, label and close. An `error` result is answered too, then fails the job so a maintainer sees it.
report() {
  local number="$1" out="$2" work="$3" live="$4"
  local status label close_reason
  status="$(py field --out "$out" --name status)"
  if [ "$status" = skipped ]; then
    echo "issue #$number: nothing to do"
    return 0
  fi
  label="$(py field --out "$out" --name label)"
  close_reason="$(py field --out "$out" --name close_reason)"
  if ! still_current "$number" "$live" "$live.current"; then
    echo "issue #$number changed before reply; leaving the current submission unanswered"
    return 0
  fi
  gh issue comment "$number" --body-file "$out/comment.md"
  : > "$work/replied"
  # The comment itself updates updated_at. Its title/body/author must still match before closing or
  # applying an answer label, so an edit during reporting remains eligible for its queued run/sweep.
  if ! still_current "$number" "$live" "$live.current" --after-comment; then
    echo "issue #$number changed while replying; leaving the current submission open"
    return 0
  fi
  # Close first: a closed issue is never picked up again, even if labelling fails afterwards.
  if [ -n "$close_reason" ]; then
    gh issue close "$number" --reason "$close_reason"
  fi
  if [ -n "$label" ]; then
    # Creating a label that already exists fails harmlessly; a missing label must not leave the
    # issue unanswered for the sweep to comment on again.
    gh label create "$label" > /dev/null 2>&1 || true
    gh issue edit "$number" --add-label "$label"
  fi
  if [ "$status" = error ]; then
    echo "::error::issue #$number needs a maintainer; the reason is in the comment on the issue"
    return 1
  fi
}

main() {
  local event="${1:?usage: bash tools/publish_issue.sh <event.json>}"
  local number work account live out
  number="$(py event-field --event "$event" --name number)"
  work="${RUNNER_TEMP:?RUNNER_TEMP is not set}/publish-$number"
  rm -rf "$work"
  mkdir -p "$work"
  account="$work/account.json"
  live="$work/live.json"
  out="$work/out"

  publish_loop "$event" "$account" "$out" "$live" "$number"
  report "$number" "$out" "$work" "$live"
}

main "$@"; exit "$?"
