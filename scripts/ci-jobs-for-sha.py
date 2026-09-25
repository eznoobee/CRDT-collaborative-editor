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

AND THE SECOND TIME IT CRIED WOLF, IT WAS THIS. The paragraph above was
written about an embedded here-string and is kept because the lesson recurred by
a different route: `fetch` returned `{}` for every failure, so a 301, a 403, a
spent rate limit and a genuine absence of runs were one answer. The owner
account was renamed, api.github.com began answering 301, and this printed
`0 0 0` — which push.sh reads, correctly, as "NO RUN AT ALL". CI was running
the whole time.

So "asked and got nothing" and "could not ask" are now different exits. A gate
may say a thing is broken, or say it does not know; it may never say the first
when it means the second. Redirects are followed, and the diagnostic names the
status so the next cause is visible rather than inferred.

  ci-jobs-for-sha.py <owner/repo> <sha>

Exit 0 with three numbers on stdout, or exit 3 with a reason on stderr and
nothing on stdout.
"""

import json
import os
import subprocess
import sys


class Unreachable(Exception):
    """The API did not answer with something countable."""


def fetch(url: str) -> dict:
    # -L because a renamed owner or repository answers 301 and the body of a
    # redirect has no runs in it. -w to capture the final status: a checker that
    # cannot see the status cannot tell empty from refused.
    #
    # The token when there is one. Unauthenticated works against a public
    # repository and is rate-limited to sixty an hour, which this script can
    # spend in one push — and a spent limit used to read as "no runs".
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN") or ""
    command = ["curl", "-sSL", "-w", "\n%{http_code}", url]
    if token:
        command[1:1] = ["-H", "Authorization: Bearer " + token]

    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        raise Unreachable("curl failed for " + url + ": " + result.stderr.strip())

    body, _, status = result.stdout.rpartition("\n")
    if status.strip() != "200":
        raise Unreachable("HTTP " + status.strip() + " for " + url)

    try:
        return json.loads(body)
    except (json.JSONDecodeError, ValueError):
        raise Unreachable("unparseable JSON from " + url)


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__.strip().splitlines()[-3], file=sys.stderr)
        return 2

    repo, sha = sys.argv[1], sys.argv[2]
    api = "https://api.github.com/repos/" + repo + "/actions"

    try:
        runs = fetch(api + "/runs?per_page=30&head_sha=" + sha).get("workflow_runs", [])
        jobs = 0
        empty = 0
        for run in runs:
            mine = len(fetch(api + "/runs/" + str(run["id"]) + "/jobs?per_page=50").get("jobs", []))
            jobs += mine
            if mine == 0:
                empty += 1
    except Unreachable as why:
        # NOT "0 0 0". That is a verdict, and this is an absence of one.
        print("cannot determine CI state: " + str(why), file=sys.stderr)
        return 3

    print(str(len(runs)) + " " + str(jobs) + " " + str(empty))
    return 0


if __name__ == "__main__":
    sys.exit(main())
