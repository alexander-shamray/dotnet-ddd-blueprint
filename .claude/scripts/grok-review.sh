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
set -euo pipefail

# Docker on Windows wants a Windows path in --volume; elsewhere the path is
# already right. cygpath exists only under MSYS/Git Bash, which is the tell.
host_path() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

branch=$(git branch --show-current)
[ -n "$branch" ] || { echo "not on a branch" >&2; exit 2; }
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
if [ -n "${XAI_API_KEY:-}" ] &&
   docker run --rm --env XAI_API_KEY "$image" \
     grok -p "ok" >/dev/null 2>&1; then
  mounts+=(--env XAI_API_KEY)
else
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
limit_probe=$(docker run --rm "${mounts[@]}" "$image" grok -p "ok" 2>&1) || true
if grep -qiE 'rate.?limit|429|quota|usage limit|too many requests|(no|any) credits' <<<"$limit_probe"; then
  echo "grok is out of usage limits — skipping this review, not failing it:" >&2
  grep -ioE 'rate.?limit|429|quota|usage limit|too many requests|(no|any) credits' <<<"$limit_probe" | head -1 >&2
  exit 12
fi

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
if grep -qE '"stopReason"[[:space:]]*:[[:space:]]*"(cancelled|refusal|error[^"]*)"' "$result"; then
  grep -oE '"(stopReason|cancellationCategory)"[[:space:]]*:[[:space:]]*"[^"]*"' "$result" >&2 || true
  echo "grok stopped early; the review did not run and suggestions.md is left as it was" >&2
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
