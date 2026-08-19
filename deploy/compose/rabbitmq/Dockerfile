# The broker, plus the one plugin §9.6's saga cannot run without.
#
# MassTransit's `UseDelayedMessageScheduler` (ADR-021) schedules a message by
# publishing it to a delayed exchange, and on RabbitMQ that exchange type comes
# from `rabbitmq_delayed_message_exchange` — a community plugin that ships in
# no official image.
#
# Without it, measured: the bus starts CLEAN, and the first `.Schedule(…)`
# fails `exchange.declare` with `precondition_failed: unknown exchange type
# 'x-delayed-message'`, after which MassTransit retries the topology for ever.
# The call never returns, the saga's transition never completes, and the only
# trace is a channel error in the BROKER's log every few seconds — the service
# stays healthy and silent. That is §9.6's stuck order arriving as
# infrastructure rather than as a missing transition.
#
# The tag is pinned to a minor rather than tracking `4`, and that is the
# plugin's constraint rather than a preference: the plugin is built against a
# specific broker line and `rabbitmq-plugins enable` refuses one it does not
# match. A floating `4` would enable cleanly today and fail the image build on
# whatever Tuesday RabbitMQ 4.2 becomes latest.
FROM rabbitmq:4.1-management-alpine

# ADD rather than curl, so the fetch is a build step Docker caches and a
# reviewer can read — not a runtime `rabbitmq-plugins enable` against a volume
# nobody looks at.
ADD https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/releases/download/v4.1.0/rabbitmq_delayed_message_exchange-4.1.0.ez \
    /opt/rabbitmq/plugins/

# The digest and the mode, in a RUN rather than as `ADD --checksum= --chmod=`.
# **Both of those flags are BuildKit-only**, and this image is built by two
# different builders: Compose uses BuildKit, and §12.4's Testcontainers fixture
# uses the classic `/build` API, which rejects them outright — "the --chmod
# option requires BuildKit". A Dockerfile only one of them can build is a
# fixture that silently falls back to a stock broker, which is the exact defect
# this file exists to prevent.
#
# The checksum still fails the build on a substituted asset; what changes is
# that it fails one layer later, after the file has been written rather than
# before. Nothing reaches a running broker either way.
#
# The chmod is not tidiness and the failure it prevents is worth naming: ADD
# from a URL writes the file 0600 and root-owned, the broker runs as
# `rabbitmq`, and the plugin is then unreadable. `rabbitmq-plugins enable
# --offline` never opens the archive — it edits enabled_plugins — so the IMAGE
# BUILDS CLEANLY and the container dies at start with an Erlang `eacces` naming
# a path inside the .ez. Measured exactly that way here.
#
# --offline because the broker is not running during a build; the change lands
# in enabled_plugins and takes effect at start.
RUN cd /opt/rabbitmq/plugins \
    && echo "567f876378e70af9d949de4066bbb2fc30162b46bcbc9efe2430076b172e4a87  rabbitmq_delayed_message_exchange-4.1.0.ez" \
        | sha256sum -c - \
    && chmod 644 rabbitmq_delayed_message_exchange-4.1.0.ez \
    && rabbitmq-plugins enable --offline rabbitmq_delayed_message_exchange
