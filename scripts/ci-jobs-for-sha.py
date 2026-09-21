#!/usr/bin/env python3
"""What GitHub has for one commit: runs, jobs, and runs with no jobs at all.

Prints three numbers and nothing else, so a shell can `read` them. Used by
scripts/push.sh to answer the question that would have caught every silent CI
stoppage this repository has had: after a push, is there a run WITH JOBS for
this exact sha?

THE THIRD NUMBER IS THE POINT, and it was nearly left out. The first version
printed runs and total jobs, and checking `jobs > 0` would NOT have caught the
7b.12 outage: this repository has two workflows, the mutation one was
untouched, and every commit in that window reports `2 runs, 1 job` — one job,
from the workflow that was fine. The broken workflow contributed zero and the
total was still non-zero.

Verified against the window itself rather than argued: 52b8769, 322a0c0 and
443c271 each give `2 1 1`, and 82c92de, the first healthy commit after the
fix, gives `2 16 0`. A gate that counts across the things it is checking cannot
see one of them stop.

Its own file rather than a here-string inside push.sh. The first version was
embedded, and embedding Python inside a single-quoted shell string means every
quote in an f-string has to be escaped past two parsers; it was syntactically
broken and the script would have reported "no jobs" for a healthy push, which
is the failure mode of a checker that cries wolf.

  ci-jobs-for-sha.py <owner/repo> <sha>
"""

import json
import subprocess
import sys


def fetch(url: str) -> dict:
    result = subprocess.run(["curl", "-sS", url], capture_output=True, text=True)
    try:
        return json.loads(result.stdout)
    except (json.JSONDecodeError, ValueError):
        return {}


def main() -> int:
    if len(sys.argv) != 3:
        print("0 0 0")
        return 2

    repo, sha = sys.argv[1], sys.argv[2]
    api = "https://api.github.com/repos/" + repo + "/actions"

    runs = fetch(api + "/runs?per_page=30&head_sha=" + sha).get("workflow_runs", [])
    jobs = 0
    empty = 0
    for run in runs:
        mine = len(fetch(api + "/runs/" + str(run["id"]) + "/jobs?per_page=50").get("jobs", []))
        jobs += mine
        if mine == 0:
            empty += 1

    print(str(len(runs)) + " " + str(jobs) + " " + str(empty))
    return 0


if __name__ == "__main__":
    sys.exit(main())
