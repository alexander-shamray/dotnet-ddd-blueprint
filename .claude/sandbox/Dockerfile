# The external reviewer's boundary.
#
# grok-review.sh has isolated the reviewer's *edits* since it was written — a
# disposable git worktree, removed afterwards, with only suggestions.md crossing
# back. What that never bounded is everything else the process could reach: this
# host's filesystem, its network, and its credentials. `gh` is authenticated
# here, so a reviewer with a terminal could push to a remote the session that
# spawned it is denied — a deny list bounds the session, never the subprocess.
#
# This image is that missing bound. What does NOT enter it is the point: no gh
# token, no SSH keys, no ~/.aws, no host filesystem beyond the one directory the
# review is about. The only credential mounted is ~/.grok/auth.json, read-only,
# because the reviewer cannot call the model without it.
#
# Egress is deliberately NOT restricted here, and that is the remaining
# residual. Restricting it to api.x.ai needs an allow-list proxy on an internal
# network, which is its own change; Docker on its own can only choose between
# "all" and "none", and "none" stops the review too. The credential half is the
# half Copilot's finding named, and it is the half this closes.

FROM debian:trixie-slim

# git, because /review-branch spends its whole life in it. curl and CA certs for
# the installer below and for the model calls afterwards.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends \
        ca-certificates \
        curl \
        git \
    && rm --recursive --force /var/lib/apt/lists/*

# Not root. The container is the boundary, but a reviewer that is root inside it
# is one container escape away from being root outside, and it needs no
# privilege that a plain user lacks.
RUN useradd --create-home --uid 1000 reviewer
USER reviewer
ENV HOME=/home/reviewer

# The official installer, which is what x.ai documents and what installed the
# host's copy. Run as `reviewer` so the CLI lands in this user's home and reads
# the auth.json mounted beside it.
#
# Pinned, and not for tidiness. Unpinned this takes the latest, and the
# reviewer then drifts away from the developer's client without anyone
# choosing that. It is not hypothetical: with the host on 0.2.118 and this
# image unpinned onto 1.0.0, the newer client presented with the host's OAuth
# session resolved it to a *newly created team* carrying no credits, so every
# review died on `403 permission-denied` while the host kept working against
# the very same file. A credential has to mean the same thing on both sides of
# the mount, and only a pin makes that true.
#
# Track the host's version when it moves. Both are 1.0.0 today.
ARG GROK_VERSION=1.0.0
RUN curl --fail --silent --show-error --location https://x.ai/cli/install.sh \
    | bash -s "${GROK_VERSION}"

ENV PATH="/home/reviewer/.grok/bin:${PATH}"

# The mounted clone is owned by whoever created it on the host, never by uid
# 1000, so git refuses it as dubious ownership. Declared here rather than left
# to the reviewer, which is not a nicety: the first sandboxed run watched grok
# notice the refusal and reach for `git config` to fix it itself, and a
# reviewer that edits git configuration to get started is a reviewer whose
# grant has to include doing so.
RUN git config --global --add safe.directory /review

# The review's only view of the repository, bind-mounted at run time. A clone
# rather than a worktree: a worktree's .git is a file pointing back into the
# main checkout, which is exactly the path this image refuses to mount, so git
# inside the container would resolve nothing.
WORKDIR /review
