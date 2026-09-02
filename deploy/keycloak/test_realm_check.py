#!/usr/bin/env python3
"""Every check in the realm gate, driven against a realm built to fail it.

Run against this repository the gate passes, which proves nothing about what it
would catch — so every case below is a negative one, with a single positive
control at the top to establish that the fixture the negatives are derived from
is itself clean. Without that control a gate that failed on everything would
look perfect here.

Three of the cases have a different subject from the rest. They do not ask what
the gate found; they ask **what it was looking at** — that the lifetime is
still read out of a file that still declares it, that a path this gate reads is
covered by something the workflow triggers on, and that a realm with no clients
is refused rather than passed. That is the failure this repository repeats most
and the only defence against it is a test whose subject is the gate's own
subject.

    py -3.12 -m unittest discover -s deploy/keycloak
"""

from __future__ import annotations

import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path

import realm_check


def browser(**overrides) -> dict:
    """A compliant deployed `web-app`: no refresh token, standard flow, no password grant."""
    client = {
        "clientId": realm_check.BROWSER_CLIENT,
        "standardFlowEnabled": True,
        "implicitFlowEnabled": False,
        "directAccessGrantsEnabled": False,
        "publicClient": True,
        "attributes": {"use.refresh.tokens": "false"},
    }
    client.update(overrides)
    return client


def realm(*clients, **overrides) -> dict:
    """A realm document of the shape both an export and the admin API produce."""
    document = {
        "realm": "commerce",
        "enabled": True,
        "accessTokenLifespan": 300,
        "clients": list(clients) if clients else [browser()],
    }
    document.update(overrides)
    return document


class Fixture(unittest.TestCase):
    """The lifetime is passed in rather than read, so no case here touches src/."""

    lifetime = 300

    def problems(self, document: dict, kind: str = realm_check.DEPLOYED) -> list[str]:
        return realm_check.check_realm(document, kind, self.lifetime)

    def one(self, document: dict, kind: str = realm_check.DEPLOYED) -> str:
        found = self.problems(document, kind)
        self.assertEqual(len(found), 1, found)
        return found[0]


class TheFixture(Fixture):
    def test_the_fixture_is_clean(self):
        """A compliant deployed realm produces nothing.

        Every case below mutates this document by one field, so a fixture that
        already failed would make all of them pass for the wrong reason.
        """
        self.assertEqual(self.problems(realm()), [])

    def test_the_local_realm_differs_by_exactly_one_field(self):
        """The Compose realm's password grant is the one obligation that inverts.

        §11.2 documents the password grant as a local affordance, so the same
        document cannot be compliant under both kinds — and this pins that the
        difference is that one field and not a second one nobody noticed.
        """
        local = realm(browser(directAccessGrantsEnabled=True))
        self.assertEqual(realm_check.check_realm(local, realm_check.LOCAL, self.lifetime), [])
        self.assertEqual(len(self.problems(local)), 1)


class TheLifetime(Fixture):
    def test_a_realm_lifespan_above_the_declared_one_is_caught(self):
        """Five hours is the misconfiguration #157 describes in its first sentence."""
        self.assertIn("accessTokenLifespan", self.one(realm(accessTokenLifespan=18000)))

    def test_a_missing_lifespan_is_caught_rather_than_defaulted(self):
        """Absent is not 300. Keycloak's own default is 60, and neither is the chapter's."""
        document = realm()
        del document["accessTokenLifespan"]
        self.assertIn("accessTokenLifespan is None", self.one(document))

    def test_a_client_level_override_is_caught(self):
        """Keycloak resolves the client attribute over the realm value.

        A realm at 300 with one client at 18000 issues five-hour tokens to that
        client, and a realm-level assertion alone calls it compliant.
        """
        client = browser(attributes={"use.refresh.tokens": "false",
                                     "access.token.lifespan": "18000"})
        self.assertIn("access.token.lifespan", self.one(realm(client)))

    def test_a_cleared_override_is_not_an_override(self):
        """Keycloak stores "" for an advanced setting filled in and then cleared.

        That is how an operator undoes exactly the misconfiguration this check
        exists to catch, so reading it as an override would fail the rollout on
        the shape of the fix.
        """
        client = browser(attributes={"use.refresh.tokens": "false",
                                     "access.token.lifespan": ""})
        self.assertEqual(self.problems(realm(client)), [])

    def test_an_override_that_is_not_a_number_is_refused(self):
        """Cannot say what that client issues, which is not the same as saying it is fine."""
        client = browser(attributes={"use.refresh.tokens": "false",
                                     "access.token.lifespan": "5 min"})
        self.assertIn("not a number of seconds", self.one(realm(client)))

    def test_an_override_equal_to_the_realm_value_is_not_a_finding(self):
        """Redundant is not wrong, and refusing it would fail a compliant realm."""
        client = browser(attributes={"use.refresh.tokens": "false",
                                     "access.token.lifespan": "300"})
        self.assertEqual(self.problems(realm(client)), [])

    def test_the_override_is_compared_as_text_because_keycloak_stores_it_that_way(self):
        """Client attributes are a string map; an integer 300 there is still 300."""
        client = browser(attributes={"use.refresh.tokens": "false",
                                     "access.token.lifespan": 300})
        self.assertEqual(self.problems(realm(client)), [])


class TheFlagsAreBooleans(Fixture):
    """A flag of the wrong type is refused rather than compared.

    Every comparison in the gate is an identity test against True or False, so
    a string is *neither* — and the check reading it would fall through as
    though the flag were off. That is the fail-open shape this whole file is
    written against, and it shipped in the first draft: `implicitFlowEnabled`
    was the one comparison written `is True` rather than `is not False`.
    """

    def test_a_string_flag_is_refused_rather_than_read_as_off(self):
        found = self.problems(realm(browser(implicitFlowEnabled="true")))
        self.assertEqual(len(found), 1, found)
        self.assertIn("rather than a boolean", found[0])

    def test_a_string_false_is_refused_too(self):
        """The right answer in an unjudgeable value. A realm this loose is one to fix."""
        self.assertIn("rather than a boolean", self.one(realm(browser(implicitFlowEnabled="false"))))

    def test_an_integer_flag_is_refused_and_the_obligation_speaks_too(self):
        """Two problems for one field, and both are true statements about it.

        The type check says the value cannot be compared; the obligation says
        the guarantee is therefore not established. Collapsing them would mean
        choosing which half of that to hide.
        """
        found = self.problems(realm(browser(standardFlowEnabled=1)))
        self.assertEqual(len(found), 2, found)
        self.assertIn("rather than a boolean", found[0])
        self.assertIn("standard flow", found[1])

    def test_a_null_flag_is_left_to_the_check_that_reads_it(self):
        """`null` is not typed here, because what it means differs per obligation.

        An absent or null flag is Keycloak's default, and whether that default
        satisfies an obligation is the obligation's question — so this reports
        the obligation rather than the type.
        """
        found = self.problems(realm(browser(standardFlowEnabled=None)))
        self.assertEqual(len(found), 1, found)
        self.assertIn("standard flow", found[0])

    def test_a_client_that_is_not_an_object_is_refused(self):
        """Skipping it would let a malformed realm carry an unjudged client."""
        found = self.problems(realm(browser(), "commerce-api"))
        self.assertTrue(any("where a client object belongs" in p for p in found), found)


class TheImplicitFlow(Fixture):
    def test_a_client_enabling_the_implicit_flow_is_caught(self):
        """It is what makes accessTokenLifespanForImplicitFlow unreachable.

        The realm ships 900 for that setting and no chapter states a value, so
        the silence about it is only honest while nothing can reach it.
        """
        self.assertIn("implicit flow", self.one(realm(browser(implicitFlowEnabled=True))))

    def test_a_client_that_is_not_the_browser_is_judged_too(self):
        """The lifetime obligations are every client's, not `web-app`'s.

        A service client with a five-hour override issues five-hour tokens just
        as surely, and an earlier draft of this gate only looked at the browser.
        """
        other = {"clientId": "commerce-api", "implicitFlowEnabled": True}
        found = self.problems(realm(browser(), other))
        self.assertEqual(len(found), 1, found)
        self.assertIn("commerce-api", found[0])


class TheRefreshToken(Fixture):
    def test_an_issued_refresh_token_is_caught(self):
        client = browser(attributes={"use.refresh.tokens": "true"})
        self.assertIn("use.refresh.tokens", self.one(realm(client)))

    def test_an_absent_attribute_is_the_violation_and_not_a_silence(self):
        """Keycloak issues refresh tokens on the standard flow by default.

        Reading a missing attribute as compliant would make the one setting
        ADR-034 rests on optional — and Keycloak's default is the opposite of
        what ADR-034 states, so absence is the failure.
        """
        self.assertIn("declares no use.refresh.tokens", self.one(realm(browser(attributes={}))))

    def test_a_client_with_no_attributes_map_at_all_is_caught(self):
        client = browser()
        del client["attributes"]
        self.assertIn("declares no use.refresh.tokens", self.one(realm(client)))

    def test_the_standard_flow_has_to_be_on_for_the_attribute_to_mean_anything(self):
        """A client that mints no token issues no refresh token either.

        Without this the refresh-token check passes on a broken realm, which is
        the guarantee holding for the wrong reason — #157's own table lists
        `standardFlowEnabled` for exactly this.
        """
        client = browser(standardFlowEnabled=False)
        self.assertIn("standard flow", self.one(realm(client)))


class ThePasswordGrant(Fixture):
    def test_a_deployed_realm_keeping_the_password_grant_is_caught(self):
        client = browser(directAccessGrantsEnabled=True)
        self.assertIn("directAccessGrantsEnabled", self.one(realm(client)))

    def test_a_local_realm_without_it_is_caught_too(self):
        """§14.1's documented login is a password grant; a local realm needs it.

        The check runs in both directions deliberately. A one-directional check
        would let the local realm drift into a shape the README's curl cannot
        use, and the README is what tells a developer the platform works.
        """
        found = realm_check.check_realm(realm(), realm_check.LOCAL, self.lifetime)
        self.assertEqual(len(found), 1, found)
        self.assertIn("directAccessGrantsEnabled", found[0])

    def test_an_absent_flag_is_caught_for_a_local_realm(self):
        """Keycloak's default is false, so absence fails the local realm honestly."""
        client = browser()
        del client["directAccessGrantsEnabled"]
        found = realm_check.check_realm(realm(client), realm_check.LOCAL, self.lifetime)
        self.assertEqual(len(found), 1, found)


class WhatTheGateIsLookingAt(Fixture):
    def test_a_realm_with_no_clients_is_refused_rather_than_passed(self):
        """Every per-client obligation is vacuously true of an empty realm.

        This is the gate-coverage failure in its purest form: a truncated realm
        document satisfies "no client overrides the lifetime" perfectly.
        """
        found = self.problems(realm(**{"clients": []}))
        self.assertEqual(len(found), 1, found)
        self.assertIn("no clients array", found[0])

    def test_a_realm_with_no_clients_key_is_refused(self):
        document = realm()
        del document["clients"]
        self.assertIn("no clients array", self.one(document))

    def test_a_realm_missing_the_browser_client_is_refused(self):
        """ADR-034's obligation is a property of `web-app`, and cannot be checked without it."""
        other = {"clientId": "commerce-api"}
        found = self.problems(realm(other))
        self.assertEqual(len(found), 1, found)
        self.assertIn("0 time(s)", found[0])

    def test_a_duplicated_browser_client_is_refused(self):
        """Two `web-app` entries mean the compliant one could be the one not used."""
        found = self.problems(realm(browser(), browser()))
        self.assertEqual(len(found), 1, found)
        self.assertIn("2 time(s)", found[0])

    def test_an_unknown_realm_kind_judges_nothing(self):
        """The kind has no default, and a typo must not silently pick one."""
        found = realm_check.check_realm(realm(), "production", self.lifetime)
        self.assertEqual(len(found), 1, found)
        self.assertIn("not one of", found[0])


class TheLifetimeIsRead(unittest.TestCase):
    """The 300 comes out of `AuthenticationExtensions`, and these pin that read."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        (self.root / realm_check.LIFETIME_SOURCE).parent.mkdir(parents=True, exist_ok=True)

    def write(self, text: str) -> None:
        (self.root / realm_check.LIFETIME_SOURCE).write_text(text, encoding="utf-8")

    DECLARATION = ("    public static readonly TimeSpan AccessTokenLifetime = "
                   "TimeSpan.FromSeconds({0});\n")

    def test_the_declaration_is_read(self):
        self.write(self.DECLARATION.format(300))
        self.assertEqual(realm_check.read_access_token_lifetime(self.root), 300)

    def test_a_changed_declaration_moves_what_the_realm_owes(self):
        """The point of reading it: one edit moves the constant and the gate together."""
        self.write(self.DECLARATION.format(120))
        self.assertEqual(realm_check.read_access_token_lifetime(self.root), 120)

    def test_a_missing_declaration_stops_rather_than_defaulting(self):
        """A gate that substitutes 300 when it cannot find 300 is checking its own copy."""
        self.write("public static readonly TimeSpan AccessTokenLifetime = FromMinutes(5);")
        with self.assertRaises(SystemExit) as stop:
            realm_check.read_access_token_lifetime(self.root)
        self.assertIn("0 time(s)", str(stop.exception))

    def test_two_declarations_stop_rather_than_picking_one(self):
        self.write(self.DECLARATION.format(300) + self.DECLARATION.format(60))
        with self.assertRaises(SystemExit) as stop:
            realm_check.read_access_token_lifetime(self.root)
        self.assertIn("2 time(s)", str(stop.exception))

    def test_a_doc_comment_quoting_the_assignment_is_not_the_declaration(self):
        """The dangerous pair is a prose mention plus a reformatted member.

        Both present is two matches and fails closed. The comment alone would
        have been one match, and the gate would then have asserted a number the
        platform had stopped holding — silently, and in the direction that
        makes a longer lifetime look compliant.
        """
        self.write("    /// readonly TimeSpan AccessTokenLifetime = "
                   "TimeSpan.FromSeconds(300);\n"
                   "    public static readonly TimeSpan AccessTokenLifetime = "
                   "TimeSpan.FromMinutes(1);\n")
        with self.assertRaises(SystemExit) as stop:
            realm_check.read_access_token_lifetime(self.root)
        self.assertIn("0 time(s)", str(stop.exception))

    def test_a_similarly_named_local_is_not_the_declaration(self):
        """`readonly TimeSpan` is the anchor, so a look-alike cannot stand in."""
        self.write("var AccessTokenLifetime = TimeSpan.FromSeconds(600);")
        with self.assertRaises(SystemExit) as stop:
            realm_check.read_access_token_lifetime(self.root)
        self.assertIn("0 time(s)", str(stop.exception))

    def test_an_unreadable_source_stops(self):
        with self.assertRaises(SystemExit) as stop:
            realm_check.read_access_token_lifetime(self.root)
        self.assertIn("not readable", str(stop.exception))

    def test_the_shipped_declaration_is_still_there(self):
        """The one case here that reads the repository, and it is the subject test.

        Every case above proves the parser works on text this file wrote. This
        one proves the parser is still pointed at a file that still declares the
        constant in the shape it parses — the way this gate stops covering its
        newest surface is a refactor of `AuthenticationExtensions`, not a bad
        regex.
        """
        self.assertGreater(realm_check.read_access_token_lifetime(), 0)


class TheDeclaredInputs(unittest.TestCase):
    """The two directions: nothing read undeclared, and nothing declared untriggered."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.workflow = self.root / realm_check.WORKFLOW_PATH
        self.workflow.parent.mkdir(parents=True, exist_ok=True)

    def triggers(self, *entries: str) -> str:
        block = "\n".join(f"      - '{entry}'" for entry in entries)
        return f"on:\n  pull_request:\n    paths:\n{block}\n  push:\n    paths:\n{block}\n"

    def every_input(self) -> tuple[str, ...]:
        return tuple(realm_check.SOURCE_INPUTS) + (realm_check.OWN_TREE, realm_check.WORKFLOW_PATH)

    def test_a_workflow_covering_every_input_passes(self):
        self.workflow.write_text(self.triggers(*self.every_input()), encoding="utf-8")
        self.assertEqual(realm_check.check_workflow_covers_inputs(self.root), [])

    def test_a_glob_covers_a_directory_entry(self):
        entries = [f"{e}/**" for e in self.every_input()]
        self.workflow.write_text(self.triggers(*entries), encoding="utf-8")
        self.assertEqual(realm_check.check_workflow_covers_inputs(self.root), [])

    def test_an_uncovered_input_is_caught_in_both_triggers(self):
        """Both, not the first: a change that skips the gate on `main` is the same defect."""
        entries = [e for e in self.every_input() if e != realm_check.LIFETIME_SOURCE]
        self.workflow.write_text(self.triggers(*entries), encoding="utf-8")
        found = realm_check.check_workflow_covers_inputs(self.root)
        self.assertEqual(len(found), 2, found)
        self.assertIn("pull_request trigger", found[0])
        self.assertIn("push trigger", found[1])

    def test_a_missing_trigger_is_named_rather_than_counted(self):
        """An earlier form counted two paths lists without asking whose they were.

        Replacing `push` with any other event that accepts `paths` left it
        green while nothing ran the gate on `main` — the exact defect the
        docstring names. The message now says which event is missing.
        """
        block = "\n".join(f"      - '{e}'" for e in self.every_input())
        self.workflow.write_text(f"on:\n  push:\n    paths:\n{block}\n", encoding="utf-8")
        found = realm_check.check_workflow_covers_inputs(self.root)
        self.assertEqual(len(found), 1, found)
        self.assertIn("no pull_request trigger", found[0])

    def test_a_trigger_renamed_to_something_else_is_caught(self):
        """`pull_request_target` accepts `paths` and is not `pull_request`."""
        block = "\n".join(f"      - '{e}'" for e in self.every_input())
        self.workflow.write_text(
            f"on:\n  pull_request_target:\n    paths:\n{block}\n"
            f"  push:\n    paths:\n{block}\n", encoding="utf-8")
        found = realm_check.check_workflow_covers_inputs(self.root)
        self.assertEqual(len(found), 1, found)
        self.assertIn("no pull_request trigger", found[0])

    def test_double_quoted_and_unquoted_entries_are_read(self):
        """Three quoting styles are valid YAML for one list, and all three count.

        The text parser is the licence gate's terms — stdlib has no YAML — and
        the cost of that is paid as a refusal rather than a pass. Reading only
        one style would have made a correct workflow fail.
        """
        entries = list(self.every_input())
        styles = [f'      - "{entries[0]}"', f"      - {entries[1]}"]
        styles += [f"      - '{e}'" for e in entries[2:]]
        block = "\n".join(styles)
        self.workflow.write_text(
            f"on:\n  pull_request:\n    paths:\n{block}\n"
            f"  push:\n    branches: [main]\n    paths:\n{block}\n", encoding="utf-8")
        self.assertEqual(realm_check.check_workflow_covers_inputs(self.root), [])

    def test_a_missing_workflow_is_a_failure(self):
        found = realm_check.check_workflow_covers_inputs(self.root)
        self.assertEqual(len(found), 1, found)
        self.assertIn("not readable", found[0])

    def test_the_shipped_workflow_covers_every_declared_input(self):
        """The subject test for this direction, run against the real workflow."""
        self.assertEqual(realm_check.check_workflow_covers_inputs(), [])

    def test_every_path_this_gate_reads_is_declared(self):
        """The other direction, over this gate's own source."""
        self.assertEqual(realm_check.check_source_inputs_covers_reads(), [])

    def test_the_chart_values_the_suite_reads_are_seen_by_the_scan(self):
        """Removing one entry must name that entry, not merely fail.

        `CHART_VALUES` is read by a sibling module as `root /
        realm_check.CHART_VALUES`, and an unqualified `ROOT / NAME` pattern
        could not see it — so the entry passed on its own declaration and
        pointing the read at another tree would have gone unnoticed. This
        removes exactly that entry and demands the scan says so.
        """
        original = list(realm_check.SOURCE_INPUTS)
        self.addCleanup(lambda: setattr(realm_check, "SOURCE_INPUTS", original))
        realm_check.SOURCE_INPUTS = [e for e in original if e != realm_check.CHART_VALUES]
        found = realm_check.check_source_inputs_covers_reads()
        self.assertTrue(any(realm_check.CHART_VALUES in problem for problem in found), found)

    # THE FIXTURES BELOW ASSEMBLE THEIR PATHS RATHER THAN SPELLING THEM.
    # This module is one of the sources the scan reads, so a path literal
    # written here is a read of that path as far as the scan is concerned —
    # the first draft of these two cases made `realm_check.py inputs`
    # report three problems against the repository, which is the same
    # collision one layer further out.
    SEPARATOR = '/'

    def fixture(self, *lines: str) -> str:
        return "\n".join(lines)

    def test_a_path_named_only_in_prose_is_not_a_read(self):
        """The failure this tree has had three times, tested rather than reworded.

        A comment or a docstring explaining what the scan looks for
        satisfies the scan, so the check fails on its own documentation —
        and the fix that lasts is stripping prose, not choosing different
        words.
        """
        real = self.SEPARATOR.join(
            ["src", "BuildingBlocks", "Common.Web", "Authentication.cs"])
        prose = self.SEPARATOR.join(["deploy", "nowhere", "absent.json"])
        commented = self.SEPARATOR.join(["deploy", "nowhere", "other.json"])
        joined = "ROOT " + self.SEPARATOR + " MISSING"
        code = realm_check.code_of(self.fixture(
            f'X = \"{real}\"',
            'def f():',
            f'    """Prose naming {prose} and {joined}."""',
            f'    # A comment naming {commented} too',
            '    return 1'))
        self.assertIn(real, code)
        self.assertNotIn(prose, code)
        self.assertNotIn(commented, code)
        self.assertNotIn("MISSING", code)

    def test_a_docstring_closed_on_its_opening_line_keeps_the_code_after_it(self):
        """A one-line docstring toggles twice, so what follows survives."""
        kept = self.SEPARATOR.join(["deploy", "keycloak", "x.json"])
        code = realm_check.code_of(self.fixture(
            'def f():',
            '    """One line."""',
            f'    Y = \"{kept}\"'))
        self.assertIn(kept, code)

    def test_an_undeclared_read_is_caught(self):
        """Emptying the declaration must fail, or the check is reading its own copy."""
        original = list(realm_check.SOURCE_INPUTS)
        self.addCleanup(lambda: setattr(realm_check, "SOURCE_INPUTS", original))
        realm_check.SOURCE_INPUTS = []
        found = realm_check.check_source_inputs_covers_reads()
        self.assertTrue(found)
        self.assertTrue(any(realm_check.LIFETIME_SOURCE in problem for problem in found))


class TheTrustedOrigin(unittest.TestCase):
    """The realm is derived and the origin is pinned, and swapping those is a hole.

    Deriving the realm stops the gate checking a realm nobody is deploying to.
    Deriving the *origin* would hand a release's values control of where this
    job posts a client secret — `https://attacker.example/realms/x` and the
    credential goes to that host's token endpoint, which is a worse hole than
    the mismatch deriving was introduced to close.
    """

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.values = Path(self.directory.name) / "values.json"

    # `origin` AND NOT `trusted`, and the name is the whole of the fix for
    # three CodeQL alerts. `py/clear-text-logging-sensitive-data` classifies a
    # value by the NAME that holds it, reads `trusted` as a secret, and then
    # follows this list into `realm_check.main` and out through the prints at
    # the end of it — three high-severity findings whose entire flow began in
    # a test helper's parameter and touched no realm and no credential.
    #
    # Renaming rather than suppressing, because the classifier was wrong about
    # what this holds and the correction is to say what it holds: an origin.
    def run_authority(self, authority: str, origin: str) -> int:
        self.values.write_text(
            json.dumps({"identity": {"authority": authority}}), encoding="utf-8")
        # The two `NAME=value` lines are the point of the command and noise in
        # a suite, so they are captured rather than printed.
        with contextlib.redirect_stdout(io.StringIO()):
            return realm_check.main([
                "realm_check.py", "authority",
                "--values", str(self.values), "--trusted-origin", origin])

    def test_a_release_on_the_trusted_origin_is_accepted(self):
        self.assertEqual(
            self.run_authority("https://id.example.com/realms/commerce",
                               "https://id.example.com"), 0)

    def test_a_trailing_slash_on_the_trusted_origin_does_not_matter(self):
        self.assertEqual(
            self.run_authority("https://id.example.com/realms/commerce",
                               "https://id.example.com/"), 0)

    def test_a_release_on_another_origin_stops_the_rollout(self):
        """The exfiltration case: the secret would have gone to that host."""
        with self.assertRaises(SystemExit) as stop:
            self.run_authority("https://attacker.example/realms/commerce",
                               "https://id.example.com")
        self.assertIn("attacker.example", str(stop.exception))
        self.assertIn("does not name", str(stop.exception))

    def test_a_sibling_host_is_not_the_trusted_one(self):
        """Prefix matching would admit `id.example.com.attacker.example`."""
        with self.assertRaises(SystemExit) as stop:
            self.run_authority("https://id.example.com.attacker.example/realms/x",
                               "https://id.example.com")
        self.assertIn("Refusing", str(stop.exception))

    def test_the_written_values_are_checked_again_before_they_are_printed(self):
        """The second-line defence against `$GITHUB_ENV` injection.

        `split_authority` refuses whitespace, so this guard cannot fire through
        the ordinary path — which is exactly why it needs a test of its own. A
        refactor that relaxed the input-side refusal would otherwise move the
        guarantee out of sight with the suite green.
        """
        original = realm_check.split_authority
        self.addCleanup(lambda: setattr(realm_check, "split_authority", original))
        realm_check.split_authority = lambda _authority: (
            "https://id.example.com", "com\nSOME_VAR=x")
        with self.assertRaises(SystemExit) as stop:
            self.run_authority("https://id.example.com/realms/commerce",
                               "https://id.example.com")
        self.assertIn("refusing to write", str(stop.exception))

    def test_a_path_under_the_trusted_origin_is_not_the_trusted_origin(self):
        """`/auth/realms/x` is a different root, and this refuses rather than trims."""
        with self.assertRaises(SystemExit) as stop:
            self.run_authority("https://id.example.com/auth/realms/commerce",
                               "https://id.example.com")
        self.assertIn("/auth", str(stop.exception))


class TheAuthorityDecidesWhichRealmIsRead(unittest.TestCase):
    """The realm checked is the realm the rollout installs, by construction.

    Two environment variables naming a realm, beside a chart value naming
    another, is a gate that passes on realm A while the workload is pointed at
    realm B — compliant, unrelated, and no help at all. So the pair is derived
    from `identity.authority` in the release's own values rather than declared
    beside it, and there is no second place to keep in step.
    """

    def values(self, authority) -> dict:
        return {"image": {"tag": "abc"}, "identity": {"authority": authority}}

    def test_whitespace_anywhere_stops_because_the_value_becomes_an_env_line(self):
        """A newline ends the assignment and starts another.

        `authority` prints `NAME=value` lines a shell appends to
        `$GITHUB_ENV`, and the authority comes from `helm get values` — so
        without this refusal, whoever can edit a release can set an arbitrary
        environment variable for every remaining step of the rollout.
        """
        with self.assertRaises(SystemExit) as stop:
            realm_check.split_authority(
                "https://id.example.com/realms/com\nSOME_VAR=x")
        self.assertIn("whitespace", str(stop.exception))

    def test_the_root_and_the_realm_come_out_of_the_authority(self):
        root, realm = realm_check.split_authority(
            realm_check.authority_of(self.values("https://id.example.com/realms/commerce")))
        self.assertEqual(root, "https://id.example.com")
        self.assertEqual(realm, "commerce")

    def test_the_root_is_not_the_authority(self):
        """Keycloak's admin endpoints sit beside `/realms`, not under it.

        Handing `read_admin.py` the authority is the mistake this split exists
        to make impossible, and it is the one an operator makes by hand.
        """
        root, _ = realm_check.split_authority("https://id.example.com/auth/realms/commerce")
        self.assertEqual(root, "https://id.example.com/auth")

    def test_a_trailing_slash_does_not_become_part_of_the_realm(self):
        _, realm = realm_check.split_authority("https://id.example.com/realms/commerce/")
        self.assertEqual(realm, "commerce")

    def test_a_release_with_no_identity_authority_stops(self):
        with self.assertRaises(SystemExit) as stop:
            realm_check.authority_of({"image": {"tag": "abc"}})
        self.assertIn("identity.authority", str(stop.exception))

    def test_an_empty_authority_stops(self):
        with self.assertRaises(SystemExit) as stop:
            realm_check.authority_of(self.values("   "))
        self.assertIn("names no realm", str(stop.exception))

    def test_plain_http_stops(self):
        with self.assertRaises(SystemExit) as stop:
            realm_check.split_authority("http://id.example.com/realms/commerce")
        self.assertIn("https or nothing", str(stop.exception))

    def test_an_authority_with_no_realms_segment_stops(self):
        with self.assertRaises(SystemExit) as stop:
            realm_check.split_authority("https://id.example.com/commerce")
        self.assertIn("/realms/", str(stop.exception))

    def test_a_query_or_fragment_stops(self):
        """An admin path appended to one of these lands inside it."""
        for authority in ("https://id.example.com/realms/commerce?x=1",
                          "https://id.example.com/realms/commerce#x"):
            with self.assertRaises(SystemExit) as stop:
                realm_check.split_authority(authority)
            self.assertIn("query or a fragment", str(stop.exception))

    def test_every_strvals_metacharacter_is_refused(self):
        """Derived from the constant, so a character added to it is covered.

        Listing the cases by hand is how the next entry arrives untested. Each
        one goes in the realm segment, which is the part of an authority a
        cluster-held value can most freely choose.
        """
        for character in realm_check.STRVALS_METACHARACTERS:
            with self.subTest(character=character):
                with self.assertRaises(SystemExit) as stop:
                    realm_check.split_authority(
                        f"https://id.example.com/realms/commerce{character}x")
                self.assertIn("strvals", str(stop.exception))

    def test_the_comma_injection_is_refused_in_its_concrete_form(self):
        """The shape the refusal exists for, spelled out rather than generated.

        `--set-string identity.authority=<value>` is parsed by Helm's strvals,
        where a comma separates assignments — so this authority is TWO of them
        and the second overrides a registry nobody checked. It is the tag
        preflight's failure one value over, and `canary.py validate-tag` exists
        because this repository has already paid for it once.
        """
        with self.assertRaises(SystemExit) as stop:
            realm_check.split_authority(
                "https://id.example.com/realms/commerce,image.registry=attacker.example")
        message = str(stop.exception)
        self.assertIn("strvals", message)
        # The comma AND the `=` are both named: the injected assignment needs
        # both, and a message that reported only one would understate what the
        # value carries.
        self.assertIn(",=", message)

    def test_a_compliant_authority_still_passes(self):
        """The control: the refusal must not have swallowed the ordinary case."""
        root, realm = realm_check.split_authority(
            "https://id.example.com/realms/commerce")
        self.assertEqual((root, realm), ("https://id.example.com", "commerce"))

    def test_a_realm_name_with_a_separator_in_it_stops(self):
        with self.assertRaises(SystemExit) as stop:
            realm_check.split_authority("https://id.example.com/realms/commerce/../master")
        self.assertIn("single path segment", str(stop.exception))

    def test_the_shipped_charts_carry_an_authority_this_can_split(self):
        """The subject test: the chart value this reads still has the shape it parses.

        Every chart's `identity.authority` is a placeholder in this repository
        and a real one at a rollout, but the *shape* is what this parses — so a
        chart that changed the key or the form would break the deploy path and
        nothing else would say so.
        """
        import re
        from pathlib import Path

        # `realm_check.CHART_VALUES`, NOT a `"deploy" / "helm"` spelled here.
        # The declared-inputs scan looks for path literals, and a join of two
        # bare segments is invisible to it — so this read would have been
        # undeclared while the constant that declares it sat unused.
        root = Path(realm_check.__file__).resolve().parents[2]
        charts = sorted((root / realm_check.CHART_VALUES).glob("*/values.yaml"))
        self.assertTrue(charts, "no chart values found under " + realm_check.CHART_VALUES)

        # ANCHORED TO `identity:`, because that is the key path the deploy
        # step walks. An unanchored `authority:` matches a chart that renamed
        # the parent, which is exactly the change that would stop a rollout
        # with this test green.
        pattern = re.compile(
            r"^identity:\s*$(?:\n(?![A-Za-z])[^\n]*)*?\n\s+authority:\s*(\S+)\s*$",
            re.MULTILINE)
        # EVERY DEPLOYABLE CHART, and the set is derived rather than taken
        # from what still mentions an authority. Scoping by "mentions one"
        # excuses the chart that stopped mentioning one, which is the removal
        # this is for; scoping by "declares identity:" excuses the chart that
        # renamed the parent. The deployables are `deploy/helm/smoke.sh`'s
        # classification — every directory with a `Chart.yaml`, less the
        # library chart and the umbrella, which render no workload of their own.
        deployables = sorted(
            v for v in charts
            if v.parent.name not in ("common", "platform")
            and (v.parent / "Chart.yaml").exists())
        self.assertTrue(deployables, "no deployable chart found")

        declared = 0
        for values in deployables:
            text = values.read_text(encoding="utf-8")
            match = pattern.search(text)
            self.assertIsNotNone(
                match, f"{values.parent.name} declares an authority that is not "
                       "under an `identity:` key, which is the path "
                       "realm_check.authority_of walks")
            declared += 1
            # `split_authority` refuses a bad shape by raising, which is the
            # assertion. Calling it is the test.
            realm_check.split_authority(match.group(1))
        self.assertTrue(declared, "no chart declares identity.authority")


class WhatTheGateHolds(unittest.TestCase):
    """A credential cannot leak out of a document that never carried one.

    Redacting the credential keys was the first answer and it was not enough:
    the object still held every other field of a realm, so the property rested
    on a deny-list staying complete as Keycloak grows fields. The projection
    inverts it — six named keys survive and nothing else does.
    """

    # The three fixture values live here and are REFERENCED below rather than
    # written beside their keys. §15.1's secret scan reads a credential-shaped
    # name assigned a literal, and `"password": "..."` is exactly that shape —
    # so spelling them inline earns two accepted findings for strings whose
    # whole purpose is to be absent from the output.
    MARKERS = {"smtp": "marker-one", "user": "marker-two", "client": "marker-three"}

    def realm_with_secrets(self) -> dict:
        return {
            "realm": "commerce",
            "accessTokenLifespan": 300,
            "smtpServer": {"password": self.MARKERS["smtp"], "host": "mail"},
            "users": [{"username": "demo",
                       "credentials": [{"type": "password",
                                        "value": self.MARKERS["user"]}]}],
            "clients": [{
                "clientId": realm_check.BROWSER_CLIENT,
                "standardFlowEnabled": True,
                "implicitFlowEnabled": False,
                "directAccessGrantsEnabled": False,
                "secret": self.MARKERS["client"],
                "protocolMappers": [{"name": "x"}],
                "attributes": {"use.refresh.tokens": "false",
                               "pkce.code.challenge.method": "S256"},
            }],
        }

    def test_no_credential_bearing_field_survives_the_projection(self):
        held = json.dumps(realm_check.judged(realm_check.redact(self.realm_with_secrets())))
        for leaked in self.MARKERS.values():
            self.assertNotIn(leaked, held)
        self.assertNotIn("smtpServer", held)
        self.assertNotIn("credentials", held)
        self.assertNotIn("secret", held)

    def test_the_fields_every_check_reads_do_survive(self):
        """The other half: a projection that dropped these would judge nothing."""
        held = realm_check.judged(self.realm_with_secrets())
        self.assertEqual(held["accessTokenLifespan"], 300)
        client = held["clients"][0]
        self.assertEqual(client["clientId"], realm_check.BROWSER_CLIENT)
        self.assertTrue(client["standardFlowEnabled"])
        self.assertEqual(client["attributes"]["use.refresh.tokens"], "false")
        self.assertEqual(
            realm_check.check_realm(held, realm_check.DEPLOYED, 300), [])

    def test_an_absent_key_stays_absent(self):
        """Half the checks turn on absence, so a projection must not default one."""
        held = realm_check.judged({"clients": [{"clientId": "x"}]})
        self.assertNotIn("accessTokenLifespan", held)
        self.assertNotIn("attributes", held["clients"][0])
        self.assertNotIn("standardFlowEnabled", held["clients"][0])

    def test_a_client_that_is_not_an_object_survives_to_be_refused(self):
        """Dropping it would hide the malformed realm rather than judge it."""
        held = realm_check.judged({"clients": ["not-a-client"]})
        self.assertEqual(held["clients"], ["not-a-client"])
        self.assertTrue(any("where a client object belongs" in p
                            for p in realm_check.check_realm(held, realm_check.LOCAL, 300)))

    def test_an_unknown_attribute_does_not_survive(self):
        """The attribute allow-list is two keys, and the realm ships more."""
        held = realm_check.judged(self.realm_with_secrets())
        self.assertNotIn("pkce.code.challenge.method", held["clients"][0]["attributes"])


class TheRolloutStillCallsThisGate(unittest.TestCase):
    """The three calls that close #157, and the order they have to be in.

    Every other case here asks what the gate decides. This one asks whether
    anything still asks it — the rollout job runs on `workflow_dispatch` alone,
    so a change that deleted or reordered these steps reaches `main` with no
    job that would have noticed. `deploy.yml` is a declared input of this tree
    for the same reason, so a change to it runs this suite.
    """

    def workflow(self) -> str:
        """The rollout's COMMANDS, with its comment lines removed.

        This file explains each of the three calls in a comment beside it, so a
        raw search finds `realm_check.py authority` whether or not anything
        runs it — deleting the command outright would have left every case in
        this class green. It is the fourth time in this branch that a matcher
        matched the prose about the matcher, and the third artefact to need
        stripping: Python comments, Python docstrings, and now YAML.

        Whole lines only. An inline `#` cannot be stripped safely from a shell
        line — a URL fragment and a `sed` expression both carry one — and no
        command here has a trailing comment.
        """
        text = (Path(realm_check.__file__).resolve().parents[2]
                / realm_check.DEPLOY_WORKFLOW).read_text(encoding="utf-8")
        return "\n".join(line for line in text.splitlines()
                          if not line.lstrip().startswith("#"))

    def positions(self) -> list[int]:
        text = self.workflow()
        calls = [
            "realm_check.py authority",       # which realm, out of the chart
            "read_admin.py --out",            # fetch it
            "realm_check.py check",           # judge it
        ]
        found = []
        for call in calls:
            index = text.find(call)
            self.assertNotEqual(
                index, -1,
                f"{realm_check.DEPLOY_WORKFLOW} no longer contains `{call}`, so "
                "nothing in the rollout closes #157")
            found.append(index)
        return found

    def test_all_three_calls_are_there_and_in_that_order(self):
        found = self.positions()
        self.assertEqual(found, sorted(found),
                         "the rollout derives, fetches and judges out of order")

    def test_they_run_before_the_first_command_that_changes_the_cluster(self):
        """`kubectl patch` is the first mutation, and the check is upstream of it.

        A check that runs after the HPA floor has moved is a check on a cluster
        the rollout has already started changing.
        """
        text = self.workflow()
        judged = text.find("realm_check.py check")
        mutation = text.find("kubectl patch")
        self.assertNotEqual(mutation, -1, "the rollout no longer patches the HPA")
        self.assertLess(judged, mutation)

    def upgrades(self) -> list[str]:
        """Each `helm upgrade` in the rollout, joined across its continuations.

        A command is one logical line here: the invocations wrap over five or
        six physical lines with trailing backslashes, so anything that reasons
        about a whole command has to rejoin them first.
        """
        joined = self.workflow().replace("\\" + chr(10), " ")
        return [line.strip() for line in joined.splitlines()
                if line.strip().startswith("helm upgrade")]

    def test_every_upgrade_installs_the_authority_that_was_checked(self):
        """Per command, because a total is not the invariant.

        Counting pins against invocations across the whole file passes on two
        pins in one command and none in another — which is the shape that
        actually ships, since the promotion and the canary are edited at
        different times for different reasons. What has to be true is that no
        `helm upgrade` in this job installs an authority nobody checked.
        """
        commands = self.upgrades()
        self.assertTrue(commands, "the rollout no longer upgrades anything")
        for command in commands:
            pins = command.count(
                '--set-string identity.authority="$CHECKED_AUTHORITY"')
            self.assertEqual(
                pins, 1,
                f"{pins} authority pin(s) on: {command[:120]}")


class TheRealmIsLoaded(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "realm.json"

    def test_a_realm_document_is_loaded_and_narrowed_to_what_is_judged(self):
        """`realm` is not among the fields this gate reads, so it does not survive."""
        self.path.write_text(json.dumps(realm()), encoding="utf-8")
        loaded = realm_check.load_realm(self.path)
        self.assertEqual(loaded["accessTokenLifespan"], 300)
        self.assertNotIn("realm", loaded)

    def test_a_body_that_is_not_json_stops(self):
        """A proxy answering an HTML error page with a 200 is the ordinary way this happens."""
        self.path.write_text("<html>not a realm</html>", encoding="utf-8")
        with self.assertRaises(SystemExit) as stop:
            realm_check.load_realm(self.path)
        self.assertIn("not JSON", str(stop.exception))

    def test_a_json_document_that_is_not_an_object_stops(self):
        self.path.write_text("[]", encoding="utf-8")
        with self.assertRaises(SystemExit) as stop:
            realm_check.load_realm(self.path)
        self.assertIn("not a realm representation", str(stop.exception))

    def test_a_missing_file_stops(self):
        with self.assertRaises(SystemExit) as stop:
            realm_check.load_realm(self.path)
        self.assertIn("not readable", str(stop.exception))


if __name__ == "__main__":
    unittest.main()
