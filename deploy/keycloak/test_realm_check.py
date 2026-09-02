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
        self.assertIn("not a number", self.one(realm(client)))

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
        self.assertIn("not a boolean", found[0])

    def test_a_string_false_is_refused_too(self):
        """The right answer in an unjudgeable value. A realm this loose is one to fix."""
        self.assertIn("not a boolean", self.one(realm(browser(implicitFlowEnabled="false"))))

    def test_an_integer_flag_is_refused_and_the_obligation_speaks_too(self):
        """Two problems for one field, and both are true statements about it.

        The type check says the value cannot be compared; the obligation says
        the guarantee is therefore not established. Collapsing them would mean
        choosing which half of that to hide.
        """
        found = self.problems(realm(browser(standardFlowEnabled=1)))
        self.assertEqual(len(found), 2, found)
        self.assertIn("not a boolean", found[0])
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

    def test_an_undeclared_read_is_caught(self):
        """Emptying the declaration must fail, or the check is reading its own copy."""
        original = list(realm_check.SOURCE_INPUTS)
        self.addCleanup(lambda: setattr(realm_check, "SOURCE_INPUTS", original))
        realm_check.SOURCE_INPUTS = []
        found = realm_check.check_source_inputs_covers_reads()
        self.assertTrue(found)
        self.assertTrue(any(realm_check.LIFETIME_SOURCE in problem for problem in found))


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

        root = Path(realm_check.__file__).resolve().parents[2]
        found = []
        for values in sorted((root / "deploy" / "helm").glob("*/values.yaml")):
            text = values.read_text(encoding="utf-8")
            match = re.search(r"^\s*authority:\s*(\S+)\s*$", text, re.MULTILINE)
            if match:
                found.append((values.name, match.group(1)))
        self.assertTrue(found, "no chart declares identity.authority")
        for name, authority in found:
            root_url, realm = realm_check.split_authority(authority)
            self.assertTrue(root_url.startswith("https://"), name)
            self.assertTrue(realm, name)


class TheRealmIsLoaded(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "realm.json"

    def test_a_realm_document_is_loaded(self):
        self.path.write_text(json.dumps(realm()), encoding="utf-8")
        self.assertEqual(realm_check.load_realm(self.path)["realm"], "commerce")

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
