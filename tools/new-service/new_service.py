#!/usr/bin/env python3
"""Render a new service from the Catalog template (Appendix C, PR-11).

    python tools/new-service/new_service.py Yankee --port 5199

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
from datetime import datetime, timedelta, timezone
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

# The nine projects a render creates, by suffix. Named once because the
# solution writer and the identity check below must agree about them.
PROJECT_SUFFIXES = (
    "Domain",
    "Application",
    "Infrastructure",
    "Migrator",
    "Api",
    "Domain.Tests",
    "Application.Tests",
    "Api.Tests",
    "TestSupport",
)

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
        "src/Services/Catalog/Catalog.Application/Integration/CatalogIntegrationEventMapper.cs",
        "src/Services/Catalog/Catalog.Domain/Catalog.Domain.csproj",
        "src/Services/Catalog/Catalog.Infrastructure/Catalog.Infrastructure.csproj",
        "src/Services/Catalog/Catalog.Infrastructure/DependencyInjection.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Messaging/DependencyInjection.cs",
        "src/Services/Catalog/Catalog.Infrastructure/SqlConnectionFactory.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/CatalogDbContext.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/EfDomainEventCollector.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/OutboxPublisher.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/EfUnitOfWork.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/OutboxMessageConfiguration.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/InboxMessageConfiguration.cs",
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
        "tests/Catalog.Application.Tests/IdempotencyOptInTests.cs",
        "tests/Catalog.Api.Tests/ArchitectureTests.cs",
        "tests/Catalog.Api.Tests/Catalog.Api.Tests.csproj",
        "tests/Catalog.Api.Tests/DatabaseSmokeTests.cs",
        "tests/Catalog.Api.Tests/HostSmokeTests.cs",
        "tests/Catalog.Api.Tests/IntegrationCollection.cs",
        "tests/Catalog.Api.Tests/MessageTypeMapValidatorTests.cs",
        "tests/Catalog.Api.Tests/MessagingRegistrationTests.cs",
        "tests/Catalog.Api.Tests/InboxFilterTests.cs",
        "tests/Catalog.Api.Tests/OutboxDispatcherTests.cs",
        "tests/Catalog.Api.Tests/RetentionPurgeTests.cs",
        "tests/Catalog.Api.Tests/TransientFaultInjection.cs",
        "tests/Catalog.TestSupport/Catalog.TestSupport.csproj",
        "tests/Catalog.TestSupport/CatalogApiFactory.cs",
        # §12.4's test scheme. Copied rather than omitted even though a
        # scaffolded service has no endpoint to authorise: CatalogApiFactory
        # installs it unconditionally, so a service without this file does not
        # compile, and the first slice needs it on the day it arrives.
        "tests/Catalog.TestSupport/TestAuthHandler.cs",
        "tests/Catalog.TestSupport/Outbox/OutboxRows.cs",
        "tests/Catalog.TestSupport/Outbox/OutboxTestEvents.cs",
        "tests/Catalog.TestSupport/ServiceFixture.cs",
    }
)

# PR-10's slice, and nothing else. A scaffolded service is PR-07's state with
# the wiring accumulated through PR-14 on it — not PR-10's state with the
# nouns changed. Renaming Product to Order would hand the next service a
# deletion job and a vocabulary it did not choose.
OMITTED = frozenset(
    {
        "src/Services/Catalog/Catalog.Api/Endpoints/ProductEndpoints.cs",
        # PR-19's pricing hop, whole. §9.7 permits exactly one synchronous
        # downstream call in the platform and Catalog is the callee, so a
        # service scaffolded from it inherits a gRPC server nobody calls,
        # a contract nobody consumes and a second Kestrel endpoint serving
        # neither. The .proto is Catalog's own API rather than a shape
        # every service has.
        #
        # appsettings.json goes with it because it exists ONLY for that
        # hop: it declares the Http2 endpoint gRPC needs, and a cleartext
        # port cannot serve HTTP/1.1 and h2c at once. Omitting it returns
        # the service to the container image's own port configuration,
        # which is what every other host here uses — and NOT omitting it
        # would be worse than redundant, because that file overrides
        # ASPNETCORE_HTTP_PORTS, so a service inheriting it would silently
        # stop listening on whatever its deployment set.
        "src/Services/Catalog/Catalog.Api/appsettings.json",
        "src/Services/Catalog/Catalog.Api/Protos/pricing.proto",
        "src/Services/Catalog/Catalog.Api/Grpc/PricingService.cs",
        # Generic in subject — it translates any ValidationException into
        # InvalidArgument — and slice by requirement: it is registered on
        # AddGrpc, which leaves with the hop, so a service keeping it would
        # carry an interceptor nothing installs.
        "src/Services/Catalog/Catalog.Api/Grpc/ValidationInterceptor.cs",
        # The permission vocabulary (§11.4) is the slice's, not the service's.
        # A host with no endpoint requires no permission, and carrying
        # `ordering:write` into a service that grants it to nothing would put a
        # name in the realm nobody can act on — the same objection as a policy
        # registered and never referenced. The first slice brings the first
        # permission, and the Program.cs patch below drops the policy that
        # names this one.
        "src/Services/Catalog/Catalog.Api/CatalogPermissions.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/GetPricesHandler.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/GetPricesQuery.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/GetPricesValidator.cs",
        "src/Services/Catalog/Catalog.Application/Products/GetPrices/ProductPriceDto.cs",
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
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/MoneyJsonConverter.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/ProductConfiguration.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/ProductRepository.cs",
        "tests/Catalog.Domain.Tests/MoneyTests.cs",
        "tests/Catalog.Domain.Tests/ProductTests.cs",
        "tests/Catalog.Application.Tests/CatalogIntegrationEventMapperTests.cs",
        "tests/Catalog.Application.Tests/GetPricesValidatorTests.cs",
        "tests/Catalog.Application.Tests/GetProductsHandlerTests.cs",
        "tests/Catalog.Application.Tests/OutboxSerialisationTests.cs",
        "tests/Catalog.TestSupport/Outbox/StagesThenFails.cs",
        "tests/Catalog.Application.Tests/PublishProductHandlerTests.cs",
        "tests/Catalog.Application.Tests/PublishProductValidatorTests.cs",
        "tests/Catalog.Api.Tests/OutboxTransportIdentityTests.cs",
        # The gRPC service's own suite, and it leaves for two reasons at
        # once: there is no PricingService to drive, and the channel it
        # builds needs the generated client the csproj patch below drops.
        "tests/Catalog.Api.Tests/PricingServiceTests.cs",
        # PR-26's provider verification, which leaves for a third reason on
        # top of that pair: it is one named consumer's expectations of one
        # named provider. Web.Bff asks Catalog for prices (§9.7 permits
        # exactly one synchronous hop and this is it), so a scaffolded
        # service inherits neither the RPC nor anyone consuming it — and a
        # contract copied to a service no consumer calls is an expectation
        # nobody holds, which is the one thing a consumer-driven contract
        # must never become. The csproj patch below drops the linked
        # PricingContract.cs with it, for the same reason.
        "tests/Catalog.Api.Tests/PricingContractVerificationTests.cs",
        # Both name /v1/catalog/products, so both are slice by requirement:
        # they read the host as a deployment rather than a fixture, and a
        # service with no endpoint has nothing to read. They return with the
        # first slice, beside the endpoint tests below. HostSmokeTests keeps
        # the factory they share — which is why those two tests live in a file
        # of their own rather than in it.
        "tests/Catalog.Api.Tests/EndpointSecurityTests.cs",
        # §11.4's callout, executed: every policy an endpoint names must
        # resolve. With no endpoint there is no policy to enumerate, and the
        # suite's own guard against passing vacuously is what fails first.
        "tests/Catalog.Api.Tests/AuthorizationPolicyTests.cs",
        # Not slice by subject — it is about EfUnitOfWork's rollback — but slice
        # by requirement: the claim is that a rejected command leaves nothing
        # tracked, and making it needs a tracked aggregate. A service with no
        # entity cannot assert it, so it returns with the first real slice.
        "tests/Catalog.Api.Tests/UnitOfWorkRollbackTests.cs",
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
            "        // §4.2's registration sample is the shape. §7.5's real dispatcher\n"
            "        // since PR-14; the NullDomainEventDispatcher that dropped every\n"
            "        // ProductPublishedDomainEvent between PR-10 and here is deleted, not\n"
            "        // disabled, so nothing can register it back by accident.\n",
            "        // Explicit rather than scanned, beside the dispatcher it serves —\n"
            "        // §4.2's registration sample is the shape. It stages nothing until\n"
            "        // this service has an aggregate raising domain events, and needs no\n"
            "        // null object to say so: a collector over an empty change tracker\n"
            "        // returns nothing and the dispatcher exits early (§7.5).\n",
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
    "src/Services/Catalog/Catalog.Application/Integration/CatalogIntegrationEventMapper.cs": (
        (
            "using Catalog.Domain.Products;\n"
            "using Common.Application;\n"
            "using Common.Contracts.Catalog.V1;\n"
            "using Common.Domain;\n",
            "using Common.Application;\n"
            "using Common.Domain;\n",
        ),
        (
            "    // The allow-list. Catalog's other two facts of §3.2 — PriceChanged and\n"
            "    // ProductDiscontinued — join it with the domain operations that raise\n"
            "    // them; an entry here with no domain event behind it would not compile,\n"
            "    // which is the property that keeps this list honest.\n"
            "    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = new()\n"
            "    {\n"
            "        // Domain type in, contract type out. The suffix (§5.5) is what makes\n"
            "        // that visible — with one name for both, this reads as identity, and\n"
            "        // §12.4's \"the domain type never reaches the broker\" would have\n"
            "        // nothing to assert against.\n"
            "        [typeof(ProductPublishedDomainEvent)] = e => ToContract((ProductPublishedDomainEvent)e)\n"
            "    };\n",
            "    // The allow-list, empty until this service publishes something. Every\n"
            "    // domain event it raises is local-only while this dictionary is empty,\n"
            "    // which is the correct state for a service with no contracts — and not\n"
            "    // a gap, because §9.3 makes translation opt-in precisely so that a new\n"
            "    // event is internal until somebody decides otherwise.\n"
            "    //\n"
            "    // An entry is one line and one private ToContract method beside it:\n"
            "    //\n"
            "    //     [typeof(OrderPlacedDomainEvent)] = e => ToContract((OrderPlacedDomainEvent)e)\n"
            "    //\n"
            "    // with the contract living in Common.Contracts under a versioned\n"
            "    // namespace (§9.2), carrying primitives only, and taking its MessageId\n"
            "    // and CorrelationId from the mapper rather than from Stage (§9.1).\n"
            "    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = [];\n",
        ),
        (
            "\n"
            "    // V1.ProductPublished, not ProductPublishedDomainEvent: Money is\n"
            "    // decomposed into a decimal and an ISO code, because a contract may not\n"
            "    // carry domain types (§9.1).\n"
            "    private static ProductPublished ToContract(ProductPublishedDomainEvent e) => new()\n"
            "    {\n"
            "        // Minted here and nowhere else. Stage copies both onto the row and\n"
            "        // DeliverAsync copies them onto the transport, so the body, the row,\n"
            "        // the broker header and the inbox key are one GUID (§9.1).\n"
            "        MessageId = Guid.CreateVersion7(),\n"
            "        // The product, not an ambient request id: a business correlation is\n"
            "        // what a support tool follows across services, and §9.3 sets it from\n"
            "        // the aggregate for exactly that reason.\n"
            "        CorrelationId = e.ProductId.Value,\n"
            "        OccurredAt = e.OccurredAt,\n"
            "        ProductId = e.ProductId.Value,\n"
            "        Name = e.Name,\n"
            "        ThumbnailUrl = e.ThumbnailUrl,\n"
            "        Amount = e.Price.Amount,\n"
            "        Currency = e.Price.Currency\n"
            "    };\n",
            "",
        ),
        (
            "/// §9.3's allow-list for Catalog. §5.5 states the principle — never publish a\n",
            "/// §9.3's allow-list for this service. §5.5 states the principle — never publish a\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Application/Catalog.Application.csproj": (
        # The mapper's registry is emptied and its `using` removed, so nothing
        # in a generated Application project names a contract. Keeping the
        # reference would be the untruth this repository refuses everywhere
        # else: an unused project reference is a claim about the dependency
        # graph that nothing makes true. It returns with the first contract,
        # beside the first registry entry.
        (
            "    <ProjectReference Include=\"..\\..\\..\\BuildingBlocks\\Common.Contracts\\Common.Contracts.csproj\" />\n",
            "",
        ),
        (
            "    Domain, Common.Application and Common.Contracts — the §4.2 dependency\n"
            "    table's second row, complete since PR-14. Contracts arrives with the §9.3\n"
            "    mapper, which is the only type here that names one: the allow-list turns a\n"
            "    domain event into a public record, so the layer that owns the allow-list\n"
            "    is the layer that pays for the reference. §4.3's one assembly that crosses\n"
            "    a service boundary, and it crosses at the mapper.\n",
            "    Domain and Common.Application — the §4.2 dependency table's second row,\n"
            "    minus Common.Contracts. The §9.3 mapper is the only type that would name\n"
            "    a contract, and its allow-list is empty until this service publishes\n"
            "    something — so the reference joins with the first entry in it, and not\n"
            "    before. §4.3's one assembly that crosses a service boundary; it crosses\n"
            "    at the mapper or nowhere.\n",
        ),
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
    "src/Services/Catalog/Catalog.Infrastructure/Catalog.Infrastructure.csproj": (
        (
            "    <!-- §9.4's outbox: the entity this assembly maps, the type map, the\n"
            "         metrics and the dispatcher it hosts. PR-14's edge, and the reason\n"
            "         Common.Infrastructure stopped being a project no service referenced —\n"
            "         PR-12's Redis helpers are still nobody's dependency here, and the\n"
            "         first service to wire them will not need a new reference. -->\n",
            "    <!-- §9.4's outbox: the entity this assembly maps, the type map, the\n"
            "         metrics and the dispatcher it hosts. §8's Redis helpers ride in on\n"
            "         the same reference, so wiring a cache later costs no new one. -->\n",
        ),
        (
            "    <!-- typeof(ProductPublished).Assembly, the Broker lane's half of\n"
            "         MessageTypeSource. Transitive through Catalog.Application, named\n"
            "         directly because this file names the type. -->\n",
            "    <!-- MessageTypeSource's Broker half. Named through IIntegrationEvent\n"
            "         until this service has a contract of its own, at which point the\n"
            "         anchor becomes that contract — same assembly either way. -->\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/DependencyInjection.cs": (
        # Domain, not Domain.Products: the aggregate goes with the slice, and
        # the AssemblyMarker that MessageTypeSource anchors on stays — it lives
        # one namespace up, and dropping the using outright left the generated
        # service naming a type it could not see.
        ("using Catalog.Domain.Products;\n", "using Catalog.Domain;\n"),
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
        ("using Common.Contracts.Catalog.V1;\n", "using Common.Contracts;\n"),
        (
            "        services.AddSingleton(\n"
            "            new MessageTypeSource(typeof(ProductPublished).Assembly, typeof(Product).Assembly));\n",
            "        // IIntegrationEvent and AssemblyMarker stand in for the two anchors\n"
            "        // §9.4 names — this service's contracts and its domain — because it\n"
            "        // has neither yet. Both point at the right assemblies regardless, so\n"
            "        // the first contract and the first aggregate change what these lines\n"
            "        // say and not what they resolve to.\n"
            "        services.AddSingleton(\n"
            "            new MessageTypeSource(typeof(IIntegrationEvent).Assembly, typeof(AssemblyMarker).Assembly));\n",
        ),
        (
            "        // The payload format (§9.4), and the converters that make this\n"
            "        // service's value objects part of it. MoneyJsonConverter is the same\n"
            "        // decision as ProductConfiguration's ComplexProperty one file over —\n"
            "        // Money is persisted twice, as two columns and as two JSON members,\n"
            "        // and knows about neither. Its absence is silent: a Money round-trips\n"
            "        // to zero and a null currency rather than throwing.\n"
            "        services.AddSingleton<JsonConverter, MoneyJsonConverter>();\n"
            "        services.AddSingleton<OutboxJson>();\n",
            "        // The payload format (§9.4). No converters yet, and the first value\n"
            "        // object this service puts on a domain event needs one registered\n"
            "        // here — a type with a private constructor and get-only properties\n"
            "        // does not fail loudly on the Local lane, it deserialises to its\n"
            "        // default. §12.4's round-trip assertion is what catches that, and it\n"
            "        // arrives with the first domain event for the same reason.\n"
            "        services.AddSingleton<OutboxJson>();\n",
        ),
        ("using System.Text.Json.Serialization;\n", ""),
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
    "src/Services/Catalog/Catalog.Api/Catalog.Api.csproj": (
        (
            "    <!-- The server half of §9.7's one synchronous hop. Grpc.AspNetCore brings\n"
            "         Grpc.Tools and Google.Protobuf with it, which is why neither is named\n"
            "         here — Appendix B registers all four as one row because they ship and\n"
            "         version as one thing. -->\n"
            "    <PackageReference Include=\"Grpc.AspNetCore\" />\n",
            "",
        ),
        # The whole ItemGroup, not just the Protobuf line: with the contract
        # gone the group is empty, and an empty ItemGroup is a place a reader
        # looks for something that is not there.
        (
            "\n"
            "  <ItemGroup>\n"
            "    <!-- Catalog owns the contract because Catalog serves it. Web.Bff compiles\n"
            "         this same file as a Client, by link — see the comment on that\n"
            "         reference, and pricing.proto's own header.\n"
            "\n"
            "         BOTH halves, not Server alone, and the client half is here for its own\n"
            "         suite. Catalog.Api.Tests drives PricingService over the real pipeline,\n"
            "         which needs a client; generating one in the test project instead would\n"
            "         put a second copy of every message type in a compilation that already\n"
            "         references this assembly, and CS0436 is an error under ADR-019. So the\n"
            "         choice is a generated client nothing in production calls, or a\n"
            "         transport adapter no test can reach — and an untested adapter is the\n"
            "         worse of the two. Web.Bff.TestSupport's own file argues the mirror\n"
            "         image of this for the server half. -->\n"
            "    <Protobuf Include=\"Protos\\pricing.proto\" GrpcServices=\"Both\" />\n"
            "  </ItemGroup>\n",
            "",
        ),
    ),
    "src/Services/Catalog/Catalog.Api/Program.cs": (
        ("using Catalog.Api;\nusing Catalog.Api.Endpoints;\n", ""),
        ("using Catalog.Api.Grpc;\n", ""),
        # §9.7's hop is Catalog's, so its registration and its mapping both
        # leave. What does NOT leave is the middleware pair below — every
        # host validates its own tokens (§11.2) whether or not it serves
        # anything, which is the same split the permission policy takes.
        (
            "\n"
            "// §9.7's server half. The interceptor is what keeps a malformed request from\n"
            "// arriving at the caller as Unknown, which the BFF would report as its own\n"
            "// 500 rather than the caller's 400 — its own file argues that at length.\n"
            "builder.Services.AddGrpc(o => o.Interceptors.Add<ValidationInterceptor>());\n",
            "",
        ),
        (
            "\n"
            "// §9.7. Reachable only on the Http2 endpoint appsettings.json declares —\n"
            "// gRPC needs HTTP/2, and mapping it says nothing about which port serves it.\n"
            "// The [Authorize] is on the service class, not here, so it travels with the\n"
            "// type rather than with this line.\n"
            "app.MapGrpcService<PricingService>();\n",
            "",
        ),
        # The permission policies leave with the slice that names them. What
        # stays is UseAuthentication/UseAuthorization below: every host
        # validates its own tokens (§11.2) whether or not it has an endpoint,
        # and a service that acquired the middleware only with its first slice
        # would be a service whose health probes were briefly the only thing
        # anybody had checked.
        (
            "// Catalog's permission policies (§11.4). Deliberately not inside either helper\n"
            "// above: Application knows nothing about HTTP, and Common.Web must not know\n"
            "// Catalog's names. One policy, because one endpoint names one — the write\n"
            "// path. A policy nothing references would be an unused registration, and\n"
            "// §11.4's callout is about the opposite mistake: a name an endpoint uses and\n"
            "// nobody registered throws InvalidOperationException on the first request that\n"
            "// reaches it, never at startup. AuthorizationPolicyTests asserts both\n"
            "// directions, from the endpoint metadata rather than from this list.\n"
            "//\n"
            "// RequirePermission rather than RequireClaim(\"permission\", …): the claim type\n"
            "// is Common.Web's (§11.4), so a policy here and the resource-level check\n"
            "// behind ICurrentUser cannot drift apart.\n"
            "builder.Services\n"
            "    .AddAuthorizationBuilder()\n"
            "    .AddPolicy(CatalogPermissions.Write, p => p.RequirePermission(CatalogPermissions.Write));\n"
            "\n",
            "// This service registers no permission policy, because it names no endpoint\n"
            "// that needs one. The first slice brings both together (§11.4):\n"
            "//\n"
            "//     builder.Services\n"
            "//         .AddAuthorizationBuilder()\n"
            "//         .AddPolicy(<Service>Permissions.Write, p => p.RequirePermission(…));\n"
            "//\n"
            "// A policy registered before an endpoint names it is an unused registration;\n"
            "// an endpoint naming one nobody registered throws on the first request that\n"
            "// reaches it, never at startup. Add AuthorizationPolicyTests with the slice —\n"
            "// it enumerates the endpoints and requires every policy they name to resolve.\n"
            "\n",
        ),
        (
            "app.MapOpenApi();\n"
            "app.MapProductEndpoints();        // §11.4\n",
            "app.MapOpenApi();\n"
            "\n"
            "// This service maps no endpoint of its own yet. The first one goes here,\n"
            "// behind RequireAuthorization at the group (§11.4) — fail closed, and let\n"
            "// any deliberately public endpoint say AllowAnonymous out loud.\n",
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
    # §8.5's opt-in gate travels to every service, and one of its three tests
    # cannot travel as written. "This service declares commands; the selector
    # found none" is the anti-vacuity half — and a scaffolded service has no
    # commands at all until its first slice, so that assertion would fail on a
    # tree that is perfectly correct.
    #
    # Deleting it is the wrong fix, and CLAUDE.md names why: a gate that
    # silently stops covering the newest surface is this repository's
    # most-repeated failure, and a vacuous gate with its vacuity check removed
    # IS that failure, written down and shipped. So the assertion is INVERTED
    # instead. The rendered service asserts it has no commands YET, which fails
    # the day it gains one — and the failure message is the instruction to
    # restore the real form. Self-clearing, on the same argument as
    # deploy/observability/awaiting-signal.yaml: a list of things known to be
    # missing needs a gate asserting they are still missing.
    "tests/Catalog.Application.Tests/IdempotencyOptInTests.cs": (
        (
            "    public void The_gate_above_is_looking_at_this_service_s_commands()\n",
            "    public void This_service_has_no_commands_for_the_gate_above_to_look_at_yet()\n",
        ),
        (
            '        Commands().ShouldNotBeEmpty("Catalog declares commands; '
            'the selector above found none");\n',
            "        Commands().ShouldBeEmpty(\n"
            '            "This service declares no commands yet, so the gate above is vacuous. '
            'The day it "\n'
            '            + "gains its first command this test fails — replace it with the '
            'ShouldNotBeEmpty "\n'
            '            + "form, which is what keeps a vacuous gate from quietly becoming a '
            'permanent one.");\n',
        ),
        # The same inversion, one gate down, and it is owed for the same
        # reason: a rendered service opts no command into idempotency, so
        # that gate's own floor would fail on a tree that is correct. It
        # clears itself the day the service opts its first command in.
        (
            '        names.ShouldNotBeEmpty("Catalog declares an idempotent command; '
            'the selector above found none");\n',
            "        names.ShouldBeEmpty(\n"
            '            "This service opts no command into idempotency yet, so the '
            'check below is "\n'
            '            + "vacuous. The day it does, this test fails — replace it '
            'with the ShouldNotBeEmpty "\n'
            '            + "form, which is what keeps a vacuous gate from quietly '
            'becoming a permanent one.");\n',
        ),
        # And a THIRD, for the same reason again — which is the argument for
        # keeping these as data rather than as a rule someone reapplies. §8.5's
        # shape gate opens with its own anti-vacuity floor over `candidates`,
        # and a rendered service has none, so the floor fails on a tree that is
        # correct exactly as the two above would. The count is what makes the
        # point: every anti-vacuity floor added to a template file is owed an
        # entry here, and the third was owed the moment the gate was rewritten
        # to §8.5's specified form.
        (
            "        candidates.ShouldNotBeEmpty(\n"
            '            "no command in this assembly implements IIdempotentCommand, '
            'so this test is " +\n'
            '            "looking at nothing — the interface has been renamed, '
            'moved, or not yet applied.");\n',
            "        candidates.ShouldBeEmpty(\n"
            '            "This service opts no command into idempotency yet, so the '
            'two shape checks below " +\n'
            '            "are vacuous. The day it does, this test fails — restore '
            'the ShouldNotBeEmpty " +\n'
            '            "form, which is what keeps a vacuous gate from quietly '
            'becoming a permanent one.");\n',
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
            "using Catalog.Application.Products.GetPrices;\n"
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
            "\n"
            "        // A query's validator, and it is not the same assertion twice: the scan\n"
            "        // is one call, but ValidationBehavior is unconstrained (§6.3), so a\n"
            "        // query validator lost this way disables the id-list ceiling that is\n"
            "        // GetPrices' only bound — and nothing else would notice.\n"
            "        services.ShouldContain(\n"
            "            d => d.ServiceType == typeof(FluentValidation.IValidator<GetPricesQuery>));\n"
            "    }\n"
            "\n"
            "    [Fact]\n"
            "    public void AddCatalogApplication_registers_the_slice_handlers()\n"
            "    {\n"
            "        // The §6.2 scan found nothing until PR-10; these are the registrations\n"
            "        // it produces, so the scan itself is testable. Every slice adds a row\n"
            "        // here — the scan is public-only, and a handler it misses registers as\n"
            "        // nothing at all rather than as something wrong.\n"
            "        ServiceCollection services = new();\n"
            "\n"
            "        services.AddCatalogApplication();\n"
            "\n"
            "        services.ShouldContain(d =>\n"
            "            d.ServiceType == typeof(ICommandHandler<PublishProductCommand, Result<Guid>>));\n"
            "        services.ShouldContain(d =>\n"
            "            d.ServiceType == typeof(IQueryHandler<GetProductsQuery, CursorPage<ProductSummaryDto>>));\n"
            "\n"
            "        // PR-19's third slice. The scan is public-only (§6.2), so an internal\n"
            "        // handler, a rename or a missed IQueryHandler<,> registers as nothing\n"
            "        // and fails on the first gRPC call rather than at startup —\n"
            "        // ValidateOnBuild never constructs the dispatcher's handler map.\n"
            "        // PricingServiceTests would catch it, but only in the Docker suite.\n"
            "        services.ShouldContain(d =>\n"
            "            d.ServiceType == typeof(IQueryHandler<GetPricesQuery, IReadOnlyList<ProductPriceDto>>));\n"
            "    }\n"
            "}\n",
            "\n"
            "    // Two tests are missing here, and they come back separately rather\n"
            "    // than together. The first handler of either kind earns the one that\n"
            "    // asserts the §6.2 scan produced a registration; the first validator\n"
            "    // earns the one that asserts the validator scan found it. Both scans\n"
            "    // fail silently when lost, which is why neither is left implicit —\n"
            "    // and a query-only slice needs the first and not the second.\n"
            "}\n",
        ),
    ),
    "tests/Catalog.TestSupport/Outbox/OutboxRows.cs": (
        ("using Common.Contracts.Catalog.V1;\n", ""),
        (
            "    /// <summary>\n"
            "    /// A Broker-lane row carrying a real contract, so the publish half of\n"
            "    /// <c>DeliverAsync</c> is exercised against the running broker rather than\n"
            "    /// inferred from the staging tests.\n"
            "    /// </summary>\n"
            "    public static OutboxMessage Broker(ServiceFixture fixture, Guid productId) =>\n"
            "        OutboxMessage.Stage(\n"
            "            new ProductPublished\n"
            "            {\n"
            "                MessageId = Guid.CreateVersion7(),\n"
            "                CorrelationId = productId,\n"
            "                OccurredAt = Raised,\n"
            "                ProductId = productId,\n"
            "                Name = \"Walnut desk\",\n"
            "                ThumbnailUrl = null,\n"
            "                Amount = 19.99m,\n"
            "                Currency = \"EUR\"\n"
            "            },\n"
            "            OutboxLane.Broker,\n"
            "            productId,\n"
            "            fixture.MessageTypes,\n"
            "            fixture.OutboxJson);\n"
            "\n",
            "    // A Broker-lane builder returns with this service's first contract,\n"
            "    // together with the dispatcher test that uses it: staging that lane\n"
            "    // needs a type Common.Contracts publishes on this service's behalf,\n"
            "    // and the allow-list mapper is empty until there is one (§9.3).\n"
            "\n",
        ),
    ),
    "tests/Catalog.Api.Tests/OutboxDispatcherTests.cs": (
        (
            "\n"
            "    [Fact]\n"
            "    public async Task A_broker_row_is_published_and_completed()\n"
            "    {\n"
            "        // The Broker half of DeliverAsync, against the real RabbitMQ the\n"
            "        // fixture runs. Everything else here exercises the Local lane, so\n"
            "        // without this a failure in payload deserialisation, type resolution\n"
            "        // or the publish call would ship while the staging tests and the\n"
            "        // direct-bus smoke both stayed green.\n"
            "        //\n"
            "        // What is asserted is that the row completed — not what reached the\n"
            "        // transport. §12.4 refuses the latter deliberately: observing the\n"
            "        // headers needs an ITestHarness, and this fixture runs the real host\n"
            "        // against the real broker on purpose. Publishing without throwing and\n"
            "        // marking the row processed is the part this suite owns.\n"
            "        await fixture.StageOutboxAsync(OutboxRows.Broker(fixture, Guid.CreateVersion7()));\n"
            "\n"
            "        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(1);\n"
            "\n"
            "        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();\n"
            "        row.Lane.ShouldBe(OutboxLane.Broker);\n"
            "        row.ProcessedAt.ShouldNotBeNull();\n"
            "        row.LastError.ShouldBeNull();\n"
            "    }\n",
            "\n"
            "    // Three tests return with this service's first contract, beside the\n"
            "    // OutboxRows.Broker builder they all need — this one, the Local\n"
            "    // lane's guard below, and OutboxTransportIdentityTests, which pins\n"
            "    // §9.1's single identity onto the transport. Until then the\n"
            "    // allow-list is empty and nothing can build a contract instance, so\n"
            "    // each would assert against a row no code here can produce.\n",
        ),
        (
            "\n"
            "    [Fact]\n"
            "    public async Task An_integration_event_on_the_local_lane_never_reaches_a_projection()\n"
            "    {\n"
            "        // The mirror, and the quieter of the two: ProjectionInvoker is\n"
            "        // generic and unconstrained, so without the guard a contract would be\n"
            "        // offered to any matching IProjectionHandler<T> and the row marked\n"
            "        // processed — no publish, no handler, no trace.\n"
            "        OutboxMessage row = OutboxRows.Broker(fixture, Guid.CreateVersion7());\n"
            "        await fixture.StageOutboxAsync(row);\n"
            "        await fixture.SetOutboxLaneAsync(row.MessageId, OutboxLane.Local);\n"
            "\n"
            "        await fixture.ProcessOutboxBatchAsync();\n"
            "\n"
            "        OutboxMessage failed = (await fixture.OutboxAsync()).ShouldHaveSingleItem();\n"
            "        failed.ProcessedAt.ShouldBeNull();\n"
            "        failed.LastError.ShouldNotBeNull().ShouldContain(nameof(IDomainEvent));\n"
            "    }\n",
            "",
        ),
        # The Broker-lane guard above keeps IIntegrationEvent in use; nothing
        # left names IDomainEvent once the test that did leaves, and an unused
        # using is a claim about a dependency that is not there.
        ("using Common.Domain;\n", ""),
    ),
    "tests/Catalog.TestSupport/ServiceFixture.cs": (
        (
            "    /// this context's <c>HasDefaultSchema</c>, and is no part of what PR-08\n"
            "    /// claims.\n",
            "    /// this context's <c>HasDefaultSchema</c>, and is no part of what this\n"
            "    /// fixture claims.\n",
        ),
        (
            "/// the engine. §12.4's name and §4.1's home: the fixture serves\n"
            "/// <c>Catalog.Application.Tests</c> and <c>Catalog.Api.Tests</c>, which\n"
            "/// cannot reference each other — each declares its own\n",
            "/// the engine. §12.4's name and §4.1's home: the fixture serves\n"
            "/// <c>Catalog.Api.Tests</c> today, and the application suite the moment that\n"
            "/// suite gains a handler test — the two cannot reference each other, so each\n"
            "/// declares its own\n",
        ),
    ),
    "tests/Catalog.Api.Tests/Catalog.Api.Tests.csproj": (
        # PricingServiceTests is Catalog's and does not travel, so the package
        # it named goes with it. A reference nothing in the rendered project
        # uses is the unused-dependency claim CLAUDE.md rules out, one file
        # type over.
        (
            "    <!-- GrpcChannel and Grpc.Core's StatusCode, which PricingServiceTests uses\n"
            "         to call the gRPC server over loopback. Carried transitively through\n"
            "         Catalog.Api; named here on the same honesty rule Web.Bff.Tests states,\n"
            "         because a project that names a type declares the package rather than\n"
            "         relying on a production csproj it does not control. -->\n"
            "    <PackageReference Include=\"Grpc.Net.ClientFactory\" />\n",
            "",
        ),
        # PR-26's linked contract, dropped with the verification suite that
        # compiles it. A rendered service keeps neither: the file is Web.Bff's
        # expectations of CATALOG, so a link to it from Inventory.Api.Tests
        # would compile a contract naming a hop that service does not serve —
        # and then fail to build, because PricingContract names the generated
        # pricing types the Protobuf item above already left with the .proto.
        (
            "  <ItemGroup>\n"
            "    <!-- PR-26's consumer-driven contract, LINKED rather than referenced — the\n"
            "         same relationship pricing.proto already has, one level up. The .proto\n"
            "         is Catalog's because Catalog serves the RPC; this file is Web.Bff's\n"
            "         because only a consumer can say what it needs, and it is compiled into\n"
            "         this suite so the provider can be held to it.\n"
            "\n"
            "         A FILE and not an assembly, so no project dependency is created and\n"
            "         §4.3 is untouched: Common.Contracts is still the only assembly that\n"
            "         crosses a service boundary, and a test helper is expressly not it —\n"
            "         which is why Gateway.Api.Tests carries its own copy of Catalog's\n"
            "         TestAuthHandler rather than referencing one.\n"
            "\n"
            "         The cost is a build-time path into another suite's tree, and unlike the\n"
            "         .proto it is paid once: no Dockerfile builds a test project, so there\n"
            "         is no COPY line to keep in step with it. -->\n"
            "    <Compile Include=\"..\\Web.Bff.TestSupport\\PricingContract.cs\""
            " Link=\"Contract\\PricingContract.cs\" />\n"
            "  </ItemGroup>\n"
            "\n",
            "",
        ),
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
        # The Domain anchor, twice: the whole-service gates need one type per
        # project and a scaffolded service has no aggregate to name, so both
        # sites take the marker the same way Catalog.Application.Tests does.
        # The gates themselves travel unchanged — every one of them is about
        # the shape of the reference graph, which an empty service has as much
        # as a full one.
        ("using Catalog.Domain.Products;\n", "using Catalog.Domain;\n"),
        (
            "        typeof(Product).Assembly,\n",
            "        typeof(AssemblyMarker).Assembly,\n",
        ),
        (
            "/// Vacuously green from PR-07 until PR-10's first endpoint — a rule\n"
            "/// introduced before the violations exist is a constraint, not a backlog\n"
            "/// item — and judging real types since.\n",
            "/// Vacuously green until this service maps its first endpoint: a rule\n"
            "/// introduced before the violations exist is a constraint, not a backlog\n"
            "/// item. The rule was observed failing against a deliberately added\n"
            "/// forbidden reference before it was trusted — in the service this one\n"
            "/// was scaffolded from, not here, where there is nothing yet to judge.\n",
        ),
        # The whole gate travels now, and that is the point of the shape it
        # arrived at: it selects the entire assembly and subtracts the
        # composition root from the FAILURES, so it says something true about a
        # host with no adapters at all. A namespace selector said nothing.
        #
        # Only the two adapter names leave, because they are the exemplar's.
        (
            "        exempted.ShouldContain(\"Program\");\n",
            "        exempted.ShouldContain(\"Program\");\n"
            "\n"
            "        // The other half of this assertion belongs with the first endpoint:\n"
            "        // that the gate is judging something. Until then Program is all\n"
            "        // there is, and naming an adapter that does not exist is not an\n"
            "        // assertion — see the service this one was scaffolded from.\n",
        ),
    ),
    "tests/Catalog.Api.Tests/HostSmokeTests.cs": (
        (
            "/// string has a readiness check and a host without one does not; Catalog\n"
            "/// acquired the SQL pair in PR-08 and the bus pair in PR-13, and both\n",
            "/// string has a readiness check and a host without one does not; this\n"
            "/// service has both pairs from its first commit, and both\n",
        ),
        # The production-scheme host survives the copy — every service wants
        # one — but two claims in its comment are Catalog's rather than the
        # mechanism's. "The one host in the repository" is false the moment a
        # second service is scaffolded, and EndpointSecurityTests is omitted
        # here, so the sentence naming what restoring the base call would
        # delete names a file the reader cannot find.
        (
            "        /// The one host in the repository that keeps the production JWT scheme.\n"
            "        /// Every other factory swaps in <c>TestAuthHandler</c>, which is what\n"
            "        /// lets those suites authenticate at all — and precisely why none of\n"
            "        /// them can say whether its headers mean anything to a real\n"
            "        /// deployment. A test scheme cannot prove its own absence.\n",
            "        /// This service's one host that keeps the production JWT scheme. Every\n"
            "        /// other factory swaps in <c>TestAuthHandler</c>, which is what lets\n"
            "        /// those suites authenticate at all — and precisely why none of them\n"
            "        /// can say whether its headers mean anything to a real deployment. A\n"
            "        /// test scheme cannot prove its own absence.\n",
        ),
        (
            "            // Deliberately empty. Not \"not yet\" — restoring the base call here\n"
            "            // would silently delete EndpointSecurityTests, which is the only\n"
            "            // suite that reads this host as a deployment rather than a fixture.\n",
            "            // Deliberately empty. Not \"not yet\" — this host is the only one\n"
            "            // that reads as a deployment rather than a fixture, and restoring\n"
            "            // the base call would silently take that with it. The forged-header\n"
            "            // suite that reads it arrives with the first endpoint to forge\n"
            "            // against.\n",
        ),
    ),
    "tests/Catalog.Api.Tests/TransientFaultInjection.cs": (
        (
            "/// of the retry defect is assertable before PR-10's first aggregate exists.\n",
            "/// of the retry defect is assertable before this service has an aggregate.\n",
        ),
    ),
    "tests/Catalog.TestSupport/CatalogApiFactory.cs": (
        # The override still matters — it is what a service's first endpoint
        # test will use — but the Catalog source names EndpointSecurityTests as
        # the suite that reads it, and the scaffold omits that file. A generated
        # comment pointing at a suite the service has not got is the same class
        # of false claim as one scheduling a landed PR, which is what the
        # Catalog text used to say and what GeneratedGuidanceIsTrue caught.
        (
            "    /// Virtual, and the one override matters. A host that keeps the production\n"
            "    /// scheme is the only thing that can prove <see cref=\"TestAuthHandler\"/>'s\n"
            "    /// headers mean nothing to a real deployment, which is what\n"
            "    /// <c>EndpointSecurityTests</c> reads it for. A flag would say the same\n"
            "    /// thing; a method says it at the site that makes the decision, which is\n"
            "    /// where the argument for it belongs.\n",
            "    /// Virtual, and the one override matters. A host that keeps the production\n"
            "    /// scheme is the only thing that can prove <see cref=\"TestAuthHandler\"/>'s\n"
            "    /// headers mean nothing to a real deployment, so it arrives with the first\n"
            "    /// endpoint there is anything to forge against. A flag would say the same\n"
            "    /// thing; a method says it at the site that makes the decision, which is\n"
            "    /// where the argument for it belongs.\n",
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
    # Both of the entries below carry the same argument out of a copied file:
    # *why* Catalog binds no receive endpoint is a fact about Catalog's row in
    # §3.2, and a scaffolded service inherits the state without inheriting the
    # reason. The generic replacement says the rule the reason produced.
    "tests/Catalog.Api.Tests/MessagingRegistrationTests.cs": (
        (
            "        // Asserted rather than assumed, which is PR-14's shape one lane over:\n"
            "        // that PR asserted Catalog stages no Local row rather than leaving the\n"
            "        // absence to be inferred.\n"
            "        //\n"
            "        // §3.2 gives Catalog exactly one Consumes cell — StockLevelChanged,\n"
            "        // owned by Inventory, which does not exist. Even with the contract now\n"
            "        // present (PR-15), binding it would create an endpoint whose every\n"
            "        // message reaches §9.4's throw: \"the endpoint binds this type, so\n"
            "        // something should handle it\" is one of the two sites where an empty\n"
            "        // handler list must fail, and §8.4's cache invalidator — the handler\n"
            "        // that eventually arrives — needs a cached query to invalidate.\n",
            "        // Asserted rather than assumed: an absence nobody states is an absence\n"
            "        // nobody notices changing.\n"
            "        //\n"
            "        // A consumer belongs here once §3.2 gives this service something to\n"
            "        // consume and an IIntegrationEventHandler exists for it. Binding a\n"
            "        // type with no handler registered creates an endpoint whose every\n"
            "        // message reaches §9.4's throw: \"the endpoint binds this type, so\n"
            "        // something should handle it\" is one of the two sites where an empty\n"
            "        // handler list must fail rather than proceed.\n",
        ),
        (
            "            \"a consumer here is a subscription §3.2 does not give Catalog — and one bound with no \" +\n",
            "            \"a consumer here is a subscription §3.2 does not give this service — and one bound with no \" +\n",
        ),
    ),
    "tests/Catalog.Api.Tests/InboxFilterTests.cs": (
        (
            "/// Catalog binds no receive endpoint of its own (§3.2 gives it one Consumes\n"
            "/// cell, owned by a service that does not exist), so this suite declares the\n"
            "/// endpoints it needs. That is the same reason PR-14's <c>Local</c> lane was\n"
            "/// proven by handlers in <c>Catalog.TestSupport</c>: the mechanism lands before\n"
            "/// the first service that uses it, and inventing a consumer for Catalog would\n"
            "/// be inventing a subscription §3.2 does not give it.\n",
            "/// This service binds no receive endpoint of its own yet, so this suite\n"
            "/// declares the endpoints it needs. The mechanism lands before the first\n"
            "/// consumer that uses it, and binding one here to make a test easier would be\n"
            "/// inventing a subscription §3.2 does not give this service.\n",
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
            "        // Named and ordered, not merely counted: the migrator's job is to\n"
            "        // apply every migration in sequence, and a count alone would pass on\n"
            "        // a shorter prefix of them applied twice.\n"
            "        string[] applied = await fixture.AppliedMigrationsAsync();\n"
            "        applied.Length.ShouldBe(5);\n"
            "        applied[0].ShouldEndWith(\"_InitialCreate\");\n"
            "        applied[1].ShouldEndWith(\"_AddProducts\");\n"
            "        applied[2].ShouldEndWith(\"_AddOutbox\");\n"
            "        applied[3].ShouldEndWith(\"_AddInbox\");\n"
            "        applied[4].ShouldEndWith(\"_AddOutboxRetentionIndex\");\n",
            "        schema.ShouldBe(1, \"InitialCreate's hand-written EnsureSchema is what creates it\");\n"
            "\n"
            "        // Named and ordered, not merely counted: the migrator's job is to\n"
            "        // apply every migration in sequence, and a count alone would pass on\n"
            "        // a shorter prefix of them applied twice. What a scaffolded service\n"
            "        // starts with is the schema, then §9.4's outbox table, §9.5's inbox\n"
            "        // and the index the retention purge deletes through — all of them\n"
            "        // wiring every service has rather than anything this one chose.\n"
            "        string[] applied = await fixture.AppliedMigrationsAsync();\n"
            "        applied.Length.ShouldBe(4);\n"
            "        applied[0].ShouldEndWith(\"_InitialCreate\");\n"
            "        applied[1].ShouldEndWith(\"_AddOutbox\");\n"
            "        applied[2].ShouldEndWith(\"_AddInbox\");\n"
            "        applied[3].ShouldEndWith(\"_AddOutboxRetentionIndex\");\n",
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
    (
        # The second PR-10 in this file, and it went out unpatched: a
        # scaffolded service inherited "the input to PR-10's migrations add",
        # which is Catalog's history and false everywhere else. Found by a
        # reviewer reading Ordering's rendered copy, one patch below the one
        # that had already neutralised the *first* PR-10 three lines up —
        # a reminder that a file with a patch table is not therefore a file
        # whose references have all been checked.
        "/// the analysers, and are left exactly as the tool wrote them: the snapshot is\n"
        "/// the input to PR-10's <c>migrations add</c>, and an edited one produces a\n"
        "/// wrong migration two PRs later.\n",
        "/// the analysers, and are left exactly as the tool wrote them: the snapshot is\n"
        "/// the input to the next <c>migrations add</c>, and an edited one produces a\n"
        "/// wrong migration the moment one is run.\n",
    ),
)

# The outbox migration's twin, shape-keyed for the same reason: its path carries
# Catalog's timestamp. Only the prose is patched — the DDL below it is the
# tool's own output and is what gives a scaffolded service its outbox table.
OUTBOX_MIGRATION_PATCHES: tuple[tuple[str, str], ...] = (
    (
        "/// §9.4's outbox table, generated from <see cref=\"OutboxMessageConfiguration\"/>\n"
        "/// on AddProducts' terms — the configuration is the source of truth and only\n"
        "/// this file's dress is hand-authored (file-scoped namespace, this comment,\n"
        "/// the field CA1861 asks for). The <c>.Designer.cs</c> and the snapshot beside\n"
        "/// it are machine-owned and untouched.\n",
        "/// §9.4's outbox table, generated from <see cref=\"OutboxMessageConfiguration\"/>\n"
        "/// — the configuration is the source of truth and only this file's dress is\n"
        "/// hand-authored (file-scoped namespace, this comment, the field CA1861 asks\n"
        "/// for). The <c>.Designer.cs</c> and the snapshot beside it are machine-owned\n"
        "/// and untouched.\n",
    ),
)

# And the inbox migration's, for the same reason again. Its remark argues why
# Catalog carries a table it never writes to, which is a fact about Catalog;
# the scaffolded service's copy states the general rule the argument produced.
INBOX_MIGRATION_PATCHES: tuple[tuple[str, str], ...] = (
    (
        "/// §9.5's inbox table, generated from <see cref=\"InboxMessageConfiguration\"/>\n"
        "/// on <c>AddOutbox</c>'s terms — the configuration is the source of truth and\n"
        "/// only this file's dress is hand-authored (file-scoped namespace, this\n"
        "/// comment). The <c>.Designer.cs</c> and the snapshot beside it are\n"
        "/// machine-owned and untouched.\n",
        "/// §9.5's inbox table, generated from <see cref=\"InboxMessageConfiguration\"/>\n"
        "/// — the configuration is the source of truth and only this file's dress is\n"
        "/// hand-authored (file-scoped namespace, this comment). The\n"
        "/// <c>.Designer.cs</c> and the snapshot beside it are machine-owned and\n"
        "/// untouched.\n",
    ),
    (
        "/// <b>The table ships to every service, including the ones that consume\n"
        "/// nothing.</b> Catalog binds no receive endpoint yet (§3.2 gives it one\n"
        "/// Consumes cell, owned by a service that does not exist), so nothing writes a\n"
        "/// row here — but <c>RetentionPurgeService</c> runs from first boot and purges\n"
        "/// both tables, and a purge against a table that is not there logs a failure\n"
        "/// every pass. That is the same argument that keeps <c>AddOutbox</c> in the\n"
        "/// scaffold's output, inverted: the dispatcher would fail a claim, this would\n"
        "/// fail a delete.\n",
        "/// <b>The table arrives before the first consumer, deliberately.</b> A service\n"
        "/// that binds no receive endpoint writes no row here — but\n"
        "/// <c>RetentionPurgeService</c> runs from first boot and purges both tables,\n"
        "/// and a purge against a table that is not there logs a failure every pass.\n"
        "/// That is the same argument that keeps the outbox migration here, inverted:\n"
        "/// the dispatcher would fail a claim, this would fail a delete.\n",
    ),
)

# The retention index's, shape-keyed like the two above. What is dropped is the
# prose about how the gap was found — a scaffolded service inherits the index
# without inheriting the review that noticed its absence.
RETENTION_INDEX_MIGRATION_PATCHES: tuple[tuple[str, str], ...] = (
    (
        "/// The index §9.4's retention purge deletes through, generated from\n"
        "/// <see cref=\"OutboxMessageConfiguration\"/> on <c>AddInbox</c>'s terms — the\n"
        "/// configuration is the source of truth and only this file's dress is\n"
        "/// hand-authored. The <c>.Designer.cs</c> and the snapshot beside it are\n"
        "/// machine-owned and untouched.\n",
        "/// The index §9.4's retention purge deletes through, generated from\n"
        "/// <see cref=\"OutboxMessageConfiguration\"/> — the configuration is the source\n"
        "/// of truth and only this file's dress is hand-authored. The\n"
        "/// <c>.Designer.cs</c> and the snapshot beside it are machine-owned and\n"
        "/// untouched.\n",
    ),
    (
        "/// <para>\n"
        "/// The inbox got its <c>IX_Inbox_HandledAt</c> when its purge was written and\n"
        "/// this one did not, which is the asymmetry a review caught. Filtered the other\n"
        "/// way for the same reason its twin is filtered: the purge never reads an\n"
        "/// unprocessed row, so the index stays the size of the undeleted backlog rather\n"
        "/// than of the table.\n"
        "/// </para>\n",
        "/// <para>\n"
        "/// Filtered the other way for the same reason its twin is filtered: the purge\n"
        "/// never reads an unprocessed row, so the index stays the size of the undeleted\n"
        "/// backlog rather than of the table.\n"
        "/// </para>\n",
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
#
# **Two searches, at two different moments, and the split is load-bearing.**
# The template token is looked for *after* the rename, with the requested name
# masked out, because a service may legitimately contain it — `CatalogSearch`.
# The slice token is looked for *before* the rename, because masking cannot
# help there: a service called `Product` would mask away every real leftover
# along with its own name and the render would call itself domain-neutral.
# Before the rename a `Product` is unambiguous, since the rename maps the
# template's casings and never the slice's.
# Both case-insensitive, because the two halves have to hold to the same
# standard: `SLICE_TOKEN` was not, so `PRODUCT_ENDPOINT` in a copied file
# passed a guard that rejects `ProductEndpoint`.
BENIGN = re.compile(r"production|productversion", re.IGNORECASE)
TEMPLATE_TOKEN = re.compile(re.escape(TEMPLATE), re.IGNORECASE)
SLICE_TOKEN = re.compile(r"roduct", re.IGNORECASE)

# Every anchored pattern here is applied with `fullmatch`, never `match`.
# Python's `$` matches at the end of the string *or just before a trailing
# newline*, so `NAME.match("Zulu\n")` and `MIGRATION_ID.match("20260809120000\n")`
# both succeeded — and the newline then went into a directory name, a file
# name and a C# namespace. `fullmatch` is the whole fix, and it is worth
# stating because the patterns look exhaustive on their own.
NAME = re.compile(r"^[A-Z][A-Za-z0-9]*$")

# The three casings, matched in one pass. Distinct strings under a
# case-sensitive match, so alternation order carries no meaning.
CASINGS = re.compile("|".join((TEMPLATE, TEMPLATE.lower(), TEMPLATE.upper())))

# Windows reserves these as file and directory base names, with or without an
# extension — so neither `src/Services/Con` nor `Con.Domain.csproj` can be
# created there. They pass every other check in this file: PascalCase, no
# template token, no collision. Without this they fail *inside* `apply()`,
# which is the one place the script promises not to.
# The service name becomes a database name and a schema name, and SQL Server's
# `sysname` is nvarchar(128). Past that the projects render happily and the
# first migration is the thing that fails — late, on a machine with a database
# attached, which is the worst place for this script to be wrong.
SQL_IDENTIFIER_LIMIT = 128

# SQL Server's own. `Database=Master` points the migrator at a system database
# instead of an isolated one, and `Sys` collides with the reserved schema —
# both from a name that passes every other check and fails, if it fails at all,
# against a live server.
SQL_RESERVED = frozenset({"MASTER", "MODEL", "MSDB", "TEMPDB", "SYS"})

WINDOWS_RESERVED = frozenset(
    {"CON", "PRN", "AUX", "NUL"}
    | {f"COM{port}" for port in range(1, 10)}
    | {f"LPT{port}" for port in range(1, 10)}
)

# A migration id is the timestamp EF generates, and it reaches a path. Anything
# else is both invalid metadata and, with a `..` in it, a write outside the
# service tree — from a flag whose whole purpose is to make a test repeatable.
MIGRATION_ID = re.compile(r"^\d{14}$")

# The three shapes EF puts in a migrations directory. Anything else there is
# somebody's addition, and the scaffold refuses rather than dropping it.
INITIAL_CREATE = re.compile(r"^\d{14}_InitialCreate(\.Designer)?\.cs$")
# The outbox table is wiring, not slice: §9.4 gives every service one, and a
# scaffolded service that carried the dispatcher without the table would log a
# failed claim twice a second from its first boot. So this migration is copied
# with InitialCreate rather than dropped with Catalog's model changes.
OUTBOX_MIGRATION = re.compile(r"^\d{14}_AddOutbox(\.Designer)?\.cs$")
# The inbox table travels for the mirror of the outbox's reason: §9.5 gives
# every service one, the retention purge runs from first boot and deletes from
# both, and a service that carried the purge without the table would log a
# failed delete every pass. Consuming nothing does not exempt it — Catalog
# itself consumes nothing and has the table for exactly this.
INBOX_MIGRATION = re.compile(r"^\d{14}_AddInbox(\.Designer)?\.cs$")
# The purge's index, and it travels for the same reason the two tables do: the
# claim's index is filtered `WHERE ProcessedAt IS NULL` and so excludes every
# row the purge deletes. A service scaffolded without this one scans its whole
# outbox table hourly from its first boot — the same class of silent cost as a
# dispatcher with no table, and invisible for exactly as long as the table is
# small.
RETENTION_INDEX_MIGRATION = re.compile(r"^\d{14}_AddOutboxRetentionIndex(\.Designer)?\.cs$")
LATER_MIGRATION = re.compile(r"^\d{14}_\w+(\.Designer)?\.cs$")

# The migrations a scaffolded service starts with, in the order they are
# applied — which is the order their ids have to be generated in. A tuple
# rather than one named constant each, because every place below that cares
# needs the position rather than the name: the id is the base plus the index in
# minutes, and the snapshot is derived from the last one's designer.
#
# No count anywhere in this comment, and that is deliberate. It has said two,
# then three, then four inside one pull request, and each stale sentence
# survived alongside its replacement. The tuple is the count.
TEMPLATE_MIGRATIONS = (
    INITIAL_CREATE,
    OUTBOX_MIGRATION,
    INBOX_MIGRATION,
    RETENTION_INDEX_MIGRATION,
)

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
            if any(shape.fullmatch(name) for shape in TEMPLATE_MIGRATIONS):
                copied.append(relative)
            elif LATER_MIGRATION.fullmatch(name) or name == f"{TEMPLATE}DbContextModelSnapshot.cs":
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

    # Each pair counted separately, one shape at a time. A single total over
    # the whole directory would be satisfied by duplicates of one migration and
    # none of another — which is precisely the state that ships a dispatcher
    # with no table behind it, or a purge with no index.
    for shape, label in zip(
        TEMPLATE_MIGRATIONS,
        (
            "InitialCreate",
            "AddOutbox",
            "AddInbox",
            "AddOutboxRetentionIndex",
        ),
        strict=True,
    ):
        pair = [p for p in copied if shape.fullmatch(PurePosixPath(p).name)]
        if len(pair) != 2:
            raise ScaffoldError(
                f"expected {label}.cs and {label}.Designer.cs under "
                f"{MIGRATIONS}, found {len(pair)}"
            )

    # The mirror of the check above, and the half that was missing. A file
    # *added* to Catalog stops the run; a file *deleted* from it did not — the
    # loop simply never saw it, so its patches never ran and `plan` succeeded
    # with a service missing a piece. That is the fail-open shape this whole
    # script is built to avoid, on the manifest itself.
    if (deleted := COPIED - set(discovered)):
        raise ScaffoldError(
            "the template no longer has: "
            + ", ".join(sorted(deleted))
            + ". Remove them from COPIED — with their PATCHES entries — or restore them."
        )

    # And no patch may be inert. A PATCHES key for a file that is not copied
    # never reaches `require_once`, so the anchor it guards would be unbound
    # while every other anchor still looked enforced.
    if (inert := set(PATCHES) - set(copied)):
        raise ScaffoldError(
            "PATCHES names files the scaffold does not copy: "
            + ", ".join(sorted(inert))
            + ". A patch that never runs is an anchor that guards nothing."
        )
    return copied


SLICE_ENTITY = f'            modelBuilder.Entity("{TEMPLATE}.Domain.Products.Product", b =>\n'
# The leading newline matters: without it this matches inside a nested block's
# deeper closer, because sixteen spaces then `});` is a substring of
# twenty-four spaces then `});`. The ComplexProperty block inside Catalog's
# aggregate is exactly that shape, so the removal stopped halfway and left the
# entity's own tail behind — caught by the check at the end of the function,
# which is the reason that check is there rather than trusted away.
ENTITY_END = "\n                });\n\n"


def without_slice_entity(designer: str) -> str:
    """The model body with Catalog's aggregate removed, and nothing else touched.

    A scaffolded service has the outbox entity and no aggregate, so the model
    EF would describe for it is exactly Catalog's minus one `Entity(...)`
    block. Removing that block is the one edit made to a machine-owned file
    here, and it is anchored at both ends rather than parsed: the opening line
    is exact and unique, and the closing `});` at that indent is the first one
    after it. Everything else — property order, annotations, the `using` block
    — stays byte-for-byte what the tool wrote.

    The alternative was to keep deriving from `InitialCreate.Designer.cs`,
    which describes an empty model. That stopped being the truth when the
    outbox joined the template: the snapshot would omit an entity the
    `DbContext` maps, and the first `migrations add` in a scaffolded service
    would generate a second `CreateTable` for a table its own InitialCreate had
    already created.
    """
    require_once(designer, SLICE_ENTITY, "the outbox migration's designer")
    start = designer.index(SLICE_ENTITY)

    end = designer.find(ENTITY_END, start)
    if end == -1:
        raise ScaffoldError(
            "the slice entity block in the designer has no closing `});` at its own indent"
        )

    stripped = designer[:start] + designer[end + len(ENTITY_END):]

    # The aggregate took a using with it. EF emits
    # `using System.Collections.Generic;` for a ComplexProperty mapped as a
    # Dictionary<string, object>, which is how §5.3's Money reaches the model —
    # so with the entity gone the using is unreferenced, and EF would not have
    # written it. Guarded rather than assumed: if any Dictionary< survives the
    # removal the using is still earning its place and stays.
    #
    # Found by diffing against the tool, which is the only way it could be:
    # the scaffolded service built and its migration produced an empty Up, and
    # the sole difference from EF's own rewritten snapshot was this line.
    dictionary_using = "using System.Collections.Generic;\n"
    if "Dictionary<" not in stripped and dictionary_using in stripped:
        stripped = stripped.replace(dictionary_using, "", 1)

    # Masked, like the render loop's own check: EF stamps a ProductVersion
    # annotation on every model it describes, and that token is nobody's
    # aggregate.
    if SLICE_TOKEN.search(BENIGN.sub("", stripped)) is not None:
        raise ScaffoldError(
            "the designer still names the slice after its entity block was removed — "
            "Catalog has gained a second entity, and the scaffold will not guess which"
        )
    return stripped


def snapshot_from_designer(designer: str, migration_id: str) -> str:
    """The model snapshot, from the tool's own description of the same model.

    Catalog's snapshot cannot be copied — it describes `Product`, and the next
    `migrations add` in a service that has no such entity would generate a
    drop. Writing one by hand would break the rule that machine-owned files are
    left exactly as the tool wrote them. The *last* template migration's
    designer resolves both: it already holds EF's description of a model with
    both messaging entities in it, which is what a scaffolded service has once
    `without_slice_entity` has taken the aggregate out, so the class wrapper is
    rewritten and the model body is never retyped.

    The last one, and taking an earlier one would be wrong in a way with no
    symptom until the service's first `migrations add`: the outbox designer
    knows nothing of the inbox, so the snapshot would omit a table the
    `DbContext` maps and EF would generate a second `CreateTable` for one the
    scaffolded migrations had already created.

    The designer reaching here has already had the aggregate removed — the
    render loop does that before its slice check, so that a failed removal
    stops the run rather than reaching a shipped file. Stripping again here
    would find nothing and `require_once` would say so.
    """
    text = designer
    for needle, replacement in (
        ("using Microsoft.EntityFrameworkCore.Migrations;\n", ""),
        (f'    [Migration("{migration_id}_AddOutboxRetentionIndex")]\n', ""),
        (
            "    partial class AddOutboxRetentionIndex\n",
            "    partial class CatalogDbContextModelSnapshot : ModelSnapshot\n",
        ),
        ("        /// <inheritdoc />\n", ""),
        (
            "        protected override void BuildTargetModel(ModelBuilder modelBuilder)\n",
            "        protected override void BuildModel(ModelBuilder modelBuilder)\n",
        ),
    ):
        require_once(text, needle, "AddOutboxRetentionIndex.Designer.cs")
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
    #
    # System first, which is the other half of the tool's order and did not
    # show until the outbox designer arrived: until then the only usings were
    # Microsoft.* and the service's own, and a plain sort happened to agree.
    # EF writes `using System;` and `using System.Collections.Generic;` above
    # everything else, so a service whose name sorts before `System` — every
    # one of them, since these are the only two — would otherwise get a block
    # the next `migrations add` immediately rewrites.
    def namespace(line: str) -> tuple[int, str]:
        name = line[len("using "):].strip().rstrip(";")
        return (0 if name == "System" or name.startswith("System.") else 1, name)

    lines[first:last] = sorted(lines[first:last], key=namespace)
    return "".join(lines)


def next_migration_id(migration_id: str, minutes: int = 1) -> str:
    """The id `minutes` after the given one, keeping EF's 14-digit shape.

    A plain `int(...) + 1` is wrong on every boundary the format has: second 59
    rolls into 60, and so do minute, hour and month. Parsed and re-formatted
    instead, which is the only arithmetic that is right for all of them.

    MIGRATION_ID accepts any fourteen digits, which is the right shape check
    and not a calendar one — `20261301000000` passes it and is month thirteen.
    `strptime` is what notices, and its ValueError is not a ScaffoldError, so
    without this the CLI printed a traceback where every other refusal prints
    one line. OverflowError joins it for the year-9999 end of the range, where
    adding a minute leaves what `datetime` can represent.
    """
    try:
        stamp = datetime.strptime(migration_id, "%Y%m%d%H%M%S") + timedelta(minutes=minutes)
    except (ValueError, OverflowError) as error:
        raise ScaffoldError(
            f"--migration-id {migration_id} is fourteen digits but not a timestamp: {error}"
        ) from error

    return stamp.strftime("%Y%m%d%H%M%S")


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
        elif PurePosixPath(relative).name.endswith("_AddOutbox.cs"):
            patches = (*patches, *OUTBOX_MIGRATION_PATCHES)
        elif PurePosixPath(relative).name.endswith("_AddInbox.cs"):
            patches = (*patches, *INBOX_MIGRATION_PATCHES)
        elif PurePosixPath(relative).name.endswith("_AddOutboxRetentionIndex.cs"):
            patches = (*patches, *RETENTION_INDEX_MIGRATION_PATCHES)
        for needle, replacement in patches:
            require_once(text, needle, relative)
            text = text.replace(needle, replacement)

        # The outbox designer describes Catalog's whole model, aggregate
        # included. Stripped here rather than further down, because the slice
        # check immediately below is exactly the check that should see the
        # result — a Product block surviving the removal must stop the run, not
        # reach the file the service ships.
        # Every designer, not only the last one. Each describes the model as of
        # its own migration and each therefore carries Catalog's aggregate, so
        # leaving the earlier one alone would ship a service a designer that
        # claims a table it never creates — and would trip the slice check
        # below, which is the guard that made this obvious.
        if PurePosixPath(relative).name.endswith(
            (
                "_AddOutbox.Designer.cs",
                "_AddInbox.Designer.cs",
                "_AddOutboxRetentionIndex.Designer.cs",
            )):
            text = without_slice_entity(text)

        # Before the rename, where a slice token means only itself. Doing this
        # after it — with the requested name masked, as the template check
        # must be — would let a service called `Product` mask away the very
        # leftovers this looks for.
        if (slice_token := SLICE_TOKEN.search(BENIGN.sub("", text))) is not None:
            line = BENIGN.sub("", text)[: slice_token.start()].count("\n") + 1
            raise ScaffoldError(
                f"{relative}:{line}: the slice survived. This file names "
                f"Product somewhere the patches do not reach."
            )
        if SLICE_TOKEN.search(relative) is not None:
            raise ScaffoldError(f"{relative}: the path itself names the slice")

        target = relative
        rendered = names.rename(text)
        if relative.startswith(MIGRATIONS + "/"):
            name = PurePosixPath(relative).name
            template_id = name.split("_", 1)[0]

            # One id per template migration, and the order between them is the
            # order they are applied in — EF sorts by this prefix, so a service
            # whose outbox table were ordered before its schema would fail on
            # the first run. A minute apart, spaced by position in
            # TEMPLATE_MIGRATIONS rather than by name, so the next one added is
            # an entry in that tuple and no arithmetic here.
            #
            # No count in this comment on purpose. It has said two, then three,
            # then four inside one pull request, and the stale sentences stacked
            # rather than being replaced — three contradictory claims about the
            # same tuple, which is what a review caught. The tuple is the count.
            offset = next(
                index for index, shape in enumerate(TEMPLATE_MIGRATIONS) if shape.fullmatch(name)
            )
            new_id = next_migration_id(migration_id, offset) if offset else migration_id
            target = f"{MIGRATIONS}/{name.replace(template_id, new_id, 1)}"
            text = text.replace(template_id, new_id)
            rendered = names.rename(text)
            if name.endswith(".Designer.cs"):
                rendered = sort_usings(rendered)
                if offset == len(TEMPLATE_MIGRATIONS) - 1:
                    # Only the last migration's designer describes the model
                    # the service ends up with, and the snapshot is a
                    # description of exactly that.
                    snapshot = names.rename(snapshot_from_designer(text, new_id))
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
    keys = [(i, m.group(1)) for i, line in enumerate(lines) if (m := SERVICE_KEY.fullmatch(line))]
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

    # The anchor is the contiguous pair, and the pair is also what gets
    # written — one string, so the two cannot drift apart. Anchoring the API
    # half alone let a change to the migrator's entry through unnoticed while
    # this went on emitting the shape it used to have, which is the drift the
    # anchors exist to stop.
    pair = (
        f'  {TEMPLATE.lower()}-migrator:\n'
        f'    profiles: [ "excluded" ]\n'
        f'  {TEMPLATE.lower()}-api:\n'
        f'    profiles: [ "excluded" ]\n'
    )
    require_once(text, pair, "infra-only override")
    return restore(text + names.rename(pair), newline)


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
    if not NAME.fullmatch(name):
        raise ScaffoldError(f"'{name}' is not a PascalCase service name")
    if len(name) > SQL_IDENTIFIER_LIMIT:
        raise ScaffoldError(
            f"'{name[:20]}…' is {len(name)} characters; the name becomes a SQL Server "
            f"database and schema, and sysname stops at {SQL_IDENTIFIER_LIMIT}. The "
            f"projects would render and the first migration would not run."
        )
    if name.upper() in SQL_RESERVED:
        raise ScaffoldError(
            f"'{name}' is a SQL Server system name: the service's database and schema "
            f"take this name, so the migrator would target the server's own."
        )
    if name.upper() in WINDOWS_RESERVED:
        raise ScaffoldError(
            f"'{name}' is a reserved device name on Windows: neither "
            f"src/Services/{name} nor {name}.Domain.csproj can be created there, and "
            f"a repository that half-renders on one platform is not portable."
        )
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
    if not MIGRATION_ID.fullmatch(migration_id):
        raise ScaffoldError(
            f"'{migration_id}' is not a 14-digit migration timestamp; it reaches a file path"
        )
    if port not in PORTS:
        raise ScaffoldError(f"port {port} is outside 1–65535 and Docker cannot publish it")
    if not (repo_root / COPY_ROOTS[0]).is_dir():
        raise ScaffoldError(f"{repo_root} does not look like the repository: no {COPY_ROOTS[0]}")

    names = Names(name)

    # Assembly identity first, because it is the more fundamental refusal:
    # "this name can never work here", ahead of "this service already exists".
    # `Common` renders Common.Domain and Common.Application beside the
    # building blocks of exactly those names, and a solution holding two
    # projects with one identity does not build. It was refused before this
    # check too — `tests/Common.Domain.Tests` exists, so the directory test
    # below fired — but by accident and with a message about the wrong thing,
    # and a name colliding on identity without colliding on disk sailed through.
    # Compared case-insensitively, because a .NET assembly simple name is.
    # `COMMON` clears every check above on a case-sensitive filesystem, and
    # `COMMON.Domain` does not intersect `Common.Domain` as a string — so the
    # first version of this check let through exactly the case it was written
    # to stop.
    generated = {f"{names.pascal}.{suffix}": suffix for suffix in PROJECT_SUFFIXES}
    existing = {
        path.stem.lower()
        for directory in ("src", "tests")
        if (repo_root / directory).is_dir()
        for path in (repo_root / directory).rglob("*.csproj")
    }
    if (clash := sorted(project for project in generated if project.lower() in existing)):
        raise ScaffoldError(
            "the solution already has " + ", ".join(clash) + ", give or take casing. Two "
            "projects with one assembly identity do not build, whatever directory each "
            "sits in and however each is spelt."
        )

    for root in COPY_ROOTS:
        target = repo_root / names.rename(root)
        if target.exists():
            raise ScaffoldError(f"{names.rename(root)} already exists; this script creates, never merges")

    # The new service's own name is masked before the search, or a legitimate
    # one that contains a template token — CatalogSearch, ProductReviews — is
    # rejected for the tokens it was asked for. What is left after masking is a
    # mention the rename did not reach, which is the only thing this check is
    # about.
    #
    # The mask is why the SLICE half of this check does not run here. Masking a
    # service called `Product` would strip every genuine `Product` leftover
    # along with its own name, and the render would report itself
    # domain-neutral while the slice survived in it. `render_projects` runs
    # that half before the rename instead, where a `Product` is unambiguous —
    # the rename maps the template's casings and never touches the slice's.
    mask = re.compile("|".join(re.escape(n) for n in (names.pascal, names.lower, names.upper)))
    created = render_projects(repo_root, names, migration_id)
    for relative, text in created.items():
        stripped = BENIGN.sub("", mask.sub("", text))
        if (left := TEMPLATE_TOKEN.search(stripped)) is not None:
            line = stripped[: left.start()].count("\n") + 1
            raise ScaffoldError(
                f"{relative}:{line}: '{left.group(0)}' survived the rename. "
                f"The file names Catalog somewhere this script does not patch."
            )
        if TEMPLATE_TOKEN.search(mask.sub("", relative)) is not None:
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
