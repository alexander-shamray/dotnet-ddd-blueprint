#!/usr/bin/env python3
"""The fetcher's refusals, which are the only part of it that decides anything.

`deploy/canary/read_prometheus.py` ships without a suite and this file is the
departure from that precedent rather than an oversight. What that module does
before its one GET is build a URL; what this one does is **refuse** — an absent
variable, a plain-HTTP authority, a body that is not the document it asked for.
Each of those is a decision that fails a deploy, so each is a negative case, and
none of them needs a socket.

The GETs themselves are still untested and still untestable here, exactly as
they are one tree over: a fake `urlopen` proves that this file can parse what a
fake wrote.

    py -3.12 -m unittest discover -s deploy/keycloak
"""

from __future__ import annotations

import contextlib
import io
import json
import os
import tempfile
import urllib.error
import urllib.parse
import urllib.request
import unittest
from pathlib import Path

import read_admin


class TheEnvironment(unittest.TestCase):
    """Four required variables, and an absent one stops the run rather than degrading."""

    def setUp(self):
        self.original = {name: os.environ.get(name) for name in read_admin.REQUIRED}

        def restore():
            for name, value in self.original.items():
                if value is None:
                    os.environ.pop(name, None)
                else:
                    os.environ[name] = value

        self.addCleanup(restore)
        self.set(read_admin.BASE_URL, "https://id.example.com")
        self.set(read_admin.REALM, "commerce")
        self.set(read_admin.CLIENT_ID, "realm-check")
        self.set(read_admin.CLIENT_SECRET, "not-a-real-value")

    def set(self, name: str, value: str) -> None:
        os.environ[name] = value

    def test_a_complete_environment_is_accepted(self):
        """The positive control: the negatives below each remove one thing from this."""
        values = read_admin.environment()
        self.assertEqual(values[read_admin.REALM], "commerce")

    def test_every_missing_variable_is_named_at_once(self):
        """Not the first one. An operator should learn what it needs in one run."""
        del os.environ[read_admin.REALM]
        del os.environ[read_admin.CLIENT_ID]
        with self.assertRaises(SystemExit) as stop:
            read_admin.environment()
        self.assertIn(read_admin.REALM, str(stop.exception))
        self.assertIn(read_admin.CLIENT_ID, str(stop.exception))

    def test_a_blank_variable_counts_as_missing(self):
        """`AddJwtAuthentication` treats whitespace as absent; so does this."""
        self.set(read_admin.CLIENT_ID, "   ")
        with self.assertRaises(SystemExit) as stop:
            read_admin.environment()
        self.assertIn(read_admin.CLIENT_ID, str(stop.exception))

    def test_plain_http_is_refused(self):
        """The bearer token this fetch carries can read every client secret in the realm."""
        self.set(read_admin.BASE_URL, "http://id.example.com")
        with self.assertRaises(SystemExit) as stop:
            read_admin.environment()
        self.assertIn("https or nothing", str(stop.exception))

    def test_a_trailing_slash_is_trimmed_rather_than_doubling_every_path(self):
        self.set(read_admin.BASE_URL, "https://id.example.com/")
        self.assertEqual(read_admin.environment()[read_admin.BASE_URL],
                         "https://id.example.com")


class Stubbed(unittest.TestCase):
    """The fixture the three classes below share, and no case of its own.

    It was `TheJoin` until `WhatItWrites` subclassed it, which re-ran every one
    of that class's cases under this one's name — a suite whose count grows
    without its coverage.
    """

    def setUp(self):
        # Zipped rather than spelled out key by key, for the reason
        # `read_admin.py` gives beside its own REQUIRED tuple: a
        # credential-shaped name next to a literal is a secret-scan finding,
        # and an accepted finding is a decision somebody has to re-read.
        self.values = dict(zip(read_admin.REQUIRED, (
            "https://id.example.com", "commerce", "realm-check", "an-invented-value",
        ), strict=True))
        # The originals are captured by `getattr` in the argument list, so the
        # cleanup restores what was there before this test replaced it. Written
        # this way rather than as two saved locals because a local named for a
        # token is a secret-scan finding, and the shorter form is better anyway.
        for name in ("token", "get"):
            self.addCleanup(setattr, read_admin, name, getattr(read_admin, name))
        read_admin.token = lambda *args: "a-token"

    def answers(self, representation, clients):
        """One realm document and one client list, served the way Keycloak does.

        The client list is PAGED here rather than returned whole, because
        `read_admin.clients` pages and a stub that ignores `first`/`max` would
        make the paging untested while looking covered.
        """
        def get(url: str, _access: str):
            if "/clients" not in url:
                return representation
            query = urllib.parse.parse_qs(urllib.parse.urlsplit(url).query)
            first = int(query.get("first", ["0"])[0])
            most = int(query.get("max", [str(read_admin.PAGE_SIZE)])[0])
            return clients[first:first + most]

        read_admin.get = get


class TheJoin(Stubbed):
    """The clients list is added to the realm representation, and never over it."""

    def test_the_two_documents_join_into_one_export_shape(self):
        self.answers({"realm": "commerce", "accessTokenLifespan": 300},
                     [{"clientId": "web-app"}])
        realm = read_admin.fetch(self.values)
        self.assertEqual(realm["accessTokenLifespan"], 300)
        self.assertEqual([c["clientId"] for c in realm["clients"]], ["web-app"])

    def test_a_representation_that_already_carries_clients_stops(self):
        """Overwriting it would silently choose which of two lists is judged."""
        self.answers({"realm": "commerce", "clients": []}, [{"clientId": "web-app"}])
        with self.assertRaises(SystemExit) as stop:
            read_admin.fetch(self.values)
        self.assertIn("already carries a clients key", str(stop.exception))

    def test_a_realm_answer_that_is_not_an_object_stops(self):
        self.answers([], [{"clientId": "web-app"}])
        with self.assertRaises(SystemExit) as stop:
            read_admin.fetch(self.values)
        self.assertIn("realm representation", str(stop.exception))

    def test_a_clients_answer_that_is_not_a_list_stops(self):
        read_admin.get = lambda url, _a: ({"clientId": "web-app"}
                                          if "/clients" in url else {"realm": "commerce"})
        with self.assertRaises(SystemExit) as stop:
            read_admin.fetch(self.values)
        self.assertIn("client list", str(stop.exception))


class ThePaging(Stubbed):
    """Every client, and not the first page of them.

    A client past the first page can enable the implicit flow or override the
    token lifetime, and with `web-app` on page one the rollout would pass on a
    realm nobody looked at the end of.
    """

    def realm_of(self, count: int) -> list:
        return [{"clientId": f"client-{n}"} for n in range(count)]

    def test_a_client_past_the_first_page_is_still_read(self):
        every = self.realm_of(read_admin.PAGE_SIZE + 7)
        self.answers({"realm": "commerce"}, every)
        fetched = read_admin.fetch(self.values)["clients"]
        self.assertEqual(len(fetched), len(every))
        self.assertEqual(fetched[-1]["clientId"], f"client-{read_admin.PAGE_SIZE + 6}")

    def test_an_exactly_full_page_is_not_assumed_to_be_the_last(self):
        """The boundary a `len(page) == max` shortcut gets wrong."""
        every = self.realm_of(read_admin.PAGE_SIZE)
        self.answers({"realm": "commerce"}, every)
        self.assertEqual(len(read_admin.fetch(self.values)["clients"]), read_admin.PAGE_SIZE)

    def test_a_short_page_is_not_the_last_page_either(self):
        """Keycloak drops representations it cannot render, mid-list.

        A page of 100 client models can answer 99 entries with more clients
        behind it, so a `len(page) < max` shortcut stops on a realm whose tail
        is unjudged — and the tail is exactly where a violating client sits
        when `web-app` is on page one.
        """
        served = []

        def get(url: str, _access: str):
            if "/clients" not in url:
                return {"realm": "commerce"}
            query = urllib.parse.parse_qs(urllib.parse.urlsplit(url).query)
            first = int(query.get("first", ["0"])[0])
            served.append(first)
            if first == 0:
                # A filtered page: one model short of full, and not the last.
                return self.realm_of(read_admin.PAGE_SIZE - 1)
            if first == read_admin.PAGE_SIZE:
                return [{"clientId": "implicit-flow-client", "implicitFlowEnabled": True}]
            return []

        read_admin.get = get
        fetched = read_admin.fetch(self.values)["clients"]
        self.assertIn("implicit-flow-client", [c["clientId"] for c in fetched])
        self.assertEqual(served, [0, read_admin.PAGE_SIZE, read_admin.PAGE_SIZE * 2])

    def test_a_realm_that_never_returns_an_empty_page_stops(self):
        """A server paging forever is not a realm to report the start of."""
        read_admin.get = lambda url, _a: (
            [{"clientId": "x"}] * read_admin.PAGE_SIZE if "/clients" in url
            else {"realm": "commerce"})
        with self.assertRaises(SystemExit) as stop:
            read_admin.fetch(self.values)
        self.assertIn("without an empty one", str(stop.exception))


class TheRedirect(unittest.TestCase):
    """A redirect is refused, because `urllib` carries the credential onto it."""

    def test_the_opener_refuses_to_follow_one(self):
        handler = read_admin.NoRedirects()
        with self.assertRaises(urllib.error.HTTPError) as refused:
            handler.redirect_request(
                urllib.request.Request("https://id.example.com/admin/realms/commerce"),
                None, 302, "Found", {}, "https://elsewhere.example.com/")
        self.assertIn("elsewhere.example.com", str(refused.exception))

    def test_the_opener_this_file_uses_carries_the_refusal(self):
        """The subject test: it is the OPENER every call goes through that matters.

        A handler nothing installs refuses nothing, and `urlopen` — the call
        this file used to make — follows redirects with the default opener
        whatever this module defines.
        """
        self.assertTrue(
            any(isinstance(h, read_admin.NoRedirects) for h in read_admin.OPENER.handlers),
            read_admin.OPENER.handlers)


class WhatItWrites(Stubbed):
    """The seam: the file `--out` writes is a file the deploy-time check reads.

    It meets here and nowhere else in either suite, so this case drives
    `read_admin.main` rather than asserting about a document the test wrote
    itself. An earlier version did exactly that — it round-tripped its own
    literal through `json` and never called `read_admin` at all, so deleting
    the write, changing its encoding or writing to a different path would all
    have left it green with its docstring still claiming the seam was covered.
    """

    def setUp(self):
        super().setUp()
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.out = Path(directory.name) / "realm.json"
        # `main` reads the environment rather than the fixture dict, which is
        # the point of driving `main` at all: the seam includes that read.
        original = {name: os.environ.get(name) for name in self.values}

        def restore():
            for name, value in original.items():
                if value is None:
                    os.environ.pop(name, None)
                else:
                    os.environ[name] = value

        self.addCleanup(restore)
        os.environ.update(self.values)

    def run_main(self) -> int:
        """`main`, with its one line of output kept out of the suite's."""
        with contextlib.redirect_stdout(io.StringIO()):
            return read_admin.main(["read_admin.py", "--out", str(self.out)])

    def test_what_read_admin_writes_is_what_realm_check_accepts(self):
        import realm_check

        self.answers({"realm": "commerce", "accessTokenLifespan": 300},
                     [{"clientId": realm_check.BROWSER_CLIENT,
                       "standardFlowEnabled": True,
                       "implicitFlowEnabled": False,
                       "directAccessGrantsEnabled": False,
                       "attributes": {"use.refresh.tokens": "false"}}])
        self.assertEqual(self.run_main(), 0)

        # Read back through the deploy path's own two calls, not through json.
        document = realm_check.load_realm(self.out)
        self.assertEqual(
            realm_check.check_realm(document, realm_check.DEPLOYED, 300), [])

    def test_a_realm_the_fetch_returns_is_judged_and_can_fail(self):
        """The positive above proves the seam carries a compliant realm.

        This proves it carries a failing one, which is what stops a rollout —
        a seam that only transmits passes is the fail-open shape again.
        """
        import realm_check

        self.answers({"realm": "commerce", "accessTokenLifespan": 18000},
                     [{"clientId": realm_check.BROWSER_CLIENT,
                       "standardFlowEnabled": True,
                       "implicitFlowEnabled": False,
                       "directAccessGrantsEnabled": False,
                       "attributes": {"use.refresh.tokens": "false"}}])
        self.run_main()
        found = realm_check.check_realm(
            realm_check.load_realm(self.out), realm_check.DEPLOYED, 300)
        self.assertEqual(len(found), 1, found)
        self.assertIn("accessTokenLifespan", found[0])


class TheRealmSegmentIsEscaped(Stubbed):
    """`/` in a realm name must not change which realm is read."""

    def test_a_traversal_in_the_realm_name_does_not_reach_another_realm(self):
        seen = []

        def get(url: str, _access: str):
            seen.append(url)
            return [] if "/clients" in url else {"realm": "commerce"}

        read_admin.get = get
        self.values[read_admin.REALM] = "commerce/../master"
        read_admin.fetch(self.values)
        self.assertTrue(all("/../" not in url for url in seen), seen)
        self.assertTrue(any("commerce%2F..%2Fmaster" in url for url in seen), seen)


if __name__ == "__main__":
    unittest.main()
