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
"""

from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath

TEMPLATE = "Catalog"

# The five service projects and the four test projects §4.1 gives a service.
# Catalog.TestSupport is not a test project and is copied all the same — the
# fixture is the template's, and its second consumer arrives with the new
# service's first handler test, exactly as it did for Catalog in PR-10.
COPY_ROOTS = (
    "src/Services/Catalog",
    "tests/Catalog.Domain.Tests",
    "tests/Catalog.Application.Tests",
    "tests/Catalog.Api.Tests",
    "tests/Catalog.TestSupport",
)

MIGRATIONS = "src/Services/Catalog/Catalog.Infrastructure/Persistence/Migrations"

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
PATCHES: dict[str, tuple[tuple[str, str], ...]] = {
    "src/Services/Catalog/Catalog.Application/DependencyInjection.cs": (
        ("using Catalog.Application.Products.PublishProduct;\r\n", ""),
        (
            "        // Explicit rather than scanned, beside the dispatcher it serves —\r\n"
            "        // §4.2's registration sample is the shape. Since PR-10 the null\r\n"
            "        // object drops real ProductPublished events, stated in CLAUDE.md's\r\n"
            "        // phase note; PR-14's outbox dispatcher takes this line over.\r\n",
            "        // Explicit rather than scanned, beside the dispatcher it serves —\r\n"
            "        // §4.2's registration sample is the shape. The null object drops\r\n"
            "        // whatever the first aggregate raises until PR-14's outbox\r\n"
            "        // dispatcher takes this line over.\r\n",
        ),
        (
            "        services.AddValidatorsFromAssemblyContaining<PublishProductValidator>();\r\n",
            "        // §4.2's line spelt over the assembly rather than over a type in\r\n"
            "        // it, because there is no validator yet to name — and this class,\r\n"
            "        // the obvious anchor, is static and cannot be a type argument.\r\n"
            "        // Move to AddValidatorsFromAssemblyContaining<TFirstValidator>()\r\n"
            "        // with the first one, and restore the registration test\r\n"
            "        // Catalog.Application.Tests carries: ValidationBehavior takes\r\n"
            "        // IEnumerable<IValidator<T>>, so a lost scan is a pipeline that\r\n"
            "        // validates nothing and says so to nobody.\r\n"
            "        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);\r\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Application/NullDomainEventDispatcher.cs": (
        (
            "/// Drops what it is handed, and since PR-10 it is handed something real:\r\n"
            "/// every <c>Product.Publish</c> raises a <c>ProductPublishedDomainEvent</c>\r\n"
            "/// that ends here, because there is no outbox to stage into until PR-14 —\r\n"
            "/// whose real dispatcher replaces this class. The drop is stated in\r\n"
            "/// CLAUDE.md's phase note rather than hidden; the aggregate raises anyway,\r\n"
            "/// so PR-14 picks the events up without touching the domain (§5.5). In\r\n"
            "/// Application because §4.2's registration sample puts the real one there,\r\n"
            "/// beside the dispatcher it serves.\r\n",
            "/// Drops what it is handed, which is nothing until this service has an\r\n"
            "/// aggregate — and everything it raises after that, because there is no\r\n"
            "/// outbox to stage into until PR-14, whose real dispatcher replaces this\r\n"
            "/// class. The aggregate must raise anyway: the domain cannot be allowed to\r\n"
            "/// teach the defect of not raising, and PR-14 then picks the events up\r\n"
            "/// without touching it (§5.5). In Application because §4.2's registration\r\n"
            "/// sample puts the real one there, beside the dispatcher it serves.\r\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Application/Catalog.Application.csproj": (
        (
            "  <ItemGroup>\r\n"
            "    <!-- The read side of §6.5: query handlers use Dapper directly, never EF —\r\n"
            "         the architecture gate in Catalog.Application.Tests holds that line. -->\r\n"
            "    <PackageReference Include=\"Dapper\" />\r\n",
            "  <ItemGroup>\r\n"
            "    <!-- Dapper is not here yet: §6.5's read side uses it directly, and this\r\n"
            "         project has no query handler to use it. It joins with the first\r\n"
            "         one — an unused package reference is a claim this project would\r\n"
            "         not be making. -->\r\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/DependencyInjection.cs": (
        ("using Catalog.Domain.Products;\r\n", ""),
        (
            "        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3\r\n"
            "        services.AddScoped<IProductRepository, ProductRepository>();         // §5.6\r\n",
            "        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3\r\n"
            "\r\n"
            "        // §5.6's repository registrations join with the first aggregate.\r\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/Persistence/CatalogDbContext.cs": (
        (
            "        // Landed before its first find (PR-08) so that PR-10 added an\r\n"
            "        // IEntityTypeConfiguration<T> and not also the line that discovers\r\n"
            "        // it; ProductConfiguration is what it finds today. §7.2 puts mapping\r\n"
            "        // in these classes and never in attributes on domain types, which\r\n"
            "        // would put EF Core in Catalog.Domain.\r\n",
            "        // Here before its first find, so that the first entity adds an\r\n"
            "        // IEntityTypeConfiguration<T> and not also the line that discovers\r\n"
            "        // it. §7.2 puts mapping in these classes and never in attributes on\r\n"
            "        // domain types, which would put EF Core in Catalog.Domain.\r\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Api/Program.cs": (
        ("using Catalog.Api.Endpoints;\r\n", ""),
        (
            "app.MapOpenApi();\r\n"
            "app.MapProductEndpoints();        "
            "// deliberately unauthenticated until PR-16 (Appendix C)\r\n",
            "app.MapOpenApi();\r\n"
            "\r\n"
            "// This service maps no endpoint of its own yet. The first one goes here,\r\n"
            "// and it is unauthenticated until PR-16 — say so in\r\n"
            "// deploy/compose/README.md when it lands, as Catalog does (§C.4).\r\n",
        ),
    ),
    "tests/Catalog.Domain.Tests/ArchitectureTests.cs": (
        ("using Catalog.Domain.Products;\r\n", ""),
        (
            "        // System.Collections earned its line with the first domain event: a\r\n"
            "        // record's generated equality goes through EqualityComparer<T>, which\r\n"
            "        // lives there. No collection type appears in any domain signature.\r\n"
            "        // System.Linq earned its line with Money's currency guard —\r\n"
            "        // enumerable logic over owned values is domain work, not an I/O\r\n"
            "        // dependency, and §5.4's Order sample already leans on it.\r\n"
            "        string[] allowed = [\"Common.Domain\", \"System.Runtime\", \"System.Collections\", \"System.Linq\"];\r\n"
            "\r\n"
            "        IEnumerable<string> referenced = typeof(Product).Assembly\r\n",
            "        // Two entries, because two is what an empty domain references. The\r\n"
            "        // ones that follow are the ones Catalog added and why:\r\n"
            "        // System.Collections with the first domain event, whose generated\r\n"
            "        // record equality goes through EqualityComparer<T>, and System.Linq\r\n"
            "        // with the first value object doing enumerable logic over owned\r\n"
            "        // values — domain work, not an I/O dependency.\r\n"
            "        string[] allowed = [\"Common.Domain\", \"System.Runtime\"];\r\n"
            "\r\n"
            "        IEnumerable<string> referenced = typeof(AssemblyMarker).Assembly\r\n",
        ),
    ),
    "tests/Catalog.Application.Tests/ArchitectureTests.cs": (
        ("using Catalog.Domain.Products;\r\n", "using Catalog.Domain;\r\n"),
        (
            "        Assembly[] assemblies = [typeof(DependencyInjection).Assembly, typeof(Product).Assembly];\r\n",
            "        Assembly[] assemblies =\r\n"
            "            [typeof(DependencyInjection).Assembly, typeof(AssemblyMarker).Assembly];\r\n",
        ),
    ),
    "tests/Catalog.Application.Tests/Catalog.Application.Tests.csproj": (
        (
            "    <!-- ServiceCollection itself, for the registration tests: the\r\n"
            "         abstractions package Catalog.Application compiles against has no\r\n"
            "         container in it to build. -->\r\n"
            "    <PackageReference Include=\"Microsoft.Extensions.DependencyInjection\" />\r\n"
            "    <!-- The handler tests seed and assert through the real CatalogDbContext\r\n"
            "         (§12.4's seeding rule — a raw INSERT drifts from the aggregate the\r\n"
            "         first time it gains a column). -->\r\n"
            "    <PackageReference Include=\"Microsoft.EntityFrameworkCore.SqlServer\" />\r\n",
            "    <!-- ServiceCollection itself, for the registration tests: the\r\n"
            "         abstractions package Catalog.Application compiles against has no\r\n"
            "         container in it to build. -->\r\n"
            "    <PackageReference Include=\"Microsoft.Extensions.DependencyInjection\" />\r\n",
        ),
        (
            "    <ProjectReference Include=\"..\\..\\src\\Services\\Catalog\\Catalog.Application\\Catalog.Application.csproj\" />\r\n"
            "    <!-- §12.1 homes the handler tests here, with real containers — the\r\n"
            "         fixture lives in TestSupport, shared with Catalog.Api.Tests, which\r\n"
            "         this project cannot reference. -->\r\n"
            "    <ProjectReference Include=\"..\\..\\tests\\Catalog.TestSupport\\Catalog.TestSupport.csproj\" />\r\n"
            "    <!-- CatalogDbContext by name, for seeding and read-back. The test\r\n"
            "         project may: §4.2's gate binds Catalog.Application, not its tests. -->\r\n"
            "    <ProjectReference Include=\"..\\..\\src\\Services\\Catalog\\Catalog.Infrastructure\\Catalog.Infrastructure.csproj\" />\r\n",
            "    <ProjectReference Include=\"..\\..\\src\\Services\\Catalog\\Catalog.Application\\Catalog.Application.csproj\" />\r\n"
            "    <!-- No Catalog.TestSupport and no Catalog.Infrastructure yet, and no\r\n"
            "         Docker with them: §12.1 homes the handler tests here against real\r\n"
            "         containers, and this project has no handler to test. The fixture\r\n"
            "         reference, the DbContext reference and the provider package all\r\n"
            "         return with the first one. -->\r\n",
        ),
    ),
    "tests/Catalog.Application.Tests/DependencyInjectionTests.cs": (
        (
            "using Catalog.Application.Products.GetProducts;\r\n"
            "using Catalog.Application.Products.PublishProduct;\r\n",
            "",
        ),
        (
            "\r\n"
            "    [Fact]\r\n"
            "    public void AddCatalogApplication_registers_the_command_validator()\r\n"
            "    {\r\n"
            "        // ValidationBehavior takes IEnumerable<IValidator<T>>, so a missing\r\n"
            "        // scan is not a failure — it is a pipeline that validates nothing and\r\n"
            "        // says so to nobody. The registration is the only place to catch it.\r\n"
            "        ServiceCollection services = new();\r\n"
            "\r\n"
            "        services.AddCatalogApplication();\r\n"
            "\r\n"
            "        services.ShouldContain(\r\n"
            "            d => d.ServiceType == typeof(FluentValidation.IValidator<PublishProductCommand>),\r\n"
            "            \"AddValidatorsFromAssemblyContaining is §4.2's line, and losing it fails silently\");\r\n"
            "    }\r\n"
            "\r\n"
            "    [Fact]\r\n"
            "    public void AddCatalogApplication_registers_the_slice_handlers()\r\n"
            "    {\r\n"
            "        // The §6.2 scan found nothing until this PR; these two are the first\r\n"
            "        // real registrations it produces, so the scan itself is now testable.\r\n"
            "        ServiceCollection services = new();\r\n"
            "\r\n"
            "        services.AddCatalogApplication();\r\n"
            "\r\n"
            "        services.ShouldContain(d =>\r\n"
            "            d.ServiceType == typeof(ICommandHandler<PublishProductCommand, Result<Guid>>));\r\n"
            "        services.ShouldContain(d =>\r\n"
            "            d.ServiceType == typeof(IQueryHandler<GetProductsQuery, CursorPage<ProductSummaryDto>>));\r\n"
            "    }\r\n"
            "}\r\n",
            "\r\n"
            "    // Two tests are missing here until this service has a slice, and both\r\n"
            "    // guard a scan that fails silently: that the validator scan finds a\r\n"
            "    // validator, and that the §6.2 handler scan produces a registration.\r\n"
            "    // Catalog.Application.Tests carries both — copy them across with the\r\n"
            "    // first command and query.\r\n"
            "}\r\n",
        ),
    ),
    "tests/Catalog.Api.Tests/ArchitectureTests.cs": (
        (
            "/// Vacuously green from PR-07 until PR-10's first endpoint — a rule\r\n"
            "/// introduced before the violations exist is a constraint, not a backlog\r\n"
            "/// item — and judging <c>ProductEndpoints</c> for real since.\r\n",
            "/// Vacuously green until this service maps its first endpoint: a rule\r\n"
            "/// introduced before the violations exist is a constraint, not a backlog\r\n"
            "/// item. It has been observed failing against a deliberately added\r\n"
            "/// forbidden reference in Catalog before being trusted here.\r\n",
        ),
    ),
    "tests/Catalog.Api.Tests/DatabaseSmokeTests.cs": (
        (
            "        schema.ShouldBe(1, \"InitialCreate's hand-written EnsureSchema creates it; "
            "AddProducts' is a no-op after it\");\r\n"
            "\r\n"
            "        string[] applied = await fixture.AppliedMigrationsAsync();\r\n"
            "        applied.Length.ShouldBe(2);\r\n"
            "        applied[0].ShouldEndWith(\"_InitialCreate\");\r\n"
            "        applied[1].ShouldEndWith(\"_AddProducts\");\r\n",
            "        schema.ShouldBe(1, \"InitialCreate's hand-written EnsureSchema is what creates it\");\r\n"
            "\r\n"
            "        string[] applied = await fixture.AppliedMigrationsAsync();\r\n"
            "        applied.ShouldHaveSingleItem().ShouldEndWith(\"_InitialCreate\");\r\n",
        ),
    ),
}

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
        return (
            text.replace(TEMPLATE, self.pascal)
            .replace(TEMPLATE.lower(), self.lower)
            .replace(TEMPLATE.upper(), self.upper)
        )


@dataclass
class Plan:
    """Everything the run would write, before any of it is written."""

    created: dict[str, str] = field(default_factory=dict)
    updated: dict[str, str] = field(default_factory=dict)


def read(repo_root: Path, relative: str) -> str:
    """Read preserving line endings and the BOM — the repository is CRLF, and
    EF Core writes its machine-owned files with a byte-order mark that has to
    survive the copy."""
    raw = (repo_root / relative).read_bytes()
    text = raw.decode("utf-8-sig")
    # Escaped, never the character itself: a U+FEFF sitting invisibly inside a
    # string literal is one "strip the BOM" editor command away from silently
    # changing what this script writes.
    return "\ufeff" + text if raw.startswith(b"\xef\xbb\xbf") else text


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
            # rule rather than by name: a scaffolded service starts at
            # InitialCreate — the hand-written EnsureSchema of §7.4 — and every
            # later migration, and the snapshot, belongs to Catalog's model.
            if PurePosixPath(relative).name.split("_", 1)[-1].startswith("InitialCreate"):
                copied.append(relative)
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
        ("using Microsoft.EntityFrameworkCore.Migrations;\r\n", ""),
        (f'    [Migration("{migration_id}_InitialCreate")]\r\n', ""),
        (
            "    partial class InitialCreate\r\n",
            "    partial class CatalogDbContextModelSnapshot : ModelSnapshot\r\n",
        ),
        ("        /// <inheritdoc />\r\n", ""),
        (
            "        protected override void BuildTargetModel(ModelBuilder modelBuilder)\r\n",
            "        protected override void BuildModel(ModelBuilder modelBuilder)\r\n",
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

    for relative in classify(repo_root):
        text = read(repo_root, relative)
        for needle, replacement in PATCHES.get(relative, ()):
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
                    sort_usings(snapshot)
                )

        created[names.rename(target)] = rendered

    created[names.rename(f"src/Services/{TEMPLATE}/{TEMPLATE}.Domain/AssemblyMarker.cs")] = (
        names.rename(ASSEMBLY_MARKER.replace("\n", "\r\n"))
    )
    return created


def update_solution(repo_root: Path, names: Names) -> str:
    """Five projects in their own solution folder, four test entries, alphabetical."""
    lines = read(repo_root, "Platform.slnx").splitlines(keepends=True)

    folder = [
        f'  <Folder Name="/src/Services/{names.pascal}/">\r\n',
        *(
            f'    <Project Path="src/Services/{names.pascal}/{names.pascal}.{layer}'
            f'/{names.pascal}.{layer}.csproj" />\r\n'
            for layer in ("Api", "Application", "Domain", "Infrastructure", "Migrator")
        ),
        "  </Folder>\r\n",
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
        at = lines.index("  </Folder>\r\n", last) + 1
    lines[at:at] = folder

    tests = [
        f'    <Project Path="tests/{names.pascal}.{suite}/{names.pascal}.{suite}.csproj" />\r\n'
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

    return "".join(lines)


def update_compose(repo_root: Path, names: Names, port: int) -> str:
    """Catalog's own pair, lifted out of the file being edited and renamed.

    An extraction rather than a template: the block's comments argue the
    inline-default rule and §7.1's two keys, and they travel with the copy.
    """
    text = read(repo_root, "deploy/compose/docker-compose.yml")
    if re.search(rf'"{port}:\d+"', text):
        raise ScaffoldError(f"port {port} is already published in deploy/compose/docker-compose.yml")

    start = text.index(f"  {TEMPLATE.lower()}-migrator:")
    end = text.index("  otel-collector:")
    block = names.rename(text[start:end])

    published = re.search(r'ports: \[ "(\d+):8080" \]', block)
    if published is None:
        raise ScaffoldError("the template's api block publishes no port to substitute")
    block = block.replace(published.group(0), f'ports: [ "{port}:8080" ]')

    return text[:end] + block + text[end:]


def update_infra_only(repo_root: Path, names: Names) -> str:
    """Both halves of the pair join the excluded profile — §14.1's own rule."""
    text = read(repo_root, "deploy/compose/docker-compose.infra-only.yml")
    require_once(text, f'  {TEMPLATE.lower()}-api:\r\n    profiles: [ "excluded" ]\r\n', "infra-only override")
    return text + names.rename(
        f'  {TEMPLATE.lower()}-migrator:\r\n'
        f'    profiles: [ "excluded" ]\r\n'
        f'  {TEMPLATE.lower()}-api:\r\n'
        f'    profiles: [ "excluded" ]\r\n'
    )


def update_env_example(repo_root: Path, names: Names) -> str:
    """Catalog's commented pair, extracted so its argument comes with it."""
    text = read(repo_root, "deploy/compose/.env.example")
    marker = f"# {TEMPLATE}'s two §7.1 keys"
    require_once(text, marker, ".env.example")
    return text + "\r\n" + names.rename(text[text.index(marker):])


def update_ports_readme(repo_root: Path, names: Names, port: int) -> str:
    """One row in the application-services table — the keyboard inventory (§14.1)."""
    text = read(repo_root, "deploy/compose/README.md")
    header = "| Service | Host port(s) | Notes |\r\n"
    require_once(text, header, "deploy/compose/README.md")

    start = text.index(header)
    end = text.index("\r\n\r\n", start) + 2
    row = (
        f"| {names.pascal} API | http://localhost:{port} | "
        f"`/health/live`, `/health/ready`, `/openapi/v1.json` |\r\n"
    )
    return text[:end] + row + text[end:]


def plan(repo_root: Path, name: str, port: int, migration_id: str) -> Plan:
    """Everything the run would write, validated. Nothing is written here."""
    if not NAME.match(name):
        raise ScaffoldError(f"'{name}' is not a PascalCase service name")
    if name == TEMPLATE:
        raise ScaffoldError(f"{TEMPLATE} is the template; it cannot be its own copy")
    if not (repo_root / COPY_ROOTS[0]).is_dir():
        raise ScaffoldError(f"{repo_root} does not look like the repository: no {COPY_ROOTS[0]}")

    names = Names(name)
    for root in COPY_ROOTS:
        target = repo_root / names.rename(root)
        if target.exists():
            raise ScaffoldError(f"{names.rename(root)} already exists; this script creates, never merges")

    created = render_projects(repo_root, names, migration_id)
    for relative, text in created.items():
        stripped = BENIGN.sub("", text)
        if (left := STRAGGLERS.search(stripped)) is not None:
            line = stripped[: left.start()].count("\n") + 1
            raise ScaffoldError(
                f"{relative}:{line}: '{left.group(0)}' survived the rename. "
                f"The file names Catalog or its slice somewhere this script does not patch."
            )
        if STRAGGLERS.search(relative) is not None:
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
