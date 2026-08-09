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

## Application services

Application services join this file as their images land —
[Appendix C](../../docs/backend-architecture/appendix-c-delivery-plan.md)
sequences them. Catalog is the first: `catalog-migrator` applies the schema
and exits, then `catalog-api` starts (§14.1's pair rule).

| Service | Host port(s) | Notes |
|---|---|---|
| Catalog API | http://localhost:5102 | `/health/live`, `/health/ready`, `/openapi/v1.json`, `/v1/catalog/products` |

**Catalog's endpoints are deliberately unauthenticated.** Security lands with
PR-16's Keycloak realm and JWT validation
([Appendix C](../../docs/backend-architecture/appendix-c-delivery-plan.md));
until then anything that can reach port 5102 can publish and list products.
Naming the temporary gap here and scheduling its closure is the honest
version of shipping it (§C.4).

Override connection strings with `CATALOG_CONNECTION` and
`CATALOG_MIGRATOR_CONNECTION`; both default to the `sa` login above, and only
the configuration *keys* differ locally
([§7.1](../../docs/backend-architecture/07-persistence.md),
[§14.2](../../docs/backend-architecture/14-local-development.md)).

To run the infrastructure alone — services on the host, under a debugger —
apply the override (§14.1):

```bash
docker compose -f deploy/compose/docker-compose.yml -f deploy/compose/docker-compose.infra-only.yml up -d --wait
```
