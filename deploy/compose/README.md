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

## Getting a token

PR-16 closed the gap this file used to name: publishing a product now needs a
bearer token carrying `catalog:write`. **Listing products does not, and that is
permanent** — [§10.2](../../docs/backend-architecture/10-api-gateway.md)'s
`catalog-public` route is GET-only and carries no authorization policy, so a
product listing is public at the edge and public here.

The realm ships two logins, both development defaults in the sense §14.1
already uses for `admin/admin` and `guest/guest`:

| User | Password | Holds |
|---|---|---|
| `demo` | `demo` | `catalog:write` |
| `browser` | `browser` | nothing — the account that proves a refusal |

```bash
TOKEN=$(curl -s http://localhost:8080/realms/commerce/protocol/openid-connect/token \
    -d grant_type=password -d client_id=web-app \
    -d username=demo -d password=demo |
    python -c 'import json,sys; print(json.load(sys.stdin)["access_token"])')

curl -X POST http://localhost:5102/v1/catalog/products \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d '{"name":"Walnut desk","amount":19.99,"currency":"EUR"}'
```

The same call as `browser` is a 403 and the same call with no header is a 401.
Both are worth running once: they are the difference between a token being
*checked* and a token being *carried*.

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

The host process reads none of the `environment:` blocks above, so the three
keys a service refuses to start without have to reach it another way — the
runtime connection string, the bus, and the authority `AddJwtAuthentication`
reads eagerly (§11.3). Same values, host names in place of service names:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Catalog='Server=localhost;Database=Catalog;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
export ConnectionStrings__RabbitMq='amqp://guest:guest@localhost:5672'
export Identity__Authority='http://localhost:8080/realms/commerce'
dotnet run --project src/Services/Catalog/Catalog.Api
```

`ASPNETCORE_ENVIRONMENT` is the first line for a reason: no project here ships
a `launchSettings.json`, so `dotnet run` is Production unless something says
otherwise, and `RequireHttpsMetadata` is on in Production (§11.3). The
authority above is plain HTTP, so the host refuses to fetch the discovery
document at all and every bearer request fails before validation begins. The
container sets the same variable, which is why the Compose path never shows
this.

The override excludes `catalog-migrator` as well as `catalog-api`, so the
schema is nobody's job until it is run — on the host, under the *other*
connection string, because §7.1 keeps the two identities apart even where the
local login is one:

```bash
export ConnectionStrings__CatalogMigrator='Server=localhost;Database=Catalog;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
dotnet run --project src/Services/Catalog/Catalog.Migrator
```
