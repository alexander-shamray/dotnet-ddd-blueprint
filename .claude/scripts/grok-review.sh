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
# Egress is NOT restricted, and that is the remaining residual, recorded rather
# than hidden. Confining it to api.x.ai needs an allow-list proxy on an internal
# network; Docker alone offers "all" or "none", and "none" stops the review too.
# The credential half is what the finding named, and it is what this closes.
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

# The slot this review spends and which kind of check it is. Validated to the
# ledger's own vocabulary rather than passed through, because a slot outside
# 1..12 is a claim about a cap that does not exist.
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
  { echo "usage: grok-review.sh <slot 1-12> <full|recheck>" >&2; exit 2; }
slot="$1"
mode="$2"
[[ "$slot" =~ ^([1-9]|1[0-2])$ ]] ||
  { echo "slot must be 1..12 — the ledger's whole vocabulary: $slot" >&2; exit 2; }
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
limit_re='rate.?limit|quota|usage limit|usage balance|balance exhausted|too many requests|(no|any) credits|402 payment required|(status|code)[^0-9]{0,3}(402|429)'

# What a FINISHED turn looks like, and this one is an allow-list because it was
# a deny-list of three and that is the same fail-open one level on. It used to
# refuse `cancelled|refusal|error*` and pass everything else — but grok's
# documented vocabulary for this field is `end_turn`, `max_tokens`,
# `max_turn_requests`, `refusal` and `cancelled`, so a reviewer that exhausted
# its output budget or its turn budget exited 0, wrote JSON, left no
# suggestions.md, and had that absence read as a clean verdict. No attacker is
# needed: a long branch is the ordinary way to reach it, and a long branch is
# when review matters most. One that wants it can also buy it, by making the
# diff large enough to blow the reviewer's budget — and under the two-clean-
# passes rule two such rounds end the loop.
#
# So the only accepted terminal state is `end_turn`, and every other value —
# including the field being absent — is "did not run". Pinned the way the
# client version is pinned in .claude/sandbox/Dockerfile: a grok bump must
# re-verify this string. Verified against grok 1.0.5's `--output-format json`
# and its own headless-mode documentation.
stop_any_re='"stopReason"[[:space:]]*:[[:space:]]*"[^"]*"'
stop_ok_re='^"stopReason"[[:space:]]*:[[:space:]]*"end_turn"$'

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
# Exactly one open pull request, and both other counts are refusals rather than
# something to guess past. None means there is no ledger to write to and
# therefore no cap to enforce, which is a state to stop in, not to review
# through. More than one is ambiguous, and picking either would be inventing
# the answer to the question this line exists to ask.
pr=$(gh pr list --head "$branch" --state open --json number --jq '.[].number') ||
  { echo "cannot ask GitHub which pull request $branch has" >&2; exit 2; }
[ "$(grep -c . <<<"$pr")" -eq 1 ] ||
  { echo "expected exactly one open pull request for $branch, found: ${pr:-none}" >&2; exit 2; }
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
# implementation detail: a slot is spent if and only if the review's own model
# call was launched. Everything that can refuse before this line — a dirty
# tree, no daemon, a missing credential, a bad suggestions.md shape, and all
# three of the usage-limit skips above — spends nothing, which is why exit 12
# has no release to post and why the release verb has no caller left in this
# repository. ship.md's argument for writing before the call is interruption
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
ledger="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/grok-ledger.sh"
ledger_rc=0
bash "$ledger" "$pr" reserve "$slot" "$mode" >&2 || ledger_rc=$?
[ "$ledger_rc" -eq 0 ] ||
  { echo "could not reserve check $slot/12 on PR $pr (ledger exit $ledger_rc); the review did not run" >&2; exit 13; }

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
# The allow-list declared at the head of this file, applied. EXACTLY one
# stopReason, and it must be end_turn.
#
# Exactly one rather than "the last one": `--output-format json` emits a single
# object carrying a single such field, so a second occurrence means the output
# shape changed under the pin and the pin has to be re-read — which is a build
# this loop should stop on, not one it should guess at. A mention inside the
# review's own prose cannot be miscounted, because JSON escapes the quotes and
# `\"stopReason\"` never presents the `"` this pattern needs.
stop_reasons=$(grep -oE "$stop_any_re" "$result" || true)
stop_count=$(grep -c . <<<"$stop_reasons" || true)
if [ "$stop_count" -ne 1 ] || ! grep -qE "$stop_ok_re" <<<"$stop_reasons"; then
  [ -z "$stop_reasons" ] || printf '%s\n' "$stop_reasons" >&2
  grep -oE '"cancellationCategory"[[:space:]]*:[[:space:]]*"[^"]*"' "$result" >&2 || true
  echo "grok did not finish its turn — expected exactly one stopReason of \"end_turn\", saw $stop_count; the review did not run and suggestions.md is left as it was" >&2
  exit 6
fi
cat "$result"
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
