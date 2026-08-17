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
| Gateway | http://localhost:5000 | `/health/live`, `/health/ready`, and [§10.2](../../docs/backend-architecture/10-api-gateway.md)'s four routes |
| Ordering API | http://localhost:5101 | `/health/live`, `/health/ready`, `/openapi/v1.json`, `/v1/orders` — every route needs a token, unlike Catalog's listing |

The gateway is the single entry point for external clients
([§10.1](../../docs/backend-architecture/10-api-gateway.md)), so the same
listing is reachable two ways — and the two are not equivalent:

```bash
curl http://localhost:5102/v1/catalog/products     # the service, directly
curl http://localhost:5000/api/v1/catalog/products # through the gateway
```

The edge adds `/api`, which the gateway strips before forwarding, and applies
what the service does not: the rate limit, the CORS policy, and a correlation
ID on every request that arrives without one. **Two of the four routes have no
service behind them yet** — `/api/v1/inventory` and `/bff` answer 502 until
Inventory and the BFF land — and they are in the file deliberately, because
the two configuration tests over it are what PR-17 exists to deliver.
`/api/v1/orders` was the third until PR-18, which is what "stops answering
502" looks like: the route file did not change, because PR-17 shipped it whole
and a service PR that re-decides a route is the mistake §10.2's dual-version
trap describes. `/api/v1/catalog` is GET-only at the edge, so publishing a
product is a call to port 5102 and not to port 5000.

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
| `demo` | `demo` | `catalog:write`, `orders:write`, `orders:cancel` |
| `browser` | `browser` | nothing — the account that proves a refusal |

`orders:admin` is grantable and held by **nobody**, deliberately: it overrides
§11.4's ownership check, and the 404 that hides another customer's order stays
demonstrable only while no shipped login can bypass it.

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

Ordering takes the same token, and **every** one of its routes needs one —
there is no anonymous half, because an order belongs to somebody where a
product listing does not:

```bash
curl -X POST http://localhost:5101/v1/orders \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d '{"items":[{"productId":"00000000-0000-0000-0000-000000000001","quantity":1}],
         "shippingAddress":{"line1":"1 Test Street","city":"Almaty","postalCode":"050000","country":"KZ"},
         "currency":"EUR"}'
```

**No cancel call here, deliberately, because there is no id to cancel with.**
The obvious next line — capture the response and interpolate it into
`/v1/orders/$ORDER/cancel` — is wrong twice over: today the body is a
problem document, and after PR-20 it is `Results.Ok(guid)`, whose body is a
JSON *string* with the quotes still on it, which `{id:guid}` cannot bind.
A reader who wants the id then needs `| jq -r .`, and a README that says so
before the table can produce one is documenting a shell trick rather than the
service.

**The first call answers 422 `order.products_unavailable` until PR-20 lands**,
and that is the honest state rather than a broken example: prices come from a
local projection of Catalog's events (§6.4), and the projection that fills
`ordering.ProductPrices` is PR-20's. The table ships with its reader because
`PlaceOrderHandler` is the consumer and PR-20 depends on this PR, not the
other way round. Until then the reachable proofs are the 401, the 403 as
`browser`, and the 404 an order you do not own returns.

Override connection strings with `CATALOG_CONNECTION` /
`CATALOG_MIGRATOR_CONNECTION` and `ORDERING_CONNECTION` /
`ORDERING_MIGRATOR_CONNECTION` — one pair per service, all four commented out
in `.env.example` so the nested `${SQL_PASSWORD:-…}` keeps following an
overridden password rather than freezing it. They default to the `sa` login
above, and only the configuration *keys* differ locally
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

Ordering is the same shape with its own key — `ConnectionStrings__Ordering`,
never Catalog's, because `AddOrderingInfrastructure` reads its own name and
`AddSqlServer` throws without it:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__Ordering='Server=localhost;Database=Ordering;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
export ConnectionStrings__RabbitMq='amqp://guest:guest@localhost:5672'
export Identity__Authority='http://localhost:8080/realms/commerce'
dotnet run --project src/Services/Ordering/Ordering.Api
```

`ASPNETCORE_ENVIRONMENT` is the first line for a reason: no project here ships
a `launchSettings.json`, so `dotnet run` is Production unless something says
otherwise, and `RequireHttpsMetadata` is on in Production (§11.3). The
authority above is plain HTTP, so the host refuses to fetch the discovery
document at all and every bearer request fails before validation begins. The
container sets the same variable, which is why the Compose path never shows
this.

The override excludes the `gateway` too, and running that one on the host takes
a fourth variable: §10.2's destinations are container names, which resolve on
the Compose network and nowhere else, so a host-run gateway has to be told
where the service actually is.

```bash
export ASPNETCORE_ENVIRONMENT=Development
export Identity__Authority='http://localhost:8080/realms/commerce'
export ReverseProxy__Clusters__catalog__Destinations__d1__Address='http://localhost:5102/'
export ReverseProxy__Clusters__ordering__Destinations__d1__Address='http://localhost:5101/'
dotnet run --project src/Gateway/Gateway.Api
```

**A destination joins this block with the PR that builds its service**, the
same rule the Compose file's `depends_on` follows. Ordering's line arrived with
PR-18; without it a host-run gateway 502s `/api/v1/orders`, which is the exact
path that PR exists to stop answering 502.

`ASPNETCORE_ENVIRONMENT` leads this block for the same reason it leads the one
above, and the block is written to stand alone rather than as a delta on that
shell: without it the authority on the next line is plain HTTP outside
Development, `AddJwtAuthentication` refuses it at startup, and the gateway does
not run at all. Every host here validates tokens (§11.2), so every host-run
block that names an authority needs this line — and the migrator below does
not, because its job never sees a token. **That is the rule and deliberately
not a count**: this sentence said "both of them" until Ordering's block made
three, which is the same way the compose smoke's image count went stale, one
file over.

`Cors__Enabled` and `Ingress__Enabled` are both absent above and both default
to off, which is the shape the flags are written for — off is a valid
topology, on-but-unconfigured is not, and the host refuses to start in the
second state rather than starting into it.

The override excludes `catalog-migrator` as well as `catalog-api`, so the
schema is nobody's job until it is run — on the host, under the *other*
connection string, because §7.1 keeps the two identities apart even where the
local login is one:

```bash
export ConnectionStrings__CatalogMigrator='Server=localhost;Database=Catalog;User Id=sa;Password=Local_Dev_Pa55w0rd!;TrustServerCertificate=True'
dotnet run --project src/Services/Catalog/Catalog.Migrator
```
