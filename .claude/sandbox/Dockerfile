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
# Egress is deliberately NOT restricted, and that is the remaining residual.
# Confining it to api.x.ai needs an allow-list proxy on an internal network,
# which is its own change; Docker alone offers "all" or "none", and "none" stops
# the review too. The credential half is what the finding named.

FROM debian:trixie-slim

# git, because /review-branch spends its whole life in it. curl and CA certs for
# the installer below and the model calls afterwards.
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
ARG REVIEWER_UID=1000
ARG REVIEWER_GID=1000
RUN groupadd --gid "${REVIEWER_GID}" reviewer \
    && useradd --create-home --uid "${REVIEWER_UID}" --gid "${REVIEWER_GID}" reviewer
USER reviewer
ENV HOME=/home/reviewer

# The official installer, which is what x.ai documents and what installed the
# host's copy. Run as `reviewer` so the CLI lands in this user's home, beside
# the credentials mounted into it.
#
# Downloaded first, then executed, rather than piped. `RUN a | b` reports only
# b's status under /bin/sh, and bash handed an empty script exits 0 — so a
# failed download would publish an image with no grok in it and defer the real
# error to the first review, where it arrives as an authentication problem.
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
# Both artefacts are pinned now, and the second one matters more than the first:
# the installer performs NO verification of the 163 MB binary it downloads —
# read, not assumed, by grepping the script for sha256/checksum/gpg and finding
# nothing — so pinning only the bootstrap would have left the larger artefact
# arriving unchecked over the same channel from the same origin.
#
# The digests were taken by fetching each artefact and hashing it, and the
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
# Residual, stated because it is real: the installer runs the downloaded binary
# once itself (`"$binary_tmp" --version`, its own smoke check) BEFORE this
# verification can run, so a tampered artefact would execute once in the build
# container. It never reaches an image — the build fails and nothing ships —
# and closing the gap entirely means replacing the installer with a vendored
# install, which trades a narrow, stated residual for reimplementing install
# semantics (the bin symlinks and the `[cli]` config block) that only a full
# review round would prove wrong.
ARG GROK_VERSION=1.0.5
ARG GROK_INSTALLER_SHA256=43d0943123edade1383a476a4f778674877acee7c1f98a00f094c4a0f7349321
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
        https://x.ai/cli/install.sh --output /tmp/install-grok.sh; \
    got="$(sha256sum /tmp/install-grok.sh | cut -d' ' -f1)"; \
    [ "$got" = "${GROK_INSTALLER_SHA256}" ] || \
        { echo "install.sh sha256 ${got} does not match the recorded ${GROK_INSTALLER_SHA256}" >&2; exit 1; }; \
    bash /tmp/install-grok.sh "${GROK_VERSION}"; \
    binary="${HOME}/.grok/downloads/grok-linux-${arch}"; \
    got="$(sha256sum "${binary}" | cut -d' ' -f1)"; \
    [ "$got" = "${want}" ] || \
        { echo "grok ${GROK_VERSION} ${arch} sha256 ${got} does not match the recorded ${want}" >&2; exit 1; }; \
    rm --force /tmp/install-grok.sh; \
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
