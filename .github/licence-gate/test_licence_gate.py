#!/usr/bin/env python3
"""Negative cases for the licence gate.

A gate is only worth having if it fails when it should, and every carve-out in
Section 4.4 is a branch where it deliberately does not. Those branches are what
this file pins down: run against the real repository the gate passes, which
proves nothing about what it would catch.
"""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import licence_gate

ALLOWED = {"MIT", "Apache-2.0", "BSD-3-Clause"}

REGISTER_HEADER = """# Appendix B — Dependency licence register

## Chosen — free for commercial use

| Package | Licence | Role |
|---|---|---|
"""


def register(*rows: str) -> list[tuple[list[str], str]]:
    """Parse a Chosen table built from the given row bodies."""
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "appendix-b-licences.md"
        path.write_text(REGISTER_HEADER + "".join(f"{row}\n" for row in rows), encoding="utf-8")
        return licence_gate.read_register(path)


def pins(*identities: str) -> str:
    """A Directory.Packages.props naming the given packages."""
    entries = "".join(
        f'    <PackageVersion Include="{identity}" Version="1.0.0" />\n' for identity in identities)
    return f"<Project>\n  <ItemGroup>\n{entries}  </ItemGroup>\n</Project>\n"


def pins_doc(body: str) -> str:
    """A Directory.Packages.props whose ItemGroup is the given lines."""
    return f"<Project>\n  <ItemGroup>\n{body}  </ItemGroup>\n</Project>\n"


def compare(props_text: str, chapter_text: str) -> list[str]:
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "04-solution-structure.md"
        path.write_text(chapter_text, encoding="utf-8")
        return licence_gate.compare_sample(props_text, licence_gate.read_chapter_sample(path))


def read_pins(document: str) -> set[str]:
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "Directory.Packages.props"
        path.write_text(document, encoding="utf-8")
        return licence_gate.read_pins(path)


class ReadPins(unittest.TestCase):
    def test_reads_every_pinned_identity(self):
        self.assertEqual(read_pins(pins("Dapper", "Scrutor")), {"Dapper", "Scrutor"})

    def test_refuses_a_props_file_declaring_a_dtd(self):
        bomb = '<!DOCTYPE lolz [<!ENTITY lol "lol">]>\n' + pins("Dapper")
        with self.assertRaises(ValueError):
            read_pins(bomb)


class ReadRegister(unittest.TestCase):
    def test_skips_the_header_and_separator_rows(self):
        self.assertEqual(register("| `Dapper` | Apache 2.0 | Data |"), [(["Dapper"], "Apache 2.0")])

    def test_reads_every_identity_in_a_multi_package_row(self):
        rows = register("| `Grpc.Tools`, `Google.Protobuf` | Apache 2.0 / BSD-3 | gRPC |")
        self.assertEqual(rows, [(["Grpc.Tools", "Google.Protobuf"], "Apache 2.0 / BSD-3")])

    def test_ignores_tables_other_than_chosen(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "appendix-b-licences.md"
            path.write_text(
                REGISTER_HEADER
                + "| `Dapper` | Apache 2.0 | Data |\n"
                + "\n## Avoided — commercial, with the replacement used here\n\n"
                + "| Package | Change | Replacement |\n|---|---|---|\n"
                + "| **MediatR** v13+ | Commercial from 2025 | Hand-rolled dispatcher |\n",
                encoding="utf-8")
            self.assertEqual(licence_gate.read_register(path), [(["Dapper"], "Apache 2.0")])


class Audit(unittest.TestCase):
    def test_passes_when_every_pin_is_registered_and_cleared(self):
        rows = register("| `Dapper` | Apache 2.0 | Data |")
        self.assertEqual(licence_gate.audit({"Dapper"}, rows, ALLOWED), [])

    def test_fails_a_pin_absent_from_the_register(self):
        rows = register("| `Dapper` | Apache 2.0 | Data |")
        findings = licence_gate.audit({"Dapper", "MediatR"}, rows, ALLOWED)
        self.assertEqual(len(findings), 1)
        self.assertIn("MediatR", findings[0])
        self.assertIn("never been cleared", findings[0])

    def test_fails_a_registered_licence_outside_the_allow_list(self):
        rows = register("| `RabbitMQ.Client` | MPL 2.0 | Broker |")
        findings = licence_gate.audit({"RabbitMQ.Client"}, rows, ALLOWED)
        self.assertEqual(len(findings), 1)
        self.assertIn("MPL-2.0", findings[0])
        self.assertIn("outside the allow-list", findings[0])

    def test_clears_a_dual_licence_when_either_half_is_allowed(self):
        rows = register("| `Google.Protobuf` | Apache 2.0 / BSD-3 | gRPC |")
        self.assertEqual(licence_gate.audit({"Google.Protobuf"}, rows, ALLOWED), [])

    def test_fails_a_licence_spelling_the_gate_cannot_map(self):
        # Fail closed. A licence this gate cannot name is one it must not clear.
        rows = register("| `Something` | Business Source Licence | Whatever |")
        findings = licence_gate.audit({"Something"}, rows, ALLOWED)
        self.assertEqual(len(findings), 1)
        self.assertIn("outside the allow-list", findings[0])

    def test_reports_a_registered_identity_that_is_pinned_nowhere(self):
        rows = register("| `Dapper` | Apache 2.0 | Data |")
        findings = licence_gate.audit(set(), rows, ALLOWED)
        self.assertEqual(len(findings), 1)
        self.assertIn("pinned nowhere", findings[0])

    def test_ignores_a_row_that_names_a_product_rather_than_a_package(self):
        # Section 4.4: match on package identity, never on the product a package
        # is named after. Keycloak carries no identity, so it never enters.
        rows = register("| Keycloak | Apache 2.0 | Identity provider |")
        self.assertEqual(licence_gate.audit(set(), rows, ALLOWED), [])

    def test_skips_the_deliberately_unpinned_aspire_rows(self):
        rows = register(
            "| `Aspire.Hosting.*` (`AppHost`, `SqlServer`) | MIT | Topology for the AppHost |",
            "| `Aspire.*` client integrations | MIT | The service-side half |")
        self.assertEqual(licence_gate.audit(set(), rows, ALLOWED), [])

    def test_does_not_report_the_unchosen_half_of_an_either_or_row(self):
        rows = register("| `Shouldly` *or* `AwesomeAssertions` | BSD-3 / Apache 2.0 | Assertions |")
        self.assertEqual(licence_gate.audit({"Shouldly"}, rows, ALLOWED), [])


class CompareSample(unittest.TestCase):
    def sample(self, body: str) -> str:
        return f"## 4.4 Pinning\n\n```xml\n<Project>\n  <PropertyGroup>\n" \
               f"    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n" \
               f"  </PropertyGroup>\n{body}</Project>\n```\n"

    def test_passes_when_the_chapter_prints_what_the_props_file_pins(self):
        body = '    <PackageVersion Include="Dapper" Version="2.1.66" />\n'
        self.assertEqual(compare(pins_doc(body), self.sample(body)), [])

    def test_fails_when_the_chapter_prints_a_different_version(self):
        props = '    <PackageVersion Include="Dapper" Version="2.1.66" />\n'
        stale = '    <PackageVersion Include="Dapper" Version="2.1.35" />\n'
        findings = compare(pins_doc(props), self.sample(stale))
        self.assertEqual(len(findings), 1)
        self.assertIn("props pins 2.1.66", findings[0])
        self.assertIn("prints 2.1.35", findings[0])

    def test_fails_when_the_chapter_omits_a_pin(self):
        props = ('    <PackageVersion Include="Dapper" Version="2.1.66" />\n'
                 '    <PackageVersion Include="Scrutor" Version="6.1.0" />\n')
        findings = compare(pins_doc(props), self.sample(
            '    <PackageVersion Include="Dapper" Version="2.1.66" />\n'))
        self.assertEqual(len(findings), 1)
        self.assertIn("Scrutor", findings[0])
        self.assertIn("prints nothing", findings[0])

    def test_fails_when_the_chapter_has_no_sample_at_all(self):
        findings = compare(pins_doc(""), "")
        self.assertEqual(len(findings), 1)
        self.assertIn("no central package management sample", findings[0])

    def test_finds_the_sample_among_other_xml_blocks(self):
        text = ("```xml\n<Project>\n  <PackageReference Include=\"Nothing\" />\n</Project>\n```\n"
                + self.sample('    <PackageVersion Include="Dapper" Version="2.1.66" />\n'))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "04-solution-structure.md"
            path.write_text(text, encoding="utf-8")
            block = licence_gate.read_chapter_sample(path)
        self.assertIn("ManagePackageVersionsCentrally", block)
        self.assertIn("Dapper", block)


class RealRepository(unittest.TestCase):
    def test_the_repository_passes_its_own_gate(self):
        self.assertEqual(licence_gate.main([]), 0)


if __name__ == "__main__":
    unittest.main()
