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
command -v docker >/dev/null 2>&1 ||
  { echo "docker is required: the reviewer runs in a container (.claude/sandbox/Dockerfile)" >&2; exit 7; }

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
if [ -e suggestions.md ] || [ -L suggestions.md ]; then
  [ -L suggestions.md ] &&
    { echo "suggestions.md is a symlink; refusing to import it" >&2; exit 9; }
  [ -f suggestions.md ] &&
    cp --no-dereference suggestions.md "$work/repo/suggestions.md"
fi

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
build_args=()
if ! command -v cygpath >/dev/null 2>&1; then
  build_args+=(--build-arg "REVIEWER_UID=$(id -u)" --build-arg "REVIEWER_GID=$(id -g)")
fi
docker build --quiet --tag grok-reviewer:local "${build_args[@]}" \
  --file "$sandbox_host/Dockerfile" "$sandbox_host" >/dev/null

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
   docker run --rm --env XAI_API_KEY grok-reviewer:local \
     grok -p "ok" >/dev/null 2>&1; then
  mounts+=(--env XAI_API_KEY)
else
  [ -z "${XAI_API_KEY:-}" ] ||
    echo "XAI_API_KEY is set but did not authenticate — no credits on its team? Using the OAuth session instead." >&2
  [ -f "$HOME/.grok/auth.json" ] ||
    { echo "no usable XAI_API_KEY and no $HOME/.grok/auth.json: the reviewer cannot authenticate" >&2; exit 8; }
  cp "$HOME/.grok/auth.json" "$auth/auth.json"
  chmod 600 "$auth/auth.json"
  mounts+=(--volume "$(host_path "$auth/auth.json"):/home/reviewer/.grok/auth.json")
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
    mounts+=(--volume "$(host_path "$auth/$extra"):/home/reviewer/.grok/$extra")
  done
fi

set +e
docker run --rm \
  --volume "$(host_path "$work/repo"):/review" \
  "${mounts[@]}" \
  --workdir /review \
  grok-reviewer:local \
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
if [ -L "$work/repo/suggestions.md" ]; then
  echo "the review left suggestions.md as a symlink; refusing to import it" >&2
  exit 9
fi
rm -f suggestions.md
if [ -f "$work/repo/suggestions.md" ]; then
  cp --no-dereference "$work/repo/suggestions.md" suggestions.md
fi
