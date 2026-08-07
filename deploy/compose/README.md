# Local development environment

[§14.1](../../docs/backend-architecture/14-local-development.md) is the
specification for this directory; this file records what a developer needs
at the keyboard. One command starts the infrastructure:

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --wait
```

| Service | Host port(s) | Credentials |
|---|---|---|
| SQL Server | 1433 | `sa` / `Local_Dev_Pa55w0rd!` — override with `SQL_PASSWORD` |
| Redis (cache) | 6379 | — |
| Redis (coordination) | 6380 | — |
| RabbitMQ | 5672, management http://localhost:15672 | guest/guest |
| Keycloak | http://localhost:8080 | admin/admin |
| OTel collector | 4317 (OTLP gRPC), 4318 (OTLP HTTP) | — |
| Grafana | http://localhost:3000 | — |

The credentials are development defaults, documented deliberately (§14.1);
every deployed environment takes its secrets from a vault
([§15.4](../../docs/backend-architecture/15-cicd-deployment.md)). Copy
`.env.example` to `.env` to override one.

Application services join this file as their images land —
[Appendix C](../../docs/backend-architecture/appendix-c-delivery-plan.md)
sequences them. Until then the seven services above are the whole file.
