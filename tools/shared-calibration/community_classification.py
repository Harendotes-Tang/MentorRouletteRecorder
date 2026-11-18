"""One category rule for issue event handlers, sweep, and calibration guards."""

MARKER = "<!-- mentor-recorder:community-content:v1 -->"
LABEL = "community-content"
CALIBRATION = {"share-calibration", "calibration-report", "calibration-evidence", "template-observation"}


def labels(issue):
    return {label.get("name") for label in issue.get("labels", []) if isinstance(label, dict)}


def classify(issue, current_labels=None):
    issue_labels = labels(issue) if current_labels is None else current_labels
    body = issue.get("body") or ""
    lines = body.splitlines()
    marker = bool(lines and (lines[0].strip() == MARKER or
                   (lines[0].strip() == "### Community submission" and
                    next((line.strip() for line in lines[1:] if line.strip()), "") == MARKER)))
    if not (marker or LABEL in issue_labels):
        return "other"
    return "conflict" if issue_labels & CALIBRATION else "community"
