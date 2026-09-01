"""The scaffold's own tests, run against the real repository.

A fixture tree would test the script against a template that cannot drift.
The whole risk this design accepts is that Catalog moves under the anchors
(see the module docstring next door), and only rendering the tree that
actually exists catches that. So `plan()` reads the checkout — and writes
nothing, which is why it can: the render is a value, and `apply()` is the only
thing that touches disk.

    cd tools/new-service && python -m unittest
"""

import contextlib
import io
import re
import shutil
import tempfile
import unittest
from pathlib import Path

import new_service
from new_service import (
    COPY_ROOTS,
    MIGRATIONS as MIGRATIONS_DIR,
    OMITTED,
    SCAN_ALLOW_LIST,
    SCAN_GATE,
    TEMPLATE_MIGRATIONS,
    Names,
    Plan,
    ScaffoldError,
    apply,
    environment_keys,
    load_scan_gate,
    main,
    plan,
)

REPO_ROOT = Path(__file__).resolve().parents[2]
MIGRATION_ID = "20260809120000"
# The outbox migration's id, one minute on — the script derives it, and spelling
# it out here rather than calling next_migration_id keeps the assertion
# independent of the arithmetic it is checking.
OUTBOX_MIGRATION_ID = "20260809120100"
# And the inbox migration's, one minute on again, spelt out for the same reason.
INBOX_MIGRATION_ID = "20260809120200"
# And the retention index's, one minute on again.
RETENTION_MIGRATION_ID = "20260809120300"
# A port no service publishes, and it has to stay that way — the scaffold
# refuses a port already in `deploy/compose/docker-compose.yml`, and these
# tests render against the real repository. It was 5101 until PR-18 allocated
# that to Ordering, at which point every render in this file raised
# ScaffoldError and 50 tests went red at once. 5199 is chosen to sit outside
# the 51xx block §14.1 hands out sequentially, so the next real service does
# not collide with it the way Ordering did.
#
# The second probe below takes PORT - 1 rather than PORT + 1, and PR-19 is
# why. 5200 was the BFF's the whole time — §14.1's fence has shown it beside
# `web-bff` since PR-06 — so the paragraph above reasoned carefully about the
# 51xx block while the adjacent port it silently also consumed was already
# spoken for, and every render here started refusing the day that block
# landed. **A port chosen for one constant is not a port reserved for two.**
PORT = 5199

# The second probe's port. Named rather than spelt as arithmetic at the two
# call sites, because the arithmetic is what hid the collision: `PORT + 1`
# reads as "one more than a port we checked" and is in fact a second
# allocation nobody checked.
SECOND_PORT = PORT - 1


# Zulu and Yankee, and neither will ever be a service. The probes used to be
# Ordering and Inventory — both on Appendix C's plan — and every
# repository-backed render would have started refusing the day PR-18 created
# `src/Services/Ordering`, which is the day this suite matters most. A probe
# name has to be one the platform can never take.
#
# Zulu rather than Alfa because it must also sort *after* `Microsoft`: the
# using-block test below exists for the case where the service's own namespace
# moves past EF's in the sort order, and a probe sorting before it would have
# quietly stopped covering that.
PROBE = "Zulu"
SECOND_PROBE = "Yankee"


def render(name: str = PROBE, port: int = PORT, repo_root: Path = REPO_ROOT) -> Plan:
    return plan(repo_root, name, port, MIGRATION_ID)


def template_copy(destination: Path) -> Path:
    """The template and the shared files a render edits, minus §15.1's gate.

    Only the tests that need to *break* the template copy it; everything else
    reads the checkout directly. The secret scan is deliberately absent rather
    than forgotten — `template_copy_with_gate` below adds it, and the pair is
    what makes the degraded path testable from both sides. The files are
    listed and not counted, because the list has already grown twice.
    """
    for root in COPY_ROOTS:
        shutil.copytree(
            REPO_ROOT / root,
            destination / root,
            ignore=shutil.ignore_patterns("bin", "obj"),
        )
    for shared in (
        "Platform.slnx",
        "deploy/compose/docker-compose.yml",
        "deploy/compose/docker-compose.infra-only.yml",
        "deploy/compose/.env.example",
        "deploy/compose/README.md",
        "deploy/compose/rabbitmq/definitions.json",
    ):
        (destination / shared).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(REPO_ROOT / shared, destination / shared)
    return destination


def template_copy_with_gate(destination: Path) -> Path:
    """The same tree, and §15.1's secret scan beside it.

    The tree above has no `.github/`, so "the scaffold degraded because the
    gate is not there" and "the step was never wired in at all" write the same
    nothing and pass the same assertion. This root is the positive control
    that makes the absence mean something: the same render, one directory
    different, and an allow-list entry has to appear.
    """
    root = template_copy(destination)
    shutil.copytree(
        REPO_ROOT / Path(SCAN_GATE).parent,
        root / Path(SCAN_GATE).parent,
        ignore=shutil.ignore_patterns("__pycache__"),
    )
    return root


def allow_list_entries(gate, text: str) -> tuple[list, list[str]]:
    """An allow-list body, parsed by the gate's own reader.

    Never a second parser written here. What the entries mean is the gate's
    question and this suite is asking whether the scaffold's answers satisfy
    it, so re-implementing the four-field split would be a double agreeing
    with itself about a format neither of them owns.
    """
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / Path(SCAN_ALLOW_LIST).name
        path.write_bytes(text.encode("utf-8"))
        return gate.read_allowed(path, {rule.id for rule in gate.RULES})


class RendersTheTemplate(unittest.TestCase):
    def setUp(self):
        self.rendered = render()

    def test_it_writes_nine_projects_five_service_three_test_and_test_support(self):
        for project in (
            "src/Services/Zulu/Zulu.Domain/Zulu.Domain.csproj",
            "src/Services/Zulu/Zulu.Application/Zulu.Application.csproj",
            "src/Services/Zulu/Zulu.Infrastructure/Zulu.Infrastructure.csproj",
            "src/Services/Zulu/Zulu.Migrator/Zulu.Migrator.csproj",
            "src/Services/Zulu/Zulu.Api/Zulu.Api.csproj",
            "tests/Zulu.Domain.Tests/Zulu.Domain.Tests.csproj",
            "tests/Zulu.Application.Tests/Zulu.Application.Tests.csproj",
            "tests/Zulu.Api.Tests/Zulu.Api.Tests.csproj",
            "tests/Zulu.TestSupport/Zulu.TestSupport.csproj",
        ):
            self.assertIn(project, self.rendered.created)

    def test_it_writes_both_dockerfiles(self):
        # §15.2 builds two images per service, and PR-10 found that both need
        # the -extra tag: SqlClient refuses to open a connection under the
        # globalization-invariant mode plain chiselled runs in.
        for image in ("Zulu.Api", "Zulu.Migrator"):
            path = f"src/Services/Zulu/{image}/Dockerfile"
            self.assertIn(path, self.rendered.created)
            self.assertIn("-chiseled-extra", self.rendered.created[path])

    def test_it_writes_the_assembly_marker_the_gates_anchor_on(self):
        marker = "src/Services/Zulu/Zulu.Domain/AssemblyMarker.cs"
        self.assertIn(marker, self.rendered.created)
        self.assertIn("namespace Zulu.Domain;", self.rendered.created[marker])

    def test_no_generated_path_or_line_still_names_the_template(self):
        # The single best check a rename script can carry, and the one that
        # catches what the anchors cannot: a mention of Catalog or its slice in
        # a file nobody thought to patch. `plan` runs it too and refuses — this
        # asserts it is running, over every file, rather than trusting it.
        benign = re.compile(r"[Pp]roduction|ProductVersion")
        for path, text in self.rendered.created.items():
            self.assertNotRegex(path, r"(?i)catalog|roduct")
            self.assertNotRegex(benign.sub("", text), r"(?i)catalog|roduct", path)

    def test_it_renames_the_schema_the_keys_and_the_environment_variables(self):
        context = self.rendered.created[
            "src/Services/Zulu/Zulu.Infrastructure/Persistence/ZuluDbContext.cs"
        ]
        self.assertIn('modelBuilder.HasDefaultSchema("zulu");', context)

        infrastructure = self.rendered.created[
            "src/Services/Zulu/Zulu.Infrastructure/DependencyInjection.cs"
        ]
        self.assertIn('configuration.GetConnectionString("Zulu")', infrastructure)

        migrator = self.rendered.created["src/Services/Zulu/Zulu.Migrator/MigratorHost.cs"]
        self.assertIn('GetConnectionString("ZuluMigrator")', migrator)

        self.assertIn(
            "ZULU_MIGRATOR_CONNECTION", self.rendered.updated["deploy/compose/.env.example"]
        )

    def test_the_messaging_registration_and_its_smoke_travel_with_the_template(self):
        # PR-13's wiring is template, not slice: a new service owns a bus
        # connection from its first commit, and the harness smoke proves the
        # pipeline with no broker running.
        messaging = self.rendered.created[
            "src/Services/Zulu/Zulu.Infrastructure/Messaging/DependencyInjection.cs"
        ]
        self.assertIn("public static IServiceCollection AddMassTransitMessaging(", messaging)
        self.assertIn('configuration.GetConnectionString("RabbitMq")', messaging)
        self.assertIn("namespace Zulu.Infrastructure.Messaging;", messaging)

        infrastructure = self.rendered.created[
            "src/Services/Zulu/Zulu.Infrastructure/DependencyInjection.cs"
        ]
        self.assertIn("services.AddMassTransitMessaging(configuration);", infrastructure)

        # PR-20's correction, guarded here because nowhere else can guard it.
        # ConfigureEndpoints(context) gives a registered consumer with no
        # explicit binding a queue named after its type, carrying neither the
        # inbox filter nor the retry policy §9.8 requires of every endpoint.
        # Catalog has no consumers, so its own registration tests stay green if
        # the call comes back — and this file renders Catalog, so a rendered
        # service is the only place the absence is observable at all. Without
        # this assertion the trap can be handed silently to every service
        # generated from here.
        #
        # The CALL, not the identifier: the template's comment explains why the
        # line is gone and names it doing so, so a bare "ConfigureEndpoints"
        # assertion fails on the prose that documents the fix. Caught by
        # writing it that way first.
        self.assertNotIn("cfg.ConfigureEndpoints(", messaging)

        self.assertIn(
            "tests/Zulu.Api.Tests/MessagingRegistrationTests.cs", self.rendered.created
        )


class OmitsTheSlice(unittest.TestCase):
    def setUp(self):
        self.rendered = render()

    def test_it_writes_nothing_from_the_catalog_slice(self):
        # Every omitted path, renamed, must be absent. A scaffolded service is
        # PR-07's state with the later wiring on it, not PR-10's state with
        # the nouns changed.
        for omitted in OMITTED:
            self.assertNotIn(omitted.replace("Catalog", "Zulu"), self.rendered.created)

    def test_the_host_maps_no_endpoint_and_says_where_the_first_one_goes(self):
        program = self.rendered.created["src/Services/Zulu/Zulu.Api/Program.cs"]
        self.assertNotIn("MapProductEndpoints", program)
        self.assertIn("app.MapCommonHealthEndpoints();", program)
        self.assertIn("This service maps no endpoint of its own yet.", program)

    def test_the_validator_scan_re_anchors_on_the_assembly(self):
        # §4.2's line still scans this assembly; it just cannot name a type
        # inside it, there being no validator yet and DependencyInjection
        # being static — CS0718, found by compiling the scaffolded service.
        application = self.rendered.created[
            "src/Services/Zulu/Zulu.Application/DependencyInjection.cs"
        ]
        self.assertIn(
            "services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);", application
        )

    def test_the_repository_registration_is_gone_and_the_unit_of_work_is_not(self):
        infrastructure = self.rendered.created[
            "src/Services/Zulu/Zulu.Infrastructure/DependencyInjection.cs"
        ]
        self.assertIn("services.AddScoped<IUnitOfWork, EfUnitOfWork>();", infrastructure)
        self.assertNotIn("IRepository", infrastructure)

    def test_both_architecture_gates_anchor_on_the_marker(self):
        domain = self.rendered.created["tests/Zulu.Domain.Tests/ArchitectureTests.cs"]
        self.assertIn("typeof(AssemblyMarker).Assembly", domain)
        self.assertIn('string[] allowed = ["Common.Domain", "System.Runtime"];', domain)

        application = self.rendered.created["tests/Zulu.Application.Tests/ArchitectureTests.cs"]
        self.assertIn("typeof(AssemblyMarker).Assembly", application)
        self.assertIn("using Zulu.Domain;", application)

    def test_the_application_test_project_carries_no_container_wiring(self):
        # With no handler test there is no collection member and no fixture
        # consumer, so the references and the provider package go with them.
        csproj = self.rendered.created[
            "tests/Zulu.Application.Tests/Zulu.Application.Tests.csproj"
        ]
        self.assertNotIn("Zulu.TestSupport.csproj", csproj)
        self.assertNotIn("Zulu.Infrastructure.csproj", csproj)
        self.assertNotIn("Microsoft.EntityFrameworkCore.SqlServer", csproj)
        self.assertNotIn(
            "tests/Zulu.Application.Tests/IntegrationCollection.cs", self.rendered.created
        )

    def test_the_application_project_carries_no_dapper_until_its_first_query(self):
        csproj = self.rendered.created[
            "src/Services/Zulu/Zulu.Application/Zulu.Application.csproj"
        ]
        self.assertNotIn('PackageReference Include="Dapper"', csproj)
        self.assertIn('PackageReference Include="FluentValidation"', csproj)

    def test_the_registration_suite_keeps_the_tests_about_the_template(self):
        # The exact set, not a subset. Written as `assertIn`s it both
        # miscounted — the dispatcher test is copied too — and could not fail
        # if that test were dropped, which is the one registration whose
        # absence makes the first resolved TransactionBehavior throw.
        #
        # In declaration order, which is also the order the registrations run
        # in: a set comparison would pass on a suite that had lost the pipeline
        # ordering test and gained a duplicate of another.
        tests = self.rendered.created["tests/Zulu.Application.Tests/DependencyInjectionTests.cs"]

        self.assertEqual(
            [
                "AddZuluApplication_registers_the_dispatcher_scoped",
                "AddZuluApplication_registers_the_system_clock",
                "AddZuluApplication_registers_the_request_metrics_singleton",
                "AddZuluApplication_registers_the_real_domain_event_dispatcher_scoped",
                "AddZuluApplication_registers_the_projection_registry_scoped",
                "AddZuluApplication_registers_the_allow_list_mapper",
                "AddZuluApplication_registers_the_four_behaviours_in_pipeline_order",
                "AddZuluApplication_registers_the_key_carrier_the_two_behaviours_share",
            ],
            re.findall(r"public void (\w+)\(\)", tests),
        )


class GeneratedGuidanceIsTrue(unittest.TestCase):
    """A generated comment must be true of the service it lands in.

    The rename is what makes this a real hazard rather than a theoretical one:
    a patch that names the template as an *example* — "as Catalog does",
    "Catalog.Application.Tests carries both" — comes out naming the new
    service, and the sentence is then about the wrong one. Every case below
    told a developer to copy tests from a suite that does not have them, or
    claimed a history the new service has not got. A Grok review found five;
    two more were beside them in copied files.

    The straggler check cannot catch this class, because the rename *is* the
    defect. These assertions are the guard, and they render under the second
    probe rather than the first: a sentence that is false of *any* service is
    false under either name, so using the same one everywhere would have let a
    default-name assumption hide inside an assertion about names.
    """

    def setUp(self):
        # SECOND_PORT, not a service-range literal: §14.1 hands out 51xx
        # sequentially, so a successful render pinned to 5103 fails the day
        # Inventory takes it — the exact collision moving PORT off 5101 was
        # meant to remove, left behind in the same change. It was PORT + 1
        # until PR-19, which is when 5200 stopped being free; see the
        # constant's own note.
        self.rendered = render(name=SECOND_PROBE, port=SECOND_PORT)

    def claim(self, path: str) -> str:
        # Normalised, because these assertions are about wording and a
        # multi-line phrase spelt with LF would otherwise pass on the runner
        # and fail here — the same trap the anchors themselves fell into.
        return self.rendered.created[path].replace("\r\n", "\n")

    def test_the_host_does_not_cite_itself_as_the_precedent(self):
        program = self.claim("src/Services/Yankee/Yankee.Api/Program.cs")
        self.assertNotIn("as Yankee does", program)

        # PR-16 replaced the "unauthenticated until PR-16" note this used to
        # pin — the gap it named is closed, and a generated comment scheduling
        # a PR that has landed is exactly the false claim this class exists to
        # catch. What replaced it has to keep saying where the first endpoint
        # goes, and now also what shape it takes.
        self.assertIn("This service maps no endpoint of its own yet", program)
        self.assertIn("behind RequireAuthorization at the group (§11.4)", program)
        self.assertIn("This service registers no permission policy", program)

    def test_the_host_keeps_the_middleware_but_not_the_policies(self):
        # The split PR-16 forced: a policy belongs to the endpoint that names
        # it and leaves with the slice, while token validation belongs to every
        # host (§11.2) and stays. A scaffolded service that dropped both would
        # serve its probes as the only thing anybody had ever checked.
        program = self.claim("src/Services/Yankee/Yankee.Api/Program.cs")

        self.assertIn("app.UseAuthentication();", program)
        self.assertIn("app.UseAuthorization();", program)
        # The commented example in the generated guidance keeps the name, so
        # match on the executable form: a real call sits at the head of a line.
        self.assertNotIn("\n    .AddAuthorizationBuilder()", program)
        self.assertNotIn("AddPolicy(YankeePermissions", program)

    def test_the_registration_suite_does_not_send_the_reader_to_itself(self):
        tests = self.claim("tests/Yankee.Application.Tests/DependencyInjectionTests.cs")
        self.assertNotIn("Yankee.Application.Tests carries both", tests)

    def test_the_two_missing_tests_are_conditioned_separately(self):
        # "write them with the first command and query" conflated two
        # independent triggers: a query-only slice was told to wait for a
        # command and to add a validator test with no validator to scan for.
        tests = self.claim("tests/Yankee.Application.Tests/DependencyInjectionTests.cs")
        self.assertIn("come back separately rather", tests)
        self.assertIn("first handler of either kind", tests)
        self.assertIn("first validator", tests)
        self.assertNotIn("with the first command and query", tests)

    def test_the_validator_note_does_not_cite_a_suite_without_the_test(self):
        di = self.claim("src/Services/Yankee/Yankee.Application/DependencyInjection.cs")
        self.assertNotIn("Yankee.Application.Tests carries", di)

    def test_the_domain_gate_does_not_claim_a_history_the_service_lacks(self):
        gate = self.claim("tests/Yankee.Domain.Tests/ArchitectureTests.cs")
        self.assertNotIn("the ones Yankee added", gate)

    def test_the_endpoints_gate_says_where_it_was_observed_red(self):
        gate = self.claim("tests/Yankee.Api.Tests/ArchitectureTests.cs")
        self.assertNotIn("forbidden reference in Yankee before being trusted", gate)
        self.assertIn("the service this one\n/// was scaffolded from", gate)

    def test_no_generated_file_claims_this_service_did_something_in_a_past_pr(self):
        """A PR number may cite the plan; it may not narrate this service's past.

        `until PR-14`, `PR-22's deliverable`, `does not exist until PR-15` are
        all true of any service — they cite Appendix C. `Yankee acquired
        both in PR-08` and `the model had no entity types until PR-10` are not:
        the service was created today and did none of it.

        The two are not separable by pattern, so this is an allow-list, like
        the domain gate's. A new co-occurrence of the service name and a PR
        number fails here and forces the same decision: plan citation, or false
        history. Copilot raised this class twice — five sites, then three more
        it found beside them.

        **It is proximity, not comprehension.** A PR number more than ~170
        characters from any mention of the service escapes it, so this narrows
        the class rather than closing it; the named assertions below are what
        pin the sites actually found. Said plainly because a guard that is
        described as exhaustive stops being read.
        """
        allowed = (
            "PR-07's OpenAPI deliverable",          # Appendix C's row for the host
            "category is PR-22's",                  # Testcontainers categories
            "Appendix C's PR-09 test",              # names the test's origin, not the service's
            "drift PR-08 forbids",                  # a rule, cited like an ADR
        )
        # Spelt to survive comment wrapping: the entry above was written as
        # "the snapshot drift PR-08 forbids" and matched nothing, because the
        # comment breaks between "snapshot" and "drift". The guard caught its
        # own allow-list, which is the right way round.
        for path, text in self.rendered.created.items():
            body = text.replace("\r\n", "\n")
            for match in re.finditer(r"PR-\d+", body):
                window = body[max(0, match.start() - 170) : match.end() + 170]
                if "Yankee" not in window:
                    continue
                self.assertTrue(
                    any(phrase in window for phrase in allowed),
                    f"{path}: '{match.group(0)}' sits beside the service name outside the "
                    f"allow-list — plan citation, or a history this service has not got?\n"
                    f"{window}",
                )

    def test_the_fixture_does_not_claim_a_consumer_that_does_not_reference_it(self):
        # The scaffold drops the application suite's TestSupport reference —
        # there is no handler test to need it — so a fixture claiming to serve
        # both suites is describing the template, not this service.
        fixture = self.claim("tests/Yankee.TestSupport/ServiceFixture.cs")
        self.assertNotIn("serves <c>Yankee.Application.Tests</c> and", fixture)

        csproj = self.claim("tests/Yankee.Api.Tests/Yankee.Api.Tests.csproj")
        self.assertNotIn("shared with\n         Yankee.Application.Tests", csproj)


class TheMigrationAndItsSnapshot(unittest.TestCase):
    def setUp(self):
        self.rendered = render()
        self.prefix = "src/Services/Zulu/Zulu.Infrastructure/Persistence/Migrations"

    def test_it_copies_initial_create_under_a_fresh_id(self):
        migration = f"{self.prefix}/{MIGRATION_ID}_InitialCreate.cs"
        self.assertIn(migration, self.rendered.created)
        self.assertIn('migrationBuilder.EnsureSchema("zulu")', self.rendered.created[migration])

    def test_the_file_name_and_the_migration_attribute_agree(self):
        # EF resolves the id from the attribute and the zulu from the file
        # name; a pair that disagreed would apply under one identity and be
        # recorded under another.
        designer = self.rendered.created[f"{self.prefix}/{MIGRATION_ID}_InitialCreate.Designer.cs"]
        self.assertIn(f'[Migration("{MIGRATION_ID}_InitialCreate")]', designer)

    def test_it_copies_the_outbox_migration_under_the_next_id(self):
        # §9.4's table is wiring every service has, so it travels with
        # InitialCreate rather than being dropped with Catalog's model changes.
        # A scaffolded service without it would carry the dispatcher and log a
        # failed claim twice a second from its first boot.
        migration = f"{self.prefix}/{OUTBOX_MIGRATION_ID}_AddOutbox.cs"
        self.assertIn(migration, self.rendered.created)
        self.assertIn('name: "OutboxMessages"', self.rendered.created[migration])

    def test_the_outbox_migration_is_ordered_after_the_schema(self):
        # EF applies in id order, and the outbox table cannot be created in a
        # schema that does not exist yet.
        self.assertLess(MIGRATION_ID, OUTBOX_MIGRATION_ID)

    def test_it_copies_the_inbox_migration_under_the_id_after_that(self):
        # §9.5's table travels for the mirror of the outbox's reason: the
        # retention purge deletes from every table it was given from first
        # boot, so a service carrying the purge without this table logs a
        # failed delete every pass. Consuming nothing does not exempt it —
        # the template itself consumes nothing and has the table for this.
        migration = f"{self.prefix}/{INBOX_MIGRATION_ID}_AddInbox.cs"
        self.assertIn(migration, self.rendered.created)
        self.assertIn('name: "InboxMessages"', self.rendered.created[migration])
        self.assertLess(OUTBOX_MIGRATION_ID, INBOX_MIGRATION_ID)

    def test_it_copies_the_retention_index_under_the_id_after_that(self):
        # The purge's index travels for the same reason the tables do, and
        # its absence is the quietest of the three: the claim's index is
        # filtered `WHERE ProcessedAt IS NULL`, so it excludes every row the
        # purge deletes and the hourly pass scans the whole outbox table. A
        # service scaffolded without it pays that from its first boot, and the
        # cost grows exactly as the processed rows do.
        migration = f"{self.prefix}/{RETENTION_MIGRATION_ID}_AddOutboxRetentionIndex.cs"
        self.assertIn(migration, self.rendered.created)
        self.assertIn('name: "IX_Outbox_Processed"', self.rendered.created[migration])
        self.assertIn('filter: "[ProcessedAt] IS NOT NULL"', self.rendered.created[migration])
        self.assertLess(INBOX_MIGRATION_ID, RETENTION_MIGRATION_ID)

    def test_the_snapshot_comes_from_the_last_migrations_designer(self):
        # The snapshot describes the model the service ends up with, so it is
        # derived from whichever template migration applies last — and that
        # moved twice in one PR, from AddOutbox to AddInbox to this one. Taking
        # an earlier designer is a defect with no symptom until the service's
        # first `migrations add`, where EF would emit a CreateTable for a table
        # its own migrations had already created.
        snapshot = self.rendered.created[f"{self.prefix}/ZuluDbContextModelSnapshot.cs"]
        self.assertNotIn("[Migration(", snapshot)
        self.assertNotIn("partial class AddOutboxRetentionIndex", snapshot)
        self.assertIn('modelBuilder.Entity("Common.Infrastructure.Inbox.InboxMessage"', snapshot)
        self.assertIn('.HasDatabaseName("IX_Outbox_Processed")', snapshot)

    def test_the_inbox_migration_keeps_no_claim_about_the_templates_own_row(self):
        # The template carries this table while binding no receive endpoint,
        # and *why* is a fact about its row in §3.2 rather than about the
        # service being scaffolded. The rule survives the copy; the reason does
        # not travel with it.
        migration = self.rendered.created[f"{self.prefix}/{INBOX_MIGRATION_ID}_AddInbox.cs"]
        self.assertIn("runs from first boot and deletes from every", migration)
        self.assertNotIn("Consumes cell", migration)

    def test_it_leaves_no_later_catalog_migration_behind(self):
        # Every template migration, its designer, and one snapshot — derived
        # from TEMPLATE_MIGRATIONS rather than written out, because a literal
        # here is a second place to edit when that tuple grows and it says
        # nothing the tuple does not. Catalog's own AddProducts is what must
        # not be here.
        migrations = [path for path in self.rendered.created if path.startswith(self.prefix)]
        self.assertEqual((len(TEMPLATE_MIGRATIONS) * 2) + 1, len(migrations), migrations)
        self.assertFalse([path for path in migrations if "AddProducts" in path])

    def test_the_snapshot_describes_the_technical_tables_and_nothing_else(self):
        # Catalog's snapshot cannot be copied — it describes Product, and the
        # next `migrations add` here would generate a drop for a table that
        # never existed. This one is EF's own description of the model a
        # scaffolded service actually has: the outbox, the inbox and §8.5's
        # marker, no aggregate.
        #
        # Both halves matter, and the inbox is the half that was easy to miss.
        # Deriving the snapshot from the outbox migration's designer — the last
        # one until PR-15 — would omit an entity the DbContext maps, so the
        # first `migrations add` would emit a second CreateTable for a table
        # the scaffolded migrations had already created. With Product in it, a
        # drop.
        snapshot = self.rendered.created[f"{self.prefix}/ZuluDbContextModelSnapshot.cs"]
        self.assertIn("partial class ZuluDbContextModelSnapshot : ModelSnapshot", snapshot)
        self.assertIn("protected override void BuildModel(ModelBuilder modelBuilder)", snapshot)
        self.assertIn('.HasDefaultSchema("zulu")', snapshot)
        self.assertNotIn("[Migration(", snapshot)
        self.assertIn('modelBuilder.Entity("Common.Infrastructure.Outbox.OutboxMessage"', snapshot)
        self.assertIn('modelBuilder.Entity("Common.Infrastructure.Inbox.InboxMessage"', snapshot)
        self.assertIn(
            'modelBuilder.Entity("Common.Infrastructure.Idempotency.IdempotencyMarker"', snapshot)
        self.assertEqual(3, snapshot.count("modelBuilder.Entity("))
        self.assertNotIn("Product", snapshot.replace("ProductVersion", ""))

    def test_the_machine_owned_files_keep_the_sorted_using_block_ef_writes(self):
        # The rename moves the service's own namespace past Microsoft's in the
        # sort order, and EF writes that block sorted. Left alone, the first
        # real `migrations add` in the new service would reorder it and produce
        # a diff nobody made.
        # Spelt out rather than re-derived, so the assertion is independent of
        # the sort the script applies: this is the order `dotnet ef migrations
        # add` produced against a scaffolded service.
        # System first, which is EF's order and not a plain alphabetical one —
        # the outbox designer is the first machine-owned file here to carry a
        # System using at all, and it is what made the difference visible.
        #
        # System.Collections.Generic appears in neither, though Catalog's own
        # outbox designer carries it: EF emits it for the
        # Dictionary<string, object> a ComplexProperty is mapped as, which is
        # how §5.3's Money reaches the model. The aggregate is removed from
        # both files here, so the using goes with it — verified against a real
        # `migrations add` in a scaffolded service, whose Up came out empty and
        # whose rewritten snapshot was byte-identical to the emitted one.
        system = ["using System;"]
        efcore = [
            "using Microsoft.EntityFrameworkCore;",
            "using Microsoft.EntityFrameworkCore.Infrastructure;",
            "using Microsoft.EntityFrameworkCore.Metadata;",
        ]
        tail = ["using Microsoft.EntityFrameworkCore.Storage.ValueConversion;"]
        expected = {
            f"{self.prefix}/{MIGRATION_ID}_InitialCreate.Designer.cs": [
                *efcore,
                "using Microsoft.EntityFrameworkCore.Migrations;",
                *tail,
                "using Zulu.Infrastructure.Persistence;",
            ],
            f"{self.prefix}/{OUTBOX_MIGRATION_ID}_AddOutbox.Designer.cs": [
                *system,
                *efcore,
                "using Microsoft.EntityFrameworkCore.Migrations;",
                *tail,
                "using Zulu.Infrastructure.Persistence;",
            ],
            f"{self.prefix}/ZuluDbContextModelSnapshot.cs": [
                *system,
                *efcore,
                *tail,
                "using Zulu.Infrastructure.Persistence;",
            ],
        }
        for machine_owned, usings in expected.items():
            text = self.rendered.created[machine_owned]
            self.assertEqual(
                usings, [line for line in text.splitlines() if line.startswith("using ")]
            )

    def test_the_machine_owned_files_keep_their_byte_order_mark(self):
        for machine_owned in (
            f"{self.prefix}/{MIGRATION_ID}_InitialCreate.Designer.cs",
            f"{self.prefix}/ZuluDbContextModelSnapshot.cs",
        ):
            text = self.rendered.created[machine_owned]
            self.assertTrue(text.startswith("\ufeff"), machine_owned)


class EditsTheSharedFiles(unittest.TestCase):
    def setUp(self):
        self.rendered = render()

    def test_the_solution_gains_a_folder_of_five_and_four_test_entries(self):
        solution = self.rendered.updated["Platform.slnx"]
        self.assertIn('<Folder Name="/src/Services/Zulu/">', solution)
        for layer in ("Api", "Application", "Domain", "Infrastructure", "Migrator"):
            self.assertIn(
                f'<Project Path="src/Services/Zulu/Zulu.{layer}'
                f'/Zulu.{layer}.csproj" />',
                solution,
            )
        for suite in ("Api.Tests", "Application.Tests", "Domain.Tests", "TestSupport"):
            self.assertIn(
                f'<Project Path="tests/Zulu.{suite}/Zulu.{suite}.csproj" />', solution
            )

    def test_the_solution_stays_sorted(self):
        solution = self.rendered.updated["Platform.slnx"]
        tests = re.findall(r'<Project Path="tests/([^"]+)" />', solution)
        self.assertEqual(sorted(tests), tests)
        services = re.findall(r'<Folder Name="/src/Services/([^/]+)/">', solution)
        self.assertEqual(sorted(services), services)

    def test_the_compose_pair_lands_before_the_collector_on_the_requested_port(self):
        compose = self.rendered.updated["deploy/compose/docker-compose.yml"]
        self.assertLess(compose.index("  zulu-migrator:"), compose.index("  otel-collector:"))
        self.assertLess(compose.index("  zulu-api:"), compose.index("  otel-collector:"))
        self.assertIn(f'ports: [ "127.0.0.1:{PORT}:8080" ]', compose)
        self.assertIn(
            'ports: [ "127.0.0.1:5102:8080" ]', compose, "Catalog keeps its own port"
        )

    def test_every_mapping_in_the_rendered_file_is_bound_to_loopback(self):
        # §14.1 publishes on 127.0.0.1 because the credentials in that file are
        # deliberate development defaults, which makes the interface the only
        # control in front of them. A scaffolded service that dropped the
        # prefix would reopen that one service at a time, and every other
        # assertion in this class is satisfied just as happily by a 0.0.0.0
        # bind — so the subject here is the mapping's *shape*, and it is every
        # mapping in the file rather than the new one alone: an assertion
        # scoped to the rendered pair cannot notice the template regressing.
        compose = self.rendered.updated["deploy/compose/docker-compose.yml"]
        mappings = [
            mapping
            for published in re.findall(r"ports: \[ ([^\]]*)\]", compose)
            for mapping in re.findall(r'"([^"]+)"', published)
        ]
        # The positive control: a pattern that matched nothing would pass the
        # loop below in silence, which is this repository's most-repeated way
        # for a check to stop checking.
        self.assertGreater(len(mappings), 1, "found no published mapping to judge")
        for mapping in mappings:
            self.assertTrue(
                mapping.startswith("127.0.0.1:"),
                f"{mapping} publishes on every interface",
            )

    def test_the_compose_pair_keeps_the_two_key_split_of_section_7_1(self):
        compose = self.rendered.updated["deploy/compose/docker-compose.yml"]
        self.assertIn("ConnectionStrings__ZuluMigrator:", compose)
        self.assertIn("ConnectionStrings__Zulu:", compose)
        self.assertIn("condition: service_completed_successfully", compose)

    def test_the_api_block_carries_the_bus_key_and_waits_for_the_broker(self):
        # The bus wiring is template, not slice: AddMassTransitMessaging
        # throws without the key, so a scaffolded api block missing it is a
        # container that cannot start. The migrator half must NOT gain either
        # line — a job host has no bus.
        compose = self.rendered.updated["deploy/compose/docker-compose.yml"].replace("\r\n", "\n")
        api = compose[compose.index("  zulu-api:"):]
        api = api[: api.index("\n  otel-collector:")] if "\n  otel-collector:" in api else api
        # The service's OWN broker account, not `guest` (#44). The rename
        # carries both halves — the login and the password — so a scaffolded
        # service arrives with an identity rather than the shared administrator
        # every service used to hold.
        self.assertIn('ConnectionStrings__RabbitMq: "amqp://zulu-svc:local-dev-zulu@rabbitmq:5672"', api)
        self.assertNotIn("guest:guest", api)
        self.assertIn("rabbitmq: { condition: service_healthy }", api)

        migrator = compose[compose.index("  zulu-migrator:"): compose.index("  zulu-api:")]
        self.assertNotIn("RabbitMq", migrator)

    def test_the_service_gets_a_broker_account_it_can_actually_authenticate_with(self):
        # Since #44 the broker has no shared account, so a service the scaffold
        # renders and nobody grants cannot connect AT ALL — it starts, fails
        # authentication and reports nothing a reader would connect to a
        # missing definitions entry. The compose block above names
        # `zulu-svc`; this is the other half of that name existing.
        import base64
        import hashlib
        import json

        definitions = json.loads(
            self.rendered.updated["deploy/compose/rabbitmq/definitions.json"])

        account = next(
            (user for user in definitions["users"] if user["name"] == "zulu-svc"), None)
        self.assertIsNotNone(account, "no zulu-svc user in definitions.json")
        self.assertEqual([], account["tags"], "a service account needs no tags")

        # THE HASH IS COMPUTED, NOT COPIED. RabbitMQ stores
        # base64(salt || sha256(salt || utf8(password))), so an entry that
        # copied Catalog's hash would authenticate Catalog's password under
        # Zulu's name — one credential, two services, and the compose block
        # says `local-dev-zulu` while the broker expects `local-dev-catalog`.
        raw = base64.b64decode(account["password_hash"])
        salt, stored = raw[:4], raw[4:]
        self.assertEqual(
            hashlib.sha256(salt + b"local-dev-zulu").digest(),
            stored,
            "the hash does not verify against the password the compose block carries")

        permission = next(
            (entry for entry in definitions["permissions"] if entry["user"] == "zulu-svc"), None)
        self.assertIsNotNone(permission, "no zulu-svc permissions in definitions.json")

        # Its own contracts, and nobody else's. The template is a publisher, so
        # what it inherits is a publisher's grant — the negative half is what
        # #44 is about and what check_permissions.py holds every service to.
        self.assertIn("Zulu", permission["write"])
        self.assertNotIn("Catalog", permission["write"])
        self.assertNotIn("ordering-", permission["write"])

    def test_both_halves_of_the_pair_join_the_excluded_profile(self):
        # §14.1's own rule: every application block added to docker-compose.yml
        # joins this list in the same change, or `up` on the override starts a
        # service the developer is running on the host.
        override = self.rendered.updated["deploy/compose/docker-compose.infra-only.yml"]
        lines = override.replace("\r\n", "\n")
        self.assertIn('  zulu-migrator:\n    profiles: [ "excluded" ]', lines)
        self.assertIn('  zulu-api:\n    profiles: [ "excluded" ]', lines)

    def test_the_ports_readme_gains_one_row(self):
        readme = self.rendered.updated["deploy/compose/README.md"]
        self.assertIn(f"| Zulu API | http://localhost:{PORT} |", readme)

    def test_the_ports_readme_row_says_the_document_needs_a_token(self):
        # The whole row, not its prefix. ADR-030's fallback covers MapOpenApi,
        # so a rendered service's document answers 401 to an anonymous request
        # exactly as Catalog's and Ordering's do — and the row is the scaffold's
        # reconciliation with that. A prefix assertion passes just as happily
        # over a row that dropped the note, which would re-introduce the claim
        # the compose README was corrected to remove, once per new service.
        readme = self.rendered.updated["deploy/compose/README.md"]
        self.assertIn(
            f"| Zulu API | http://localhost:{PORT} | "
            "`/health/live`, `/health/ready`, "
            "`/openapi/v1.json` (needs a token — see below) |",
            readme,
        )

    def test_the_allow_list_gains_an_entry_for_every_finding_the_render_adds(self):
        # §15.1's secret scan reads the working tree, so it reads the tree this
        # leaves behind. Every credential-shaped literal Catalog carries is an
        # entry in that file, written by hand the day Catalog landed; a
        # rendered service carries the same literals under its own name and
        # therefore under its own fingerprints, and until #161 nobody wrote
        # those — so a scaffolded service could not be committed at all.
        gate = load_scan_gate(REPO_ROOT)
        self.assertIsNotNone(gate, f"{SCAN_GATE} is this repository's own gate")

        self.assertIn(SCAN_ALLOW_LIST, self.rendered.updated)
        before = (REPO_ROOT / SCAN_ALLOW_LIST).read_bytes().decode("utf-8")
        after = self.rendered.updated[SCAN_ALLOW_LIST]
        self.assertTrue(after.startswith(before), "the render rewrote the allow-list")

        # The gate's own reader, which is what refuses a line that is not four
        # fields, a path with a glob in it, a rule nobody declares or a reason
        # too short to be one. Asserting no problem is asserting all four.
        existing, _ = allow_list_entries(gate, before)
        entries, problems = allow_list_entries(gate, after)
        self.assertEqual([], problems)

        added = entries[len(existing):]
        self.assertNotEqual([], added, "the render generated no entry at all")

        # A duplicate is a failed build from the tool that exists to prevent
        # one: `audit` reports the second entry as duplicating the first.
        keys = [entry.key() for entry in entries]
        self.assertEqual(len(set(keys)), len(keys), "a generated entry duplicates another")

        # And every one of them is for a finding this render INTRODUCED. The
        # assertion this replaces asked whether the entry's path or sentence
        # named the probe, which the mislabelling bug satisfies trivially:
        # the reason is renamed on the way out, so an entry written over
        # another service's credential says `Zulu` as readily as a correct one.
        # What cannot be faked is the file as it stands in the checkout — a key
        # already in it is somebody else's finding, whatever the sentence
        # claims. Green here against the old code too, because this
        # repository's allow-list is complete and the defect needs a missing
        # entry to surface; `TheAllowListStepDegrades` is where it is driven
        # red, and that gap is exactly why the weak assertion looked adequate.
        for entry in added:
            if entry.path not in self.rendered.updated:
                continue                      # a created file has no "before"
            original = (REPO_ROOT / entry.path).read_bytes().decode("utf-8")
            self.assertNotIn(
                entry.key(),
                {found.key() for found in gate.scan_text(entry.path, original, gate.RULES)},
                entry.reason,
            )

    def test_no_generated_reason_carries_the_value_it_is_about(self):
        # The allow-list is walked like every other file, so a reason quoting
        # the literal reports itself — the file's own header says so. Measured
        # by running the rules over what was appended rather than by looking
        # for the values: what counts as a value is the gate's question, and a
        # search written here would be a second answer to it.
        gate = load_scan_gate(REPO_ROOT)
        before = (REPO_ROOT / SCAN_ALLOW_LIST).read_bytes().decode("utf-8")
        appended = self.rendered.updated[SCAN_ALLOW_LIST][len(before):]
        self.assertEqual(
            [],
            [str(finding) for finding in gate.scan_text(SCAN_ALLOW_LIST, appended, gate.RULES)],
        )

    def test_every_shared_file_keeps_the_line_endings_it_had(self):
        # Not "keeps CRLF": `.gitattributes` forces that on `*.cs` only, so
        # every file edited here is CRLF on Windows and LF on the runner. The
        # rendered text has to follow the checkout, not the author's platform.
        for path, text in self.rendered.updated.items():
            # Bytes, not read_text(newline=…): that keyword is Python 3.13 and
            # CI pins 3.12, which is the floor this tool is written to.
            source = (REPO_ROOT / path).read_bytes().decode("utf-8")
            expected = "\r\n" if "\r\n" in source else "\n"
            stripped = text.replace(expected, "")
            self.assertNotIn("\n", stripped, path)
            self.assertNotIn("\r", stripped, path)


class RendersOnEitherCheckout(unittest.TestCase):
    """The template's line endings depend on the platform, and the script must not.

    `.gitattributes` forces `*.cs text eol=crlf`, so C# is CRLF on every
    machine. Nothing else here carries an attribute, so `.csproj`, `.slnx`, the
    Compose YAML, the Markdown and the Dockerfiles are CRLF on a Windows
    checkout and LF on the Ubuntu runner. The first version of this script
    spelt its anchors with CRLF, passed on the machine that wrote it, and
    matched nothing in CI — which the anchor check caught, loudly, as it was
    built to. These tests render both checkouts from one.
    """

    @staticmethod
    def rewrite(root: Path, newline: str, only: str | None = None) -> None:
        for path in root.rglob("*"):
            if not path.is_file() or (only and path.suffix != only):
                continue
            raw = path.read_bytes()
            path.write_bytes(raw.replace(b"\r\n", b"\n").replace(b"\n", newline.encode()))

    def test_a_checkout_whose_shared_files_are_lf(self):
        # The Ubuntu runner: C# CRLF by attribute, everything else LF.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            self.rewrite(root, "\n")
            self.rewrite(root, "\r\n", only=".cs")

            rendered = plan(root, "Zulu", PORT, MIGRATION_ID)

            csproj = rendered.created[
                "src/Services/Zulu/Zulu.Application/Zulu.Application.csproj"
            ]
            self.assertNotIn("\r", csproj, "an LF template must render LF")
            program = rendered.created["src/Services/Zulu/Zulu.Api/Program.cs"]
            self.assertNotIn("\n", program.replace("\r\n", ""), "C# is CRLF by attribute")
            self.assertNotIn("\r", rendered.updated["Platform.slnx"])

    def test_a_checkout_whose_files_are_all_crlf(self):
        # The Windows developer machine, where core.autocrlf converts on the
        # way out and every file arrives CRLF.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            self.rewrite(root, "\r\n")

            rendered = plan(root, "Zulu", PORT, MIGRATION_ID)

            for path, text in {**rendered.created, **rendered.updated}.items():
                self.assertNotIn("\n", text.replace("\r\n", ""), path)

    def test_the_marker_follows_the_template_s_own_c_sharp(self):
        # The one generated file with no template beside it to take endings
        # from. It takes C#'s, observed rather than assumed.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            self.rewrite(root, "\n")

            rendered = plan(root, "Zulu", PORT, MIGRATION_ID)

            marker = rendered.created["src/Services/Zulu/Zulu.Domain/AssemblyMarker.cs"]
            self.assertNotIn("\r", marker)


class RendersASecondServiceBesideTheFirst(unittest.TestCase):
    """Two scaffolds into one checkout, which is the whole point of the tool.

    Every test above renders once, and once is the case where "to the end of
    the file" and "to the next service" are the same span. They are not the
    same span afterwards: the second run's extraction swallowed the first
    service's Compose pair and its `.env.example` variables and wrote them
    again — duplicate keys, an invalid Compose file, and no check anywhere
    that noticed. A Copilot review asked what a second run does; this is the
    coverage that was missing when it did.
    """

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        root = template_copy(Path(self.directory.name))
        apply(root, plan(root, PROBE, PORT, MIGRATION_ID))
        self.root = root
        # A second free port, for the second render. Spelt relative to the
        # first so the pair moves together the next time a real service takes
        # one of them — which is how the literal 5101 here survived until
        # PR-18 allocated it.
        self.second = plan(root, SECOND_PROBE, SECOND_PORT, "20260810120000")

    def tearDown(self):
        self.directory.cleanup()

    def test_every_compose_service_key_is_unique(self):
        compose = self.second.updated["deploy/compose/docker-compose.yml"]
        keys = re.findall(r"^  ([a-z0-9][a-z0-9-]*):$", compose.replace("\r\n", "\n"), re.M)
        self.assertEqual(sorted(set(keys)), sorted(keys), keys)
        for expected in ("catalog-api", "zulu-api", "yankee-api"):
            self.assertIn(expected, keys)

    def test_each_service_declares_its_connection_variables_once(self):
        env = self.second.updated["deploy/compose/.env.example"].replace("\r\n", "\n")
        for service in ("CATALOG", "ZULU", "YANKEE"):
            self.assertEqual(1, env.count(f"# {service}_CONNECTION="), service)
            self.assertEqual(1, env.count(f"# {service}_MIGRATOR_CONNECTION="), service)

    def test_the_new_pair_lands_after_the_services_already_there(self):
        compose = self.second.updated["deploy/compose/docker-compose.yml"]
        self.assertLess(compose.index("  zulu-api:"), compose.index("  yankee-migrator:"))
        self.assertLess(compose.index("  yankee-api:"), compose.index("  otel-collector:"))

    def test_the_ports_table_and_the_override_gain_one_entry_each(self):
        readme = self.second.updated["deploy/compose/README.md"]
        self.assertEqual(1, readme.count("| Zulu API |"))
        self.assertEqual(1, readme.count("| Yankee API |"))

        override = self.second.updated["deploy/compose/docker-compose.infra-only.yml"]
        self.assertEqual(1, override.count("  yankee-api:"))
        self.assertEqual(1, override.count("  zulu-api:"))

    def test_the_second_service_cannot_be_the_first_under_another_casing(self):
        # Rejecting `CATALOG` closed the template alias and not the general
        # case: after Zulu exists, `ZULU` is a distinct directory on a
        # case-sensitive filesystem and renders the same lower-cased Compose
        # keys and connection variables.
        # PORT + 2 rather than 5105: the raise this asserts is about the name,
        # and a service-range literal would let the test keep passing for the
        # port instead — green for the wrong reason, which is the failure mode
        # a red check that goes red wrongly already taught this file.
        for alias in ("ZULU", "zulu", "ZuLu"):
            with self.assertRaises(ScaffoldError):
                plan(self.root, alias, PORT + 2, "20260810120000")

    def test_the_solution_still_sorts_with_two_services_in_it(self):
        solution = self.second.updated["Platform.slnx"]
        services = re.findall(r'<Folder Name="/src/Services/([^/]+)/">', solution)

        # Sortedness is the property, and the two probes being present is what
        # keeps the assertion from being vacuous. This listed the whole set
        # literally — `["Catalog", "Yankee", "Zulu"]` — which encoded "Catalog
        # is the only real service" into a test about ordering, and went red
        # the day PR-18 added Ordering. Every later service would have broken
        # it again for the same non-reason.
        self.assertEqual(sorted(services), services)
        self.assertIn("Yankee", services)
        self.assertIn("Zulu", services)


class TheAllowListStep(unittest.TestCase):
    """§15.1's gate over a tree the render can actually be applied to.

    Everything here needs a root of its own, for two different reasons. The
    degradation pair needs one root with `.github/` and one without, because
    the step degrading and the step never having been wired in write the same
    nothing — the absence test is worth exactly nothing on its own, and is
    stated after the control that gives it meaning. The rest need a render
    that has been *written*, because what they are about is the second run and
    what the first one left behind.
    """

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = template_copy_with_gate(Path(self.directory.name))
        self.gate = load_scan_gate(self.root)

    def tearDown(self):
        self.directory.cleanup()

    def allow_list(self) -> str:
        return (self.root / SCAN_ALLOW_LIST).read_bytes().decode("utf-8")

    def test_a_root_without_the_gate_writes_no_entry(self):
        # The only root in this class without `.github/`, and the only one the
        # step is allowed to be silent about.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))

            rendered = plan(root, PROBE, PORT, MIGRATION_ID)

            self.assertIsNone(load_scan_gate(root))
            self.assertNotIn(SCAN_ALLOW_LIST, rendered.updated)

    def test_the_same_render_with_the_gate_beside_it_writes_the_entries(self):
        existing, _ = allow_list_entries(self.gate, self.allow_list())

        rendered = plan(self.root, PROBE, PORT, MIGRATION_ID)

        self.assertIn(SCAN_ALLOW_LIST, rendered.updated)
        entries, problems = allow_list_entries(self.gate, rendered.updated[SCAN_ALLOW_LIST])
        self.assertEqual([], problems)
        self.assertGreater(len(entries), len(existing))

    def test_a_rendered_service_passes_the_secret_scan(self):
        # The property that actually matters, and the one nothing asserted
        # before #161: §4.5 claims this script produces a service that builds
        # and its tests pass, and a service a mandatory CI job refuses is
        # neither.
        #
        # Over the APPLIED tree, with the gate's own walk. The first version of
        # this test scanned `{**created, **updated}` with `gate.RULES` — which
        # is what `update_allowed_secrets` does, from the same dict with the
        # same rules, appending a line per key it does not find. Two runs of
        # one loop cannot disagree, so the assertion could not fail. `scan_tree`
        # reads what is on disk, which is the thing CI reads.
        apply(self.root, plan(self.root, PROBE, PORT, MIGRATION_ID))

        entries, problems = allow_list_entries(self.gate, self.allow_list())
        self.assertEqual([], problems)
        findings, scanned = self.gate.scan_tree(self.root, self.gate.RULES)

        # Positive controls, and neither is decoration: a walk that read
        # nothing and a scanner that matched nothing both satisfy the
        # assertion below over an empty list, and both report a tree as clean
        # without having looked at it. This is the gate's own argument in
        # `main`, which refuses a clean report over an empty subject.
        self.assertGreater(scanned, 0, "the walk read no files")
        self.assertNotEqual([], findings, "the gate matched nothing in the applied tree")

        accepted = {entry.key() for entry in entries}
        self.assertEqual(
            [], [str(finding) for finding in findings if finding.key() not in accepted])

    def test_a_second_service_appends_beside_the_first(self):
        # Two renders into one checkout, which is what the tool is for. The
        # allow-list accumulates a block per service exactly as the Compose
        # file accumulates a block per service, and a second run that rewrote
        # it would take the first service's entries away — the gate then
        # reports the first service's own literals as unexplained, and the
        # branch that added the second service is what goes red.
        apply(self.root, plan(self.root, PROBE, PORT, MIGRATION_ID))
        first = self.allow_list()

        second = plan(self.root, SECOND_PROBE, SECOND_PORT, "20260810120000")

        after = second.updated[SCAN_ALLOW_LIST]
        self.assertTrue(after.startswith(first), "the second render rewrote the file")

        entries, problems = allow_list_entries(self.gate, after)
        self.assertEqual([], problems)
        paths = {entry.path for entry in entries}
        self.assertIn(f"tests/{PROBE}.Api.Tests/HostSmokeTests.cs", paths)
        self.assertIn(f"tests/{SECOND_PROBE}.Api.Tests/HostSmokeTests.cs", paths)

        keys = [entry.key() for entry in entries]
        self.assertEqual(len(set(keys)), len(keys), "the second render duplicated an entry")

    def test_a_render_never_explains_a_finding_it_did_not_introduce(self):
        # The defect #161's own fix shipped with. Ownership was decided by
        # `path == finding.path and rule == finding.rule.id and marker in
        # line`, and not one of those three names the service being rendered:
        # the `definitions.json` row's marker is empty, `"" in line` is true of
        # every line, and the line the scanner reports there carries the hash
        # with the account name above it. So a render whose allow-list had lost
        # an earlier service's entry matched that service's finding, wrote a
        # sentence naming ITSELF, and cleared it — a suppression arriving for a
        # credential the run is not writing, which is the outcome the
        # allow-list's own header exists to refuse.
        apply(self.root, plan(self.root, PROBE, PORT, MIGRATION_ID))

        # A bad merge, a hand edit, a partial checkout — the entry is gone and
        # the first service's hash is unexplained again.
        lines = self.allow_list().splitlines()
        orphaned = [
            line for line in lines
            if line.startswith("deploy/compose/rabbitmq/definitions.json") and PROBE in line
        ]
        self.assertEqual(1, len(orphaned), "the fixture found no entry to remove")
        stolen = orphaned[0].split("|")[2].strip()
        (self.root / SCAN_ALLOW_LIST).write_bytes(
            ("\n".join(line for line in lines if line != orphaned[0]) + "\n").encode("utf-8"))

        second = plan(self.root, SECOND_PROBE, SECOND_PORT, "20260810120000")

        before, _ = allow_list_entries(self.gate, self.allow_list())
        entries, problems = allow_list_entries(
            self.gate, second.updated[SCAN_ALLOW_LIST])
        self.assertEqual([], problems)
        added = entries[len(before):]
        self.assertNotEqual([], added, "the second render generated nothing")

        # The named half: the first service's fingerprint, under the second
        # service's name.
        for entry in added:
            self.assertNotEqual(stolen, entry.fingerprint, entry.reason)

        # And the general half, which is the rule rather than the instance: a
        # key the file already carried is not this render's to explain.
        for entry in added:
            if entry.path not in second.updated:
                continue                      # a created file has no "before"
            original = (self.root / entry.path).read_bytes().decode("utf-8")
            self.assertNotIn(
                entry.key(),
                {found.key() for found in self.gate.scan_text(
                    entry.path, original, self.gate.RULES)},
                entry.reason,
            )


class RefusesToRun(unittest.TestCase):
    def test_a_name_that_is_not_pascal_case(self):
        # The last two needed `fullmatch`: Python's `$` matches before a
        # trailing newline, so the anchored pattern accepted one and it went
        # into a directory name, a file name and a C# namespace.
        for name in ("zulu", "Zulu.Api", "order-ing", "", "Zulu\n", "Zulu\nEvil"):
            with self.assertRaises(ScaffoldError):
                render(name=name)

    def test_the_template_cannot_be_its_own_copy(self):
        with self.assertRaises(ScaffoldError):
            render(name="Catalog")

    def test_the_template_under_another_casing_is_still_the_template(self):
        # `CATALOG` passes an exact-match check and its lower casing is still
        # `catalog`, so the Compose block it renders keeps the template's own
        # service keys — a duplicate pair, on a case-sensitive checkout where
        # the directory collision does not catch it first.
        for alias in ("CATALOG", "catalog", "CaTaLoG"):
            with self.assertRaises(ScaffoldError):
                render(name=alias)

    def test_a_rename_never_re_enters_its_own_output(self):
        # Chained replaces fed each pass to the next: `CATALOGSearch` turned a
        # source `Catalog` into `CATALOGSEARCHSearch`, because the pascal pass
        # produced text the upper pass then matched.
        self.assertEqual("CATALOGSearch.Domain", Names("CATALOGSearch").rename("Catalog.Domain"))
        self.assertEqual("catalogsearch-api", Names("CATALOGSearch").rename("catalog-api"))

        rendered = render(name="CATALOGSearch")
        self.assertIn(
            "src/Services/CATALOGSearch/CATALOGSearch.Domain/AssemblyMarker.cs", rendered.created
        )

    def test_a_name_longer_than_a_sql_server_identifier(self):
        # The name is the database and the schema, and `sysname` is
        # nvarchar(128). Past that everything renders and the first migration
        # is what fails — late, against a real database.
        self.assertIsNotNone(render(name="A" + "b" * 127))
        with self.assertRaises(ScaffoldError) as raised:
            render(name="A" + "b" * 128)
        self.assertIn("128", str(raised.exception))

    def test_a_name_sql_server_reserves_for_itself(self):
        # The name becomes `Database=<name>`, so `Master` points the migrator
        # at a system database rather than an isolated one, and `Sys` collides
        # with the reserved schema. Both pass every other check and fail — if
        # at all — against a live server.
        for name in ("Master", "Model", "Msdb", "Tempdb", "Sys", "MASTER"):
            with self.assertRaises(ScaffoldError) as raised:
                render(name=name)
            self.assertIn("system name", str(raised.exception), name)

    def test_a_service_named_product_cannot_mask_the_slice_check(self):
        # The mask that lets `CatalogSearch` through would, for a service
        # called `Product`, strip every genuine slice leftover along with the
        # service's own name — and the render would report itself
        # domain-neutral. The slice half therefore runs before the rename,
        # where a `Product` means only itself.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            program = root / "src/Services/Catalog/Catalog.Api/Program.cs"
            text = program.read_bytes().decode("utf-8")
            program.write_bytes((text + "// leftover: ProductEndpoints\n").encode("utf-8"))

            with self.assertRaises(ScaffoldError) as raised:
                plan(root, "Product", 5199, MIGRATION_ID)
            self.assertIn("the slice survived", str(raised.exception))

    def test_a_slice_leftover_in_any_casing(self):
        # The template guard has always been case-insensitive; the slice
        # guard was not, so `PRODUCT_ENDPOINT` cleared a check that rejects
        # `ProductEndpoint`. Two halves of one invariant held to one standard.
        for leftover in ("ProductEndpoints", "PRODUCT_ENDPOINT", "product_id"):
            with tempfile.TemporaryDirectory() as directory:
                root = template_copy(Path(directory))
                program = root / "src/Services/Catalog/Catalog.Api/Program.cs"
                text = program.read_bytes().decode("utf-8")
                program.write_bytes((text + f"// leftover: {leftover}\n").encode("utf-8"))

                with self.assertRaises(ScaffoldError) as raised:
                    render(repo_root=root)
                self.assertIn("the slice survived", str(raised.exception), leftover)

    def test_a_name_windows_reserves_as_a_device(self):
        # These clear PascalCase, the template check and every collision test,
        # and then `apply()` cannot create `src/Services/Con` or
        # `Con.Domain.csproj` on Windows — a failure in the middle of the
        # write, which is the one place this script promises not to fail.
        for name in ("Con", "Prn", "Aux", "Nul", "Com1", "Lpt9", "CON"):
            with self.assertRaises(ScaffoldError) as raised:
                render(name=name)
            self.assertIn("reserved device name", str(raised.exception), name)

    def test_a_name_whose_projects_would_collide_with_the_building_blocks(self):
        # `Common` renders Common.Domain and Common.Application beside the
        # building blocks of exactly those names — two projects, one assembly
        # identity, a solution that does not build. It was refused before this
        # check too, but only because tests/Common.Domain.Tests happens to
        # exist: an accident, with a message about the wrong thing.
        with self.assertRaises(ScaffoldError) as raised:
            render(name="Common")
        self.assertIn("assembly identity", str(raised.exception))

    def test_a_service_section_4_1_gives_a_worker(self):
        # Shipping and Notifications take a Worker in place of an Api, and
        # Notifications has no Domain project. Documenting "no Worker template"
        # did not stop the script producing an Api for either — a note is not
        # a guard, and the output would have contradicted the chapter.
        for name in ("Shipping", "Notifications", "SHIPPING"):
            with self.assertRaises(ScaffoldError) as raised:
                render(name=name)
            self.assertIn("Worker", str(raised.exception))

    def test_a_name_that_contains_a_template_token_is_still_a_name(self):
        # The straggler check searches for `catalog` and `roduct`, and a
        # service legitimately called CatalogSearch or ProductReviews puts
        # those in every path it generates. Masking the requested name first
        # is what separates "the rename did not reach this" from "the caller
        # asked for it".
        for name in ("CatalogSearch", "ProductReviews"):
            rendered = render(name=name)
            self.assertIn(
                f"src/Services/{name}/{name}.Domain/AssemblyMarker.cs", rendered.created, name
            )

    def test_a_service_that_already_exists(self):
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            (root / "src/Services/Zulu").mkdir(parents=True)
            with self.assertRaises(ScaffoldError):
                render(repo_root=root)

    def test_a_port_another_service_already_publishes(self):
        with self.assertRaises(ScaffoldError):
            render(port=5102)

    def test_a_template_whose_api_block_is_not_bound_to_loopback(self):
        # The other half of the loopback rule, and the half a render-and-read
        # assertion cannot reach: the prefix is REQUIRED of the template rather
        # than copied off it, so removing §14.1's bind from Catalog stops the
        # scaffold rather than propagating into every service scaffolded after.
        # Bytes rather than text, because the copy's line endings are the
        # platform's and this substitution has no business changing them.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            compose = root / "deploy/compose/docker-compose.yml"
            unbound = compose.read_bytes().replace(
                b'ports: [ "127.0.0.1:5102:8080" ]', b'ports: [ "5102:8080" ]'
            )
            self.assertIn(b'ports: [ "5102:8080" ]', unbound, "the substitution missed")
            compose.write_bytes(unbound)

            with self.assertRaises(ScaffoldError):
                render(repo_root=root)

    def test_a_name_that_collides_with_an_infrastructure_connection_key(self):
        # §7.1's runtime key is ConnectionStrings__<Service> and the rename is
        # what writes it, while §14.1's api block already declares
        # ConnectionStrings__RabbitMq, ConnectionStrings__RedisCache and
        # ConnectionStrings__RedisCoordination beside it. A service named after
        # one of those renders the same mapping key twice — and this was the
        # sixth bad name and the only one whose failure was silent: the rename
        # worked, the straggler check saw no template token, the YAML stayed
        # valid, and one of the two connection strings was discarded by
        # whatever read the file.
        for name in ("RabbitMq", "RedisCache", "RedisCoordination"):
            with self.assertRaises(ScaffoldError) as raised:
                render(name=name)
            self.assertIn(f"ConnectionStrings__{name}", str(raised.exception), name)

    def test_the_duplicate_key_check_is_looking_at_the_environment_mappings(self):
        # The subject test for the refusal above, and the reason it is a
        # predicate rather than those three names: the check is only as good as
        # the keys handed to it, and a pattern that stops matching the
        # template's shape hands it nothing — over which every name there is
        # passes. So this asserts what environment_keys extracted, not what the
        # caller concluded from it.
        compose = render().updated["deploy/compose/docker-compose.yml"].replace("\r\n", "\n")
        lines = compose.split("\n")
        # Sliced the way update_compose slices, with the script's own reader:
        # §14.1's pair rule renders a migrator and an api, so the block is the
        # two service keys from the migrator to the third one after it.
        starts = [i for i, line in enumerate(lines) if new_service.SERVICE_KEY.fullmatch(line)]
        at = starts.index(lines.index(f"  {PROBE.lower()}-migrator:"))
        end = starts[at + 2] if at + 2 < len(starts) else len(lines)
        block = "\n".join(lines[starts[at]:end])

        mappings = environment_keys(block)

        # Two, one per service, and each with something in it. Flattening them
        # would be weaker as well as wrong — a key in both is two containers
        # agreeing about a variable, not one saying a thing twice.
        self.assertEqual(2, len(mappings), block)
        for mapping in mappings:
            self.assertTrue(mapping, block)

        keys = [key for mapping in mappings for key in mapping]
        # Both halves of the collision, read out of the rendered block itself:
        # the key the rename writes from the service's name, and one §14.1
        # wrote long before it. Lose either and the check above is watching a
        # surface the defect is not on.
        self.assertIn(f"ConnectionStrings__{PROBE}", keys, block)
        self.assertIn("ConnectionStrings__RabbitMq", keys, block)

    def test_a_port_docker_cannot_publish(self):
        # Collision was the only check once, so -1 and 70000 planned happily
        # and were written into Compose.
        for port in (-1, 0, 70000):
            with self.assertRaises(ScaffoldError):
                render(port=port)

    def test_a_migration_id_that_is_not_a_timestamp(self):
        # It reaches a file path, so `..` in it writes outside the service
        # tree — from the flag whose only purpose is repeatable tests.
        for migration_id in (
            "../../../etc/passwd",
            "InitialCreate",
            "2026080912000",
            "20260809120000\n",   # `$` accepts a trailing newline; `fullmatch` does not
        ):
            with self.assertRaises(ScaffoldError):
                plan(REPO_ROOT, "Zulu", PORT, migration_id)

    def test_a_directory_that_is_not_the_repository(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ScaffoldError):
                render(repo_root=Path(directory))

    def test_a_file_in_the_migrations_directory_nobody_classified(self):
        # The migrations branch used to `continue` unconditionally, so this
        # directory was the one place the "will not guess" promise did not
        # hold: a helper or a README beside the migrations was dropped in
        # silence rather than stopping the run.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            stray = root / MIGRATIONS_DIR / "README.md"
            stray.write_bytes(b"# how these migrations were written\n")

            with self.assertRaises(ScaffoldError) as raised:
                render(repo_root=root)
            self.assertIn("will not guess", str(raised.exception))

    def test_a_copied_template_file_that_no_longer_exists(self):
        # The mirror of the check below, and the half that was missing. A file
        # added to Catalog stopped the run; a file deleted from it did not —
        # the loop never saw it, its patches never ran, and the scaffold
        # succeeded with a service missing a piece.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            dispatcher = "src/Services/Catalog/Catalog.Infrastructure/Persistence/EfDomainEventCollector.cs"
            (root / dispatcher).unlink()

            with self.assertRaises(ScaffoldError) as raised:
                render(repo_root=root)
            self.assertIn("no longer has", str(raised.exception))
            self.assertIn("EfDomainEventCollector.cs", str(raised.exception))

    def test_a_template_file_nobody_classified(self):
        # The hole the straggler check cannot see: a new Catalog folder carries
        # none of the tokens it searches for, so the classification is what
        # forces the decision.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            unclassified = root / "src/Services/Catalog/Catalog.Domain/Categories/Category.cs"
            unclassified.parent.mkdir(parents=True)
            unclassified.write_bytes(b"namespace Catalog.Domain.Categories;\r\n")

            with self.assertRaises(ScaffoldError) as raised:
                render(repo_root=root)
            self.assertIn("not classified", str(raised.exception))

    def test_either_half_of_the_infra_only_pair_going_missing(self):
        # The anchor used to be the API entry alone, so a change to the
        # migrator's half passed unnoticed while the scaffold went on emitting
        # the shape it used to have. Both halves, either direction.
        halves = (
            '  catalog-migrator:\n    profiles: [ "excluded" ]\n',
            '  catalog-api:\n    profiles: [ "excluded" ]\n',
        )
        for half in halves:
            with tempfile.TemporaryDirectory() as directory:
                root = template_copy(Path(directory))
                override = root / "deploy/compose/docker-compose.infra-only.yml"
                text = override.read_bytes().decode("utf-8")
                override.write_bytes(
                    text.replace(half.replace("\n", "\r\n"), "").replace(half, "").encode("utf-8")
                )

                with self.assertRaises(ScaffoldError) as raised:
                    render(repo_root=root)
                self.assertIn("infra-only override", str(raised.exception))

    def test_a_patch_anchor_the_template_no_longer_carries(self):
        # The fragility this design accepts, failing loudly rather than
        # silently producing a service that still names Catalog's slice.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            program = root / "src/Services/Catalog/Catalog.Api/Program.cs"
            program.write_bytes(
                program.read_bytes().replace(b"using Catalog.Api.Endpoints;", b"")
            )

            with self.assertRaises(ScaffoldError) as raised:
                render(repo_root=root)
            self.assertIn("Program.cs", str(raised.exception))

    def test_a_migration_shape_added_without_the_name_it_is_reported_by(self):
        # `TEMPLATE_MIGRATIONS` and `MIGRATION_LABELS` are paired positionally,
        # and the pairing used to be `zip(..., strict=True)` — a real guard
        # raising the wrong exception. `main` catches `ScaffoldError` and
        # nothing else, so a tuple grown without its label ended the run in a
        # `ValueError` traceback naming neither constant, from a script whose
        # stated contract is one line on stderr and exit 1.
        original = new_service.MIGRATION_LABELS
        new_service.MIGRATION_LABELS = original[:-1]
        self.addCleanup(setattr, new_service, "MIGRATION_LABELS", original)

        with self.assertRaises(ScaffoldError) as raised:
            render()
        self.assertIn("MIGRATION_LABELS", str(raised.exception))

    def test_a_credential_shaped_literal_in_the_template_nobody_explained(self):
        # The same refusal as an unclassified file, one gate along, and the
        # one this file's README sells as the safety property while nothing
        # exercised it. `SCAN_REASONS` gives one sentence per finding §15.1's
        # scanner reports over a render; a finding no row explains has to stop
        # the run, because the alternative is this script inventing a reason —
        # a suppression nobody wrote, which is the one thing the allow-list's
        # own header says it must never hold.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy_with_gate(Path(directory))
            smoke = root / "tests/Catalog.Api.Tests/HostSmokeTests.cs"
            # A shape the scanner already recognises, in a file the render
            # copies, under a rule no row in SCAN_REASONS pairs with that path.
            # The value is invented and belongs to nobody.
            #
            # ASSEMBLED FROM TWO PIECES, because this file is walked by the
            # scanner like every other: written as one source line the fixture
            # would itself be a finding in the suite that tests the refusal,
            # and would want an allow-list entry for a credential that never
            # leaves a temporary directory. The rules are line-based, so the
            # join happens at run time and the file this writes carries the
            # shape whole. Measured, not reasoned about — the one-line form
            # failed the scan.
            shape = b'// const string ApiKey = "' + b'not-a-real-value-either";'
            smoke.write_bytes(smoke.read_bytes() + b"\r\n" + shape + b"\r\n")

            with self.assertRaises(ScaffoldError) as raised:
                render(repo_root=root)
            self.assertIn("SCAN_REASONS", str(raised.exception))

    def test_a_second_credential_under_a_rule_the_same_file_already_carries(self):
        # The case the test above does NOT reach, and the distinction is the
        # whole of the defect: that one adds a literal under a rule no row
        # pairs with this path, so no row matches and the refusal fires. Here
        # the rule and the path both already have a row — only the credential
        # is new — and while the marker was `""` that row matched the new line
        # too, handing it a sentence written about the fixture next to it. A
        # suppression for a credential nobody explained, produced by the guard
        # that exists to refuse exactly that.
        #
        # Assembled from two pieces for the reason the neighbour states: a
        # whole password literal on one source line would make this file a
        # finding in the scan of the repository that contains it.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy_with_gate(Path(directory))
            smoke = root / "tests/Catalog.Api.Tests/HostSmokeTests.cs"
            shape = (b'// var other = "Server=x;User Id=sa;Password'
                     + b'=a-different-unusable-value;";')
            smoke.write_bytes(smoke.read_bytes() + b"\r\n" + shape + b"\r\n")

            with self.assertRaises(ScaffoldError) as raised:
                render(repo_root=root)
            self.assertIn("SCAN_REASONS", str(raised.exception))

    def test_a_row_whose_marker_explains_every_line_of_its_file(self):
        # The guard over the table itself, rather than over what the template
        # holds. An empty marker is `in` every line, so the row stops naming a
        # literal and accepts its rule blanket across that file — and it reads
        # as deliberate until the second finding arrives to be swallowed, which
        # is why the emptiness is refused rather than the collision waited for.
        original = new_service.SCAN_REASONS
        path, rule, _, reason = original[0]
        new_service.SCAN_REASONS = ((path, rule, "", reason),) + original[1:]
        self.addCleanup(setattr, new_service, "SCAN_REASONS", original)

        with self.assertRaises(ScaffoldError) as raised:
            render()
        self.assertIn("empty marker", str(raised.exception))

    def test_a_row_whose_marker_is_loose_without_being_empty(self):
        # The other half, and the one a non-empty marker does not buy. `"` is
        # in both password lines of the smoke fixture's file, so this row would
        # explain two findings with two fingerprints under one sentence —
        # narrower than `""` and wrong in exactly the same way. The check reads
        # what the table SELECTED rather than how it was spelled, which is the
        # only form that survives a marker nobody thought was loose.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy_with_gate(Path(directory))
            smoke = root / "tests/Catalog.Api.Tests/HostSmokeTests.cs"
            shape = (b'// var other = "Server=x;User Id=sa;Password'
                     + b'=a-different-unusable-value;";')
            smoke.write_bytes(smoke.read_bytes() + b"\r\n" + shape + b"\r\n")

            original = new_service.SCAN_REASONS
            path, rule, _, reason = original[0]
            new_service.SCAN_REASONS = ((path, rule, "Password=", reason),) + original[1:]
            self.addCleanup(setattr, new_service, "SCAN_REASONS", original)

            with self.assertRaises(ScaffoldError) as raised:
                render(repo_root=root)
            self.assertIn("different fingerprints", str(raised.exception))

    def test_a_gate_in_the_target_that_is_not_the_one_this_script_shipped_with(self):
        # `load_scan_gate` EXECUTES this file, and `--repo-root` chooses which
        # tree it comes out of — so rendering into an unreviewed checkout used
        # to run that checkout's Python with the developer's privileges.
        # Reading a repository's source is not a reason to execute it, and the
        # docstring that admitted the exposure argued it away as "not new".
        #
        # Loading TOOL_ROOT's copy instead would be the wrong repair: the
        # entries carry the scanner's own fingerprints and the scanner that
        # verifies them is the target's, so the two have to be one file rather
        # than a choice. A target whose gate differs is therefore refused, and
        # the refusal is what this pins — the marker below is inert Python, so
        # a run that reaches execution succeeds and proves nothing.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy_with_gate(Path(directory))
            gate = root / SCAN_GATE
            gate.write_bytes(gate.read_bytes() + b"\nSCAFFOLD_TRUST_PROBE = 1\n")

            with self.assertRaises(ScaffoldError) as raised:
                render(repo_root=root)
            self.assertIn("shipped", str(raised.exception))

    def test_an_allow_list_that_does_not_parse(self):
        # This script appends to that file, so it reads it first — and a file
        # the gate already rejects is not one to append to: the run would
        # produce a tree whose scan fails on a line the render did not write,
        # reported against the branch that rendered.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy_with_gate(Path(directory))
            allow_list = root / SCAN_ALLOW_LIST
            allow_list.write_bytes(
                allow_list.read_bytes() + b"this line has no pipes at all\n")

            with self.assertRaises(ScaffoldError) as raised:
                render(repo_root=root)
            self.assertIn("allowed-secrets.txt", str(raised.exception))

    def test_a_github_directory_carrying_neither_the_gate_nor_the_list(self):
        # `.github/` absent is the degradation and is tested next door. A
        # checkout that HAS the directory and is missing a piece of the gate is
        # a real tree, and returning None there rendered a service the scanner
        # refuses without saying so — #161 back, quietly, out of the code that
        # closed it. It is also the only shared file whose absence was
        # tolerated; the other six raise inside `read`.
        for missing in (SCAN_GATE, SCAN_ALLOW_LIST):
            with tempfile.TemporaryDirectory() as directory:
                root = template_copy_with_gate(Path(directory))
                (root / missing).unlink()

                with self.assertRaises(ScaffoldError) as raised:
                    render(repo_root=root)
                self.assertIn(missing, str(raised.exception), missing)

    def test_a_gate_that_is_not_a_python_module(self):
        # `exec_module` runs whatever is at that path, so a malformed one used
        # to leave the run as an uncaught SyntaxError — past `main`'s
        # `except ScaffoldError` and out as a traceback, from a script whose
        # stated contract is one line on stderr and exit 1.
        #
        # TOOL_ROOT IS POINTED AT THE SAME ROOT, so the trust check next door
        # passes and this one is what answers. Without that the two refusals
        # race and the earlier wins, leaving this test green about a message it
        # never reaches — the gate here is malformed AND differs from the
        # shipped copy, and only one of those can be the subject.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy_with_gate(Path(directory))
            (root / SCAN_GATE).write_bytes(b"def (\n")
            self.addCleanup(setattr, new_service, "TOOL_ROOT", new_service.TOOL_ROOT)
            new_service.TOOL_ROOT = root

            with self.assertRaises(ScaffoldError) as raised:
                render(repo_root=root)
            self.assertIn("did not load", str(raised.exception))

    def test_a_gate_that_loads_and_is_not_the_secret_scan(self):
        # A module that imports is not a module that is the gate. Without this
        # the failure is an AttributeError three frames later, naming a symbol
        # rather than the file the caller pointed at.
        #
        # TOOL_ROOT is pointed at the same root for the reason above: this
        # asserts the MEMBER check, which only runs on a gate the trust check
        # has already admitted.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy_with_gate(Path(directory))
            (root / SCAN_GATE).write_bytes(b"RULES = []\n")
            self.addCleanup(setattr, new_service, "TOOL_ROOT", new_service.TOOL_ROOT)
            new_service.TOOL_ROOT = root

            with self.assertRaises(ScaffoldError) as raised:
                render(repo_root=root)
            self.assertIn("read_allowed", str(raised.exception))

    def test_a_name_that_is_a_prefix_of_the_broker_connection_key(self):
        # NOT a refusal, and it used to be one. The Compose rows were keyed on
        # `ConnectionStrings__Catalog`, which renames to
        # `ConnectionStrings__<Name>` — a substring of the broker line's
        # `ConnectionStrings__RabbitMq` for every name that is a prefix of
        # `RabbitMq`. Both rows then matched one finding and the run stopped
        # with "the template has gained a credential-shaped literal", which is
        # actionable for nobody. Eight legal PascalCase names cleared every
        # other precondition and hit it; the markers now come from the value,
        # where no service name reaches them.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy_with_gate(Path(directory))

            rendered = plan(root, "Rabbit", PORT, MIGRATION_ID)

            entries, problems = allow_list_entries(
                load_scan_gate(root), rendered.updated[SCAN_ALLOW_LIST])
            self.assertEqual([], problems)
            reasons = [entry.reason for entry in entries if "docker-compose" in entry.path]
            self.assertIn("Rabbit's local connection default, in-cluster hostname.", reasons)
            self.assertIn(
                "Section 14.1's broker default for Rabbit, the per-service account "
                "that replaced guest.",
                reasons,
            )

    def test_nothing_is_written_when_the_run_refuses(self):
        # A *refused* run writes nothing, because the whole render is a value
        # until `apply` is called. That is the guarantee, and it is narrower
        # than "no half-scaffolded state": `apply` writes in a loop and an I/O
        # failure partway does leave a partial tree — see its docstring.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            before = sorted(path.relative_to(root).as_posix() for path in root.rglob("*"))

            with self.assertRaises(ScaffoldError):
                plan(root, "Zulu", 5102, MIGRATION_ID)

            after = sorted(path.relative_to(root).as_posix() for path in root.rglob("*"))
            self.assertEqual(before, after)


class Applies(unittest.TestCase):
    def test_it_writes_every_planned_file_and_nothing_else(self):
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            rendered = plan(root, "Zulu", PORT, MIGRATION_ID)

            apply(root, rendered)

            # Both maps, not just `created`. Read back only the new files, this
            # asserted the half of `apply()` that is least likely to break: a
            # regression that omitted or corrupted every shared-file update
            # would have passed a test whose name says "every planned file".
            for relative, text in {**rendered.created, **rendered.updated}.items():
                # utf-8, not utf-8-sig: the byte-order mark is content here,
                # and a reader that swallowed it could not tell the machine-
                # owned migration files from the hand-written ones.
                self.assertEqual(text, (root / relative).read_bytes().decode("utf-8"))
            self.assertFalse((root / "src/Services/Zulu/Zulu.Domain/Products").exists())


class TheCommandLine(unittest.TestCase):
    """`main` itself, which every test above went around.

    `plan` and `apply` were covered from the first commit and the entry point
    a developer actually types was not — so argument parsing, the default
    migration id, the exit codes and what reaches stdout and stderr were all
    uncovered. The licence gate tests its own `main`; this is the same bar.
    """

    def run_main(self, *argv: str) -> tuple[int, str, str]:
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = main(list(argv))
        return code, out.getvalue(), err.getvalue()

    def test_a_successful_run_reports_what_it_wrote_and_exits_zero(self):
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))

            code, out, err = self.run_main(
                "Zulu", "--port", str(PORT), "--repo-root", str(root),
                "--migration-id", MIGRATION_ID,
            )

            self.assertEqual(0, code)
            self.assertEqual("", err)
            # NO ENUMERATION HERE, and the third failure of one is why. The
            # comment used to name the files that made the total what it was —
            # it said 54 while the assertion said 55, then named two files
            # while the assertion said 59 — and each time it congratulated
            # itself, in its own text, for being the half that does not rot.
            # A number a test pins fails when it is wrong; a list a comment
            # keeps beside it does not, so the list is the half to delete.
            # What a reader can check is `plan().created`.
            #
            # `6 updated` and not 7: this root has no `.github/`, so §15.1's
            # allow-list step degrades — which is `TheAllowListStep`'s subject
            # and is asserted there from both sides.
            self.assertIn("61 files created, 6 updated", out)
            self.assertIn(f"port {PORT}", out)
            self.assertTrue((root / "src/Services/Zulu/Zulu.Api/Program.cs").exists())

    def test_a_refused_run_exits_one_with_the_reason_on_stderr(self):
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))

            code, out, err = self.run_main(
                "Shipping", "--port", str(PORT), "--repo-root", str(root)
            )

            self.assertEqual(1, code)
            self.assertEqual("", out, "a refused run must not report success")
            self.assertIn("Worker", err)

    def test_the_port_is_required(self):
        # argparse exits 2 rather than returning, which is its contract and
        # not this script's — asserted so a later `default=` cannot slip in.
        with self.assertRaises(SystemExit) as exit_code:
            with contextlib.redirect_stderr(io.StringIO()):
                main(["Zulu"])
        self.assertEqual(2, exit_code.exception.code)

    def test_fourteen_digits_that_are_not_a_date_refuse_in_one_line(self):
        # MIGRATION_ID checks the shape, which is its job — month thirteen is
        # fourteen digits. next_migration_id is what notices, and strptime's
        # ValueError is not a ScaffoldError, so before this the CLI answered a
        # bad flag with a traceback where every other refusal is one line.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))

            code, _, err = self.run_main(
                "Zulu", "--port", str(PORT),
                "--migration-id", "20261301000000",
                "--repo-root", str(root),
            )

            self.assertEqual(1, code)
            self.assertNotIn("Traceback", err)
            self.assertIn("20261301000000", err)

    def test_the_migration_id_defaults_to_a_utc_timestamp(self):
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))

            self.assertEqual(0, self.run_main(
                "Zulu", "--port", str(PORT), "--repo-root", str(root)
            )[0])

            migrations = root / "src/Services/Zulu/Zulu.Infrastructure/Persistence/Migrations"
            generated = [p.name for p in migrations.glob("*_InitialCreate.cs")]
            self.assertEqual(1, len(generated), generated)
            self.assertRegex(generated[0], r"^\d{14}_InitialCreate\.cs$")


if __name__ == "__main__":
    unittest.main()
