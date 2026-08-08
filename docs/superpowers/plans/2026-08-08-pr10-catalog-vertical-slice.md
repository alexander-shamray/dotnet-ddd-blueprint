# PR-10 — implementation plan

Derived from `../specs/2026-08-08-pr10-catalog-vertical-slice-design.md` and
frozen with it. Order is TDD's: each step's tests land with its code.

1. **Common building blocks.** `DomainException` in `Common.Domain`;
   `CursorPage<T>`, `Cursor`, `IDbConnectionFactory` in `Common.Application`.
   Cursor codec tests in `Common.Application.Tests`.
2. **Domain.** `ProductId`, `Money`, `DomainException` guards,
   `ProductPublishedDomainEvent`, `Product.Publish`, `IProductRepository`
   (`Add` only). `Catalog.Domain` gains its `Common.Domain` reference…
   which it already has; `AssemblyMarker` deleted, gate re-anchored to
   `Product`. Domain tests first, watched red on the missing behaviour.
3. **Application.** `PublishProduct` slice (command, validator, handler) and
   `GetProducts` slice (query, DTO, Dapper handler). Packages:
   `Dapper` and `FluentValidation.DependencyInjectionExtensions` (new pin +
   Appendix B row) into `Catalog.Application`;
   `AddValidatorsFromAssemblyContaining` into `AddCatalogApplication`.
   Validator unit tests; registration tests extended.
4. **Infrastructure.** `ProductConfiguration`, `ProductRepository`,
   `SqlConnectionFactory` (+ `Microsoft.Data.SqlClient` pin 6.1.1 + Appendix B
   row), registrations, `AddProducts` migration (hand-styled `.cs`, untouched
   Designer/snapshot).
5. **TestSupport.** New `tests/Catalog.TestSupport` library; fixture moves and
   becomes `ServiceFixture` + `ResetAsync` (Respawn); `CatalogApiFactory`
   moves; both test projects get `IntegrationCollection`;
   `DatabaseSmokeTests` joins the collection.
6. **Container tests.** Handler + pagination suite in
   `Catalog.Application.Tests`; endpoint contract suite in
   `Catalog.Api.Tests`.
7. **Endpoints.** `ProductEndpoints`, `Program.cs` wiring; the §4.2 gate now
   judges a real type.
8. **Containers.** Two Dockerfiles, root `.dockerignore`, Compose pair with
   argued divergences, `docker-compose.infra-only.yml` (profiles technique),
   `.env.example`, ports README + unauthenticated statement, smoke workflow
   timeout + header. Verified locally: `docker compose config -q`, full
   `up --wait` including the migrator exit, override run, `down -v`.
9. **Docs.** §7.1 callout rewrite; §14.1 override sample; CLAUDE.md phase +
   trees + TestSupport + events-dropped notes; Appendix B rows landed with
   their packages in 3–4.
10. **Verify.** `dotnet build` clean, full `dotnet test` with Docker,
    `/validate-blueprint`, then `/ship`.
