#!/usr/bin/env python3
"""Render a new service from the Catalog template (Appendix C, PR-11).

    python tools/new-service/new_service.py Ordering --port 5101

There is no template directory, and that is the design rather than an
omission. The template is `src/Services/Catalog` and `tests/Catalog.*`
themselves — the copy CI builds and `dotnet test` exercises — so there is
exactly one copy of the wiring and an improvement to Catalog reaches the next
service the next time this runs. A tokenised template beside it would be a
second copy that nothing builds and nothing reconciles, which is the drift
class this repository exists to avoid.

The price is that this file names text inside files people edit. It is paid
rather than hidden: every anchor below must match exactly once, the whole
render is built in memory and validated before anything is written, and a miss
raises ScaffoldError naming the file. A tool that fails open on its own
precondition reports success for work it did not do.

Stdlib only, like the licence gate, and for the same reasons: no restore, no
SDK, and it runs on Windows and on the Ubuntu runner without either noticing.

**Python 3.12 is the floor**, because that is what both CI jobs pin. A newer
interpreter on a developer machine is the hazard, not an older one: it accepts
APIs the floor does not, and the local suite goes green on code CI cannot run.
`Path.read_text(newline=…)` is 3.13 and was exactly that mistake once.
"""

from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath

TEMPLATE = "Catalog"

# The five service projects §4.1 gives a service, its three test projects, and
# Catalog.TestSupport — which §4.1 is explicit is NOT a test project, and which
# is copied all the same: the fixture is the template's, and its second consumer
# arrives with the new service's first handler test, exactly as it did for
# Catalog in PR-10. Nine projects, not "five and four".
COPY_ROOTS = (
    "src/Services/Catalog",
    "tests/Catalog.Domain.Tests",
    "tests/Catalog.Application.Tests",
    "tests/Catalog.Api.Tests",
    "tests/Catalog.TestSupport",
)

MIGRATIONS = "src/Services/Catalog/Catalog.Infrastructure/Persistence/Migrations"

# §4.1 gives these two a Worker in place of an Api, and Notifications no Domain
# project at all. This script renders the Api shape, so it refuses them by name
# rather than producing a service that contradicts the chapter — which is the
# quiet failure the documentation's "no Worker template" note did not prevent,
# because a note is not a guard. The names go when the mode arrives.
WORKER_SERVICES = frozenset({"Shipping", "Notifications"})

# Every file under COPY_ROOTS is classified here or the run fails. That is
# deliberate friction, and it is the same argument the domain allow-list gate
# makes in Catalog.Domain.Tests: extending the list is the decision the check
# exists to force. Without it, the next aggregate someone adds to Catalog ships
# silently into every service scaffolded afterwards, and no straggler check
# below would notice — a Categories folder carries none of the tokens this file
# searches for.
COPIED = frozenset(
    {
        "src/Services/Catalog/Catalog.Api/Catalog.Api.csproj",
        "src/Services/Catalog/Catalog.Api/Dockerfile",
        "src/Services/Catalog/Catalog.Api/Program.cs",
        "src/Services/Catalog/Catalog.Application/Catalog.Application.csproj",
        "src/Services/Catalog/Catalog.Application/DependencyInjection.cs",
        "src/Services/Catalog/Catalog.Application/NullDomainEventDispatcher.cs",
        "src/Services/Catalog/Catalog.Domain/Catalog.Domain.csproj",
        "src/Services/Catalog/Catalog.Infrastructure/Catalog.Infrastructure.csproj",
        "src/Services/Catalog/Catalog.Infrastructure/DependencyInjection.cs",
        "src/Services/Catalog/Catalog.Infrastructure/SqlConnectionFactory.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/CatalogDbContext.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/EfUnitOfWork.cs",
        "src/Services/Catalog/Catalog.Migrator/Catalog.Migrator.csproj",
        "src/Services/Catalog/Catalog.Migrator/Dockerfile",
        "src/Services/Catalog/Catalog.Migrator/MigrationRunner.cs",
        "src/Services/Catalog/Catalog.Migrator/MigratorHost.cs",
        "src/Services/Catalog/Catalog.Migrator/Program.cs",
        "tests/Catalog.Domain.Tests/ArchitectureTests.cs",
        "tests/Catalog.Domain.Tests/Catalog.Domain.Tests.csproj",
        "tests/Catalog.Application.Tests/ArchitectureTests.cs",
        "tests/Catalog.Application.Tests/Catalog.Application.Tests.csproj",
        "tests/Catalog.Application.Tests/DependencyInjectionTests.cs",
        "tests/Catalog.Api.Tests/ArchitectureTests.cs",
        "tests/Catalog.Api.Tests/Catalog.Api.Tests.csproj",
        "tests/Catalog.Api.Tests/DatabaseSmokeTests.cs",
        "tests/Catalog.Api.Tests/HostSmokeTests.cs",
        "tests/Catalog.Api.Tests/IntegrationCollection.cs",
        "tests/Catalog.Api.Tests/TransientFaultInjection.cs",
        "tests/Catalog.TestSupport/Catalog.TestSupport.csproj",
        "tests/Catalog.TestSupport/CatalogApiFactory.cs",
        "tests/Catalog.TestSupport/ServiceFixture.cs",
    }
)

# PR-10's slice, and nothing else. A scaffolded service is PR-07's state with
# PR-08, PR-09 and PR-10's wiring on it — not PR-10's state with the nouns
# changed. Renaming Product to Order would hand the next service a deletion
# job and a vocabulary it did not choose.
OMITTED = frozenset(
    {
        "src/Services/Catalog/Catalog.Api/Endpoints/ProductEndpoints.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProducts/GetProductsHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProducts/GetProductsQuery.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetProducts/ProductSummaryDto.cs",
        "src/Services/Catalog/Catalog.Application/Products/PublishProduct/PublishProductCommand.cs",
        "src/Services/Catalog/Catalog.Application/Products/PublishProduct/PublishProductHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/PublishProduct/PublishProductValidator.cs",
        "src/Services/Catalog/Catalog.Domain/Common/Money.cs",
        "src/Services/Catalog/Catalog.Domain/Products/IProductRepository.cs",
        "src/Services/Catalog/Catalog.Domain/Products/Product.cs",
        "src/Services/Catalog/Catalog.Domain/Products/ProductId.cs",
        "src/Services/Catalog/Catalog.Domain/Products/ProductPublishedDomainEvent.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/ProductConfiguration.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/ProductRepository.cs",
        "tests/Catalog.Domain.Tests/MoneyTests.cs",
        "tests/Catalog.Domain.Tests/ProductTests.cs",
        "tests/Catalog.Application.Tests/GetProductsHandlerTests.cs",
        "tests/Catalog.Application.Tests/PublishProductHandlerTests.cs",
        "tests/Catalog.Application.Tests/PublishProductValidatorTests.cs",
        "tests/Catalog.Api.Tests/ProductEndpointsTests.cs",
        # Not slice, but container wiring with nothing left to wire: with the
        # handler tests gone, the collection has no member and the fixture no
        # consumer here. Both return with the service's first handler test,
        # beside the two project references and the provider package the
        # csproj patch below drops for the same reason.
        "tests/Catalog.Application.Tests/IntegrationCollection.cs",
    }
)

# Anchored edits, matched against the Catalog text BEFORE renaming, each
# asserted to occur exactly once. Removing the slice leaves these files making
# claims that are no longer true; every one of them is a claim a reader would
# otherwise trust.
#
# **A replacement may name the template only where it means the new service's
# own project.** `Catalog.Domain` in a replacement is fine — it renames to
# `Inventory.Domain` and the sentence stays true. `Catalog.Application.Tests
# carries both` is not: it renames to `Inventory.Application.Tests carries
# both`, which is a sentence about the exemplar wearing the new service's name,
# and it is false in the one file where those tests are missing. The straggler
# check cannot see this — the rename is exactly what makes the claim wrong — so
# it is carried by review and by GeneratedGuidanceIsTrue in the tests, which
# pins the sites a Grok review found this way.
PATCHES: dict[str, tuple[tuple[str, str], ...]] = {
    "src/Services/Catalog/Catalog.Application/DependencyInjection.cs": (
        ("using Catalog.Application.Products.PublishProduct;\n", ""),
        (
            "        // Explicit rather than scanned, beside the dispatcher it serves —\n"
            "        // §4.2's registration sample is the shape. Since PR-10 the null\n"
            "        // object drops real ProductPublished events, stated in CLAUDE.md's\n"
            "        // phase note; PR-14's outbox dispatcher takes this line over.\n",
            "        // Explicit rather than scanned, beside the dispatcher it serves —\n"
            "        // §4.2's registration sample is the shape. The null object drops\n"
            "        // whatever the first aggregate raises until PR-14's outbox\n"
            "        // dispatcher takes this line over.\n",
        ),
        (
            "        services.AddValidatorsFromAssemblyContaining<PublishProductValidator>();\n",
            "        // §4.2's line spelt over the assembly rather than over a type in\n"
            "        // it, because there is no validator yet to name — and this class,\n"
            "        // the obvious anchor, is static and cannot be a type argument.\n"
            "        // Move to AddValidatorsFromAssemblyContaining<TFirstValidator>()\n"
            "        // with the first one, and add the registration test that guards\n"
            "        // it: ValidationBehavior takes IEnumerable<IValidator<T>>, so a\n"
            "        // lost scan is a pipeline that validates nothing and says so to\n"
            "        // nobody.\n"
            "        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Application/NullDomainEventDispatcher.cs": (
        (
            "/// Drops what it is handed, and since PR-10 it is handed something real:\n"
            "/// every <c>Product.Publish</c> raises a <c>ProductPublishedDomainEvent</c>\n"
            "/// that ends here, because there is no outbox to stage into until PR-14 —\n"
            "/// whose real dispatcher replaces this class. The drop is stated in\n"
            "/// CLAUDE.md's phase note rather than hidden; the aggregate raises anyway,\n"
            "/// so PR-14 picks the events up without touching the domain (§5.5). In\n"
            "/// Application because §4.2's registration sample puts the real one there,\n"
            "/// beside the dispatcher it serves.\n",
            "/// Drops what it is handed, which is nothing until this service has an\n"
            "/// aggregate — and everything it raises after that, because there is no\n"
            "/// outbox to stage into until PR-14, whose real dispatcher replaces this\n"
            "/// class. The aggregate must raise anyway: the domain cannot be allowed to\n"
            "/// teach the defect of not raising, and PR-14 then picks the events up\n"
            "/// without touching it (§5.5). In Application because §4.2's registration\n"
            "/// sample puts the real one there, beside the dispatcher it serves.\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Application/Catalog.Application.csproj": (
        (
            "  <ItemGroup>\n"
            "    <!-- The read side of §6.5: query handlers use Dapper directly, never EF —\n"
            "         the architecture gate in Catalog.Application.Tests holds that line. -->\n"
            "    <PackageReference Include=\"Dapper\" />\n",
            "  <ItemGroup>\n"
            "    <!-- Dapper is not here yet: §6.5's read side uses it directly, and this\n"
            "         project has no query handler to use it. It joins with the first\n"
            "         one — an unused package reference is a claim this project would\n"
            "         not be making. -->\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/DependencyInjection.cs": (
        ("using Catalog.Domain.Products;\n", ""),
        (
            "/// <c>typeof</c> anchor. The <c>IConfiguration</c> parameter arrives with PR-08\n"
            "/// because PR-08 is the first thing that reads one — an unused parameter is the\n"
            "/// same untruth as an unused <c>using</c>.\n",
            "/// <c>typeof</c> anchor. The <c>IConfiguration</c> parameter is here because\n"
            "/// this layer reads one — an unused parameter would be the same untruth as an\n"
            "/// unused <c>using</c>.\n",
        ),
        (
            "        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3\n"
            "        services.AddScoped<IProductRepository, ProductRepository>();         // §5.6\n",
            "        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3\n"
            "\n"
            "        // §5.6's repository registrations join with the first aggregate.\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/Persistence/CatalogDbContext.cs": (
        (
            "        // Landed before its first find (PR-08) so that PR-10 added an\n"
            "        // IEntityTypeConfiguration<T> and not also the line that discovers\n"
            "        // it; ProductConfiguration is what it finds today. §7.2 puts mapping\n"
            "        // in these classes and never in attributes on domain types, which\n"
            "        // would put EF Core in Catalog.Domain.\n",
            "        // Here before its first find, so that the first entity adds an\n"
            "        // IEntityTypeConfiguration<T> and not also the line that discovers\n"
            "        // it. §7.2 puts mapping in these classes and never in attributes on\n"
            "        // domain types, which would put EF Core in Catalog.Domain.\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Api/Program.cs": (
        ("using Catalog.Api.Endpoints;\n", ""),
        (
            "app.MapOpenApi();\n"
            "app.MapProductEndpoints();        "
            "// deliberately unauthenticated until PR-16 (Appendix C)\n",
            "app.MapOpenApi();\n"
            "\n"
            "// This service maps no endpoint of its own yet. The first one goes here,\n"
            "// and it is unauthenticated until PR-16 — say so in\n"
            "// deploy/compose/README.md when it lands (§C.4).\n",
        ),
    ),
    "tests/Catalog.Domain.Tests/ArchitectureTests.cs": (
        ("using Catalog.Domain.Products;\n", ""),
        (
            "        // System.Collections earned its line with the first domain event: a\n"
            "        // record's generated equality goes through EqualityComparer<T>, which\n"
            "        // lives there. No collection type appears in any domain signature.\n"
            "        // System.Linq earned its line with Money's currency guard —\n"
            "        // enumerable logic over owned values is domain work, not an I/O\n"
            "        // dependency, and §5.4's Order sample already leans on it.\n"
            "        string[] allowed = [\"Common.Domain\", \"System.Runtime\", \"System.Collections\", \"System.Linq\"];\n"
            "\n"
            "        IEnumerable<string> referenced = typeof(Product).Assembly\n",
            "        // Two entries, because two is what an empty domain references. The\n"
            "        // two that usually follow, and what earns each: System.Collections\n"
            "        // with the first domain event, whose generated record equality goes\n"
            "        // through EqualityComparer<T>, and System.Linq with the first value\n"
            "        // object doing enumerable logic over owned values — domain work,\n"
            "        // not an I/O dependency.\n"
            "        string[] allowed = [\"Common.Domain\", \"System.Runtime\"];\n"
            "\n"
            "        IEnumerable<string> referenced = typeof(AssemblyMarker).Assembly\n",
        ),
    ),
    "tests/Catalog.Application.Tests/ArchitectureTests.cs": (
        ("using Catalog.Domain.Products;\n", "using Catalog.Domain;\n"),
        (
            "        Assembly[] assemblies = [typeof(DependencyInjection).Assembly, typeof(Product).Assembly];\n",
            # 104 columns, inside CLAUDE.md's 120 budget, so the list stays on
            # one line — the wrapped form was a ragged middle the rule forbids.
            "        Assembly[] assemblies = [typeof(DependencyInjection).Assembly, typeof(AssemblyMarker).Assembly];\n",
        ),
    ),
    "tests/Catalog.Application.Tests/Catalog.Application.Tests.csproj": (
        (
            "    <!-- ServiceCollection itself, for the registration tests: the\n"
            "         abstractions package Catalog.Application compiles against has no\n"
            "         container in it to build. -->\n"
            "    <PackageReference Include=\"Microsoft.Extensions.DependencyInjection\" />\n"
            "    <!-- The handler tests seed and assert through the real CatalogDbContext\n"
            "         (§12.4's seeding rule — a raw INSERT drifts from the aggregate the\n"
            "         first time it gains a column). -->\n"
            "    <PackageReference Include=\"Microsoft.EntityFrameworkCore.SqlServer\" />\n",
            "    <!-- ServiceCollection itself, for the registration tests: the\n"
            "         abstractions package Catalog.Application compiles against has no\n"
            "         container in it to build. -->\n"
            "    <PackageReference Include=\"Microsoft.Extensions.DependencyInjection\" />\n",
        ),
        (
            "    <ProjectReference Include=\"..\\..\\src\\Services\\Catalog\\Catalog.Application\\Catalog.Application.csproj\" />\n"
            "    <!-- §12.1 homes the handler tests here, with real containers — the\n"
            "         fixture lives in TestSupport, shared with Catalog.Api.Tests, which\n"
            "         this project cannot reference. -->\n"
            "    <ProjectReference Include=\"..\\..\\tests\\Catalog.TestSupport\\Catalog.TestSupport.csproj\" />\n"
            "    <!-- CatalogDbContext by name, for seeding and read-back. The test\n"
            "         project may: §4.2's gate binds Catalog.Application, not its tests. -->\n"
            "    <ProjectReference Include=\"..\\..\\src\\Services\\Catalog\\Catalog.Infrastructure\\Catalog.Infrastructure.csproj\" />\n",
            "    <ProjectReference Include=\"..\\..\\src\\Services\\Catalog\\Catalog.Application\\Catalog.Application.csproj\" />\n"
            "    <!-- No Catalog.TestSupport and no Catalog.Infrastructure yet, and no\n"
            "         Docker with them: §12.1 homes the handler tests here against real\n"
            "         containers, and this project has no handler to test. The fixture\n"
            "         reference, the DbContext reference and the provider package all\n"
            "         return with the first one. -->\n",
        ),
    ),
    "tests/Catalog.Application.Tests/DependencyInjectionTests.cs": (
        (
            "using Catalog.Application.Products.GetProducts;\n"
            "using Catalog.Application.Products.PublishProduct;\n",
            "",
        ),
        (
            "\n"
            "    [Fact]\n"
            "    public void AddCatalogApplication_registers_the_command_validator()\n"
            "    {\n"
            "        // ValidationBehavior takes IEnumerable<IValidator<T>>, so a missing\n"
            "        // scan is not a failure — it is a pipeline that validates nothing and\n"
            "        // says so to nobody. The registration is the only place to catch it.\n"
            "        ServiceCollection services = new();\n"
            "\n"
            "        services.AddCatalogApplication();\n"
            "\n"
            "        services.ShouldContain(\n"
            "            d => d.ServiceType == typeof(FluentValidation.IValidator<PublishProductCommand>),\n"
            "            \"AddValidatorsFromAssemblyContaining is §4.2's line, and losing it fails silently\");\n"
            "    }\n"
            "\n"
            "    [Fact]\n"
            "    public void AddCatalogApplication_registers_the_slice_handlers()\n"
            "    {\n"
            "        // The §6.2 scan found nothing until this PR; these two are the first\n"
            "        // real registrations it produces, so the scan itself is now testable.\n"
            "        ServiceCollection services = new();\n"
            "\n"
            "        services.AddCatalogApplication();\n"
            "\n"
            "        services.ShouldContain(d =>\n"
            "            d.ServiceType == typeof(ICommandHandler<PublishProductCommand, Result<Guid>>));\n"
            "        services.ShouldContain(d =>\n"
            "            d.ServiceType == typeof(IQueryHandler<GetProductsQuery, CursorPage<ProductSummaryDto>>));\n"
            "    }\n"
            "}\n",
            "\n"
            "    // Two tests are missing here until this service has a slice, and both\n"
            "    // guard a scan that fails silently: that the validator scan finds a\n"
            "    // validator, and that the §6.2 handler scan produces a registration.\n"
            "    // The service this one was scaffolded from carries both — write them\n"
            "    // with the first command and query.\n"
            "}\n",
        ),
    ),
    "tests/Catalog.TestSupport/ServiceFixture.cs": (
        (
            "    /// this context's <c>HasDefaultSchema</c>, and is no part of what PR-08\n"
            "    /// claims.\n",
            "    /// this context's <c>HasDefaultSchema</c>, and is no part of what this\n"
            "    /// fixture claims.\n",
        ),
        (
            "/// machine cannot disagree about the engine. §12.4's name and §4.1's home:\n"
            "/// the fixture serves <c>Catalog.Application.Tests</c> and\n"
            "/// <c>Catalog.Api.Tests</c>, which cannot reference each other — each\n"
            "/// declares its own <c>IntegrationCollection</c> over this one type. SQL\n",
            "/// machine cannot disagree about the engine. §12.4's name and §4.1's home:\n"
            "/// the fixture serves <c>Catalog.Api.Tests</c> today, and the application\n"
            "/// suite the moment that suite gains a handler test — the two cannot\n"
            "/// reference each other, so each declares its own\n"
            "/// <c>IntegrationCollection</c> over this one type. SQL\n",
        ),
    ),
    "tests/Catalog.Api.Tests/Catalog.Api.Tests.csproj": (
        (
            "    <!-- ServiceFixture and CatalogApiFactory (§12.4, §4.1) — the containers,\n"
            "         the migrator runs and the reset live there, shared with\n"
            "         Catalog.Application.Tests, which this project cannot reference. -->\n",
            "    <!-- ServiceFixture and CatalogApiFactory (§12.4, §4.1) — the containers,\n"
            "         the migrator runs and the reset live there. The application suite\n"
            "         becomes the second consumer with its first handler test, and the\n"
            "         two cannot reference each other — which is why the fixture has a\n"
            "         project of its own. -->\n",
        ),
    ),
    "tests/Catalog.Api.Tests/ArchitectureTests.cs": (
        (
            "/// Vacuously green from PR-07 until PR-10's first endpoint — a rule\n"
            "/// introduced before the violations exist is a constraint, not a backlog\n"
            "/// item — and judging <c>ProductEndpoints</c> for real since.\n",
            "/// Vacuously green until this service maps its first endpoint: a rule\n"
            "/// introduced before the violations exist is a constraint, not a backlog\n"
            "/// item. The rule was observed failing against a deliberately added\n"
            "/// forbidden reference before it was trusted — in the service this one\n"
            "/// was scaffolded from, not here, where there is nothing yet to judge.\n",
        ),
    ),
    "tests/Catalog.Api.Tests/HostSmokeTests.cs": (
        (
            "/// has a readiness check and a host without one does not; Catalog acquired both\n"
            "/// in PR-08, and <c>AddSqlServer</c> throws on a null connection string — so a\n",
            "/// has a readiness check and a host without one does not; this service has\n"
            "/// both from its first commit, and <c>AddSqlServer</c> throws on a null\n"
            "/// connection string — so a\n",
        ),
    ),
    "tests/Catalog.Api.Tests/TransientFaultInjection.cs": (
        (
            "/// of the retry defect is assertable before PR-10's first aggregate exists.\n",
            "/// of the retry defect is assertable before this service has an aggregate.\n",
        ),
    ),
    "tests/Catalog.TestSupport/Catalog.TestSupport.csproj": (
        (
            "    other\". PR-08 gave the fixture one consumer; PR-10's handler tests are the\n"
            "    second, which is the condition §4.1 named for this project to exist.\n",
            "    other\". The API suite is its consumer today; the application suite becomes\n"
            "    the second with its first handler test, which is the condition §4.1 names\n"
            "    for this project to exist.\n",
        ),
    ),
    "tests/Catalog.Api.Tests/DatabaseSmokeTests.cs": (
        (
            "/// PR-08's deliverables against a real engine: the migrator applies the schema\n",
            "/// The persistence layer against a real engine: the migrator applies the schema\n",
        ),
        (
            "        // PR-10's first aggregate. Observed red against a Clear()-less\n",
            "        // this service has an aggregate. Observed red against a Clear()-less\n",
        ),
        (
            "        schema.ShouldBe(1, \"InitialCreate's hand-written EnsureSchema creates it; "
            "AddProducts' is a no-op after it\");\n"
            "\n"
            "        string[] applied = await fixture.AppliedMigrationsAsync();\n"
            "        applied.Length.ShouldBe(2);\n"
            "        applied[0].ShouldEndWith(\"_InitialCreate\");\n"
            "        applied[1].ShouldEndWith(\"_AddProducts\");\n",
            "        schema.ShouldBe(1, \"InitialCreate's hand-written EnsureSchema is what creates it\");\n"
            "\n"
            "        string[] applied = await fixture.AppliedMigrationsAsync();\n"
            "        applied.ShouldHaveSingleItem().ShouldEndWith(\"_InitialCreate\");\n",
        ),
    ),
}

# Keyed on the file's shape rather than on its path, because the path carries
# Catalog's migration timestamp — and a PATCHES key that stopped matching would
# fail *open*, silently leaving the file unpatched. `require_once` still binds
# each anchor.
INITIAL_CREATE_PATCHES: tuple[tuple[str, str], ...] = (
    (
        "/// Catalog's first migration. EF generated an empty <c>Up</c>, because the\n"
        "/// model had no entity types until PR-10 — the schema below is hand-written,\n",
        "/// This service's first migration. EF generates an empty <c>Up</c> for a model\n"
        "/// with no entity types, so the schema below is hand-written,\n",
    ),
    (
        "/// The schema is the one piece of Catalog's shape that exists before its first\n"
        "/// table, and creating it here means PR-10's first <c>CREATE TABLE</c> lands in\n"
        "/// a schema that is already there rather than being ordered against it.\n",
        "/// The schema is the one piece of Catalog's shape that exists before its first\n"
        "/// table, and creating it here means the first <c>CREATE TABLE</c> lands in a\n"
        "/// schema that is already there rather than being ordered against it.\n",
    ),
)

# The one file with no counterpart in Catalog, and it is written to be deleted.
ASSEMBLY_MARKER = """namespace Catalog.Domain;

/// <summary>
/// The <c>typeof</c> anchor §4.2's architecture gates need, and nothing else.
/// A gate that reasons about an assembly has to name a type inside it, and
/// this project has none until its first aggregate.
/// </summary>
/// <remarks>
/// Written to be deleted. When that aggregate lands, re-anchor
/// <c>ArchitectureTests</c> in <c>Catalog.Domain.Tests</c> and
/// <c>Catalog.Application.Tests</c> on it and remove this file. A marker is
/// what a service has before it has a domain; leaving one in place after the
/// first aggregate arrives means the gates are judging an empty type instead
/// of the model they exist to constrain.
/// </remarks>
public sealed class AssemblyMarker;
"""

# Anything left in the rendered tree fails the run. `production` and EF's own
# `ProductVersion` annotation are the two benign substrings, and they are
# removed before the search rather than excused after it.
BENIGN = re.compile(r"[Pp]roduction|ProductVersion")
STRAGGLERS = re.compile(r"catalog|roduct", re.IGNORECASE)

NAME = re.compile(r"^[A-Z][A-Za-z0-9]*$")

# The three casings, matched in one pass. Distinct strings under a
# case-sensitive match, so alternation order carries no meaning.
CASINGS = re.compile("|".join((TEMPLATE, TEMPLATE.lower(), TEMPLATE.upper())))

# A migration id is the timestamp EF generates, and it reaches a path. Anything
# else is both invalid metadata and, with a `..` in it, a write outside the
# service tree — from a flag whose whole purpose is to make a test repeatable.
MIGRATION_ID = re.compile(r"^\d{14}$")

# The three shapes EF puts in a migrations directory. Anything else there is
# somebody's addition, and the scaffold refuses rather than dropping it.
INITIAL_CREATE = re.compile(r"^\d{14}_InitialCreate(\.Designer)?\.cs$")
LATER_MIGRATION = re.compile(r"^\d{14}_\w+(\.Designer)?\.cs$")

# The two files that accumulate a block per service, and the markers that bound
# one block. Both were sliced to the end of the file once, which is the same
# span only until a second service exists.
SERVICE_KEY = re.compile(r"^  ([A-Za-z0-9][A-Za-z0-9_-]*):$")
ENV_MARKER = re.compile(r"^# ([A-Za-z0-9]+)'s two §7\.1 keys")

# Docker publishes 1–65535 and nothing else. §14.1 allocates 51xx by
# convention, which is a decision rather than a rule, so the guard is the
# protocol's limit and the convention stays in the documentation.
PORTS = range(1, 65536)


class ScaffoldError(Exception):
    """A precondition the script will not write over."""


@dataclass(frozen=True)
class Names:
    """The three casings every rename needs."""

    pascal: str

    @property
    def lower(self) -> str:
        return self.pascal.lower()

    @property
    def upper(self) -> str:
        return self.pascal.upper()

    def rename(self, text: str) -> str:
        """One pass over the three casings, never three passes.

        Chained `str.replace` calls feed each replacement to the next, and a
        name that contains a later casing of the template token is rewritten
        twice: `CATALOGSearch` turned a source `Catalog` into
        `CATALOGSEARCHSearch`, because pass one produced text that pass three
        then matched. A single alternation cannot re-enter its own output.
        """
        return CASINGS.sub(
            lambda match: {
                TEMPLATE: self.pascal,
                TEMPLATE.lower(): self.lower,
                TEMPLATE.upper(): self.upper,
            }[match.group(0)],
            text,
        )


@dataclass
class Plan:
    """Everything the run would write, before any of it is written."""

    created: dict[str, str] = field(default_factory=dict)
    updated: dict[str, str] = field(default_factory=dict)


def read(repo_root: Path, relative: str) -> tuple[str, str]:
    """The file with LF endings, and the endings it actually had.

    **The template does not have one line ending, and which files have which
    depends on the platform.** `.gitattributes` forces `*.cs text eol=crlf`, so
    C# is CRLF everywhere; every other file here — `.csproj`, `.slnx`, the
    Compose YAML, the Markdown, the Dockerfiles — carries no attribute, so it
    checks out CRLF on Windows and LF on the Ubuntu runner. Anchors written
    with CRLF therefore match on a developer machine and match nothing in CI,
    which is exactly how this was found. Every anchor in this file is spelt
    with LF and matched against normalised text; the file's own endings go back
    on by `restore` on the way out.
    """
    raw = (repo_root / relative).read_bytes()
    text = raw.decode("utf-8-sig")
    # Escaped, never the character itself: a U+FEFF sitting invisibly inside a
    # string literal is one "strip the BOM" editor command away from silently
    # changing what this script writes.
    if raw.startswith(b"\xef\xbb\xbf"):
        text = "\ufeff" + text

    newline = "\r\n" if "\r\n" in text else "\n"
    return text.replace("\r\n", "\n"), newline


def restore(text: str, newline: str) -> str:
    """Put a file's own line endings back on rendered text."""
    return text if newline == "\n" else text.replace("\n", newline)


def require_once(text: str, needle: str, where: str) -> None:
    """Assert an anchor is there exactly once, or refuse the whole run."""
    count = text.count(needle)
    if count != 1:
        first = needle.splitlines()[0] if needle else repr(needle)
        raise ScaffoldError(
            f"{where}: expected exactly one occurrence of\n"
            f"    {first}\n"
            f"  found {count}. The template has moved; reconcile "
            f"tools/new-service/new_service.py with it."
        )


def classify(repo_root: Path) -> list[str]:
    """The template files to copy, and a refusal if any is unclassified."""
    discovered: list[str] = []
    for root in COPY_ROOTS:
        for path in sorted((repo_root / root).rglob("*")):
            if not path.is_file():
                continue
            relative = PurePosixPath(path.relative_to(repo_root).as_posix())
            if "bin" in relative.parts or "obj" in relative.parts:
                continue
            discovered.append(str(relative))

    copied: list[str] = []
    for relative in discovered:
        if relative.startswith(MIGRATIONS + "/"):
            # Migration file names carry a timestamp, so they are classified by
            # shape rather than by name: a scaffolded service starts at
            # InitialCreate — the hand-written EnsureSchema of §7.4 — and every
            # later migration, and the snapshot, belongs to Catalog's model.
            #
            # Three shapes and no others. An unconditional `continue` here
            # treated *anything* in this directory as classified, so a helper
            # or a README added beside the migrations would be dropped without
            # the guard below ever seeing it — the one directory where the
            # scaffold's "it will not guess" promise silently did not hold.
            name = PurePosixPath(relative).name
            if INITIAL_CREATE.match(name):
                copied.append(relative)
            elif LATER_MIGRATION.match(name) or name == f"{TEMPLATE}DbContextModelSnapshot.cs":
                pass
            else:
                raise ScaffoldError(
                    f"{relative} is not a migration, a designer file or the model snapshot. "
                    f"Classify it in new_service.py — the scaffold will not guess."
                )
            continue
        if relative in COPIED:
            copied.append(relative)
        elif relative not in OMITTED:
            raise ScaffoldError(
                f"{relative} is not classified. Add it to COPIED if every service "
                f"needs it, or to OMITTED if it belongs to Catalog's slice — "
                f"the scaffold will not guess."
            )

    initial = [p for p in copied if p.startswith(MIGRATIONS + "/")]
    if len(initial) != 2:
        raise ScaffoldError(
            f"expected InitialCreate.cs and InitialCreate.Designer.cs under "
            f"{MIGRATIONS}, found {len(initial)}"
        )
    return copied


def snapshot_from_designer(designer: str, migration_id: str) -> str:
    """The model snapshot for an empty model, from the tool's own description of one.

    Catalog's snapshot cannot be copied — it describes `Product`, and the next
    `migrations add` in a service that has no such entity would generate a drop.
    Writing one by hand would break the rule that machine-owned files are left
    exactly as the tool wrote them. `InitialCreate.Designer.cs` resolves both:
    it already holds EF's own description of an empty model with a default
    schema, which is exactly the model a fresh service has, so only the class
    wrapper is rewritten and the model body is never retyped.
    """
    text = designer
    for needle, replacement in (
        ("using Microsoft.EntityFrameworkCore.Migrations;\n", ""),
        (f'    [Migration("{migration_id}_InitialCreate")]\n', ""),
        (
            "    partial class InitialCreate\n",
            "    partial class CatalogDbContextModelSnapshot : ModelSnapshot\n",
        ),
        ("        /// <inheritdoc />\n", ""),
        (
            "        protected override void BuildTargetModel(ModelBuilder modelBuilder)\n",
            "        protected override void BuildModel(ModelBuilder modelBuilder)\n",
        ),
    ):
        require_once(text, needle, "InitialCreate.Designer.cs")
        text = text.replace(needle, replacement)
    return text


def sort_usings(text: str) -> str:
    """Re-sort the leading `using` block, which the rename can reorder.

    EF writes the block sorted, and where the template's namespace sorts is not
    where the new service's does — `Catalog` comes before `Microsoft` and
    `Ordering` comes after it. Sorting after the rename is what keeps the file
    byte-identical to what the next `dotnet ef migrations add` in that service
    would write, so the first real migration produces no spurious diff. Checked
    against the tool rather than assumed: the difference is how it was found.
    """
    lines = text.splitlines(keepends=True)
    first = next((i for i, line in enumerate(lines) if line.startswith("using ")), None)
    if first is None:
        raise ScaffoldError("a machine-owned migration file with no using block")

    last = first
    while last < len(lines) and lines[last].startswith("using "):
        last += 1

    # Keyed on the namespace, not on the whole line: `;` sorts after `.`, so a
    # plain line sort puts Microsoft.EntityFrameworkCore.Infrastructure ahead
    # of Microsoft.EntityFrameworkCore and disagrees with the tool. Also found
    # by diffing against it.
    def namespace(line: str) -> str:
        return line[len("using "):].strip().rstrip(";")

    lines[first:last] = sorted(lines[first:last], key=namespace)
    return "".join(lines)


def render_projects(repo_root: Path, names: Names, migration_id: str) -> dict[str, str]:
    """The nine projects, the marker, the migration and its snapshot."""
    created: dict[str, str] = {}
    csharp_newline = ""

    for relative in classify(repo_root):
        text, newline = read(repo_root, relative)
        if relative.endswith(".cs"):
            csharp_newline = newline

        patches = PATCHES.get(relative, ())
        if PurePosixPath(relative).name.endswith("_InitialCreate.cs"):
            patches = (*patches, *INITIAL_CREATE_PATCHES)
        for needle, replacement in patches:
            require_once(text, needle, relative)
            text = text.replace(needle, replacement)

        target = relative
        rendered = names.rename(text)
        if relative.startswith(MIGRATIONS + "/"):
            name = PurePosixPath(relative).name
            template_id = name.split("_", 1)[0]
            target = f"{MIGRATIONS}/{name.replace(template_id, migration_id, 1)}"
            text = text.replace(template_id, migration_id)
            rendered = names.rename(text)
            if name.endswith(".Designer.cs"):
                rendered = sort_usings(rendered)
                snapshot = names.rename(snapshot_from_designer(text, migration_id))
                created[names.rename(f"{MIGRATIONS}/{TEMPLATE}DbContextModelSnapshot.cs")] = (
                    restore(sort_usings(snapshot), newline)
                )

        created[names.rename(target)] = restore(rendered, newline)

    # The marker is the only file with no template beside it to take endings
    # from, so it takes the ones the template's own C# has. Observed rather
    # than assumed: `.gitattributes` decides this, and reading it here means a
    # change to that rule carries into generated code without a second edit.
    if not csharp_newline:
        raise ScaffoldError("no C# file in the template to take line endings from")

    created[names.rename(f"src/Services/{TEMPLATE}/{TEMPLATE}.Domain/AssemblyMarker.cs")] = (
        restore(names.rename(ASSEMBLY_MARKER), csharp_newline)
    )
    return created


def update_solution(repo_root: Path, names: Names) -> str:
    """Five projects in their own solution folder, four test entries, alphabetical."""
    text, newline = read(repo_root, "Platform.slnx")
    lines = text.splitlines(keepends=True)

    folder = [
        f'  <Folder Name="/src/Services/{names.pascal}/">\n',
        *(
            f'    <Project Path="src/Services/{names.pascal}/{names.pascal}.{layer}'
            f'/{names.pascal}.{layer}.csproj" />\n'
            for layer in ("Api", "Application", "Domain", "Infrastructure", "Migrator")
        ),
        "  </Folder>\n",
    ]

    service_folder = re.compile(r'^  <Folder Name="/src/Services/([^/]+)/">')
    existing = [(i, m.group(1)) for i, line in enumerate(lines) if (m := service_folder.match(line))]
    if not existing:
        raise ScaffoldError("Platform.slnx has no /src/Services/<service>/ folder to insert beside")

    at = len(lines)
    for index, service in existing:
        if service > names.pascal:
            at = index
            break
    else:
        last, _ = existing[-1]
        at = lines.index("  </Folder>\n", last) + 1
    lines[at:at] = folder

    tests = [
        f'    <Project Path="tests/{names.pascal}.{suite}/{names.pascal}.{suite}.csproj" />\n'
        for suite in ("Api.Tests", "Application.Tests", "Domain.Tests", "TestSupport")
    ]
    entry = re.compile(r'^    <Project Path="tests/([^"]+)" />')
    positions = [(i, m.group(1)) for i, line in enumerate(lines) if (m := entry.match(line))]
    if not positions:
        raise ScaffoldError("Platform.slnx has no /tests/ project entries to insert beside")

    for line in reversed(tests):
        path = entry.match(line).group(1)
        at = next((i for i, existing_path in positions if existing_path > path), positions[-1][0] + 1)
        lines.insert(at, line)
        positions = [(i, m.group(1)) for i, l in enumerate(lines) if (m := entry.match(l))]

    return restore("".join(lines), newline)


def update_compose(repo_root: Path, names: Names, port: int) -> str:
    """Catalog's own pair, lifted out of the file being edited and renamed.

    An extraction rather than a template: the block's comments argue the
    inline-default rule and §7.1's two keys, and they travel with the copy.

    **Bounded by the key that follows the pair, never by the collector.** The
    first version sliced from `catalog-migrator` to `otel-collector`, which is
    the same span only until a service has been scaffolded — after that the
    slice swallows the previous service's pair and appends it a second time,
    and duplicate keys make the Compose file invalid. Found by a Copilot review
    asking what a *second* run does; every test until then scaffolded once.
    """
    text, newline = read(repo_root, "deploy/compose/docker-compose.yml")
    if re.search(rf'"{port}:\d+"', text):
        raise ScaffoldError(f"port {port} is already published in deploy/compose/docker-compose.yml")

    lines = text.split("\n")
    keys = [(i, m.group(1)) for i, line in enumerate(lines) if (m := SERVICE_KEY.match(line))]
    order = [name for _, name in keys]

    pair = [f"{TEMPLATE.lower()}-migrator", f"{TEMPLATE.lower()}-api"]
    at = order.index(pair[0]) if pair[0] in order else -1
    if at < 0 or order[at + 1 : at + 2] != pair[1:]:
        raise ScaffoldError(
            f"docker-compose.yml has no {pair[0]} / {pair[1]} pair to copy (§14.1's pair rule)"
        )
    if at + 2 >= len(keys):
        raise ScaffoldError("nothing follows the template's pair to bound the copy")

    start, stop = keys[at][0], keys[at + 2][0]
    # Comments immediately above a service belong to it, not to the pair above.
    while stop > start and lines[stop - 1].lstrip().startswith("#"):
        stop -= 1

    block = names.rename("\n".join(lines[start:stop]))
    published = re.search(r'ports: \[ "(\d+):8080" \]', block)
    if published is None:
        raise ScaffoldError("the template's api block publishes no port to substitute")
    block = block.replace(published.group(0), f'ports: [ "{port}:8080" ]')

    # After the last application block, so services accumulate in the order
    # they were created. `build:` is what marks one — a structural test rather
    # than a hard-coded `otel-collector`, which was only ever the service that
    # happened to come next.
    application = [
        index
        for index, (line_no, _) in enumerate(keys)
        if any(
            body.lstrip().startswith("build:")
            for body in lines[line_no : keys[index + 1][0] if index + 1 < len(keys) else len(lines)]
        )
    ]
    if not application:
        raise ScaffoldError("docker-compose.yml has no application block to insert beside")

    after = keys[application[-1] + 1][0]
    while after > 0 and lines[after - 1].lstrip().startswith("#"):
        after -= 1

    return restore("\n".join([*lines[:after], block, *lines[after:]]), newline)


def update_infra_only(repo_root: Path, names: Names) -> str:
    """Both halves of the pair join the excluded profile — §14.1's own rule."""
    text, newline = read(repo_root, "deploy/compose/docker-compose.infra-only.yml")
    require_once(text, f'  {TEMPLATE.lower()}-api:\n    profiles: [ "excluded" ]\n', "infra-only override")
    added = names.rename(
        f'  {TEMPLATE.lower()}-migrator:\n'
        f'    profiles: [ "excluded" ]\n'
        f'  {TEMPLATE.lower()}-api:\n'
        f'    profiles: [ "excluded" ]\n'
    )
    return restore(text + added, newline)


def update_env_example(repo_root: Path, names: Names) -> str:
    """Catalog's commented pair, extracted so its argument comes with it.

    Bounded by the next service's own marker rather than by the end of the
    file — the same defect as `update_compose`, and found the same way: to EOF
    is the template's block only until one service has been added, after which
    it drags that service's variables along and writes them twice.
    """
    text, newline = read(repo_root, "deploy/compose/.env.example")
    lines = text.split("\n")

    marks = [(i, m.group(1)) for i, line in enumerate(lines) if (m := ENV_MARKER.match(line))]
    template = [i for i, service in marks if service == TEMPLATE]
    if len(template) != 1:
        raise ScaffoldError(
            f".env.example: expected exactly one \"# {TEMPLATE}'s two §7.1 keys\" block, "
            f"found {len(template)}"
        )

    start = template[0]
    following = [i for i, _ in marks if i > start]
    stop = following[0] if following else len(lines)

    block = names.rename("\n".join(lines[start:stop]).rstrip("\n"))
    return restore(text.rstrip("\n") + "\n\n" + block + "\n", newline)


def update_ports_readme(repo_root: Path, names: Names, port: int) -> str:
    """One row in the application-services table — the keyboard inventory (§14.1)."""
    text, newline = read(repo_root, "deploy/compose/README.md")
    header = "| Service | Host port(s) | Notes |\n"
    require_once(text, header, "deploy/compose/README.md")

    start = text.index(header)
    end = text.index("\n\n", start) + 1
    row = (
        f"| {names.pascal} API | http://localhost:{port} | "
        f"`/health/live`, `/health/ready`, `/openapi/v1.json` |\n"
    )
    return restore(text[:end] + row + text[end:], newline)


def plan(repo_root: Path, name: str, port: int, migration_id: str) -> Plan:
    """Everything the run would write, validated. Nothing is written here."""
    if not NAME.match(name):
        raise ScaffoldError(f"'{name}' is not a PascalCase service name")
    # Case-insensitively, because the casings are what the rename keys on:
    # `CATALOG` passes an exact-match check, and its *lower* casing is still
    # `catalog`, so the Compose block it renders keeps the template's own
    # service keys and the file gains a duplicate pair. A case-sensitive
    # filesystem is where that lands — the collision check below hides it on
    # Windows and would not on the runner.
    if name.lower() == TEMPLATE.lower():
        raise ScaffoldError(
            f"{name} is the template under another casing; it cannot be its own copy"
        )

    if name.lower() in {service.lower() for service in WORKER_SERVICES}:
        raise ScaffoldError(
            f"§4.1 gives {name} a Worker in place of an Api, and this script renders the "
            f"Api shape. Worker mode joins with the PR that builds the first worker host; "
            f"until then a {name} scaffolded here would contradict the chapter."
        )

    # And the same test against every service already here, because the
    # template is only the first entry in that set. After Ordering exists,
    # `ORDERING` makes a distinct directory on a case-sensitive filesystem and
    # then renders `ordering-api` and `ordering-migrator` a second time — the
    # duplicate-key failure again, one service along.
    services = repo_root / "src" / "Services"
    taken = (
        {path.name.lower() for path in services.iterdir() if path.is_dir()}
        if services.is_dir()
        else set()
    )
    if name.lower() in taken:
        raise ScaffoldError(
            f"a service whose name differs from {name} only by casing already exists; "
            f"the two would share every lower-cased Compose key and connection variable"
        )
    if not MIGRATION_ID.match(migration_id):
        raise ScaffoldError(
            f"'{migration_id}' is not a 14-digit migration timestamp; it reaches a file path"
        )
    if port not in PORTS:
        raise ScaffoldError(f"port {port} is outside 1–65535 and Docker cannot publish it")
    if not (repo_root / COPY_ROOTS[0]).is_dir():
        raise ScaffoldError(f"{repo_root} does not look like the repository: no {COPY_ROOTS[0]}")

    names = Names(name)
    for root in COPY_ROOTS:
        target = repo_root / names.rename(root)
        if target.exists():
            raise ScaffoldError(f"{names.rename(root)} already exists; this script creates, never merges")

    # The new service's own name is masked before the search, or a legitimate
    # one that contains a template token — CatalogSearch, ProductReviews — is
    # rejected for the tokens it was asked for. What is left after masking is a
    # mention the rename did not reach, which is the only thing this check is
    # about.
    mask = re.compile("|".join(re.escape(n) for n in (names.pascal, names.lower, names.upper)))
    created = render_projects(repo_root, names, migration_id)
    for relative, text in created.items():
        stripped = BENIGN.sub("", mask.sub("", text))
        if (left := STRAGGLERS.search(stripped)) is not None:
            line = stripped[: left.start()].count("\n") + 1
            raise ScaffoldError(
                f"{relative}:{line}: '{left.group(0)}' survived the rename. "
                f"The file names Catalog or its slice somewhere this script does not patch."
            )
        if STRAGGLERS.search(mask.sub("", relative)) is not None:
            raise ScaffoldError(f"{relative}: the path itself still names the template")

    return Plan(
        created=created,
        updated={
            "Platform.slnx": update_solution(repo_root, names),
            "deploy/compose/docker-compose.yml": update_compose(repo_root, names, port),
            "deploy/compose/docker-compose.infra-only.yml": update_infra_only(repo_root, names),
            "deploy/compose/.env.example": update_env_example(repo_root, names),
            "deploy/compose/README.md": update_ports_readme(repo_root, names, port),
        },
    )


def apply(repo_root: Path, rendered: Plan) -> None:
    """Write the plan.

    **This is not atomic, and the guarantee above is about validation only.**
    Every anchor, every classification and every straggler check runs before
    the first file is created, so a run this script *refuses* writes nothing —
    that much is a property. An I/O failure partway through this loop is not
    covered: some targets will exist and some shared files will be updated.

    Staging and rolling back was considered and declined. The target is a git
    checkout, the run is a developer typing one command, and `git status`
    already shows exactly what landed with `git restore` and one `rm -rf` to
    undo it — a bespoke transaction log would be a second, untested mechanism
    for something version control does better. Narrowing the claim is the
    honest half of that decision, and a Copilot review is what caught the
    claim being wider than the code.
    """
    for relative, text in {**rendered.created, **rendered.updated}.items():
        target = repo_root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("w", encoding="utf-8", newline="") as handle:
            handle.write(text)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="new_service.py",
        description="Render a new service from the Catalog template (Appendix C, PR-11).",
    )
    parser.add_argument("name", help="the service name, PascalCase — Ordering, Inventory, Payments")
    parser.add_argument(
        "--port",
        type=int,
        required=True,
        help=(
            "the host port the API publishes. Required, never derived: a port is an "
            "allocation recorded in §14.1 and deploy/compose/README.md, and a script "
            "that guessed one would quietly disagree with a printed chapter"
        ),
    )
    parser.add_argument(
        "--repo-root",
        type=Path,
        default=Path(__file__).resolve().parents[2],
        help="the repository root (default: inferred from this script's location)",
    )
    parser.add_argument(
        "--migration-id",
        default=None,
        help="the InitialCreate migration id (default: the current UTC timestamp)",
    )
    args = parser.parse_args(argv)

    migration_id = args.migration_id or datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    try:
        rendered = plan(args.repo_root, args.name, args.port, migration_id)
        apply(args.repo_root, rendered)
    except ScaffoldError as error:
        print(f"new_service.py: {error}", file=sys.stderr)
        return 1

    print(
        f"{args.name}: {len(rendered.created)} files created, "
        f"{len(rendered.updated)} updated, API on port {args.port}."
    )
    print("Next: dotnet restore Platform.slnx && dotnet build Platform.slnx")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
