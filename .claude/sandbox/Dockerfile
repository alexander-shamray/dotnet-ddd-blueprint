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
# of the build. The .NET SDK is deliberately absent — `dotnet test` has needed a
# Docker daemon since PR-08's Testcontainers suite, so running it here would
# mean Docker-in-Docker inside a sandbox built to take capability away.
# review-branch.md says so, and says the host owns that gate.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends \
        ca-certificates \
        curl \
        git \
        python3 \
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
# Track the host's version when it moves. Both are 1.0.0 today.
#
# PATH is set before the install rather than after, so the `grok --version`
# below is a real check. After the RUN it would not be on PATH yet and the
# verification would exit 127 — which is a build failure that says nothing
# about whether grok installed.
ENV PATH="/home/reviewer/.grok/bin:${PATH}"

ARG GROK_VERSION=1.0.0
RUN curl --fail --silent --show-error --location \
        https://x.ai/cli/install.sh --output /tmp/install-grok.sh \
    && bash /tmp/install-grok.sh "${GROK_VERSION}" \
    && rm --force /tmp/install-grok.sh \
    && grok --version

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
