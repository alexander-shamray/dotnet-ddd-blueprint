#!/usr/bin/env python3
"""Negative cases for the secret scan.

Run against this repository the gate passes, and that proves nothing about what
it would catch — a pattern that matched nothing at all would pass identically.
So every rule here is exercised twice: once with the shape it exists for, and
once with the near miss it must stay quiet about. The second half is where the
work is. A scanner people turn off is a scanner that fires on ordinary code, and
the ordinary code is what the near-miss cases are.

`Coverage` below is the subject test. It asserts that every rule the scanner
declares has both cases, so a rule added without them fails here rather than
shipping as a pattern nobody has established is looking at anything.
"""

from __future__ import annotations

import contextlib
import io
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import secret_scan

RULES = {rule.id: rule for rule in secret_scan.RULES}


def found(rule_id: str, text: str) -> list[str]:
    """Every credential one rule finds in one snippet."""
    rule = RULES[rule_id]
    return [finding.secret for finding in secret_scan.scan_text("probe.txt", text, [rule])]


def allow_file(directory: Path, *lines: str) -> Path:
    path = directory / "allowed-secrets.txt"
    path.write_text("# header\n" + "".join(f"{line}\n" for line in lines), encoding="utf-8")
    return path


def parse(*lines: str) -> tuple[list[secret_scan.Suppression], list[str]]:
    with tempfile.TemporaryDirectory() as directory:
        return secret_scan.read_allowed(allow_file(Path(directory), *lines))


def run(root: Path, *allowed: str) -> tuple[int, str, str]:
    """The whole gate over one tree, with both streams captured.

    The allow-list is written OUTSIDE `root`, because the file counts as a file
    and a walk that included it would make every count in these tests one out.
    """
    with tempfile.TemporaryDirectory() as directory:
        path = allow_file(Path(directory), *allowed)
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = secret_scan.main(["--root", str(root), "--allowed", str(path)])
    return code, out.getvalue(), err.getvalue()


# Fixtures assembled to the published length of each provider's key. They are
# invented values of the right SHAPE, which is the only thing under test — and
# they are the reason this file has entries in allowed-secrets.txt, since a
# positive control has to be the literal a real one would be.
AWS_ID = "AKIAIOSFODNN7EXAMPLE"
AWS_SECRET = "wJalrXUtnFEMIK7MDENGbPxRfiCYEXAMPLEKEY12"
GITHUB = "ghp_1234567890abcdefghijklmnopqrstuvwxyzAB"
GOOGLE = "AIzaSyB-1234567890abcdefghijklmnopqrstu"
JWT = ("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0."
       "dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk")


class Detects(unittest.TestCase):
    """One positive control per rule. Named `test_<rule id with underscores>`."""

    def test_private_key_block(self):
        self.assertEqual(
            found("private-key-block", "-----BEGIN RSA PRIVATE KEY-----"),
            ["-----BEGIN RSA PRIVATE KEY-----"])
        self.assertEqual(len(found("private-key-block", "-----BEGIN PRIVATE KEY-----")), 1)
        self.assertEqual(len(found("private-key-block", "-----BEGIN OPENSSH PRIVATE KEY-----")), 1)

    def test_connection_string_password(self):
        line = 'Server=sql,1433;Database=Catalog;User Id=sa;Password=Tr0ub4dor;Encrypt=False'
        self.assertEqual(found("connection-string-password", line), ["Tr0ub4dor"])
        # The parser this mirrors tolerates spaces around the separator and does
        # not care about case, and §13.4 already had to learn both.
        self.assertEqual(found("connection-string-password", "Pwd = Tr0ub4dor"), ["Tr0ub4dor"])
        self.assertEqual(found("connection-string-password", "PASSWORD=Tr0ub4dor"), ["Tr0ub4dor"])

    def test_aws_access_key_id(self):
        self.assertEqual(found("aws-access-key-id", f"aws_key = '{AWS_ID}'"), [AWS_ID])

    def test_aws_secret_access_key(self):
        line = f"aws_secret_access_key = {AWS_SECRET}"
        self.assertEqual(found("aws-secret-access-key", line), [AWS_SECRET])

    def test_github_token(self):
        self.assertEqual(found("github-token", f"GH_TOKEN={GITHUB}"), [GITHUB])
        self.assertEqual(len(found("github-token", GITHUB.replace("ghp_", "ghs_"))), 1)

    def test_slack_token(self):
        # Assembled from two halves rather than written out, and the reason is
        # a measurement rather than a preference: GitHub's own push protection
        # refuses a push carrying this literal, fabricated or not, and it
        # scans every commit in the push rather than the tip. A scanner's
        # positive control cannot be a string that stops the branch reaching
        # the remote.
        #
        # The rule sees the same value either way -- it is handed the joined
        # string below -- so what the split costs is that this fixture is no
        # longer a finding in THIS file, which is why it has no allowed-secrets
        # entry where its neighbours do.
        token = "xox" + "b-2468013579-abcdefghijklmno"
        self.assertEqual(found("slack-token", f"slack: {token}"), [token])

    def test_stripe_live_key(self):
        key = "sk_live_abcdefghijklmnop0123"
        self.assertEqual(found("stripe-live-key", f"stripe = {key}"), [key])

    def test_google_api_key(self):
        self.assertEqual(found("google-api-key", f"key={GOOGLE}"), [GOOGLE])

    def test_json_web_token(self):
        self.assertEqual(found("json-web-token", f"Authorization: Bearer {JWT}"), [JWT])

    def test_model_provider_api_key(self):
        xai = "xai-abcdefghijklmnopqrstuvwxyz0123456789"
        anthropic = "sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789"
        self.assertEqual(found("model-provider-api-key", f"XAI_API_KEY={xai}"), [xai])
        self.assertEqual(found("model-provider-api-key", f"key: '{anthropic}'"), [anthropic])

    def test_credential_assignment(self):
        # Eight characters is the floor, so eight characters is a positive case
        # and seven is the near miss below. Writing only one of those is how a
        # boundary ends up asserted on one side.
        self.assertEqual(found("credential-assignment", 'ClientSecret = "abcdefgh"'), ["abcdefgh"])
        self.assertEqual(found("credential-assignment", '"secret": "hunter2xyz"'), ["hunter2xyz"])
        self.assertEqual(
            found("credential-assignment", "  api_key: 'abcdefghij'"), ["abcdefghij"])

    def test_env_assignment(self):
        self.assertEqual(found("env-assignment", "SQL_PASSWORD=Tr0ub4dor"), ["Tr0ub4dor"])
        self.assertEqual(found("env-assignment", "export API_TOKEN=Tr0ub4dor"), ["Tr0ub4dor"])


class NearMisses(unittest.TestCase):
    """One false-positive boundary per rule, both ends where there are two."""

    def test_private_key_block(self):
        # The neighbouring headers are ordinary and harmless. Enumerating the
        # algorithm words rather than writing `\\w+` is what keeps them out.
        self.assertEqual(found("private-key-block", "-----BEGIN PUBLIC KEY-----"), [])
        self.assertEqual(found("private-key-block", "-----BEGIN CERTIFICATE-----"), [])

    def test_connection_string_password(self):
        self.assertEqual(found("connection-string-password", "Password=;Encrypt=False"), [])
        self.assertEqual(
            found("connection-string-password", "Password=${SQL_PASSWORD};Encrypt=False"), [])
        self.assertEqual(found("connection-string-password", "password={Pwd} was used"), [])
        self.assertEqual(found("connection-string-password", "Password=<your-password>"), [])
        # A name that merely ENDS in the keyword is an assignment, not a
        # connection-string segment, and belongs to the two name rules. The
        # lookbehind is what draws that line, and it needs its own case because
        # nothing else here would notice it disappearing.
        self.assertEqual(found("connection-string-password", "SQL_PASSWORD=Tr0ub4dor"), [])
        # And a name reached through a member access is C#, not a connection
        # string — `;` or the start of the string is what precedes the keyword
        # in one of those. The dot in the lookbehind is what says so, and it
        # was added after this exact line reported itself.
        self.assertEqual(found("connection-string-password", "        this.password = pw"), [])

    def test_aws_access_key_id(self):
        # Both ends. One character short does not match, and one character long
        # does not either — the second is the end a `\\b` would have got wrong.
        self.assertEqual(found("aws-access-key-id", "AKIAIOSFODNN7EXAMPL"), [])
        self.assertEqual(found("aws-access-key-id", "AKIAIOSFODNN7EXAMPLEX"), [])
        self.assertEqual(found("aws-access-key-id", "akiaiosfodnn7example"), [])

    def test_aws_secret_access_key(self):
        self.assertEqual(found("aws-secret-access-key", f"aws_secret_access_key={AWS_SECRET[:39]}"),
                         [])
        self.assertEqual(found("aws-secret-access-key", f"unrelated_value = {AWS_SECRET}"), [])

    def test_github_token(self):
        self.assertEqual(found("github-token", "ghp_" + "a" * 35), [])
        self.assertEqual(found("github-token", "ghz_" + "a" * 40), [])

    def test_slack_token(self):
        self.assertEqual(found("slack-token", "xoxz-2468013579-abcdefghijklmno"), [])
        self.assertEqual(found("slack-token", "xoxb-short"), [])

    def test_stripe_live_key(self):
        # sk_test_ is a test-mode key by construction. Firing on it is how a
        # rule gets a reputation for crying wolf.
        self.assertEqual(found("stripe-live-key", "sk_test_abcdefghijklmnop0123"), [])
        self.assertEqual(found("stripe-live-key", "sk_live_abcdefgh"), [])

    def test_google_api_key(self):
        self.assertEqual(found("google-api-key", GOOGLE[:-1]), [])
        self.assertEqual(found("google-api-key", GOOGLE + "x"), [])

    def test_json_web_token(self):
        head, payload, _ = JWT.split(".")
        self.assertEqual(found("json-web-token", f"{head}.{payload}"), [])
        self.assertEqual(found("json-web-token", "eyJhbGci.short.sig"), [])

    def test_model_provider_api_key(self):
        self.assertEqual(found("model-provider-api-key", "xai-tooshort"), [])
        # `sk-ant` is a prefix of ordinary English, and the hyphen after it is
        # the only thing that says so.
        self.assertEqual(
            found("model-provider-api-key", "sk-antique-furniture-catalogue-2026"), [])

    def test_credential_assignment(self):
        self.assertEqual(found("credential-assignment", 'ClientSecret = "abcdefg"'), [])
        self.assertEqual(found("credential-assignment", 'if (token == "abcdefgh")'), [])
        self.assertEqual(found("credential-assignment", 'public string Token => "abcdefgh";'), [])
        self.assertEqual(found("credential-assignment", 'secret: "${VAULT_SECRET}"'), [])
        # Keycloak's realm export carries a dozen `webAuthnPolicyPasswordless*`
        # keys. The word inside them is the opposite of a credential.
        self.assertEqual(
            found("credential-assignment", '"webAuthnPolicyPasswordlessRpId": "keycloak"'), [])

    def test_env_assignment(self):
        # A constructor storing its parameter. This is the one false positive
        # the bare-word constraint could not remove, because `secret` IS a bare
        # word — what removes it is that the name already contains the value.
        self.assertEqual(found("env-assignment", "        self.secret = secret"), [])
        self.assertEqual(found("env-assignment", "        this.password = password"), [])
        # An expression rather than a literal, in the two shapes that reach the
        # left margin: a Python module constant and a C# statement.
        self.assertEqual(found("env-assignment", "TOKEN_PATTERN = re.compile(r'x')"), [])
        self.assertEqual(found("env-assignment", "        _token = someOtherToken;"), [])
        self.assertEqual(found("env-assignment", "# SQL_PASSWORD=Tr0ub4dor"), [])


class Coverage(unittest.TestCase):
    """The gate's own subject: what the tests are looking at, not what they found.

    CLAUDE.md names a gate that silently stops covering the newest surface as
    this repository's most-repeated failure, and a rule shipped without a
    positive control is that failure in its earliest form — nobody has
    established the pattern matches anything at all.
    """

    def test_every_rule_has_a_positive_and_a_near_miss_case(self):
        self.assertTrue(secret_scan.RULES, "the scanner declares no rules")
        for rule in secret_scan.RULES:
            name = "test_" + rule.id.replace("-", "_")
            self.assertTrue(
                hasattr(Detects, name),
                f"rule `{rule.id}` has no positive control: add Detects.{name}")
            self.assertTrue(
                hasattr(NearMisses, name),
                f"rule `{rule.id}` has no false-positive boundary: add NearMisses.{name}")

    def test_every_fixture_is_the_length_its_rule_requires(self):
        # A fixture one character out turns a positive control into a near miss
        # that happens to pass, which is a pattern matching nothing wearing a
        # test's clothes. Measured, not assumed: two of these were wrong when
        # they were first written, and only running them said so.
        self.assertEqual(len(AWS_ID), 20)
        self.assertEqual(len(AWS_SECRET), 40)
        self.assertEqual(len(GOOGLE), 39)
        self.assertGreaterEqual(len(GITHUB) - len("ghp_"), 36)
        self.assertEqual(len(JWT.split(".")), 3)

    def test_every_rule_has_a_distinct_id_and_a_sentence(self):
        identifiers = [rule.id for rule in secret_scan.RULES]
        self.assertEqual(len(identifiers), len(set(identifiers)))
        for rule in secret_scan.RULES:
            self.assertTrue(rule.sentence.strip(), f"rule `{rule.id}` has no sentence")


class Values(unittest.TestCase):
    """`literal()` — the structural half, which is the half the code may decide."""

    def test_a_bare_variable_reference_carries_nothing(self):
        for reference in ("${SQL_PASSWORD}", "$PGPASSWORD", "%SQL_PASSWORD%",
                          "{{ .Values.db.password }}", "{Pwd}", "<your-password>",
                          "$(OrderingRuntimePassword)"):
            self.assertEqual(secret_scan.literal(reference), "", reference)

    def test_a_defaulted_reference_is_judged_on_its_default(self):
        # §14.1's seam keeps the value out of a DEPLOYMENT. It does not keep it
        # out of the tree, so the default is what gets judged — and then
        # accepted by name in the allow-list rather than by the pattern.
        self.assertEqual(secret_scan.literal("${SQL_PASSWORD:-Tr0ub4dor}"), "Tr0ub4dor")

    def test_a_nested_default_is_unwrapped(self):
        self.assertEqual(secret_scan.literal("${A:-${B:-Tr0ub4dor}}"), "Tr0ub4dor")

    def test_a_mask_carries_nothing(self):
        for mask in ("", "   ", "****", "xxxxxxxx", "...", "---"):
            self.assertEqual(secret_scan.literal(mask), "", repr(mask))

    def test_punctuation_alone_carries_nothing(self):
        self.assertEqual(secret_scan.literal("`"), "")
        self.assertEqual(secret_scan.literal("}]>"), "")

    def test_a_real_value_survives(self):
        self.assertEqual(secret_scan.literal("  Tr0ub4dor  "), "Tr0ub4dor")


class Redaction(unittest.TestCase):
    def test_a_short_prefix_and_a_length(self):
        self.assertEqual(secret_scan.redact("Tr0ub4dor"), "Tr0... 9 chars")

    def test_the_fingerprint_is_stable_and_short(self):
        self.assertEqual(secret_scan.digest("Tr0ub4dor"), secret_scan.digest("Tr0ub4dor"))
        self.assertNotEqual(secret_scan.digest("Tr0ub4dor"), secret_scan.digest("Tr0ub4doR"))
        self.assertEqual(len(secret_scan.digest("Tr0ub4dor")), 12)

    def test_neither_stream_carries_the_secret_it_found(self):
        """Load-bearing, and asserted on BOTH streams.

        A gate that prints what it found has copied the credential into the log
        of every run that failed — where it is retained longer, and read by more
        people, than the branch ever was. Checking only stdout would pass a gate
        that wrote the value to stderr, which is where this one writes.
        """
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "leak.txt").write_text(f"aws_key = {AWS_ID}\n", encoding="utf-8")
            code, out, err = run(root)

        self.assertEqual(code, 1)
        self.assertNotIn(AWS_ID, out)
        self.assertNotIn(AWS_ID, err)
        self.assertIn("aws-access-key-id", err)
        self.assertIn("AKI... 20 chars", err)
        self.assertIn(secret_scan.digest(AWS_ID), err)


class AsciiOutput(unittest.TestCase):
    """Both streams stay ASCII even when the tree does not.

    The messages are written in ASCII, which says nothing about the output: two
    of the three things a finding line carries come from the tree — the path and
    the redacted prefix of the value. So the subject here is a file whose NAME
    and whose credential are both outside ASCII.
    """

    def test_a_non_ascii_path_and_value_still_print(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "tree"
            root.mkdir()
            (root / "café.env").write_text(
                "SECRET_TOKEN=überg3heim-passwort\n", encoding="utf-8")
            code, out, err = run(root)

        self.assertEqual(code, 1)
        (out + err).encode("ascii")  # raises if anything slipped through
        self.assertIn("env-assignment", err)


class AllowList(unittest.TestCase):
    def test_reads_a_well_formed_entry(self):
        entries, problems = parse(
            "deploy/compose/.env.example | env-assignment | abc123def456 | "
            "Section 14.1's documented local default.")
        self.assertEqual(problems, [])
        self.assertEqual(len(entries), 1)
        self.assertEqual(entries[0].rule, "env-assignment")
        self.assertEqual(entries[0].fingerprint, "abc123def456")

    def test_rejects_an_entry_with_the_wrong_number_of_fields(self):
        _, problems = parse("deploy/compose/.env.example | env-assignment | abc123def456")
        self.assertEqual(len(problems), 1)
        self.assertIn("expected", problems[0])

    def test_rejects_a_glob_path(self):
        # A glob is how a suppression arrives for a file nobody has written yet,
        # and a file that arrives pre-suppressed is the failure this gate exists
        # to avoid, with the paperwork already filled in.
        for path in ("tests/*.cs", "tests/Common.Web.Tests/?.cs"):
            _, problems = parse(f"{path} | credential-assignment | abc123def456 | A real reason.")
            self.assertEqual(len(problems), 1, path)
            self.assertIn("exact path", problems[0])

    def test_rejects_a_rule_the_scanner_does_not_declare(self):
        # Only checked when the caller supplies the set, which `main` does. A
        # typo would otherwise be reported as a stale entry: the right verdict
        # with the wrong diagnosis attached.
        with tempfile.TemporaryDirectory() as directory:
            path = allow_file(
                Path(directory),
                "a.txt | aws-acces-key-id | abc123def456 | One letter short of a rule.")
            _, problems = secret_scan.read_allowed(path, set(RULES))
        self.assertEqual(len(problems), 1)
        self.assertIn("not a rule this scanner declares", problems[0])

    def test_rejects_a_reason_that_is_not_one(self):
        _, problems = parse("a.txt | credential-assignment | abc123def456 | ok")
        self.assertEqual(len(problems), 1)
        self.assertIn("states WHY", problems[0])

    def test_reports_a_missing_allow_list(self):
        with tempfile.TemporaryDirectory() as directory:
            _, problems = secret_scan.read_allowed(Path(directory) / "absent.txt")
        self.assertEqual(len(problems), 1)
        self.assertIn("missing", problems[0])

    def test_reports_a_duplicated_entry(self):
        entry = "a.txt | credential-assignment | abc123def456 | The same reason twice over."
        entries, _ = parse(entry, entry)
        problems = secret_scan.audit([], entries)
        self.assertTrue(any("duplicates" in problem for problem in problems))


class Audit(unittest.TestCase):
    def finding(self, path="a.txt", rule_id="aws-access-key-id", secret=AWS_ID):
        return secret_scan.Finding(path, 1, RULES[rule_id], secret)

    def test_an_uncovered_finding_is_reported(self):
        self.assertEqual(len(secret_scan.audit([self.finding()], [])), 1)

    def test_a_covered_finding_is_not(self):
        entries, _ = parse(
            f"a.txt | aws-access-key-id | {secret_scan.digest(AWS_ID)} | "
            f"A positive control for the AWS rule.")
        self.assertEqual(secret_scan.audit([self.finding()], entries), [])

    def test_an_entry_naming_another_rule_covers_nothing(self):
        entries, _ = parse(
            f"a.txt | github-token | {secret_scan.digest(AWS_ID)} | "
            f"The right value under the wrong rule.")
        problems = secret_scan.audit([self.finding()], entries)
        # Two ways round: the finding is unexplained AND the entry is stale.
        self.assertEqual(len(problems), 2)

    def test_a_stale_entry_fails_the_gate(self):
        """The half that keeps the other half honest.

        A suppression whose finding has gone is a decision nobody has re-read,
        and this repository already wrote down what to do about a list of known
        exceptions: gate it, so the day one clears, the build says which.
        """
        entries, _ = parse(
            "a.txt | aws-access-key-id | 0123456789ab | The value this entry named has moved.")
        problems = secret_scan.audit([], entries)
        self.assertEqual(len(problems), 1)
        self.assertIn("no longer matches", problems[0])


class EmptySubject(unittest.TestCase):
    """A clean report over a subject nobody read is the failure, not the pass.

    `ci.yml` states it as house policy at the pipeline gate: no check trusts its
    own parser, and each fails on an empty subject rather than reporting a
    complete list it never read. A scan of no files and a scan with no rules
    both print the sentence a clean tree prints, and that sentence is the whole
    product.
    """

    def test_a_scan_of_no_files_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "empty"
            root.mkdir()
            code, _, err = run(root)
        self.assertEqual(code, 1)
        self.assertIn("scanned no files", err)

    def test_a_scan_with_no_rules_fails(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "a.txt").write_text("nothing here\n", encoding="utf-8")
            with mock.patch.object(secret_scan, "RULES", []):
                code, _, err = run(root)
        self.assertEqual(code, 1)
        self.assertIn("no rules", err)


class Walking(unittest.TestCase):
    def test_a_binary_file_is_not_scanned(self):
        # A text file sits beside it, so the empty-subject check above cannot be
        # what makes this pass — the walk read something, and skipped the blob.
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "image.bin").write_bytes(b"\x00\x01" + AWS_ID.encode("ascii"))
            (root / "kept.txt").write_text("nothing here\n", encoding="utf-8")
            code, out, _ = run(root)
        self.assertEqual(code, 0)
        self.assertIn("1 file(s)", out)

    def test_build_output_is_not_descended_into(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "obj").mkdir()
            (root / "obj" / "leak.txt").write_text(f"aws={AWS_ID}\n", encoding="utf-8")
            (root / "kept.txt").write_text("nothing here\n", encoding="utf-8")
            code, out, _ = run(root)
        self.assertEqual(code, 0)
        self.assertIn("1 file(s)", out)


class RealRepository(unittest.TestCase):
    def test_the_repository_passes_its_own_gate(self):
        """The case that makes the allow-list work get done honestly.

        It is also this suite's positive control over the real tree: the summary
        it asserts on names a non-zero count of ACCEPTED findings, so a scanner
        that had silently stopped matching would fail here rather than print a
        clean sentence about a tree it never read.
        """
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = secret_scan.main([])
        self.assertEqual(code, 0, out.getvalue())
        self.assertNotIn(" 0 accepted finding(s)", out.getvalue())


if __name__ == "__main__":
    unittest.main()
