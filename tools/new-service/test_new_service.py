"""The scaffold's own tests, run against the real repository.

A fixture tree would test the script against a template that cannot drift.
The whole risk this design accepts is that Catalog moves under the anchors
(see the module docstring next door), and only rendering the tree that
actually exists catches that. So `plan()` reads the checkout — and writes
nothing, which is why it can: the render is a value, and `apply()` is the only
thing that touches disk.

    cd tools/new-service && python -m unittest
"""

import re
import shutil
import tempfile
import unittest
from pathlib import Path

from new_service import (
    COPY_ROOTS,
    OMITTED,
    Plan,
    ScaffoldError,
    apply,
    plan,
)

REPO_ROOT = Path(__file__).resolve().parents[2]
MIGRATION_ID = "20260809120000"
PORT = 5101


def render(name: str = "Ordering", port: int = PORT, repo_root: Path = REPO_ROOT) -> Plan:
    return plan(repo_root, name, port, MIGRATION_ID)


def template_copy(destination: Path) -> Path:
    """The template and the five shared files, and nothing else.

    Only the tests that need to *break* the template copy it; everything else
    reads the checkout directly.
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
    ):
        (destination / shared).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(REPO_ROOT / shared, destination / shared)
    return destination


class RendersTheTemplate(unittest.TestCase):
    def setUp(self):
        self.rendered = render()

    def test_it_writes_the_five_projects_and_the_four_test_projects(self):
        for project in (
            "src/Services/Ordering/Ordering.Domain/Ordering.Domain.csproj",
            "src/Services/Ordering/Ordering.Application/Ordering.Application.csproj",
            "src/Services/Ordering/Ordering.Infrastructure/Ordering.Infrastructure.csproj",
            "src/Services/Ordering/Ordering.Migrator/Ordering.Migrator.csproj",
            "src/Services/Ordering/Ordering.Api/Ordering.Api.csproj",
            "tests/Ordering.Domain.Tests/Ordering.Domain.Tests.csproj",
            "tests/Ordering.Application.Tests/Ordering.Application.Tests.csproj",
            "tests/Ordering.Api.Tests/Ordering.Api.Tests.csproj",
            "tests/Ordering.TestSupport/Ordering.TestSupport.csproj",
        ):
            self.assertIn(project, self.rendered.created)

    def test_it_writes_both_dockerfiles(self):
        # §15.2 builds two images per service, and PR-10 found that both need
        # the -extra tag: SqlClient refuses to open a connection under the
        # globalization-invariant mode plain chiselled runs in.
        for image in ("Ordering.Api", "Ordering.Migrator"):
            path = f"src/Services/Ordering/{image}/Dockerfile"
            self.assertIn(path, self.rendered.created)
            self.assertIn("-chiseled-extra", self.rendered.created[path])

    def test_it_writes_the_assembly_marker_the_gates_anchor_on(self):
        marker = "src/Services/Ordering/Ordering.Domain/AssemblyMarker.cs"
        self.assertIn(marker, self.rendered.created)
        self.assertIn("namespace Ordering.Domain;", self.rendered.created[marker])

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
            "src/Services/Ordering/Ordering.Infrastructure/Persistence/OrderingDbContext.cs"
        ]
        self.assertIn('modelBuilder.HasDefaultSchema("ordering");', context)

        infrastructure = self.rendered.created[
            "src/Services/Ordering/Ordering.Infrastructure/DependencyInjection.cs"
        ]
        self.assertIn('configuration.GetConnectionString("Ordering")', infrastructure)

        migrator = self.rendered.created["src/Services/Ordering/Ordering.Migrator/MigratorHost.cs"]
        self.assertIn('GetConnectionString("OrderingMigrator")', migrator)

        self.assertIn(
            "ORDERING_MIGRATOR_CONNECTION", self.rendered.updated["deploy/compose/.env.example"]
        )


class OmitsTheSlice(unittest.TestCase):
    def setUp(self):
        self.rendered = render()

    def test_it_writes_nothing_from_the_catalog_slice(self):
        # Every omitted path, renamed, must be absent. A scaffolded service is
        # PR-07's state with the later wiring on it, not PR-10's state with
        # the nouns changed.
        for omitted in OMITTED:
            self.assertNotIn(omitted.replace("Catalog", "Ordering"), self.rendered.created)

    def test_the_host_maps_no_endpoint_and_says_where_the_first_one_goes(self):
        program = self.rendered.created["src/Services/Ordering/Ordering.Api/Program.cs"]
        self.assertNotIn("MapProductEndpoints", program)
        self.assertIn("app.MapCommonHealthEndpoints();", program)
        self.assertIn("This service maps no endpoint of its own yet.", program)

    def test_the_validator_scan_re_anchors_on_the_assembly(self):
        # §4.2's line still scans this assembly; it just cannot name a type
        # inside it, there being no validator yet and DependencyInjection
        # being static — CS0718, found by compiling the scaffolded service.
        application = self.rendered.created[
            "src/Services/Ordering/Ordering.Application/DependencyInjection.cs"
        ]
        self.assertIn(
            "services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);", application
        )

    def test_the_repository_registration_is_gone_and_the_unit_of_work_is_not(self):
        infrastructure = self.rendered.created[
            "src/Services/Ordering/Ordering.Infrastructure/DependencyInjection.cs"
        ]
        self.assertIn("services.AddScoped<IUnitOfWork, EfUnitOfWork>();", infrastructure)
        self.assertNotIn("IRepository", infrastructure)

    def test_both_architecture_gates_anchor_on_the_marker(self):
        domain = self.rendered.created["tests/Ordering.Domain.Tests/ArchitectureTests.cs"]
        self.assertIn("typeof(AssemblyMarker).Assembly", domain)
        self.assertIn('string[] allowed = ["Common.Domain", "System.Runtime"];', domain)

        application = self.rendered.created["tests/Ordering.Application.Tests/ArchitectureTests.cs"]
        self.assertIn("typeof(AssemblyMarker).Assembly", application)
        self.assertIn("using Ordering.Domain;", application)

    def test_the_application_test_project_carries_no_container_wiring(self):
        # With no handler test there is no collection member and no fixture
        # consumer, so the references and the provider package go with them.
        csproj = self.rendered.created[
            "tests/Ordering.Application.Tests/Ordering.Application.Tests.csproj"
        ]
        self.assertNotIn("Ordering.TestSupport.csproj", csproj)
        self.assertNotIn("Ordering.Infrastructure.csproj", csproj)
        self.assertNotIn("Microsoft.EntityFrameworkCore.SqlServer", csproj)
        self.assertNotIn(
            "tests/Ordering.Application.Tests/IntegrationCollection.cs", self.rendered.created
        )

    def test_the_application_project_carries_no_dapper_until_its_first_query(self):
        csproj = self.rendered.created[
            "src/Services/Ordering/Ordering.Application/Ordering.Application.csproj"
        ]
        self.assertNotIn('PackageReference Include="Dapper"', csproj)
        self.assertIn('PackageReference Include="FluentValidation"', csproj)

    def test_the_registration_suite_keeps_the_four_tests_about_the_template(self):
        tests = self.rendered.created["tests/Ordering.Application.Tests/DependencyInjectionTests.cs"]
        for kept in (
            "AddOrderingApplication_registers_the_dispatcher_scoped",
            "AddOrderingApplication_registers_the_system_clock",
            "AddOrderingApplication_registers_the_request_metrics_singleton",
            "AddOrderingApplication_registers_the_three_behaviours_in_pipeline_order",
        ):
            self.assertIn(kept, tests)
        self.assertNotIn("registers_the_slice_handlers", tests)


class TheMigrationAndItsSnapshot(unittest.TestCase):
    def setUp(self):
        self.rendered = render()
        self.prefix = "src/Services/Ordering/Ordering.Infrastructure/Persistence/Migrations"

    def test_it_copies_initial_create_under_a_fresh_id(self):
        migration = f"{self.prefix}/{MIGRATION_ID}_InitialCreate.cs"
        self.assertIn(migration, self.rendered.created)
        self.assertIn('migrationBuilder.EnsureSchema("ordering")', self.rendered.created[migration])

    def test_the_file_name_and_the_migration_attribute_agree(self):
        # EF resolves the id from the attribute and the ordering from the file
        # name; a pair that disagreed would apply under one identity and be
        # recorded under another.
        designer = self.rendered.created[f"{self.prefix}/{MIGRATION_ID}_InitialCreate.Designer.cs"]
        self.assertIn(f'[Migration("{MIGRATION_ID}_InitialCreate")]', designer)

    def test_it_leaves_no_later_catalog_migration_behind(self):
        migrations = [path for path in self.rendered.created if path.startswith(self.prefix)]
        self.assertEqual(3, len(migrations), migrations)

    def test_the_snapshot_describes_an_empty_model_with_the_default_schema(self):
        # Catalog's snapshot cannot be copied — it describes Product, and the
        # next `migrations add` here would generate a drop for a table that
        # never existed. This one is EF's own description of an empty model,
        # lifted from the designer file beside it.
        snapshot = self.rendered.created[f"{self.prefix}/OrderingDbContextModelSnapshot.cs"]
        self.assertIn("partial class OrderingDbContextModelSnapshot : ModelSnapshot", snapshot)
        self.assertIn("protected override void BuildModel(ModelBuilder modelBuilder)", snapshot)
        self.assertIn('.HasDefaultSchema("ordering")', snapshot)
        self.assertNotIn("[Migration(", snapshot)
        self.assertNotIn("modelBuilder.Entity(", snapshot)

    def test_the_machine_owned_files_keep_the_sorted_using_block_ef_writes(self):
        # The rename moves the service's own namespace past Microsoft's in the
        # sort order, and EF writes that block sorted. Left alone, the first
        # real `migrations add` in the new service would reorder it and produce
        # a diff nobody made.
        # Spelt out rather than re-derived, so the assertion is independent of
        # the sort the script applies: this is the order `dotnet ef migrations
        # add` produced against a scaffolded service.
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
                "using Ordering.Infrastructure.Persistence;",
            ],
            f"{self.prefix}/OrderingDbContextModelSnapshot.cs": [
                *efcore,
                *tail,
                "using Ordering.Infrastructure.Persistence;",
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
            f"{self.prefix}/OrderingDbContextModelSnapshot.cs",
        ):
            self.assertTrue(self.rendered.created[machine_owned].startswith("\ufeff"), machine_owned)


class EditsTheSharedFiles(unittest.TestCase):
    def setUp(self):
        self.rendered = render()

    def test_the_solution_gains_a_folder_of_five_and_four_test_entries(self):
        solution = self.rendered.updated["Platform.slnx"]
        self.assertIn('<Folder Name="/src/Services/Ordering/">', solution)
        for layer in ("Api", "Application", "Domain", "Infrastructure", "Migrator"):
            self.assertIn(
                f'<Project Path="src/Services/Ordering/Ordering.{layer}'
                f'/Ordering.{layer}.csproj" />',
                solution,
            )
        for suite in ("Api.Tests", "Application.Tests", "Domain.Tests", "TestSupport"):
            self.assertIn(
                f'<Project Path="tests/Ordering.{suite}/Ordering.{suite}.csproj" />', solution
            )

    def test_the_solution_stays_sorted(self):
        solution = self.rendered.updated["Platform.slnx"]
        tests = re.findall(r'<Project Path="tests/([^"]+)" />', solution)
        self.assertEqual(sorted(tests), tests)
        services = re.findall(r'<Folder Name="/src/Services/([^/]+)/">', solution)
        self.assertEqual(sorted(services), services)

    def test_the_compose_pair_lands_before_the_collector_on_the_requested_port(self):
        compose = self.rendered.updated["deploy/compose/docker-compose.yml"]
        self.assertLess(compose.index("  ordering-migrator:"), compose.index("  otel-collector:"))
        self.assertLess(compose.index("  ordering-api:"), compose.index("  otel-collector:"))
        self.assertIn(f'ports: [ "{PORT}:8080" ]', compose)
        self.assertIn('ports: [ "5102:8080" ]', compose, "Catalog keeps its own port")

    def test_the_compose_pair_keeps_the_two_key_split_of_section_7_1(self):
        compose = self.rendered.updated["deploy/compose/docker-compose.yml"]
        self.assertIn("ConnectionStrings__OrderingMigrator:", compose)
        self.assertIn("ConnectionStrings__Ordering:", compose)
        self.assertIn("condition: service_completed_successfully", compose)

    def test_both_halves_of_the_pair_join_the_excluded_profile(self):
        # §14.1's own rule: every application block added to docker-compose.yml
        # joins this list in the same change, or `up` on the override starts a
        # service the developer is running on the host.
        override = self.rendered.updated["deploy/compose/docker-compose.infra-only.yml"]
        lines = override.replace("\r\n", "\n")
        self.assertIn('  ordering-migrator:\n    profiles: [ "excluded" ]', lines)
        self.assertIn('  ordering-api:\n    profiles: [ "excluded" ]', lines)

    def test_the_ports_readme_gains_one_row(self):
        readme = self.rendered.updated["deploy/compose/README.md"]
        self.assertIn(f"| Ordering API | http://localhost:{PORT} |", readme)

    def test_every_shared_file_keeps_the_line_endings_it_had(self):
        # Not "keeps CRLF": `.gitattributes` forces that on `*.cs` only, so
        # every file edited here is CRLF on Windows and LF on the runner. The
        # rendered text has to follow the checkout, not the author's platform.
        for path, text in self.rendered.updated.items():
            source = (REPO_ROOT / path).read_text(encoding="utf-8", newline="")
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

            rendered = plan(root, "Ordering", PORT, MIGRATION_ID)

            csproj = rendered.created[
                "src/Services/Ordering/Ordering.Application/Ordering.Application.csproj"
            ]
            self.assertNotIn("\r", csproj, "an LF template must render LF")
            program = rendered.created["src/Services/Ordering/Ordering.Api/Program.cs"]
            self.assertNotIn("\n", program.replace("\r\n", ""), "C# is CRLF by attribute")
            self.assertNotIn("\r", rendered.updated["Platform.slnx"])

    def test_a_checkout_whose_files_are_all_crlf(self):
        # The Windows developer machine, where core.autocrlf converts on the
        # way out and every file arrives CRLF.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            self.rewrite(root, "\r\n")

            rendered = plan(root, "Ordering", PORT, MIGRATION_ID)

            for path, text in {**rendered.created, **rendered.updated}.items():
                self.assertNotIn("\n", text.replace("\r\n", ""), path)

    def test_the_marker_follows_the_template_s_own_c_sharp(self):
        # The one generated file with no template beside it to take endings
        # from. It takes C#'s, observed rather than assumed.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            self.rewrite(root, "\n")

            rendered = plan(root, "Ordering", PORT, MIGRATION_ID)

            marker = rendered.created["src/Services/Ordering/Ordering.Domain/AssemblyMarker.cs"]
            self.assertNotIn("\r", marker)


class RefusesToRun(unittest.TestCase):
    def test_a_name_that_is_not_pascal_case(self):
        for name in ("ordering", "Ordering.Api", "order-ing", ""):
            with self.assertRaises(ScaffoldError):
                render(name=name)

    def test_the_template_cannot_be_its_own_copy(self):
        with self.assertRaises(ScaffoldError):
            render(name="Catalog")

    def test_a_service_that_already_exists(self):
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            (root / "src/Services/Ordering").mkdir(parents=True)
            with self.assertRaises(ScaffoldError):
                render(repo_root=root)

    def test_a_port_another_service_already_publishes(self):
        with self.assertRaises(ScaffoldError):
            render(port=5102)

    def test_a_directory_that_is_not_the_repository(self):
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaises(ScaffoldError):
                render(repo_root=Path(directory))

    def test_a_template_file_nobody_classified(self):
        # The hole the straggler check cannot see: a new Catalog folder carries
        # none of the tokens it searches for, so the classification is what
        # forces the decision.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            unclassified = root / "src/Services/Catalog/Catalog.Domain/Categories/Category.cs"
            unclassified.parent.mkdir(parents=True)
            unclassified.write_text("namespace Catalog.Domain.Categories;\r\n", newline="")

            with self.assertRaises(ScaffoldError) as raised:
                render(repo_root=root)
            self.assertIn("not classified", str(raised.exception))

    def test_a_patch_anchor_the_template_no_longer_carries(self):
        # The fragility this design accepts, failing loudly rather than
        # silently producing a service that still names Catalog's slice.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            program = root / "src/Services/Catalog/Catalog.Api/Program.cs"
            program.write_text(
                program.read_text(encoding="utf-8", newline="").replace(
                    "using Catalog.Api.Endpoints;\r\n", ""
                ),
                encoding="utf-8",
                newline="",
            )

            with self.assertRaises(ScaffoldError) as raised:
                render(repo_root=root)
            self.assertIn("Program.cs", str(raised.exception))

    def test_nothing_is_written_when_the_run_refuses(self):
        # The whole render is a value until `apply` is called, which is what
        # makes "no half-scaffolded state" a property rather than a promise.
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            before = sorted(path.relative_to(root).as_posix() for path in root.rglob("*"))

            with self.assertRaises(ScaffoldError):
                plan(root, "Ordering", 5102, MIGRATION_ID)

            after = sorted(path.relative_to(root).as_posix() for path in root.rglob("*"))
            self.assertEqual(before, after)


class Applies(unittest.TestCase):
    def test_it_writes_every_planned_file_and_nothing_else(self):
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            rendered = plan(root, "Ordering", PORT, MIGRATION_ID)

            apply(root, rendered)

            for relative, text in rendered.created.items():
                # utf-8, not utf-8-sig: the byte-order mark is content here,
                # and a reader that swallowed it could not tell the machine-
                # owned migration files from the hand-written ones.
                self.assertEqual(text, (root / relative).read_text(encoding="utf-8", newline=""))
            self.assertFalse((root / "src/Services/Ordering/Ordering.Domain/Products").exists())


if __name__ == "__main__":
    unittest.main()
