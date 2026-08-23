"""The review loop's helpers, tested where they decide.

Five of this directory's scripts carry a judgement that the whole /ship loop
rests on, and until now none of them had a test. Each of the defects below
shipped, and each is a *negative* case here — a gate only ever observed green is
one nobody has established is looking at anything, which is the rule the
.github/**-gate suites are already written to.

What is under test, and which issue each half closes:

  #120  the usage-limit pattern missed `402 Payment Required` / `usage balance
        exhausted`, so an exhausted prepaid balance took the FAIL path and spent
        a ledger slot on a review that never started.
  #69   the did-it-run check was a deny-list of three stop reasons, so a
        reviewer that ran out of output or turn budget reported as a clean pass.
  #51   the ledger printed `0` — "nothing spent" — on its own trust-check error
        path, re-arming the twelve-check cap.
  #59   nothing made the reservation happen, and nothing tied it to the model
        call it accounts for.
  #75   the sweeps' worktree shape check was not the direct-child check its
        comment claimed, because `?` matches `/` in a bash `case`.

Two rules the suite is written to, both of them this repository's:

  *The engine under test is the engine that ships.* Every pattern assertion
  shells out to the same `grep -E` the scripts call. Re-implementing the regex
  in Python's `re` would be a second specification, and a hand-written double
  cannot disagree with itself.

  *A declared pattern needs a test whose subject is where it is APPLIED.* A
  pattern declared once and then quietly not used, or used alongside a second
  literal copy, is the drift `SOURCE_INPUTS` was invented for. So the pattern
  cases are paired with structural cases over the call sites.

Run: py -3.12 -m unittest discover -s .claude/scripts
Needs bash and grep on PATH; needs no network, no Docker, no gh and no SDK.
"""

import os
import re
import shutil
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
REVIEW = SCRIPTS / "grok-review.sh"
LEDGER = SCRIPTS / "grok-ledger.sh"
DETACH = SCRIPTS / "git-worktree-detach.sh"
DROP = SCRIPTS / "git-worktree-drop.sh"

BASH = shutil.which("bash")
GREP = shutil.which("grep")


def setUpModule():
    # Not a skip. A skip on a missing tool reports a pass, which is the fail-open
    # this repository has refused since ADR-010 made real infrastructure
    # non-optional for `dotnet test`. Absent bash, this suite has established
    # nothing and says so.
    if BASH is None or GREP is None:
        raise RuntimeError(
            "bash and grep are required: these tests exercise the same engine "
            "the scripts do, and asserting through Python's `re` instead would "
            "be testing a second specification"
        )


def declared(name):
    """Read one single-quoted pattern out of grok-review.sh by its variable name.

    The point of reading rather than restating: the test and the script then
    cannot disagree about what the pattern IS, only about what it should match.
    """
    text = REVIEW.read_text(encoding="utf-8")
    found = re.findall(rf"^{re.escape(name)}='([^']*)'$", text, re.MULTILINE)
    if len(found) != 1:
        raise AssertionError(
            f"expected exactly one declaration of {name} in {REVIEW.name}, "
            f"found {len(found)}"
        )
    return found[0]


def run_bash(script, subject="", **env_extra):
    """Run a bash fragment with the subject on stdin and everything else in env.

    Nothing is passed as an argument, and that is not fastidiousness. Under MSYS
    — the host this repository is developed on — an argv element crossing into
    `bash.exe` is re-parsed by the MSYS runtime, so a pattern containing `"`
    arrives with its quotes eaten and matches nothing. That failure is silent in
    the direction that matters: `grep` finding no stopReason reads exactly like
    a payload that carries none, so the first version of this suite reported the
    allow-list as broken when the allow-list was fine. Environment variables and
    stdin are not re-parsed, so they mean the same thing on both platforms.
    """
    env = dict(os.environ)
    env.update(env_extra)
    return subprocess.run(
        [BASH, "-c", script],
        input=subject,
        capture_output=True,
        text=True,
        env=env,
    )


def grep_matches(pattern, subject, ignore_case=True):
    """`grep -qE` (optionally -i) on the real engine the scripts call."""
    flag = "-i" if ignore_case else ""
    return run_bash(f'grep -q {flag} -E "$PAT"', subject, PAT=pattern).returncode == 0


def stop_verdict(payload):
    """Re-run grok-review.sh's did-it-run decision over one JSON payload.

    The three lines below are the script's, with both patterns read out of it —
    so a change to either the pattern or the shape of the check is a change this
    test sees.
    """
    script = textwrap.dedent(
        """
        payload=$(cat)
        stop_reasons=$(grep -oE "$ANY_RE" <<<"$payload" || true)
        stop_count=$(grep -c . <<<"$stop_reasons" || true)
        if [ "$stop_count" -ne 1 ] || ! grep -qE "$OK_RE" <<<"$stop_reasons"; then
          echo "did-not-run $stop_count"
        else
          echo "ran $stop_count"
        fi
        """
    )
    out = run_bash(
        script,
        payload,
        ANY_RE=declared("stop_any_re"),
        OK_RE=declared("stop_ok_re"),
    )
    if out.returncode != 0:
        raise AssertionError(f"the verdict fragment failed: {out.stderr}")
    return out.stdout.strip()


def bash_tmproot():
    """The temp root as BASH resolves it, which is not the one Python resolves.

    On this host `/tmp` is `D:\\tmp\\alexa` for the shell and `C:\\tmp` for every
    Python and built-in reader — different directories, both populated. A test
    that made its fixture with `pathlib` and then handed the path to a shell
    script was checking a directory the script could not see, and the refusal it
    asserted on was `not an existing directory` rather than the shape check it
    meant to exercise. So the fixtures below are made by bash too.
    """
    return run_bash('cd "${TMPDIR:-/tmp}" && pwd -P').stdout.strip()


class UsageLimitPattern(unittest.TestCase):
    """#120 — what the preflight must call a limit, and what it must not.

    The direction matters in both. A limit it fails to recognise takes the
    failure path and spends a slot on a review that never ran; a non-limit it
    recognises reports a working reviewer as out of window and skips every round
    silently. The second is the worse one, which is why the negatives here are
    not decoration.
    """

    def setUp(self):
        self.pattern = declared("limit_re")

    def assertLimit(self, text):
        self.assertTrue(
            grep_matches(self.pattern, text),
            f"limit_re should treat this as a usage limit: {text!r}",
        )

    def assertNotLimit(self, text):
        self.assertFalse(
            grep_matches(self.pattern, text),
            f"limit_re should NOT treat this as a usage limit: {text!r}",
        )

    def test_the_402_that_shipped(self):
        # Verbatim from PR #117 round 6, which is what the issue was filed over:
        # grok exited 1, the helper exited 4, and a ledger slot was spent.
        self.assertLimit(
            'Error: Internal error: {\n  "message": "API error (status 402 '
            'Payment Required): Grok Build usage balance exhausted",'
        )

    def test_402_and_the_prose_are_independently_sufficient(self):
        # Both halves are worth having: the status code is stable, the prose is
        # what a provider change is most likely to reword.
        self.assertLimit("API error (status 402 Payment Required)")
        self.assertLimit("Grok Build usage balance exhausted")

    def test_the_limits_that_already_matched_still_do(self):
        self.assertLimit("429 Too Many Requests")
        self.assertLimit("rate limit exceeded, retry after 60s")
        self.assertLimit("rate-limited by the upstream provider")
        self.assertLimit("quota exceeded for this team")
        self.assertLimit("usage limit reached")
        self.assertLimit("your newly created team doesn't have any credits")
        self.assertLimit("no credits remaining")

    def test_an_authentication_failure_is_not_a_limit(self):
        # The sharpest negative in the set. Misclassifying a broken credential
        # as a limit turns exit 8 ("cannot authenticate", which stops the loop
        # and says why) into exit 12 ("skipping this review"), so the loop runs
        # to its ceiling skipping every round and reports as though limits, not
        # a dead key, were the reason.
        self.assertNotLimit("401 Unauthorized: invalid API key")
        self.assertNotLimit("403 permission-denied")
        self.assertNotLimit("Error: could not read ~/.grok/auth.json")

    def test_a_number_inside_a_larger_number_is_not_a_status_code(self):
        self.assertNotLimit('"total_tokens": 47402')
        self.assertNotLimit('"input_tokens": 4021')
        self.assertNotLimit('"requestId": "ce40cbac-402f-4429-bef9-5805903fb4c8"')
        self.assertNotLimit("cost_usd_ticks: 1429000")

    def test_a_bare_status_number_in_an_ordinary_field_is_not_a_limit(self):
        # **The case the first version of this class missed.** The pattern was
        # `\b402\b`, and every negative here tested digits AROUND the number —
        # `47402`, `4021` — so none of them tested the number on its own. A
        # quote and a space are word boundaries too, so `"input_tokens": 402`
        # matched, and the comment beside the pattern said in as many words that
        # a token count must not read as a status code.
        #
        # A false positive is the expensive direction: it reports a working
        # reviewer as out of limits and skips every round silently. Raised by a
        # reviewer, which is the only reason it is here rather than shipped.
        self.assertNotLimit('"input_tokens": 402')
        self.assertNotLimit('"output_tokens": 429')
        self.assertNotLimit('"num_turns": 402')
        self.assertNotLimit("reasoning_tokens 429")

    def test_a_status_code_still_matches_when_it_arrives_as_one(self):
        # The other half: excluding the bare number must not exclude the code in
        # the shape the provider actually sends it.
        self.assertLimit("API error (status 402 Payment Required)")
        self.assertLimit('"http_status": 402')
        self.assertLimit("status: 429")
        self.assertLimit("HTTP/1.1 429 Too Many Requests")

    def test_an_ordinary_successful_probe_is_not_a_limit(self):
        self.assertNotLimit("ok")
        self.assertNotLimit('{"text": "ok", "stopReason": "end_turn"}')

    def test_the_status_anchor_is_what_separates_the_two(self):
        # A positive control for the mechanism itself. Without it the negatives
        # above could be passing because the pattern matches nothing at all,
        # which is this repository's most-repeated failure wearing a test's
        # clothes — and is exactly how `\b402\b` looked green while wrong.
        anchor = r"(status|code)[^0-9]{0,3}(402|429)"
        self.assertTrue(grep_matches(anchor, "(status 402 Payment Required)"))
        self.assertFalse(grep_matches(anchor, '"input_tokens": 402'))


class DidItRunAllowList(unittest.TestCase):
    """#69 — the verdict the entire loop's integrity rests on.

    grok's documented turn stop reasons are end_turn, max_tokens,
    max_turn_requests, refusal and cancelled. The old check refused three of
    them and passed the rest, so a reviewer that exhausted its output budget
    exited 0, wrote JSON, left no suggestions.md, and had that absence read as
    "nothing to report".
    """

    def payload(self, reason=None, extra=""):
        body = '{"text": "reviewed"'
        if reason is not None:
            body += f', "stopReason": "{reason}"'
        body += extra + ', "sessionId": "abc"}'
        return body

    def test_end_turn_is_the_only_accepted_terminal_state(self):
        self.assertEqual("ran 1", stop_verdict(self.payload("end_turn")))

    def test_a_budget_stop_is_not_a_clean_review(self):
        # The two the deny-list missed, and the ordinary way to reach them is a
        # long branch — which is when review matters most.
        self.assertEqual("did-not-run 1", stop_verdict(self.payload("max_tokens")))
        self.assertEqual(
            "did-not-run 1", stop_verdict(self.payload("max_turn_requests"))
        )

    def test_the_three_the_deny_list_already_caught_still_fail(self):
        self.assertEqual("did-not-run 1", stop_verdict(self.payload("cancelled")))
        self.assertEqual("did-not-run 1", stop_verdict(self.payload("refusal")))
        self.assertEqual("did-not-run 1", stop_verdict(self.payload("error_max")))

    def test_a_value_no_version_has_emitted_yet_fails_closed(self):
        # The whole reason for inverting the check: an allow-list does not have
        # to be told about a value before it can refuse it.
        for unknown in ("aborted", "timeout", "length", "tool_budget", "pause_turn"):
            with self.subTest(unknown=unknown):
                self.assertEqual(
                    "did-not-run 1", stop_verdict(self.payload(unknown))
                )

    def test_an_absent_stop_reason_is_did_not_run(self):
        self.assertEqual("did-not-run 0", stop_verdict(self.payload(None)))

    def test_a_quoted_mention_in_the_reviews_own_text_cannot_rescue_a_bad_stop(self):
        # The reviewer reads this repository, so its output can quote this very
        # file. JSON escapes the inner quotes, so `\\"stopReason\\"` never
        # presents the `"` the pattern needs — asserted rather than assumed,
        # because the whole check is a grep over the raw bytes.
        quoting = (
            '{"text": "the script greps \\"stopReason\\": \\"end_turn\\" here",'
            ' "stopReason": "max_tokens"}'
        )
        self.assertEqual("did-not-run 1", stop_verdict(quoting))

    def test_a_second_occurrence_fails_rather_than_picking_one(self):
        # A single object with a single such field is what `--output-format
        # json` emits, verified against grok 1.0.5. A second one means the shape
        # changed under the pin, which is a run to stop on rather than guess at.
        two = '{"stopReason": "end_turn", "modelUsage": {"stopReason": "end_turn"}}'
        self.assertEqual("did-not-run 2", stop_verdict(two))


class PatternsAreActuallyApplied(unittest.TestCase):
    """A declared pattern nothing applies is a gate that is not looking.

    This is the SOURCE_INPUTS lesson at one remove: the cases above prove what
    the patterns MATCH, and prove nothing about whether grok-review.sh still
    uses them. Both halves are needed, and the second is the one that rots.
    """

    def setUp(self):
        self.text = REVIEW.read_text(encoding="utf-8")

    def uses(self, name):
        # A use is a reference to the variable outside its own declaration.
        body = re.sub(rf"^{re.escape(name)}='[^']*'$", "", self.text, flags=re.M)
        return len(re.findall(rf'"\${re.escape(name)}"', body))

    def test_every_declared_pattern_has_at_least_one_call_site(self):
        for name in ("limit_re", "stop_any_re", "stop_ok_re"):
            with self.subTest(name=name):
                self.assertGreaterEqual(self.uses(name), 1, f"{name} is declared and never used")

    def code_lines(self):
        return [
            line
            for line in self.text.splitlines()
            if line.strip() and not line.lstrip().startswith("#")
        ]

    def test_the_limit_pattern_guards_every_skip_path(self):
        # Three usage-limit skips exist — the API key's own probe, the preflight
        # against whichever auth was selected, and the dead-fallback case — and
        # each has to consult the same pattern. A fourth skip added without one
        # would be a path that can never fire. Counted over code lines only: the
        # comments discuss exit 12 at length, and a prose mention is not a path.
        skips = [line for line in self.code_lines() if line.strip() == "exit 12"]
        guards = [line for line in self.code_lines() if 'grep -qiE "$limit_re"' in line]
        self.assertEqual(
            len(skips),
            len(guards),
            "every exit-12 skip must be guarded by the declared limit pattern",
        )
        self.assertGreaterEqual(len(skips), 3)

    def test_no_second_literal_copy_of_either_pattern_survives(self):
        # The drift that ends this way every time: a pattern declared once, then
        # spelled out again at a call site, and only one of the two ever updated.
        # Scoped to the two fields the declared patterns are ABOUT. A grep for
        # `cancellationCategory` beside the check is a diagnostic that prints an
        # extra detail and decides nothing, so it is not a second copy of a
        # judgement — where a second `"stopReason"` regex would be exactly that.
        stray = [
            line
            for line in self.code_lines()
            if ('"stopReason"' in line or "rate.?limit" in line)
            and not re.match(r"^\w+_re='", line)
            and "echo " not in line
        ]
        self.assertEqual([], stray, "a literal pattern copy has reappeared")


class ReservationIsTiedToTheModelCall(unittest.TestCase):
    """#59 — invocation and accounting have to be one operation.

    ship.md used to specify "reserve, then invoke the review helper" as prose
    over two separately granted commands. A run that skipped the first spent a
    check that left no record; a resumed run read a lower count and the PR ran
    past twelve. These are structural assertions because the behavioural ones
    need a Docker daemon and a paid API — but structure is exactly what failed,
    so structure is the right subject.
    """

    def setUp(self):
        self.text = REVIEW.read_text(encoding="utf-8")

    def test_the_review_helper_reserves_its_own_slot(self):
        self.assertIn("grok-ledger.sh", self.text)
        self.assertRegex(self.text, r'bash "\$ledger" "\$pr" reserve "\$slot" "\$mode"')

    def test_the_reservation_is_the_last_thing_before_the_review_runs(self):
        reserve = self.text.index('reserve "$slot"')
        run = self.text.index('grok -p "/review-branch"')
        self.assertLess(reserve, run, "the slot must be claimed before the model call")

    def test_every_usage_limit_skip_happens_before_the_reservation(self):
        # This is the accounting rule stated as an ordering, and it is what
        # makes a `release` verb unnecessary rather than merely unused: a slot is
        # reserved if and only if the review's model call was launched, so an
        # exit-12 skip has nothing to give back.
        reserve = self.text.index('reserve "$slot"')
        for match in re.finditer(r"^\s*exit 12$", self.text, re.MULTILINE):
            self.assertLess(
                match.start(),
                reserve,
                "an exit-12 skip after the reservation would spend a slot for a "
                "review that never ran, and nothing releases it",
            )

    def test_a_failed_reservation_stops_the_run_with_its_own_exit(self):
        self.assertIn("exit 13", self.text)
        self.assertLess(
            self.text.index("exit 13"),
            self.text.index('grok -p "/review-branch"'),
        )

    def test_the_ledger_still_understands_a_released_row(self):
        # The verb loses its caller, not its parser: `count` must keep folding a
        # released row out of a PR's existing history, and a human reconciling a
        # slot spent wrongly has nothing else to reach for.
        ledger = LEDGER.read_text(encoding="utf-8")
        self.assertIn("released: skipped on limits", ledger)


class ReviewArgumentValidation(unittest.TestCase):
    """Two arguments, checked against the ledger's own vocabulary.

    **The pull request is deliberately not one of them.** It was argument one for
    exactly one review round: the helper took a caller-supplied number and then
    cloned and reviewed the *current branch*, so a typo — or an instruction
    substituting another open pull request — spent someone else's slot while
    leaving this branch's cap re-armed. It is resolved from the branch now, which
    is why the old three-argument form has to be REFUSED rather than tolerated,
    and why that refusal has a case of its own below.

    Each case exits before anything is created, cloned or built, and before
    anything is asked of GitHub — which is what makes them safe to run with no
    Docker, no network and no token, and is asserted rather than assumed.
    """

    def run_review(self, *args):
        return subprocess.run(
            [BASH, str(REVIEW), *args],
            capture_output=True,
            text=True,
            cwd=str(SCRIPTS),
        )

    def test_the_no_argument_form_is_refused(self):
        result = self.run_review()
        self.assertEqual(2, result.returncode)
        self.assertIn("usage:", result.stderr)

    def test_the_pr_number_is_not_an_argument_at_all(self):
        # The defect this closes: the PR was argument one, so a numeric typo —
        # or an instruction substituting another open PR — posted the
        # reservation there while reviewing THIS branch, leaving this branch's
        # cap re-armed and spending someone else's slot. It is resolved from the
        # branch now, so the old three-argument form has to be REFUSED rather
        # than tolerated: a caller left on the old grant would otherwise pass a
        # PR number where the slot goes.
        result = self.run_review("134", "1", "full")
        self.assertEqual(2, result.returncode)
        self.assertIn("usage:", result.stderr)

    def test_the_pr_is_resolved_from_the_branch_being_cloned(self):
        text = REVIEW.read_text(encoding="utf-8")
        self.assertRegex(text, r'gh pr list --head "\$branch"')
        # And it is the same `$branch` the clone is checked out to, which is the
        # whole point — the slot and the review have to be one subject.
        self.assertRegex(text, r'git -C "\$work/repo" checkout --quiet "\$branch"')
        # Exactly one, with none and several both refused rather than guessed at.
        self.assertIn("exactly one open pull request", text)

    def test_a_slot_outside_the_cap_is_refused(self):
        for slot in ("0", "13", "12.0", "-1", "1 2"):
            with self.subTest(slot=slot):
                self.assertEqual(2, self.run_review(slot, "full").returncode)

    def test_a_mode_outside_the_two_is_refused(self):
        for mode in ("", "Full", "review", "full recheck"):
            with self.subTest(mode=mode):
                self.assertEqual(2, self.run_review("1", mode).returncode)

    def test_every_refusal_happens_before_anything_is_asked_of_github(self):
        # These cases run with no network and no token in CI, so a refusal that
        # reached `gh` would fail for the wrong reason — and a reader could not
        # tell the two apart from the exit code alone.
        for args in ((), ("13", "full"), ("1", "Full"), ("134", "1", "full")):
            with self.subTest(args=args):
                result = self.run_review(*args)
                self.assertEqual(2, result.returncode)
                self.assertNotIn("pull request", result.stderr)


class LedgerStub:
    """A `gh` on PATH that answers the two calls grok-ledger.sh makes.

    The rows are supplied post-jq: this stub cannot exercise the jq shape filter
    that keeps a ledger-looking line inside a longer comment from counting, and
    that is stated rather than glossed. What it does exercise is the trust check
    and the fold, which is where #51 lived.
    """

    def __init__(self, rows, permissions):
        self.dir = tempfile.mkdtemp(prefix="ledger-stub-")
        rows_file = Path(self.dir) / "rows"
        rows_file.write_text("".join(f"{r}\n" for r in rows), encoding="utf-8")
        perms_file = Path(self.dir) / "perms"
        perms_file.write_text(
            "".join(f"{k} {v}\n" for k, v in permissions.items()), encoding="utf-8"
        )
        gh = Path(self.dir) / "gh"
        gh.write_text(
            textwrap.dedent(
                f"""\
                #!/usr/bin/env bash
                # A stand-in for gh, driven by two files. Deliberately dumb:
                # it dispatches on the API path and nothing else.
                for arg in "$@"; do
                  case "$arg" in
                    */comments) exec cat {rows_file.as_posix()!r} ;;
                    */collaborators/*/permission)
                      login="${{arg#*/collaborators/}}"
                      login="${{login%/permission}}"
                      verdict=$(awk -v l="$login" '$1 == l {{ print $2 }}' {perms_file.as_posix()!r})
                      case "$verdict" in
                        admin|maintain|write|read|none) echo "$verdict"; exit 0 ;;
                        404) echo "gh: HTTP 404: Not Found" >&2; exit 1 ;;
                        *) echo "error connecting to api.github.com" >&2; exit 1 ;;
                      esac
                      ;;
                  esac
                done
                echo "stub gh: unexpected call: $*" >&2
                exit 99
                """
            ),
            encoding="utf-8",
        )
        gh.chmod(0o755)

    def run(self, *args):
        env = dict(os.environ)
        env["PATH"] = self.dir + os.pathsep + env["PATH"]
        return subprocess.run(
            [BASH, str(LEDGER), *args],
            capture_output=True,
            text=True,
            env=env,
        )

    def cleanup(self):
        shutil.rmtree(self.dir, ignore_errors=True)


class LedgerDoesNotFailOpen(unittest.TestCase):
    """#51 — the cap that re-armed on a network error.

    `exit 3` fired inside a `while` that was the last stage of a pipeline, so it
    ended a subshell rather than the script; the `awk` on the other side saw EOF,
    ran its END block, and printed `0`. A legitimately empty ledger prints the
    same byte, so the consumer — a model reading stdout — could not tell "nothing
    spent" from "the trust check never completed".
    """

    def ledger(self, rows, permissions):
        stub = LedgerStub(rows, permissions)
        self.addCleanup(stub.cleanup)
        return stub

    def test_a_failed_permission_lookup_prints_nothing_at_all(self):
        # The regression test for the whole issue. Both halves matter: a
        # non-zero exit AND an empty stdout. The old code satisfied the first.
        stub = self.ledger(
            ["101\talice\tGrok check 3/12 — reserved (full)"],
            {"alice": "network-error"},
        )
        result = stub.run("42", "count")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("", result.stdout.strip(), "a count was published on the error path")
        self.assertIn("permission", result.stderr)

    def test_status_does_not_publish_a_verdict_on_the_error_path(self):
        # The same shape one verb over: `unconverged` re-enters a loop that had
        # already converged.
        stub = self.ledger(
            ["101\talice\tGrok check 3/12 — converged: loop clean"],
            {"alice": "network-error"},
        )
        result = stub.run("42", "status")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual("", result.stdout.strip())

    def test_an_empty_ledger_still_legitimately_counts_zero(self):
        # The behaviour that made the two cases indistinguishable is deliberate
        # and stays: a fresh PR's ledger really is empty. Separating them had to
        # happen upstream of the fold, not inside it.
        stub = self.ledger([], {})
        result = stub.run("42", "count")
        self.assertEqual(0, result.returncode)
        self.assertEqual("0", result.stdout.strip())

    def test_a_trusted_reservation_counts_as_spent(self):
        stub = self.ledger(
            [
                "101\talice\tGrok check 1/12 — reserved (full)",
                "102\talice\tGrok check 2/12 — reserved (recheck)",
            ],
            {"alice": "write"},
        )
        self.assertEqual("2", stub.run("42", "count").stdout.strip())

    def test_a_released_slot_is_not_spent(self):
        stub = self.ledger(
            [
                "101\talice\tGrok check 1/12 — reserved (full)",
                "102\talice\tGrok check 2/12 — reserved (full)",
                "103\talice\tGrok check 2/12 — released: skipped on limits",
            ],
            {"alice": "admin"},
        )
        self.assertEqual("1", stub.run("42", "count").stdout.strip())

    def test_an_untrusted_author_is_not_state(self):
        # A 404 is an outside author and their rows are dropped — the case the
        # trust check exists for, and the one that must NOT stop the helper.
        stub = self.ledger(
            [
                "101\tdrive-by\tGrok check 12/12 — reserved (full)",
                "102\talice\tGrok check 1/12 — reserved (full)",
            ],
            {"drive-by": "404", "alice": "write"},
        )
        result = stub.run("42", "count")
        self.assertEqual(0, result.returncode)
        self.assertEqual("1", result.stdout.strip())

    def test_a_read_permission_is_not_write_and_is_dropped(self):
        stub = self.ledger(
            ["101\tobserver\tGrok check 9/12 — reserved (full)"],
            {"observer": "read"},
        )
        result = stub.run("42", "count")
        self.assertEqual(0, result.returncode)
        self.assertEqual("0", result.stdout.strip())

    def test_a_converged_marker_is_reported_and_a_later_reservation_supersedes(self):
        stub = self.ledger(
            ["101\talice\tGrok check 4/12 — converged: loop clean"],
            {"alice": "write"},
        )
        self.assertEqual("converged", stub.run("42", "status").stdout.strip())

        stub = self.ledger(
            [
                "101\talice\tGrok check 4/12 — converged: loop clean",
                "102\talice\tGrok check 5/12 — reserved (full)",
            ],
            {"alice": "write"},
        )
        self.assertEqual("unconverged", stub.run("42", "status").stdout.strip())

    def test_no_consumer_pipes_the_row_reader_directly(self):
        # The structural half. The behavioural tests above prove the three
        # consumers that exist are safe; this one is why a fourth cannot
        # reintroduce the defect by being written in the obvious way.
        text = LEDGER.read_text(encoding="utf-8")
        code = "\n".join(
            line for line in text.splitlines() if not line.lstrip().startswith("#")
        )
        self.assertNotIn("ledger_rows |", code)
        self.assertEqual(1, code.count("rows=$(ledger_rows)"))


class SweepWorktreeShape(unittest.TestCase):
    """#75 items 1 and 2 — the guard that was narrower than advertised.

    A bash `case` does no pathname expansion, so `?` matched `/` and
    `$tmproot/secsweep-a/bbbb` passed a check whose comment called it a
    direct-child check. Narrow in practice — reaching the drop helper also needs
    the path to be a registered worktree of this repo — but a guard that does not
    do what it says is the class this repository files at critical.
    """

    def setUp(self):
        self.tmproot = bash_tmproot()

    def make_dir(self, relative):
        """Create the fixture through bash, so it lands where the script looks."""
        path = f"{self.tmproot}/{relative}"
        made = run_bash('mkdir -p "$TARGET"', TARGET=path)
        self.assertEqual(0, made.returncode, made.stderr)
        top = f"{self.tmproot}/{relative.split('/')[0]}"
        self.addCleanup(lambda: run_bash('rm -rf "$TARGET"', TARGET=top))
        return path

    def drop(self, path):
        return run_bash('bash "$DROP" "$TARGET"', DROP=str(DROP), TARGET=path)

    def test_a_nested_path_under_a_sweep_shaped_parent_is_refused(self):
        # The issue's own table, reproduced. Both of these used to MATCH, because
        # `?` matches `/` in a bash `case`.
        for nested in ("secsweep-a/bbbb", "secsweep-ab/ccc"):
            with self.subTest(nested=nested):
                path = self.make_dir(nested)
                result = self.drop(path)
                self.assertEqual(2, result.returncode)
                self.assertIn("direct child", result.stderr)

    def test_the_prefix_and_length_controls_still_refuse(self):
        for name in ("secsweep-abc12", "other-abc123", "secsweep-abc1234"):
            with self.subTest(name=name):
                path = self.make_dir(name)
                result = self.drop(path)
                self.assertEqual(2, result.returncode)
                self.assertIn("sweep-shaped", result.stderr)

    def test_the_detach_helper_takes_a_commit_and_nothing_else(self):
        # Item 1: the path is made here, so no caller supplies one and both
        # sweeps drop their `Bash(mktemp:*)` grant — which took an arbitrary
        # template and was therefore a filesystem-write primitive.
        result = subprocess.run(
            [BASH, str(DETACH)], capture_output=True, text=True, cwd=str(SCRIPTS)
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("usage: git-worktree-detach.sh <commit-sha>", result.stderr)

        two_args = subprocess.run(
            [BASH, str(DETACH), "/tmp/anywhere", "0" * 40],
            capture_output=True,
            text=True,
            cwd=str(SCRIPTS),
        )
        self.assertEqual(2, two_args.returncode)

    def sweep_dirs(self):
        listing = run_bash('ls -d "$ROOT"/secsweep-* 2>/dev/null || true', ROOT=self.tmproot)
        return set(listing.stdout.split())

    def test_a_bad_commit_creates_no_directory(self):
        # The argument is checked before anything is made, so a refused call
        # leaves no litter behind for the next run to trip over.
        before = self.sweep_dirs()
        result = run_bash('bash "$DETACH" not-a-sha', DETACH=str(DETACH))
        self.assertEqual(2, result.returncode)
        self.assertEqual(before, self.sweep_dirs())

    def test_the_helper_returns_only_the_path_on_stdout(self):
        # `git worktree add` writes "HEAD is now at <sha> <subject>" to STDOUT,
        # so an unredirected call made this helper's return value a commit
        # message followed by a path — and the teardown then failed with a `not
        # an existing directory` naming the whole subject line.
        #
        # **This runs the real round trip rather than grepping the source**, and
        # the first version of this test did the latter. Two source strings
        # existing proves nothing about what a caller captures: an added debug
        # `echo`, or any other command leaking a line, recreates the defect with
        # the assertions green. So: detach at a real commit, capture stdout
        # exactly as `security-sweep.md` does, and assert it is one line and a
        # directory. Raised by a reviewer against the structural version.
        repo = str(SCRIPTS.parent.parent)
        pinned = run_bash('cd "$REPO" && git rev-parse HEAD', REPO=repo).stdout.strip()
        self.assertRegex(pinned, r"^[0-9a-f]{40}$")

        made = run_bash(
            'cd "$REPO" && bash "$DETACH" "$SHA" 2>/dev/null',
            REPO=repo, DETACH=str(DETACH), SHA=pinned,
        )
        path = made.stdout.strip()
        # Registered before anything is asserted, so a failing assertion below
        # still tears the worktree down rather than leaving one for the next run.
        self.addCleanup(
            lambda: run_bash(
                'cd "$REPO" && bash "$DROP" "$TARGET" >/dev/null 2>&1 || true',
                REPO=repo, DROP=str(DROP), TARGET=path,
            )
        )
        self.assertEqual(0, made.returncode, made.stderr)
        self.assertEqual(
            path + "\n", made.stdout,
            "stdout must be the path and nothing else — no banner, no commit subject",
        )
        self.assertEqual(1, len(made.stdout.strip().split("\n")))
        self.assertEqual(
            "yes",
            run_bash('[ -d "$P" ] && echo yes || echo no', P=path).stdout.strip(),
            "the returned string must be usable as a directory by the caller",
        )
        # And it is the shape the drop helper will later require, which is the
        # agreement between the two ends of a sweep's lifetime.
        self.assertEqual(self.tmproot, run_bash('dirname "$P"', P=path).stdout.strip())
        self.assertRegex(run_bash('basename "$P"', P=path).stdout.strip(), r"^secsweep-.{6}$")


class LabelStub:
    """A `gh` on PATH answering the three calls gh-label-ensure.sh makes.

    It records every argv it is handed, which is what turns "never spells
    `--force`" from a grep over the source into an assertion about the command
    that actually ran. `label list` answers from `labels_before` until a create
    has been attempted and from `labels_after` afterwards, which is how the
    concurrent-create branch gets a race to lose.
    """

    def __init__(self, before=(), after=None, repo="acme/widgets",
                 repo_fails=False, list_fails=False, create_fails=False):
        self.dir = tempfile.mkdtemp(prefix="label-stub-")
        d = Path(self.dir)
        (d / "before").write_text("".join(f"{x}\n" for x in before), encoding="utf-8")
        (d / "after").write_text(
            "".join(f"{x}\n" for x in (before if after is None else after)),
            encoding="utf-8",
        )
        gh = d / "gh"
        gh.write_text(
            textwrap.dedent(
                f"""\
                #!/usr/bin/env bash
                printf '%s\\n' "$*" >> {(d / 'argv').as_posix()!r}
                case "$*" in
                  *"repo view"*)
                    {"exit 7" if repo_fails else f'echo {repo!r}; exit 0'}
                    ;;
                  *"label list"*)
                    {"exit 7" if list_fails else ""}
                    if [ -f {(d / 'created').as_posix()!r} ]; then
                      cat {(d / 'after').as_posix()!r}
                    else
                      cat {(d / 'before').as_posix()!r}
                    fi
                    exit 0
                    ;;
                  *"label create"*)
                    touch {(d / 'created').as_posix()!r}
                    {"exit 1" if create_fails else "exit 0"}
                    ;;
                esac
                echo "stub gh: unexpected call: $*" >&2
                exit 99
                """
            ),
            encoding="utf-8",
        )
        gh.chmod(0o755)

    def run(self, *args):
        env = dict(os.environ)
        env["PATH"] = self.dir + os.pathsep + env["PATH"]
        return subprocess.run(
            [BASH, str(SCRIPTS / "gh-label-ensure.sh"), *args],
            capture_output=True, text=True, env=env,
        )

    def calls(self):
        f = Path(self.dir) / "argv"
        return f.read_text(encoding="utf-8").splitlines() if f.exists() else []

    def cleanup(self):
        shutil.rmtree(self.dir, ignore_errors=True)


class LabelHelperBehaviour(unittest.TestCase):
    """The paths a source grep cannot reach.

    The class below asserts what the helper REFUSES and what its text does not
    contain; every one of those cases exits before `gh` is reached. So the
    existing-label, successful-create, concurrent-create and genuine-failure
    branches were all unexercised — including the concurrent one, which was
    added in the same review round that this class answers. A reviewer pointed
    that out, and it is the gate-coverage rule turned on the suite itself: a
    branch no case reaches can regress with CI green.
    """

    def stub(self, **kw):
        s = LabelStub(**kw)
        self.addCleanup(s.cleanup)
        return s

    def test_an_existing_label_is_left_alone(self):
        s = self.stub(before=["security", "bug"])
        r = s.run("security")
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertIn("already exists", r.stdout)
        self.assertFalse(
            [c for c in s.calls() if "label create" in c],
            "an existing label must never reach `gh label create`",
        )

    def test_a_missing_label_is_created_with_no_free_parameter(self):
        s = self.stub(before=[])
        r = s.run("medium")
        self.assertEqual(0, r.returncode, r.stderr)
        create = [c for c in s.calls() if "label create" in c]
        self.assertEqual(1, len(create))
        # The whole point of the helper, asserted against the command that ran
        # rather than against the source that spells it.
        self.assertIn("--repo acme/widgets", create[0])
        self.assertIn("--color fbca04", create[0])
        self.assertNotIn("--force", create[0])
        self.assertNotIn(" -f ", create[0])

    def test_a_create_lost_to_a_concurrent_sweep_succeeds(self):
        # Two sweeps race; this one loses. `gh label create` refuses a name that
        # exists — correctly, since --force is the flag this helper exists not
        # to use — so without the re-read the loser aborts over a label that is
        # now exactly what it asked for.
        s = self.stub(before=[], after=["low"], create_fails=True)
        r = s.run("low")
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertIn("concurrently", r.stdout)

    def test_a_create_that_genuinely_failed_is_reported(self):
        # The other side of the same ambiguity: absent afterwards means the
        # create really failed, and assuming success there would be the
        # fail-open the re-read exists to avoid.
        s = self.stub(before=[], after=[], create_fails=True)
        r = s.run("high")
        self.assertNotEqual(0, r.returncode)
        self.assertIn("could not create", r.stderr)

    def test_a_near_miss_on_search_is_not_a_match(self):
        # `--search` matches rather than equals, so searching `low` also returns
        # `slow`. Concluding "already there" from that would leave the sweep
        # filing against a label that does not exist.
        s = self.stub(before=["slow", "lower"])
        r = s.run("low")
        self.assertEqual(0, r.returncode, r.stderr)
        self.assertTrue([c for c in s.calls() if "label create" in c])

    def test_an_unresolvable_repository_stops_the_helper(self):
        s = self.stub(repo_fails=True)
        r = s.run("bug")
        self.assertNotEqual(0, r.returncode)
        self.assertIn("cannot resolve", r.stderr)
        self.assertFalse([c for c in s.calls() if "label create" in c])

    def test_a_failed_listing_stops_the_helper(self):
        # Never fail open: an unreadable label list is not an absent label.
        s = self.stub(list_fails=True)
        r = s.run("bug")
        self.assertNotEqual(0, r.returncode)
        self.assertIn("cannot list", r.stderr)
        self.assertFalse([c for c in s.calls() if "label create" in c])

    def test_every_label_in_the_vocabulary_can_actually_be_created(self):
        # The class below asserts the six names appear in the case; this asserts
        # each one reaches `gh` with its own colour, so a mistyped branch cannot
        # hide behind a source match.
        for label in ("security", "bug", "critical", "high", "medium", "low"):
            with self.subTest(label=label):
                s = self.stub(before=[])
                r = s.run(label)
                self.assertEqual(0, r.returncode, r.stderr)
                create = [c for c in s.calls() if "label create" in c]
                self.assertEqual(1, len(create))
                self.assertRegex(create[0], rf"label create {label} ")
                self.assertRegex(create[0], r"--color [0-9a-f]{6}")


class LabelHelperHasNoFreeParameter(unittest.TestCase):
    """#75 item 3 — `gh label create` is create-or-overwrite.

    `--force` updates an existing label's colour and description, and `-R`
    unpinned puts that write in any repository. Both were held as prose in each
    command, which is a rule a reader enforces and a finding can talk past.
    """

    HELPER = SCRIPTS / "gh-label-ensure.sh"

    def run_helper(self, *args):
        return subprocess.run(
            [BASH, str(self.HELPER), *args], capture_output=True, text=True
        )

    def test_a_name_outside_the_vocabulary_never_reaches_gh(self):
        # Refused by the case, before `gh repo view` is called — so this test
        # needs no network and proves the refusal is the first thing that
        # happens rather than a message printed after a lookup.
        for name in ("nonsense", "security --force", "-R other/repo", ""):
            with self.subTest(name=name):
                result = self.run_helper(name)
                self.assertEqual(2, result.returncode)

    def test_it_takes_exactly_one_argument(self):
        self.assertEqual(2, self.run_helper().returncode)
        self.assertEqual(2, self.run_helper("security", "--force").returncode)

    def test_force_is_never_spelled(self):
        text = self.HELPER.read_text(encoding="utf-8")
        code = "\n".join(
            line for line in text.splitlines() if not line.lstrip().startswith("#")
        )
        self.assertNotIn("--force", code)
        self.assertNotIn(" -f ", code)

    def test_the_repository_is_resolved_rather_than_accepted(self):
        text = self.HELPER.read_text(encoding="utf-8")
        self.assertIn("gh repo view --json nameWithOwner", text)
        self.assertIn('--repo "$repo"', text)

    def test_the_vocabulary_is_the_six_labels_the_sweeps_apply(self):
        text = self.HELPER.read_text(encoding="utf-8")
        for label in ("security", "bug", "critical", "high", "medium", "low"):
            with self.subTest(label=label):
                self.assertRegex(text, rf"(?m)^\s*{label}\)\s+colour=")


if __name__ == "__main__":
    unittest.main()
