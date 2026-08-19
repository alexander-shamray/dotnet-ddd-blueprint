# The broker, plus the one plugin §9.6's saga cannot run without.
#
# MassTransit's `UseDelayedMessageScheduler` (ADR-021) schedules a message by
# publishing it to a delayed exchange, and on RabbitMQ that exchange type comes
# from `rabbitmq_delayed_message_exchange` — a community plugin that ships in
# no official image. Without it the publish is ACCEPTED and never delivered:
# the exchange declaration fails at bus start, and every saga timeout is a wait
# that silently never fires. That is the failure §9.6 exists to prevent,
# arriving as infrastructure rather than as a missing transition.
#
# The tag is pinned to a minor rather than tracking `4`, and that is the
# plugin's constraint rather than a preference: the plugin is built against a
# specific broker line and `rabbitmq-plugins enable` refuses one it does not
# match. A floating `4` would enable cleanly today and fail the image build on
# whatever Tuesday RabbitMQ 4.2 becomes latest.
FROM rabbitmq:4.1-management-alpine

# ADD rather than curl, for the checksum. Docker verifies the digest before the
# layer is written, so a substituted asset fails the build instead of reaching
# a broker — which is the whole reason this is a build step and not a runtime
# `rabbitmq-plugins enable` against a volume nobody reviews.
#
# --chmod is not tidiness and the failure it prevents is worth naming: ADD from
# a URL writes the file 0600 and root-owned, the broker runs as `rabbitmq`, and
# the plugin is then unreadable. `rabbitmq-plugins enable --offline` below
# still succeeds — it edits enabled_plugins and never opens the archive — so
# the IMAGE BUILDS CLEANLY and the container dies at start with an Erlang
# `eacces` naming a path inside the .ez. Measured exactly that way here.
ADD --chmod=644 \
    --checksum=sha256:567f876378e70af9d949de4066bbb2fc30162b46bcbc9efe2430076b172e4a87 \
    https://github.com/rabbitmq/rabbitmq-delayed-message-exchange/releases/download/v4.1.0/rabbitmq_delayed_message_exchange-4.1.0.ez \
    /opt/rabbitmq/plugins/

# --offline because the broker is not running during a build; the change lands
# in enabled_plugins and takes effect at start.
RUN rabbitmq-plugins enable --offline rabbitmq_delayed_message_exchange
