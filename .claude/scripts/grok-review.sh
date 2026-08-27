#!/usr/bin/env bash
# Run Grok's /review-branch inside a container, so the external reviewer gets
# the repository and nothing else.
#
# The disposable worktree this script used to build isolated the reviewer's
# EDITS and never anything else: the process still held this host's filesystem,
# its network and its credentials. `gh` is authenticated here, so a reviewer
# with a terminal could push to a remote that the session which spawned it is
# denied — a deny list bounds the session, never the subprocess it starts.
# Copilot raised exactly that against PR-15 and was right.
#
# The boundary is now .claude/sandbox/Dockerfile: no gh token, no SSH keys, no
# host filesystem beyond the clone below, non-root inside. bypassPermissions is
# still passed and is no longer the risk it was — the blast radius is the
# container, which is the whole reason to have one.
#
# Egress is NOT restricted, and that is one of two residuals, recorded rather
# than hidden. Confining it to api.x.ai needs an allow-list proxy on an internal
# network; Docker alone offers "all" or "none", and "none" stops the review too.
#
# **The credential half is NARROWED, not closed, and this header said "closed"
# for months (#58).** No gh token, no SSH keys and no host filesystem beyond the
# clone — all three genuinely absent. But the fallback path below copies
# ~/.grok/auth.json in, and that file carries a REFRESH-TOKEN-BEARING OAuth
# session for the x.ai account: anything inside can read it and, given the
# unrestricted egress above, post it anywhere. **The two residuals are therefore
# not independent** — the open one is what makes the credential that crosses
# exploitable — and writing them as separate bullet points is what let the
# second one read as settled.
#
# XAI_API_KEY is the documented posture: scoped, revocable, and no file crosses
# at all. The OAuth mount is a fallback, and the ordering below is that posture
# rather than an implementation convenience.
#
# This helper also OWNS the ledger slot it spends. ship.md used to specify the
# accounting as prose — "reserve, then invoke the review helper" — over two
# separately granted commands, so a run that skipped the first spent a check
# that left no record, a resumed run read a lower count, and the PR ran past
# twelve against a paid API. A bound whose two halves are two commands is a
# bound any ordering mistake lifts. Invocation and accounting are one operation
# now: this script takes the slot, RESOLVES the pull request from the branch it
# is about to clone, posts the reservation itself immediately before the model
# call it accounts for, and .claude/settings.json denies the `reserve` and
# `release` spellings to the session that invokes it.
set -euo pipefail

# The ledger, resolved early because the ceiling below is READ out of it rather
# than restated here. #140 was exactly this number living in two files at two
# values — ship.md said six, both helpers accepted twelve — and a second literal
# in this file would be that defect again with a smaller gap. Declared once,
# beside the code that enforces it, which is the SOURCE_INPUTS discipline the
# deploy/** gates arrived at, applied to a scalar.
#
# **Fails closed.** An unreadable ceiling refuses every slot rather than
# admitting all of them: the failure mode of a cap is the direction that must
# never be the quiet one.
ledger="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/grok-ledger.sh"
ceiling=$(sed -n 's/^CEILING=\([1-9][0-9]*\)$/\1/p' "$ledger" | head -1)
[[ "$ceiling" =~ ^[1-9][0-9]*$ ]] ||
  { echo "could not read CEILING from $ledger; refusing to reserve a check" >&2; exit 2; }

# The slot this review spends and which kind of check it is. Validated to the
# ledger's own vocabulary rather than passed through, because a slot outside
# 1..$ceiling is a claim about a cap that does not exist.
#
# **The PR is NOT an argument, and it was one in the first version of this
# change.** A caller-supplied number is a free parameter aimed at the one thing
# this script writes to the outside world: any numeric typo — or an instruction
# that substituted another open pull request — posted the reservation *there*
# while cloning and reviewing THIS branch, so this branch's cap stayed re-armed
# and somebody else's slot was spent. That is the same defect the change was
# written to close, one level up: accounting that can be pointed somewhere other
# than the thing it accounts for. Resolved below from the branch instead, which
# makes the slot and the review provably the same subject.
[ "$#" -eq 2 ] ||
  { echo "usage: grok-review.sh <slot 1-$ceiling> <full|recheck>" >&2; exit 2; }
slot="$1"
mode="$2"
[[ "$slot" =~ ^[1-9][0-9]*$ ]] && [ "$slot" -le "$ceiling" ] ||
  { echo "slot must be 1..$ceiling — the ceiling ship.md states: $slot" >&2; exit 2; }
case "$mode" in
  full|recheck) ;;
  *) echo "mode must be full or recheck: $mode" >&2; exit 2 ;;
esac

# Two patterns, declared together and away from the code that applies them, so
# the suite beside this file has ONE subject to read. That is the SOURCE_INPUTS
# discipline the deploy/** gates arrived at: a value a test asserts about has to
# be declared once, where the test can find it, or the test ends up asserting
# about its own second copy. test_grok_helpers.py extracts both by name and
# exercises them with real payloads.

# What a usage limit looks like, and every spelling here was paid for. The
# preflight below exists to SKIP such a round rather than fail it, and it missed
# an exhausted prepaid balance entirely: `API error (status 402 Payment
# Required): Grok Build usage balance exhausted` is not `429`, not `quota`, and
# not `(no|any) credits`, so PR #117 round 6 took the failure path and burned a
# ledger slot on a review that never started.
#
# **A status code needs a status CONTEXT, not a word boundary.** This pattern is
# matched against the whole text of a probe run, so a bare `402` would also match
# a token count or a request id — and a false positive here is the expensive
# direction: it reports a working reviewer as out of limits and skips every round
# silently, which is a review loop that has stopped reviewing.
#
# `\b402\b` was the first attempt at that and is not enough, which a reviewer
# caught and the suite did not. It excludes `47402` and `4021` — both of which
# were negative cases here — and matches `"input_tokens": 402` exactly, because
# a quote and a space are word boundaries too. The negatives tested digits
# AROUND the number and never the number on its own, so they agreed with the
# comment beside them while the pattern did not.
#
# So the number has to arrive as a status: after the word `status` or `code`
# within a few non-digits (which covers `(status 402 …)`, `status: 429` and
# `"http_status": 402`), or in the provider's own phrase. `429` also reaches the
# prose alternative `too many requests` on its own, and the observed 402 reaches
# `usage balance` and `balance exhausted` as well — the code alternatives are
# the belt to that pair's braces, since the prose is what a provider change
# rewords and the code is what stays.
#
# **And it needs a boundary on BOTH sides**, which the first status-anchored
# version had only on the left: `status 4021` and `http_status: 4290` matched,
# because nothing stopped the code alternative at the third digit. That is the
# same false positive the anchor was introduced to remove, moved from the front
# of the number to the back — the third correction to this one pattern, each
# round finding the side the previous fix had not covered. `([^0-9]|$)` closes
# it, and the suite carries a contextual larger-number negative for each side.
limit_re='rate.?limit|quota|usage limit|usage balance|balance exhausted|too many requests|(no|any) credits|402 payment required|(status|code)[^0-9]{0,3}(402|429)([^0-9]|$)'

# **What a FINISHED turn looks like is PARSED, not matched**, and the journey to
# that is the whole argument. It began as a deny-list of three — refuse
# `cancelled|refusal|error*`, pass everything else — which let a reviewer that
# exhausted its output or turn budget exit 0, write JSON, leave no
# suggestions.md, and have that absence read as a clean verdict. grok's
# documented vocabulary for the field is `end_turn`, `max_tokens`,
# `max_turn_requests`, `refusal` and `cancelled`, so two of the five passed. No
# attacker is needed: a long branch is the ordinary way there, and a long branch
# is when review matters most.
#
# Inverting it to an allow-list of `end_turn` fixed that and left a subtler hole,
# which a reviewer found: a regex cannot tell a ROOT field from a nested one.
# `{"modelUsage":{"stopReason":"end_turn"}}` produced exactly one match, matched
# `end_turn`, and was accepted — a document whose turn never ended, passing the
# check that exists to notice. Nor can grep establish that the output is JSON at
# all, so a truncated write could be read as a verdict.
#
# **A regex over a serialised structure answers a different question from the
# one being asked.** The check below asks jq for the root `stopReason` and
# compares it, which settles the shape, the nesting and the well-formedness in
# one step. The accepted value stays pinned the way the client version is in
# .claude/sandbox/Dockerfile: a grok bump must re-verify it. Verified against
# grok 1.0.5's `--output-format json` and its own headless-mode documentation.
stop_ok=end_turn

# Docker on Windows wants a Windows path in --volume; elsewhere the path is
# already right. cygpath exists only under MSYS/Git Bash, which is the tell.
host_path() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

branch=$(git branch --show-current)
[ -n "$branch" ] || { echo "not on a branch" >&2; exit 2; }
# The pull request the ledger row lands on, resolved from the branch this script
# is about to clone — so the two cannot be different subjects. See the argument
# block above for what a caller-supplied number bought.
#
# **`--head` filters on the branch NAME and nothing else**, which is not the
# question being asked. It matches across forks, so an open pull request from
# someone's fork carrying the same branch name is a candidate here — and
# selecting it would reserve a slot on THAT pull request while cloning and
# reviewing this local branch, which is precisely the mismatch this block
# replaced an argument to prevent. A fix that closes a hole by name and leaves
# it open by provenance has moved the defect rather than removed it.
#
# So the head repository is compared against the one this checkout is, and only
# pull requests from it survive. `gh repo view` reads the checkout, so both
# sides of that comparison are properties of the filesystem rather than of
# anything a caller or a finding supplied.
repo=$(gh repo view --json nameWithOwner --jq .nameWithOwner) ||
  { echo "cannot resolve this checkout's repository" >&2; exit 2; }
# **Blank counts as missing, and `||` does not see it.** `gh` printing an empty
# string exits 0, so the guard above passes and `$repo` is "" — and the
# comparison below then matches every row whose head repository is absent,
# which `// ""` renders as "" too. A deleted fork reports exactly that. So the
# filter would admit a stranger's pull request precisely when it could not
# establish whose it was, which is the fail-open direction.
[ -n "$repo" ] ||
  { echo "this checkout's repository resolved to nothing" >&2; exit 2; }
# Tab-separated and filtered in awk rather than inside --jq, because gh's --jq
# takes no --arg: embedding "$repo" in the jq program would put a shell value
# into a program text, which is the shape this directory exists to avoid.
pr=$(gh pr list --head "$branch" --state open --json number,headRepository \
       --jq '.[] | "\(.headRepository.nameWithOwner // "")\t\(.number)"' |
     awk -F'\t' -v r="$repo" '$1 == r { print $2 }') ||
  { echo "cannot ask GitHub which pull request $branch has" >&2; exit 2; }
# Exactly one, and both other counts are refusals rather than something to guess
# past. None means there is no ledger to write to and therefore no cap to
# enforce, which is a state to stop in, not to review through. More than one is
# ambiguous, and picking either would be inventing the answer to the question
# these lines exist to ask.
[ "$(grep -c . <<<"$pr")" -eq 1 ] ||
  { echo "expected exactly one open pull request for $branch in $repo, found: ${pr:-none}" >&2; exit 2; }
# suggestions.md is the one file allowed to differ — it is the review's own
# working state. Anything else, tracked or untracked, means the reviewer would
# read a state the PR does not carry: the clone below holds only commits.
status=$(git status --porcelain)
[ -z "$(grep -v '^?? suggestions.md$' <<<"$status" || true)" ] ||
  { echo "tree has uncommitted changes; commit before the review, or the reviewer reads a state the PR does not carry" >&2; exit 3; }
# The daemon, not just the CLI. `command -v docker` passes on a machine whose
# Docker Desktop is installed and stopped — which is the common case, not an
# exotic one — and the build then fails with Docker's own generic status
# instead of the exit 7 this script documents. Probe what is actually needed.
docker info >/dev/null 2>&1 ||
  { echo "docker is required and its daemon must be running: the reviewer runs in a container (.claude/sandbox/Dockerfile)" >&2; exit 7; }
# jq, because the verdict below is a JSON document and the question asked of it
# — what is the ROOT stopReason — is not one a regex can answer. Probed here
# rather than discovered at the end of a review, on the same argument as the
# daemon check above: a missing tool should cost a second, not a round.
command -v jq >/dev/null 2>&1 ||
  { echo "jq is required: the reviewer's verdict is JSON and its root stopReason must be parsed rather than matched" >&2; exit 14; }

sandbox=$(cd "$(dirname "${BASH_SOURCE[0]}")/../sandbox" && pwd)
work=$(mktemp -d "${TMPDIR:-/tmp}/grok-review-XXXXXX")
result=$(mktemp "${TMPDIR:-/tmp}/grok-review-result-XXXXXX")
auth=$(mktemp -d "${TMPDIR:-/tmp}/grok-review-auth-XXXXXX")
cleanup() {
  rm -rf "$work" "$auth" 2>/dev/null || true
  rm -f "$result" 2>/dev/null || true
}
trap cleanup EXIT
chmod 700 "$auth"

# A clone, not a worktree. A worktree's .git is a file pointing back into this
# checkout, and that path is precisely what the container must not mount — git
# inside would resolve nothing. A clone carries its own .git and its own
# origin/* refs, which is all /review-branch reads.
git clone --quiet --no-hardlinks . "$work/repo"
git -C "$work/repo" checkout --quiet "$branch"
# The clone's origin/main is built from this checkout's LOCAL main, not from its
# origin/main, and those are routinely different: /branch branches from
# origin/main without ever advancing local main, so local main here is whatever
# it was when it was last pulled. Left alone, the reviewer diffs the branch
# against a stale base and reports commits that are already on main as though
# this branch introduced them. Fetch the real remote-tracking ref, with its
# objects, so the review's base is the base the PR will merge into.
#
# Fetched through the clone's own `origin`, which git already pointed back at
# this checkout, rather than through a path built here: `$(pwd)` under MSYS is
# `/c/dev/...`, which a Windows git binary cannot resolve at all — it reports
# the source as "not a git repository", which reads like a broken checkout
# rather than a mistranslated path.
if git rev-parse --verify --quiet refs/remotes/origin/main >/dev/null; then
  git -C "$work/repo" fetch --quiet origin \
    "+refs/remotes/origin/main:refs/remotes/origin/main"
fi
# The recheck contract: an existing suggestions.md is the file the review
# re-verifies, so it crosses into the copy; nothing else does.
#
# Never through a symlink, in either direction. suggestions.md is untracked and
# the container can replace it with a link — point it at /proc/self/environ and
# a following `cp` reads the environment of whichever process dereferences it,
# carrying this host's credentials into the container on the way in, or host
# content into the imported file on the way out. `cp` follows links by default,
# so the guard has to be explicit and it has to be on both crossings: this is
# the one path through the boundary, which makes it the only one worth
# attacking.
# A regular file or nothing — a symlink is not the only shape that hurts. A
# FIFO or a socket named suggestions.md is reported by git as `?? suggestions.md`
# and so passes the dirty-tree allow-list above, then fails `-f` here and is
# silently skipped, turning an intended recheck into a full review. Anything
# that is not a plain file is refused rather than ignored.
if [ -L suggestions.md ] || { [ -e suggestions.md ] && [ ! -f suggestions.md ]; }; then
  echo "suggestions.md is a symlink or not a regular file; refusing to import it" >&2
  exit 9
fi
[ -f suggestions.md ] &&
  cp -P suggestions.md "$work/repo/suggestions.md"

# Built before the credential check below, which needs the image to run its
# preflight in.
# host_path here too: the build context and -f are paths Docker resolves, not
# paths bash does, so an MSYS spelling reaches the daemon as a directory that
# does not exist — which it reports as a missing context rather than as a path
# it could not translate.
sandbox_host=$(host_path "$sandbox")
# The reviewer's uid must match this host's, or the bind-mounted clone and the
# credential copies — which keep their host ownership — are unreadable and
# unwritable inside. Docker Desktop maps ownership and hides the problem, so
# this matters on Linux and is invisible on Windows. `id -u` is meaningless
# under MSYS, hence the guard.
#
# Native Linux only, not "anything that is not MSYS". On macOS the ids are
# passed by Docker Desktop's VM, which does not need them matched — and a
# typical macOS primary gid of 20 already exists in Debian as `dialout`, so
# `groupadd --gid 20` aborts the build over a problem that host did not have.
#
# Root is refused rather than passed through. Running the helper as root would
# send 0:0, which fails the build outright — root already exists in Debian —
# and would contradict the non-root boundary even if it succeeded. Better to
# say so than to hand back useradd's error.
build_args=()
if [ "$(uname -s)" = "Linux" ]; then
  [ "$(id -u)" -ne 0 ] ||
    { echo "refusing to build the reviewer as root: the image runs non-root by design" >&2; exit 11; }
  build_args+=(--build-arg "REVIEWER_UID=$(id -u)" --build-arg "REVIEWER_GID=$(id -g)")
fi

# The image is used by ID, never by the tag. `grok-reviewer:local` is global to
# the daemon and mutable, so a concurrent review — another checkout, other
# uid/gid arguments — can move it between this build and the runs below, and
# those runs hand the image credentials. Binding to the digest that this build
# produced makes what gets built and what gets trusted the same object.
image=$(docker build --quiet "${build_args[@]}" \
  --file "$sandbox_host/Dockerfile" "$sandbox_host")
[ -n "$image" ] ||
  { echo "docker build produced no image id" >&2; exit 10; }

# Credentials. XAI_API_KEY is the better of the two and needs no file at all:
# grok falls back to it when no session token is present, and a fresh container
# has none. It carries no refresh token and none of the account holder's
# details, so it is tried first — mint one at console.x.ai.
#
# But "set" is not "usable". A key belonging to a team without credits answers
# every call with `403 permission-denied`, and one was set here while the OAuth
# session beside it worked perfectly — so preferring the key blindly broke a
# loop that had been working. The preflight below is a trivial prompt that costs
# a second; learning the same thing from a failed review costs the review, and
# reports it as a reviewer that did not run.
# --env NAME, never --env NAME=VALUE. The second spelling puts the key in
# docker's argv, where every `ps` on this machine can read it for the life of
# the container; the first forwards the value this process already holds.
mounts=()
key_probe=""
if [ -n "${XAI_API_KEY:-}" ] &&
   key_probe=$(docker run --rm --env XAI_API_KEY "$image" \
     grok -p "ok" 2>&1); then
  mounts+=(--env XAI_API_KEY)
else
  # A key that answers with a limit signal is authenticated but out of window,
  # and the two need different exits. With a usable OAuth fallback, fall
  # through: the session may sit on a team whose window is open, and the
  # preflight below judges whatever auth was actually selected. Usable means
  # all three session files — auth.json alone with agent_id or config.toml
  # missing exits 8 below before the preflight ever runs, so a partial
  # session must not swallow the limit signal. With no usable fallback,
  # exit 8's "cannot authenticate" is the wrong class — authenticating is not
  # the problem — so this is the preflight's skip, issued one step earlier.
  oauth_ready=1
  for f in auth.json agent_id config.toml; do
    [ -f "$HOME/.grok/$f" ] || oauth_ready=0
  done
  if [ -n "${XAI_API_KEY:-}" ] && [ "$oauth_ready" = 0 ] &&
     grep -qiE "$limit_re" <<<"$key_probe"; then
    echo "grok is out of usage limits (API key, no usable OAuth fallback) — skipping this review, not failing it:" >&2
    grep -ioE "$limit_re" <<<"$key_probe" | head -1 >&2
    exit 12
  fi
  [ -z "${XAI_API_KEY:-}" ] ||
    echo "XAI_API_KEY is set but did not authenticate — no credits on its team? Using the OAuth session instead." >&2
  [ -f "$HOME/.grok/auth.json" ] ||
    { echo "no usable XAI_API_KEY and no $HOME/.grok/auth.json: the reviewer cannot authenticate" >&2; exit 8; }
  cp "$HOME/.grok/auth.json" "$auth/auth.json"
  chmod 600 "$auth/auth.json"
  mounts+=(--volume "$(host_path "$auth/auth.json"):/home/reviewer/.grok/auth.json:Z")
  # agent_id and config.toml as well, and neither is optional. The session names
  # a team, but a container missing this machine's agent identity and its
  # settled configuration does not use it: grok takes itself to be a first run,
  # registers a *new* team, and every call returns
  # `403 permission-denied: your newly created team doesn't have any credits`
  # while the host keeps working against the very same auth.json. Found the only
  # way it could be — by adding one file at a time until the review completed.
  #
  # Three files, all copies, all discarded afterwards. None of them is a
  # credential for anything but grok: no gh token, no SSH key, no cloud profile.
  # Required, not optional, and failing here rather than skipping is the whole
  # point: a run missing either of these does not degrade, it dies at the model
  # call with a 403 about a team nobody created on purpose. Saying so now costs
  # a second; discovering it costs the review.
  for extra in agent_id config.toml; do
    [ -f "$HOME/.grok/$extra" ] ||
      { echo "$HOME/.grok/$extra is missing; the OAuth session cannot resolve its team without it" >&2; exit 8; }
    cp "$HOME/.grok/$extra" "$auth/$extra"
    chmod 600 "$auth/$extra"
    mounts+=(--volume "$(host_path "$auth/$extra"):/home/reviewer/.grok/$extra:Z")
  done
fi

# Usage-limit preflight — SKIP, never fail. Authentication being good is not the
# same as the team being inside its window: a rate limit or an exhausted quota
# answers the model call, not the handshake, so the credential probe above
# passes and the review then dies mid-run and reports as "did not run" (exit 4).
# A review the limits will not currently allow is not a defect in the branch and
# must not halt the loop as one — the caller skips this round and moves on. The
# exit is distinct (12) precisely so ship.md can tell a skip from a failure: a
# failure stops the loop, a skip does not. One extra probe against the auth that
# was actually selected, so the OAuth path (which the block above never probes)
# is covered too.
probe_rc=0
limit_probe=$(docker run --rm "${mounts[@]}" "$image" grok -p "ok" 2>&1) || probe_rc=$?
if grep -qiE "$limit_re" <<<"$limit_probe"; then
  echo "grok is out of usage limits — skipping this review, not failing it:" >&2
  grep -ioE "$limit_re" <<<"$limit_probe" | head -1 >&2
  exit 12
fi
# A dead fallback must not bury the key's limit signal. File presence made
# OAuth the selected auth, but an expired, revoked or corrupt session fails
# this probe with an auth-shaped answer, not a limit-shaped one — and
# proceeding would burn a full review run to reach exit 4 and learn what
# both probes already said. The key's limit is the operative fact; skip.
if [ "$probe_rc" -ne 0 ] && [ -n "${XAI_API_KEY:-}" ] &&
   grep -qiE "$limit_re" <<<"$key_probe"; then
  echo "grok is out of usage limits (API key) and the selected OAuth fallback failed its probe — skipping this review, not failing it:" >&2
  grep -ioE "$limit_re" <<<"$key_probe" | head -1 >&2
  exit 12
fi

# The reservation, and its POSITION is the accounting rule rather than an
# implementation detail: **every path that can refuse before this line spends
# nothing** — a dirty tree, no daemon, a missing credential, a bad
# suggestions.md shape, and all three of the usage-limit skips above. That is
# why exit 12 has no release to post and why the release verb has no caller left
# in this repository.
#
# Stated as an ordering rather than as "spent if and only if the model call was
# launched", which is what this comment used to say and is not true of the
# failed-read case argued below. One file must not define two contracts. ship.md's argument for writing before the call is interruption
# safety, and the window this leaves is the microseconds between posting and
# `docker run`; written after, an interrupted run spends a check and leaves no
# record, and the resumed run spends a thirteenth.
#
# The write is also an election. Two resumed /ship runs can read the same count
# and claim the same slot, and the ledger settles it after posting: a loser
# exits 4, which arrives here as exit 13 and means a concurrent run is mid-check
# on this PR. Stop the loop rather than take the next slot — two Grok runs share
# one root suggestions.md, and the later finisher would overwrite the earlier's
# findings or pass off its rival's clean pass as its own convergence.
#
# One exit code for both a lost election and an unreachable ledger, because the
# caller does the same thing with each: do not count this round, and stop.
#
# **"If and only if" has one exception, and it is deliberate rather than
# overlooked.** The ledger posts its comment and *then* reads the rows to settle
# the election, so a trust-check failure on that read leaves the reservation
# standing while this helper exits 13 before `docker run` — a slot spent on a
# model call that was never launched. Conservative on purpose: after a failed
# read the state is exactly what is not known, and releasing on it would hand a
# slot back on the strength of a lookup that did not complete, which is the
# fail-open this file spent a branch closing. The cost is at most one wasted
# check out of $ceiling, and it is bounded; guessing the other way is not.
#
# So the contract is: a slot is spent if the review's model call was launched,
# and may also be spent when the ledger could not finish settling its own
# election. A lost election does *not* add a spend — `count` folds duplicate
# rows for a slot into one — so this exception is the failed-read case alone.
#
# `$ledger` was resolved at the head of this file, where the ceiling is read out
# of it. It is deliberately not recomputed here: two spellings of one path is
# the same class of duplication as two spellings of one bound.
ledger_rc=0
bash "$ledger" "$pr" reserve "$slot" "$mode" >&2 || ledger_rc=$?
[ "$ledger_rc" -eq 0 ] ||
  { echo "could not reserve check $slot/$ceiling on PR $pr (ledger exit $ledger_rc); the review did not run" >&2; exit 13; }

set +e
docker run --rm \
  --volume "$(host_path "$work/repo"):/review:Z" \
  "${mounts[@]}" \
  --workdir /review \
  "$image" \
  grok -p "/review-branch" --permission-mode bypassPermissions --output-format json >"$result"
grok_status=$?
set -e

# A review that did not run must never be mirrored into a clean verdict. An
# absent suggestions.md means both "nothing to report" and "the reviewer never
# looked", and only these checks separate them — the same fail-open shape §13.5
# names for an empty readiness predicate set.
[ "$grok_status" -eq 0 ] ||
  { echo "grok exited $grok_status; the review did not run" >&2; exit 4; }
[ -s "$result" ] ||
  { echo "grok produced no output; the review did not run" >&2; exit 5; }
# The allow-list declared at the head of this file, applied to the ROOT of the
# document. Three failures collapse into one question, which is the point of
# parsing rather than matching: output that is not JSON, output whose root
# carries no stopReason, and output whose root carries the wrong one.
#
# A mention inside the review's own prose cannot be mistaken for the verdict
# either — `.stopReason` names a field, where a regex only ever named a
# substring.
# Reduce a reviewer-supplied field to something that cannot carry an
# instruction: an identifier alphabet, truncated. `tr -cd` deletes the
# complement of the set, so newlines, quotes and spaces are gone rather than
# escaped — escaping is a property of the consumer and this value is printed
# straight to a terminal and into /ship's context.
safe_token() {
  printf '%.40s' "$(printf '%s' "$1" | tr -cd 'A-Za-z0-9_.-')"
}

stop=$(jq -r 'if type == "object" then (.stopReason // "<absent>") else "<not-an-object>" end' \
         "$result" 2>/dev/null) ||
  { echo "grok's output is not valid JSON; the review did not run and suggestions.md is left as it was" >&2; exit 6; }
if [ "$stop" != "$stop_ok" ]; then
  # **The rejected path crossed the boundary the accepted path does not (#52).**
  # `$stop` and `.cancellationCategory` are fields of a document the reviewer
  # wrote, and both were echoed verbatim — so a run that produced no clean
  # verdict handed /ship reviewer-authored prose anyway, complete with any
  # newlines it chose. Removing `cat "$result"` closed the success path and
  # left this one open, which is what a fix aimed at a line rather than at a
  # property looks like.
  #
  # Both are now reduced to a token alphabet before they are printed. A real
  # stopReason or cancellation category is a bare identifier, so nothing
  # diagnostic is lost; anything else arrives as the characters of it that
  # could not carry an instruction.
  category_raw=$(jq -r '.cancellationCategory // empty' "$result" 2>/dev/null)
  category=$(safe_token "$category_raw")
  [ -z "$category" ] || echo "grok reported cancellation category: $category" >&2
  echo "grok did not finish its turn — the root stopReason is \"$(safe_token "$stop")\", not \"$stop_ok\"; the review did not run and suggestions.md is left as it was" >&2
  exit 6
fi
# **The verdict is extracted; the transcript is not printed (#52).** Every byte
# of $result is reviewer-authored, and `cat`ting it put that text into the
# caller's context as prose — where /review-grok reads it holding `Edit` and
# `Write`, and /ship runs that triage unattended in a loop. Nothing ever read
# this stdout: ship.md's step 5 branches on the exit code and on whether
# suggestions.md exists, review-grok.md names no stdout at all, and
# test_grok_helpers.py asserts nothing about it. So the transcript was a second,
# unguarded crossing that bought no caller anything.
#
# The findings still cross, deliberately and by ONE route: suggestions.md,
# imported below under the shape guards. One reviewer-controlled artefact, named
# and checked, beats the same text arriving twice with only one arrival guarded.
echo "grok finished its turn (stopReason \"$stop\") — findings, if any, are in suggestions.md" >&2
# Import the one artefact the review owns. Its absence is the clean verdict —
# trustworthy only because the checks above have ruled out a cancelled run.
#
# The symlink guard again, and this is the crossing that matters most: the file
# is now reviewer-controlled, so a link planted inside the container would be
# dereferenced here, in a host process, against host paths. Rejected rather than
# resolved, and the destination is removed first so a pre-existing link on this
# side cannot be written through either.
#
# Every non-regular shape is refused, not only symlinks, and the check happens
# BEFORE the host's copy is removed. Ordered the other way this is a fail-open
# straight across the boundary: a FIFO, socket or directory left behind by the
# reviewer would delete the findings already on this side, fail `-f`, copy
# nothing, and let the run report itself clean — which is the precise failure
# this loop's verdict checks exist to make impossible.
out="$work/repo/suggestions.md"
if [ -L "$out" ] || { [ -e "$out" ] && [ ! -f "$out" ]; }; then
  echo "the review left suggestions.md as a symlink or not a regular file; refusing to import it" >&2
  exit 9
fi
rm -f suggestions.md
if [ -f "$out" ]; then
  cp -P "$out" suggestions.md
fi
