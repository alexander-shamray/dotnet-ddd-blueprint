# 14. Local development

## 14.1 Docker Compose — the baseline

One command starts the platform. This is the documented default; it requires
only Docker and works identically on every operating system and in CI.

```yaml
# deploy/compose/docker-compose.yml
name: commerce

services:
  sql:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: "${SQL_PASSWORD:-Local_Dev_Pa55w0rd!}"
      MSSQL_PID: Developer
    ports: [ "1433:1433" ]
    volumes: [ sql-data:/var/opt/mssql ]
    healthcheck:
      test: ["CMD-SHELL", "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$$MSSQL_SA_PASSWORD\" -C -Q 'SELECT 1'"]
      interval: 10s
      timeout: 5s
      retries: 10
      start_period: 30s

  # Two Redis instances, because eviction policy cannot be shared — §8.1.
  redis-cache:
    image: redis:7-alpine
    command: redis-server --maxmemory 256mb --maxmemory-policy allkeys-lru
    ports: [ "6379:6379" ]
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]

  redis-coordination:
    image: redis:7-alpine
    # noeviction: locks, idempotency keys and the denylist must never be
    # evicted. Appendonly so a restart does not silently release held locks.
    command: redis-server --appendonly yes --maxmemory 128mb --maxmemory-policy noeviction
    ports: [ "6380:6379" ]
    volumes: [ redis-coordination-data:/data ]
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 10s
      retries: 5

  rabbitmq:
    image: rabbitmq:4-management-alpine
    ports: [ "5672:5672", "15672:15672" ]
    volumes: [ rabbit-data:/var/lib/rabbitmq ]
    healthcheck:
      test: ["CMD", "rabbitmq-diagnostics", "check_running"]
      interval: 10s
      retries: 5

  keycloak:
    image: quay.io/keycloak/keycloak:26.0
    command: start-dev --import-realm
    environment:
      KC_BOOTSTRAP_ADMIN_USERNAME: admin
      KC_BOOTSTRAP_ADMIN_PASSWORD: admin
    ports: [ "8080:8080" ]
    volumes: [ ./keycloak/realm-export.json:/opt/keycloak/data/import/realm.json:ro ]

  otel-collector:
    image: otel/opentelemetry-collector-contrib:latest
    command: [ "--config=/etc/otel/config.yaml" ]
    volumes: [ ./otel/config.yaml:/etc/otel/config.yaml:ro ]
    ports: [ "4317:4317", "4318:4318" ]

  grafana:
    image: grafana/otel-lgtm:latest
    ports: [ "3000:3000" ]

  # ---- Application services ----

  ordering-migrator:
    build:
      context: ../..
      dockerfile: src/Services/Ordering/Ordering.Migrator/Dockerfile
    environment:
      # Migrator identity (DDL) — §7.1. Locally these differ only by SQL login;
      # in production they are separate secrets on separate workloads.
      ConnectionStrings__OrderingMigrator: "${ORDERING_MIGRATOR_CONNECTION}"
    depends_on:
      sql: { condition: service_healthy }
    restart: "no"

  ordering-api:
    build:
      context: ../..
      dockerfile: src/Services/Ordering/Ordering.Api/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      # Runtime identity (DML only) — never the migrator connection.
      ConnectionStrings__Ordering: "${ORDERING_CONNECTION}"
      ConnectionStrings__RedisCache: "redis-cache:6379"
      ConnectionStrings__RedisCoordination: "redis-coordination:6379"
      ConnectionStrings__RabbitMq: "amqp://guest:guest@rabbitmq:5672"
      # The authority, to validate inbound tokens (§11.2). No Identity__Client__*:
      # Ordering calls no peer synchronously — prices come from a local
      # projection (§6.4) and the rest goes over the broker. Only the BFF holds
      # client credentials (§9.7, §11.5).
      Identity__Authority: "http://keycloak:8080/realms/commerce"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
    ports: [ "5101:8080" ]
    depends_on:
      ordering-migrator:  { condition: service_completed_successfully }
      redis-cache:        { condition: service_healthy }
      redis-coordination: { condition: service_healthy }
      rabbitmq:           { condition: service_healthy }

  # catalog-api, inventory-api, payments-api, shipping-worker and
  # notifications-worker follow the same shape — and "the same shape" is a
  # PAIR: a {service}-migrator one-shot plus the service itself gated on
  # `condition: service_completed_successfully`. Every service owns a database
  # and none may migrate at startup (§4.1, ADR-007), so a service added here
  # without its migrator starts against an empty schema.

  gateway:
    build:
      context: ../..
      dockerfile: src/Gateway/Gateway.Api/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      # The authority, because the gateway validates JWTs like every other host
      # (§11.2). NOT Identity__Client__* — those are for calling other services
      # (§11.5), and the gateway calls nobody: YARP forwards the caller's token.
      Identity__Authority: "http://keycloak:8080/realms/commerce"
      # Locally the gateway IS the edge: nothing forwards, RemoteIpAddress is
      # already the client, and trusting X-Forwarded-For would let any caller
      # pick its own rate-limit bucket. In Kubernetes this is true (§15.3).
      Ingress__Enabled: "false"
      # Browsers hit the gateway directly in dev.
      Cors__Enabled: "true"
      Cors__Origins__0: "http://localhost:3000"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
    ports: [ "5000:8080" ]
    depends_on:
      keycloak: { condition: service_started }
      ordering-api: { condition: service_started }

  # The one host with client credentials, because it is the one host that calls
  # a peer synchronously (§9.7). Everything else here has the authority only.
  # Named web-bff, matching the Aspire resource (§14.2) and the YARP
  # destination (§10.2) — the gateway resolves the destination by hostname, so
  # the container name IS the routing configuration.
  web-bff:
    build:
      context: ../..
      dockerfile: src/BFF/Web.Bff/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      Identity__Authority: "http://keycloak:8080/realms/commerce"
      # Required by ValidateOnStart (§15.4) — this host refuses to boot
      # without them. Local values only; production mounts a secret.
      Identity__Client__ClientId: "web-bff"
      Identity__Client__ClientSecret: "${BFF_CLIENT_SECRET:-local-dev-secret}"
      Identity__Client__Scope: "commerce-api"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
    ports: [ "5200:8080" ]
    depends_on:
      # Keycloak only. catalog-api is elided from this file (see the comment
      # above the gateway), and Compose rejects a dependency on a service it
      # cannot see — one undefined name fails the whole `up`, not one service.
      keycloak: { condition: service_started }

volumes:
  sql-data:
  redis-coordination-data:
  rabbit-data:
```

```bash
docker compose -f deploy/compose/docker-compose.yml up -d
```

| Endpoint | URL |
|---|---|
| Gateway | http://localhost:5000 |
| Keycloak | http://localhost:8080 (admin/admin) |
| RabbitMQ management | http://localhost:15672 (guest/guest) |
| Grafana | http://localhost:3000 |

An override file runs infrastructure in containers while services run on the
host with a debugger attached — the usual inner-loop compromise:

```bash
docker compose -f docker-compose.yml -f docker-compose.infra-only.yml up -d
dotnet run --project src/Services/Ordering/Ordering.Api
```

## 14.2 Aspire — optional accelerator

Aspire (MIT, Microsoft) replaces the Compose file with a C# program that starts
containers *and* your projects, injects connection strings and service discovery
automatically, and ships an OpenTelemetry dashboard with distributed tracing
already wired.

The practical difference is the inner loop: Compose containerises your services,
so debugging seven of them at once is awkward. Aspire runs your projects as host
processes while containerising only the infrastructure, so a single F5 gives
breakpoints across every service simultaneously.

```csharp
// src/AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

// Resource names ARE connection-string names: WithReference(x) injects
// ConnectionStrings__{x.Name}. They must match the keys the code reads
// (§4.2, §8.2) exactly — configuration is case-insensitive but not
// punctuation-insensitive, so "redis-cache" would not satisfy
// GetConnectionString("RedisCache") and both Redis connections would be null.
var sql = builder.AddSqlServer("sql").WithDataVolume();

// Two Redis resources, mirroring §8.1 — the eviction policies are
// incompatible, so a single instance would silently evict held locks.
var cache = builder.AddRedis("RedisCache")
                   .WithRedisCommander();
var coordination = builder.AddRedis("RedisCoordination")
                          .WithDataVolume()       // locks must survive a restart
                          .WithPersistence();

var mq = builder.AddRabbitMQ("RabbitMq").WithManagementPlugin();

// One database per service that this AppHost runs. Inventory, Payments,
// Shipping and Notifications are omitted deliberately — adding a database
// without the service and migrator that own it creates a schema nothing
// maintains, which is the shape §4.1 rules out.
var orderingDb = sql.AddDatabase("Ordering");
var catalogDb  = sql.AddDatabase("Catalog");

var keycloak = builder.AddKeycloak("keycloak", 8080)
                      .WithRealmImport("./keycloak/realm-export.json");

// ReferenceExpression, not string concatenation: GetEndpoint() returns a
// deferred reference — the port is not allocated yet. Concatenating it with +
// would stringify the object and write a placeholder into the environment.
var authority = ReferenceExpression.Create(
    $"{keycloak.GetEndpoint("http")}/realms/commerce");

// Every host validates JWTs (§11.2), so every host needs the authority.
// Applied by one helper rather than repeated per resource — the previous
// version configured ordering-api and left catalog and the gateway with a null
// authority, which fails only at first request.
//
// Client credentials are a SEPARATE concern with a narrower audience: only a
// host that calls another service (§11.5) presents them. Passing a clientId to
// a host that makes no outbound call provisions a Keycloak client, prompts for
// a secret and mounts it, all for credentials nothing ever sends.
IResourceBuilder<ProjectResource> WithPlatformIdentity(
    IResourceBuilder<ProjectResource> project, string? callerClientId = null)
{
    project = project
        .WithEnvironment("Identity__Authority", authority)
        .WaitFor(keycloak);

    if (callerClientId is null) return project;

    return project
        .WithEnvironment("Identity__Client__ClientId", callerClientId)
        // One secret per client, not one shared across all of them: Keycloak
        // issues distinct credentials, and a shared secret would let any
        // service present itself as any other (§11.5). Prompted once and
        // stored in user secrets; in Kubernetes each is its own Secret (§15.4).
        .WithEnvironment("Identity__Client__ClientSecret",
            builder.AddParameter($"{callerClientId}-secret", secret: true))
        .WithEnvironment("Identity__Client__Scope", "commerce-api");
}

// Migrations run as a job here exactly as they do in Compose and Helm —
// ADR-007 forbids migrating at application startup, so without this the
// schema is never created and every service fails on its first query.
// One per service, because one database per service (§7.1).
var orderingMigrator = builder.AddProject<Projects.Ordering_Migrator>("ordering-migrator")
    .WithReference(orderingDb, connectionName: "OrderingMigrator")
    .WaitFor(sql);

var catalogMigrator = builder.AddProject<Projects.Catalog_Migrator>("catalog-migrator")
    .WithReference(catalogDb, connectionName: "CatalogMigrator")
    .WaitFor(sql);

var ordering = WithPlatformIdentity(
    builder.AddProject<Projects.Ordering_Api>("ordering-api")
        .WithReference(orderingDb).WaitFor(orderingDb)
        .WithReference(cache)          // → ConnectionStrings:RedisCache
        .WithReference(coordination)   // → ConnectionStrings:RedisCoordination
        .WithReference(mq).WaitFor(mq)
        // Gate on the migrator completing, not merely starting — the Compose
        // equivalent is service_completed_successfully (§14.1).
        .WaitForCompletion(orderingMigrator)
        .WithHttpHealthCheck("/health/ready"));   // authority only — no peer calls

var catalog = WithPlatformIdentity(
    builder.AddProject<Projects.Catalog_Api>("catalog-api")
        .WithReference(catalogDb).WaitFor(catalogDb)
        .WithReference(cache)
        .WithReference(coordination)
        .WithReference(mq)
        .WaitForCompletion(catalogMigrator)
        .WithHttpHealthCheck("/health/ready"));

// The gateway validates JWTs too (§11.2) — it is the component most visible
// when the authority is missing, and was the one previously left without it.
// No callerClientId: YARP forwards the caller's token rather than minting one
// of its own, so there is no "gateway" Keycloak client and no gateway secret.
WithPlatformIdentity(
    builder.AddProject<Projects.Gateway_Api>("gateway")
        .WithReference(ordering)
        .WithReference(catalog)
        // The same edge shape Compose declares (§14.1), for the same reason:
        // locally the gateway IS the edge, and browsers reach it directly.
        // Diverging here would make a SPA work under one local path and fail
        // under the other.
        .WithEnvironment("Ingress__Enabled", "false")
        .WithEnvironment("Cors__Enabled", "true")
        .WithEnvironment("Cors__Origins__0", "http://localhost:3000")
        // /health/ready, like every other resource and like the chart in
        // §15.3 — an empty readiness set is still the right question here,
        // and probing liveness instead would make the gateway the one
        // component whose local gate differs from its deployed one.
        .WithHttpHealthCheck("/health/ready")
        .WithExternalHttpEndpoints());

// The only resource with a callerClientId, matching Compose (§14.1): the BFF
// is the only host that calls a peer synchronously (§9.7). If a second one
// ever appears, ADR-017's hop budget is the thing to check first.
WithPlatformIdentity(
    builder.AddProject<Projects.Web_Bff>("web-bff")
        .WithReference(catalog)
        .WithHttpHealthCheck("/health/ready"),
    callerClientId: "web-bff");

builder.Build().Run();
```

```bash
aspire run
```

Matching the names is what makes the two local paths interchangeable: §14.1's
Compose sets `ConnectionStrings__RedisCache` by hand, Aspire derives the same
key from the resource name, and the service reads one key either way. A
mismatch here breaks only the Aspire path — which nothing in CI exercises, so it
would surface as "Aspire doesn't work on my machine" rather than as a defect.

Two deliberate local simplifications, both matching what the Compose and test
environments already do:

- **`OrderingMigrator` points at the same SQL login as the runtime connection.**
  [§7.1](07-persistence.md)'s two identities are a production control; locally there is one `sa`
  account, exactly as [§12.4](12-test-strategy.md)'s fixture notes. The *key* still differs, so the
  migrator reads the name it will read in production.
- **The client secret is an Aspire parameter**, prompted once and stored in user
  secrets, rather than a value in the AppHost. It is the same obligation [§15.4](15-cicd-deployment.md)
  records — a required setting needs a source in every environment — met a third
  way.

> **The AppHost is a deployment environment, and drifts like one.** It is the
> only one with no automated exercise: Compose runs in CI, Helm is applied by
> CD, and the integration fixture builds its own. Every configuration change
> lands in three places and can be forgotten in the fourth without anything
> failing. When a required key is added (§15.4), this file is the one to check
> last and the one most likely to be wrong.

**The escape hatch.** Adding one line emits a Compose file from the same model,
so the Aspire dependency is reversible:

```csharp
builder.AddDockerComposeEnvironment("compose");
```

```bash
aspire publish   # writes docker-compose.yaml to ./aspire-output
```

**What adopting Aspire costs.** The AppHost becomes the source of truth for
topology, so the team must learn its model. The API surface has moved quickly —
four major versions in roughly fifteen months. And it is a visible Microsoft
tooling opinion in an otherwise portable stack.

**What removing it costs.** Aspire is not in the production request path;
deployed containers are plain ASP.NET Core. The coupling is four things: the
AppHost project (delete it), `Common.Web` (your own code — keep it, swapping
only service discovery for DNS), the `Aspire.*` client integration packages (one
line per resource per service reverts to standard registration), and the
connection-string environment variable conventions (reproduce them in Compose).
For a platform this size, roughly one to three days of mechanical work.

> **Decision** — Compose is the documented baseline; Aspire is offered as an
> optional accelerator. See [ADR-011](appendix-a-adrs.md#adr-011--compose-baseline-aspire-optional).

## 14.3 Seed data

Seeding runs from the migrator container, is idempotent, and is
development-only. It should produce enough data to exercise pagination and
caching — a catalogue of three products hides every performance problem you
have.

---

[← §13 Observability](13-observability.md) · [Index](README.md) · [§15 CI/CD →](15-cicd-deployment.md)
