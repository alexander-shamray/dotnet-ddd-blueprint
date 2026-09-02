# The external reviewer's boundary.
#
# grok-review.sh isolated the reviewer's *edits* from the day it was written — a
# disposable worktree, removed afterwards, with only suggestions.md crossing
# back. What that never bounded is everything else the process could reach: this
# host's filesystem, its network, and its credentials. `gh` is authenticated
# here, so a reviewer with a terminal could push to a remote the session that
# spawned it is denied — a deny list bounds the session, never the subprocess.
#
# This image is that missing bound. What does NOT enter it is the point: no gh
# token, no SSH keys, no ~/.aws, no host filesystem beyond the one directory the
# review is about.
#
# What DOES cross is one of two things, and the header used to describe neither
# accurately. Either XAI_API_KEY by environment, or — when no usable key is
# present — writable copies of three files: ~/.grok/auth.json, agent_id and
# config.toml. Writable because grok refreshes a session mid-run; copies because
# the container must not be able to rewrite this host's credential file. All
# three are grok's and only grok's.
#
# Egress is CONFINED to api.x.ai and auth.x.ai since #17, and it takes two
# containers of this image: the reviewer on a network created with --internal,
# which Docker gives no gateway, and egress-proxy.py below as the one member
# with a second leg on the bridge — a CONNECT-only tunnel with a host
# allow-list, which grok reaches through HTTPS_PROXY. Docker alone offers
# "all" or "none"; the proxy is the third option. What remains is the crossing
# credential's own blast radius against x.ai, which no boundary here can
# shrink, and grok-review.sh's header carries that argument.

FROM debian:trixie-slim

# git, because /review-branch spends its whole life in it. curl and CA certs for
# the pinned release download below and the model calls afterwards.
#
# python3 for the one gate the review is asked to run itself: the licence gate
# is stdlib-only and needs no restore, which is exactly why §15.1 puts it ahead
# of the build. python-is-python3 beside it, and it is not decoration —
# review-branch.md invokes `python licence_gate.py`, and Debian's python3
# package ships no `python`, so without the shim the gate this image exists to
# provide still would not run. The .NET SDK is deliberately absent — `dotnet test` has needed a
# Docker daemon since PR-08's Testcontainers suite, so running it here would
# mean Docker-in-Docker inside a sandbox built to take capability away.
# review-branch.md says so, and says the host owns that gate.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends \
        ca-certificates \
        curl \
        git \
        python3 \
        python-is-python3 \
    && rm --recursive --force /var/lib/apt/lists/*

# The reviewer's only way out, baked into the image rather than bind-mounted:
# a mount would need `:Z` on an SELinux host, and `:Z` relabels the file it
# names — which would be a file inside this repository's checkout. On PATH
# under a bare name, because grok-review.sh runs under MSYS on one host and
# MSYS rewrites a leading-slash argument into a Windows path; `--workdir
# /review` already costs that script an exclusion, and this would be a second.
# Root-owned and readable by everyone, which is all a script needs. It is run
# by grok-review.sh in a SECOND container of this image — no clone, no
# credential, and a leg on the bridge the reviewer's container does not have.
COPY --chmod=755 egress-proxy.py /usr/local/bin/egress-proxy

# Not root. The container is the boundary, but a reviewer that is root inside it
# is one escape away from being root outside, and it needs no privilege a plain
# user lacks.
#
# The uid is an argument because it has to match the host's on Linux. The clone
# and the credential copies are bind-mounted with their host ownership, and
# safe.directory only silences git's ownership check — it grants no filesystem
# access. A hard-coded 1000 against a host user who is not 1000 leaves the
# reviewer unable to read the credentials or write suggestions.md. Docker
# Desktop maps ownership and hides this, which is why it has to be argued rather
# than discovered.
#
# **An id the base image already uses is reused, not created.** `groupadd` and
# `useradd` refuse an id that exists — `GID '100' already exists`, `UID 65534
# is not unique` — and debian:trixie-slim already holds gid 20 (dialout, a
# typical macOS primary group), gid 100 (users, a common Linux one) and
# uid/gid 65534 (nobody/nogroup). A host with any of those could not build the
# reviewer at all (#17). So the group is created only when its gid is free, and
# a uid that is taken has its account RENAMED into `reviewer` rather than
# joined by a second entry: `useradd --non-unique` would leave the base's name
# first in /etc/passwd, so `id` and getpwuid would answer `nobody` with a home
# of /nonexistent, and anything that resolves HOME through the account rather
# than the environment would look in the wrong place. The name and the path
# are both load-bearing — grok-review.sh mounts the credentials at the literal
# /home/reviewer/.grok/, and `USER` below is by name — so both are fixed here
# whichever branch ran, and the last line checks that they are.
#
# **0:0 is refused on purpose, where it used to be refused by accident.** The
# old form failed on root because root exists; reuse would instead rename root
# into `reviewer` and hand the review a root shell. grok-review.sh refuses to
# pass 0 before the build starts and keeps doing so; this is the same refusal
# one layer down, for a build invoked by hand.
ARG REVIEWER_UID=1000
ARG REVIEWER_GID=1000
RUN set -eu; \
    [ "${REVIEWER_UID}" -ne 0 ] && [ "${REVIEWER_GID}" -ne 0 ] || \
        { echo "refusing REVIEWER_UID=${REVIEWER_UID} REVIEWER_GID=${REVIEWER_GID}: the reviewer is non-root by design, and reusing root's account would make it root" >&2; exit 1; }; \
    getent group "${REVIEWER_GID}" >/dev/null \
        || groupadd --gid "${REVIEWER_GID}" reviewer; \
    existing="$(getent passwd "${REVIEWER_UID}" | cut -d: -f1)"; \
    if [ -n "${existing}" ]; then \
        usermod --login reviewer --comment "" --gid "${REVIEWER_GID}" \
            --home /home/reviewer --shell /bin/sh "${existing}"; \
        mkdir -p /home/reviewer; \
        chown "${REVIEWER_UID}:${REVIEWER_GID}" /home/reviewer; \
        chmod 700 /home/reviewer; \
    else \
        useradd --create-home --uid "${REVIEWER_UID}" --gid "${REVIEWER_GID}" reviewer; \
    fi; \
    [ "$(id -u reviewer)" = "${REVIEWER_UID}" ] \
        && [ "$(id -g reviewer)" = "${REVIEWER_GID}" ] \
        && [ "$(getent passwd reviewer | cut -d: -f6)" = /home/reviewer ] \
        && [ -d /home/reviewer ]
USER reviewer
ENV HOME=/home/reviewer

# The release artefact, fetched directly. It used to be x.ai's official
# installer — what the docs give and what installed the host's copy — and the
# argument for replacing it is below. Either way it lands as `reviewer`, so the
# CLI sits in this user's home beside the credentials mounted into it.
#
# `--fail`, and never a pipe into a shell. `RUN a | b` reports only b's status
# under /bin/sh, and bash handed an empty script exits 0 — so a failed download
# used to publish an image with no grok in it and defer the real error to the
# first review, where it arrives as an authentication problem. Fetching to a
# file and checking its digest keeps that closed for the same reason and more
# strictly: an empty or truncated download cannot match the pin.
#
# Pinned, and not for tidiness. Unpinned this takes the latest and the reviewer
# drifts from the developer's client without anyone choosing it: with the host
# on 0.2.118 and this image unpinned onto 1.0.0, the newer client resolved the
# host's own OAuth session to a *newly created team* carrying no credits, so
# every review died on `403 permission-denied` while the host kept working
# against the very same file. A credential must mean the same thing on both
# sides of the mount.
#
# Track the host's version when it moves. Both are 1.0.5 today.
#
# PATH is set before the install rather than after, so the `grok --version`
# below is a real check. After the RUN it would not be on PATH yet and the
# verification would exit 127 — which is a build failure that says nothing
# about whether grok installed.
ENV PATH="/home/reviewer/.grok/bin:${PATH}"

# The version was pinned and its INTEGRITY was not, which is the half of the
# argument above that was missing. Every build refetched
# https://x.ai/cli/install.sh and executed it with no checksum and no signature
# — inside the one image that exists to be a security boundary, and that
# grok-review.sh then hands the credentials the boundary was built to contain.
# Anyone able to alter that script or its delivery (an upstream compromise, or
# DNS/CDN/TLS interception on the developer's network) got code execution inside
# the boundary on the next review. The image is rebuilt routinely, so the window
# was not a one-off.
#
# The first fix pinned BOTH artefacts, the installer's digest and the binary's,
# because the installer performs NO verification of the 163 MB binary it
# downloads — read, not assumed, by grepping the script for sha256/checksum/gpg
# and finding nothing — so pinning only the bootstrap would have left the larger
# artefact arriving unchecked over the same channel from the same origin.
#
# **That is history: there is no installer here now**, for the reason argued
# below, and the only digest this file carries is the release binary's.
#
# The digests were taken by fetching the artefact and hashing it, and the
# amd64 one was cross-checked against the md5 GCS reports in its own
# `x-goog-hash` response header. That is trust on first use, and it is the point
# rather than a weakness: from here a change to what x.ai serves for a pinned
# version fails the build, and moving the pin is a reviewed diff — the same
# property Directory.Packages.props and global.json are here for.
#
# An architecture with no recorded digest FAILS rather than building unverified.
# That is the scaffold's rule one tree over: a tool that refuses input it has
# never been shown beats one that guesses. Docker Desktop on this host builds
# linux/amd64; an arm64 developer adds one line, which is exactly the reviewed
# diff a pin is for.
#
# **The installer is gone, and verifying after it ran was not good enough.** The
# first version of this pin kept `install.sh` — checksummed — and hashed the
# binary afterwards, stating the residual as "the installer smoke-runs the
# binary once before the check, so a tampered artefact executes once and then
# fails the build". That understated it, and a reviewer said so. The execution
# happens *before* the hash, as the reviewer user, with the network and a
# writable `$HOME`: a malicious binary can use that one execution to put the
# expected bytes where the check will read them, and then the check passes and
# the image ships. **A verification that runs after the thing it verifies has
# already executed does not verify anything** — the same shape as the ledger
# printing its answer before its trust check failed, two files over.
#
# So the release artefact is fetched directly and its digest checked before
# anything is executed. What replaces the installer is the handful of steps it
# performs on Linux, read out of the pinned script rather than guessed: drop the
# binary in `downloads/`, `chmod +x`, relative symlinks for `grok` and `agent`
# in `bin/` (relative because they survive a bind-mounted `$HOME`), and the
# `[cli]` block it writes to `config.toml`. The shell completions it also
# generates are omitted deliberately: producing them means running the binary,
# which is the thing being avoided, and no reviewer has a shell to complete in.
#
# `grok --version` at the end is now the FIRST execution of the binary, and it
# happens only after the digest matched.
ARG GROK_VERSION=1.0.5
ARG GROK_SHA256_X86_64=9ba87444e1819e8f6104adbbf4676a870c204380aa5c3e1c38a926c4ea677238
ARG GROK_SHA256_AARCH64=1c1fe67d7c35497fb09f44a451f57acc3787add4c9aea2c56f5c7c75dc5ffcf1
# Compared with `[ "$a" = "$b" ]` rather than piped into `sha256sum --check`,
# because the recorded digest arrives through a build argument: a stray
# character on it — a CR from a CRLF checkout of this file, most plausibly —
# would make `--check` fail to PARSE its input, which is a different error from
# a mismatch and reads like a broken command rather than a bad digest. A string
# comparison says which value disagreed, whatever is wrong with either.
RUN set -eu; \
    case "$(uname -m)" in \
        x86_64|amd64) arch=x86_64; want="${GROK_SHA256_X86_64}" ;; \
        aarch64|arm64) arch=aarch64; want="${GROK_SHA256_AARCH64}" ;; \
        *) echo "no recorded grok ${GROK_VERSION} digest for $(uname -m); add one rather than building an unverified reviewer" >&2; exit 1 ;; \
    esac; \
    curl --fail --silent --show-error --location \
        "https://x.ai/cli/grok-${GROK_VERSION}-linux-${arch}" --output /tmp/grok; \
    got="$(sha256sum /tmp/grok | cut -d' ' -f1)"; \
    [ "$got" = "${want}" ] || \
        { echo "grok ${GROK_VERSION} ${arch} sha256 ${got} does not match the recorded ${want}" >&2; exit 1; }; \
    mkdir -p "${HOME}/.grok/downloads" "${HOME}/.grok/bin"; \
    mv /tmp/grok "${HOME}/.grok/downloads/grok-linux-${arch}"; \
    chmod +x "${HOME}/.grok/downloads/grok-linux-${arch}"; \
    ln -sf "../downloads/grok-linux-${arch}" "${HOME}/.grok/bin/grok"; \
    ln -sf "../downloads/grok-linux-${arch}" "${HOME}/.grok/bin/agent"; \
    printf '[cli]\ninstaller = "internal"\n' > "${HOME}/.grok/config.toml"; \
    grok --version

# The mounted clone carries its host ownership, so git refuses it as dubiously
# owned. Declared here rather than left to the reviewer, which is not a nicety:
# the first sandboxed run watched grok notice the refusal and reach for
# `git config` to fix it, and a reviewer that edits git configuration to get
# started is a reviewer whose grant has to include doing so.
RUN git config --global --add safe.directory /review

# The review's only view of the repository, bind-mounted at run time. A clone
# rather than a worktree: a worktree's .git is a file pointing back into the
# main checkout, which is exactly the path this image refuses to mount, so git
# inside would resolve nothing.
WORKDIR /review
