"""What `.claude/hooks/guard-edit-target.py` refuses, and what it must not.

**The gate-coverage lesson is the reason this file is shaped the way it is.**
Every case below judges the hook directly, which says nothing about whether the
harness ever calls it — so the last class here has the registration itself as
its subject, and it is the one that fails if the matcher stops naming a tool
that writes.

**The link cases are run against a real link on both platforms, and neither is
a skip.** A skip on a missing capability reports a pass, which is the fail-open
this repository refused when `dotnet test` was made to need a real Docker
daemon. POSIX gets a symbolic link; Windows refuses one to an unprivileged
process — measured, `WinError 1314` — and gets a directory junction instead,
which is the same property one component up: a path whose spelling is inside an
allowed tree and whose resolution is not. If neither is available this module
fails rather than passing quietly.
"""

import importlib.util
import json
import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
HOOK = SCRIPTS.parent / "hooks" / "guard-edit-target.py"
SETTINGS = SCRIPTS.parent / "settings.json"

SYMLINKS = False
JUNCTIONS = None


def setUpModule():
    global SYMLINKS, JUNCTIONS
    probe = tempfile.mkdtemp()
    target = os.path.join(probe, "target")
    os.mkdir(target)
    try:
        os.symlink(target, os.path.join(probe, "link"),
                   target_is_directory=True)
        SYMLINKS = True
    except (OSError, NotImplementedError, AttributeError):
        SYMLINKS = False
    if not SYMLINKS:
        try:
            from _winapi import CreateJunction
        except ImportError:
            CreateJunction = None
        if CreateJunction is not None:
            CreateJunction(target, os.path.join(probe, "junction"))
            JUNCTIONS = CreateJunction

    if not SYMLINKS and JUNCTIONS is None:
        raise AssertionError(
            "this platform grants neither a symbolic link nor a junction, so "
            "the guard's whole subject is unreachable here and a green run "
            "would mean nothing")

    if not HOOK.exists():
        raise AssertionError(f"the hook is missing: {HOOK}")


class GuardCase(unittest.TestCase):
    """A scratch checkout, the shapes a link can take in it, and the verdict."""

    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="guard-root-")
        self.outside = tempfile.mkdtemp(prefix="guard-outside-")
        for tree in ("docs", os.path.join(".claude", "scripts")):
            os.makedirs(os.path.join(self.root, tree), exist_ok=True)
        self.write(os.path.join(self.root, "docs", "chapter.md"), "prose\n")
        self.write(
            os.path.join(self.root, ".claude", "scripts", "helper.sh"), "ok\n")
        self.write(os.path.join(self.outside, "loot.txt"), "secret\n")

    @staticmethod
    def write(path, text):
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)

    def link_to(self, name, real_target):
        """A path spelled under `docs/` whose resolution is `real_target`.

        With symbolic links the link IS the target's spelling. With junctions —
        which is all Windows grants an unprivileged process — the link is the
        target's *directory* and the file is named beneath it. Both produce a
        path inside an allowed tree that resolves outside it, which is the
        property every case here is about; neither is a weaker form of the
        other, and writing the tests against this helper is what keeps the
        Windows run from being a different suite.
        """
        linkpath = os.path.join(self.root, "docs", name)
        if SYMLINKS:
            os.symlink(real_target, linkpath,
                       target_is_directory=os.path.isdir(real_target))
            return linkpath
        if os.path.isdir(real_target):
            JUNCTIONS(real_target, linkpath)
            return linkpath
        JUNCTIONS(os.path.dirname(real_target), linkpath)
        return os.path.join(linkpath, os.path.basename(real_target))

    def judge(self, file_path, tool="Edit", cwd=None, key="file_path"):
        """The hook's verdict on one call: the reason, or `None` for allowed."""
        event = {
            "hook_event_name": "PreToolUse",
            "cwd": self.root if cwd is None else cwd,
            "tool_name": tool,
            "tool_input": {} if file_path is None else {key: file_path},
        }
        result = subprocess.run(
            [sys.executable, str(HOOK)],
            input=json.dumps(event), capture_output=True, text=True,
            env={**os.environ, "CLAUDE_PROJECT_DIR": self.root},
        )
        self.assertEqual(
            0, result.returncode,
            f"the hook must return a decision, not a traceback: {result.stderr}")
        if not result.stdout.strip():
            return None
        payload = json.loads(result.stdout)["hookSpecificOutput"]
        self.assertEqual("PreToolUse", payload["hookEventName"])
        self.assertEqual("deny", payload["permissionDecision"])
        return payload["permissionDecisionReason"]

    def assertRefused(self, file_path, **kwargs):
        reason = self.judge(file_path, **kwargs)
        self.assertIsNotNone(reason, f"admitted: {file_path}")
        return reason

    def assertAdmitted(self, file_path, **kwargs):
        reason = self.judge(file_path, **kwargs)
        self.assertIsNone(reason, f"refused: {file_path} — {reason}")


class ALinkIsNotTheFileItIsSpelledAs(GuardCase):

    def test_a_link_into_a_denied_tree_is_refused(self):
        # #181 exactly: `/review-grok` holds `Edit` for `docs/` and denies
        # `.claude/**`, and both denies are matched on the spelling. A link
        # under `docs/` pointing into the machinery is a path the deny never
        # sees and a write the machinery receives.
        target = os.path.join(self.root, ".claude", "scripts", "helper.sh")
        reason = self.assertRefused(self.link_to("pwned", target))
        self.assertIn("resolves elsewhere in it", reason)
        self.assertIn("helper.sh", reason)

    def test_a_link_out_of_the_checkout_is_refused(self):
        # The other half of the issue's sentence, and the one a tree deny could
        # never have covered: no repository-relative pattern says anything
        # about a path that stops being repository-relative on resolution.
        reason = self.assertRefused(
            self.link_to("escape", os.path.join(self.outside, "loot.txt")))
        self.assertIn("outside the checkout", reason)

    def test_a_link_in_the_middle_of_the_path_is_refused(self):
        # The component that is a link need not be the last one, and a guard
        # that only stats the leaf would admit this. Under junctions this is
        # the shape every case takes; under symlinks it is a case of its own,
        # which is why it is written out rather than left to the helper.
        linkdir = os.path.join(self.root, "docs", "tree")
        if SYMLINKS:
            os.symlink(os.path.join(self.root, ".claude"), linkdir,
                       target_is_directory=True)
        else:
            JUNCTIONS(os.path.join(self.root, ".claude"), linkdir)
        reason = self.assertRefused(
            os.path.join(linkdir, "scripts", "helper.sh"))
        self.assertIn("resolves elsewhere in it", reason)

    def test_a_dotdot_after_a_link_is_judged_the_way_the_kernel_resolves_it(self):
        # **The two platforms genuinely disagree here, and the guard follows
        # each rather than picking one.** POSIX resolves `..` against the
        # link's TARGET, so `docs/tree/../settings.json` through a link into
        # `.claude/scripts` lands on `.claude/settings.json` — a lexical reader
        # of that path would say `docs/`, and the hook resolves the original
        # spelling for exactly this case. Windows' path parser collapses `..`
        # BEFORE the filesystem sees it, so the same spelling really does write
        # `docs/settings.json`. Measured, not reasoned about: writing through
        # the junctioned form created the file under `docs/` and left
        # `.claude/` untouched.
        #
        # So the assertion is the platform's own semantics, and a guard that
        # refused on Windows would be refusing a write that is exactly what it
        # says it is.
        linkdir = os.path.join(self.root, "docs", "tree")
        real = os.path.join(self.root, ".claude", "scripts")
        if SYMLINKS:
            os.symlink(real, linkdir, target_is_directory=True)
        else:
            JUNCTIONS(real, linkdir)
        spelled = os.path.join(linkdir, "..", "settings.json")
        if SYMLINKS:
            self.assertIn("resolves elsewhere in it", self.assertRefused(spelled))
        else:
            self.assertAdmitted(spelled)

    def test_a_notebook_path_is_judged_too(self):
        # `NotebookEdit` carries its target under another key, and a guard that
        # reads only `file_path` would wave the whole tool through while the
        # matcher says it is covered.
        target = os.path.join(self.root, ".claude", "scripts", "helper.sh")
        self.assertRefused(self.link_to("book", target),
                           tool="NotebookEdit", key="notebook_path")


class TheOrdinaryWriteIsNotDisturbed(GuardCase):
    """The false-positive half, and it is the half that breaks a session."""

    def test_a_real_file_in_an_allowed_tree_is_admitted(self):
        self.assertAdmitted(os.path.join(self.root, "docs", "chapter.md"))

    def test_a_file_that_does_not_exist_yet_is_admitted(self):
        # `Write` creates, so the commonest target of all is a path with no
        # file behind it. `realpath` resolves the existing prefix and appends
        # the rest, which is what makes this work — asserted rather than
        # assumed, because a guard that refused every new file would be found
        # by its first user rather than by this suite.
        self.assertAdmitted(os.path.join(self.root, "docs", "new-chapter.md"))
        self.assertAdmitted(os.path.join(self.root, "docs", "sub", "deep.md"))

    def test_a_denied_tree_spelled_as_itself_is_admitted_here(self):
        # **The control that keeps this file from becoming a second deny
        # list.** `.claude/scripts/**` is denied by `.claude/settings.json` and
        # by two commands' frontmatter, and it is denied there on the spelling
        # — which is correct when the spelling is true of the file. If this
        # hook refused it as well it would hold a copy of a list it cannot see
        # changing, and it would lock the repository out of its own control
        # surface: the PR that lifts a deny to edit a helper would be refused
        # by the guard instead.
        self.assertAdmitted(
            os.path.join(self.root, ".claude", "scripts", "helper.sh"))

    def test_a_dotdot_that_stays_where_it_is_spelled_is_admitted(self):
        self.assertAdmitted(
            os.path.join(self.root, "docs", "..", "docs", "chapter.md"))

    def test_a_relative_path_is_resolved_against_the_events_cwd(self):
        # The harness sends absolute paths today. This is what the hook does if
        # that changes, and the answer must not be "resolve against whatever
        # directory the hook process happens to have inherited".
        self.assertAdmitted(os.path.join("docs", "chapter.md"))

    def test_a_checkout_reached_through_a_link_is_not_refused_wholesale(self):
        # **The false positive that would have taken the delivery chain
        # down.** `/tmp` is a link to `/private/tmp` on macOS and a worktree
        # path on Windows can arrive 8.3-shortened or through `subst`, so the
        # session's own root resolves to a different spelling — and a guard
        # comparing the raw resolution against the raw spelling would refuse
        # every edit in it. The anchor is resolved too, which is what makes
        # this pass.
        alias = os.path.join(self.outside, "checkout")
        if SYMLINKS:
            os.symlink(self.root, alias, target_is_directory=True)
        else:
            JUNCTIONS(self.root, alias)
        self.assertAdmitted(os.path.join(alias, "docs", "chapter.md"),
                            cwd=alias)

    def test_the_case_of_a_windows_spelling_is_not_a_difference(self):
        # Windows' `realpath` answers with the on-disk case, so `DOCS` comes
        # back as `docs` and a case-sensitive comparison would read that as a
        # link. On POSIX the upper-case path simply does not exist and resolves
        # to itself, so the same assertion holds for a different reason.
        self.assertAdmitted(os.path.join(self.root, "DOCS", "chapter.md"))


class WhatThisGuardIsNotTheSubjectOf(GuardCase):
    """The residuals, written as passing cases so nobody assumes they closed."""

    def test_a_path_outside_every_checkout_is_not_judged(self):
        # **Stated in the hook's docstring and pinned here.** The harness
        # writes the session's own memory and scratchpad by absolute path
        # outside the repository, and refusing those would take them with it.
        # Nothing in the exposure this closes can spell one: a review row is
        # one plain repository-relative path, and the adjudicator drops a row
        # that is not. If this starts being refused, the docstring's residual
        # paragraph is what needs rewriting.
        self.assertAdmitted(os.path.join(self.outside, "loot.txt"))

    def test_a_tool_that_does_not_write_is_not_judged(self):
        target = os.path.join(self.root, ".claude", "scripts", "helper.sh")
        link = self.link_to("read-me", target)
        self.assertAdmitted(link, tool="Read")
        self.assertAdmitted(link, tool="Bash")

    def test_a_write_with_no_path_to_judge_is_refused(self):
        # The other direction from the fail-open below, and deliberately so: an
        # unreadable EVENT establishes nothing about the session, where a
        # matched tool carrying no path establishes nothing about a write that
        # is about to happen.
        reason = self.assertRefused(None)
        self.assertIn("no file path", reason)

    def test_a_tool_input_that_is_not_an_object_is_refused_the_same_way(self):
        # The same statement about the same call — this file cannot see where
        # the write lands — and it used to get the opposite answer: a
        # `tool_input` of the wrong shape was admitted while a missing key was
        # refused. One of those two fails closed.
        for payload in ([], "file_path", 7):
            with self.subTest(payload=payload):
                event = {
                    "hook_event_name": "PreToolUse",
                    "cwd": self.root,
                    "tool_name": "Edit",
                    "tool_input": payload,
                }
                result = subprocess.run(
                    [sys.executable, str(HOOK)],
                    input=json.dumps(event), capture_output=True, text=True,
                )
                self.assertEqual(0, result.returncode)
                verdict = json.loads(result.stdout)["hookSpecificOutput"]
                self.assertEqual("deny", verdict["permissionDecision"])
                self.assertIn("no file path", verdict["permissionDecisionReason"])

    def test_a_malformed_event_does_not_take_the_session_down(self):
        # The one deliberate fail-OPEN, argued in the hook and pinned here the
        # way the argv guard's is: refusing every write because this file
        # cannot read its own input would turn a defect in it into a session
        # that can no longer edit anything.
        for payload in ("not json at all", "[1, 2, 3]", ""):
            with self.subTest(payload=payload):
                result = subprocess.run(
                    [sys.executable, str(HOOK)],
                    input=payload, capture_output=True, text=True,
                )
                self.assertEqual(0, result.returncode)
                self.assertEqual("", result.stdout.strip())
                self.assertIn("guard-edit-target", result.stderr)


class TheWiringWithoutWhichNoneOfTheAboveRuns(unittest.TestCase):

    def registered(self):
        settings = json.loads(SETTINGS.read_text(encoding="utf-8"))
        return settings.get("hooks", {}).get("PreToolUse", [])

    def guard_module(self):
        """The hook imported directly, for the one list only it holds."""
        spec = importlib.util.spec_from_file_location("guard_edit_target", HOOK)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module

    def test_the_hook_is_registered_for_every_tool_that_writes(self):
        # The subject is the matcher, not the verdict: every case above passes
        # against a hook the harness never calls, and a matcher naming `Edit`
        # alone would leave `Write` — the tool that creates the file — unjudged.
        #
        # **The asserted set is read from the hook rather than written out
        # here**, and the first version of this case wrote out three of the
        # four. `MultiEdit` was in the matcher and in `EDITING_TOOLS` and in no
        # assertion, so dropping it from the matcher would have left that tool
        # unguarded with this test still green — the gate-coverage failure
        # `CLAUDE.md` calls this repository's most-repeated, inside the test
        # written to catch it. Raised by Copilot against the first push.
        # Deriving the set is what makes a tool added to the hook a red test
        # rather than a silent gap.
        matchers = [
            (entry.get("matcher") or "", entry.get("hooks") or [])
            for entry in self.registered()
        ]
        mine = [
            matcher for matcher, hooks in matchers
            if any(HOOK.name in (h.get("command") or "") for h in hooks)
        ]
        self.assertTrue(mine, f"{HOOK.name} is registered for nothing")
        tools = self.guard_module().EDITING_TOOLS
        # The positive control: an empty or shrunken list would satisfy the
        # loop below by having nothing to check, which is the vacuous pass this
        # repository keeps finding in its own gates.
        self.assertGreaterEqual(len(tools), 4, f"EDITING_TOOLS shrank: {tools}")
        for tool in tools:
            with self.subTest(tool=tool):
                self.assertTrue(
                    any(re.fullmatch(matcher, tool) for matcher in mine),
                    f"no registered matcher selects {tool}: {mine}")

        # And the other direction, because a matcher wider than the hook's own
        # list would send it calls it answers by refusing for want of a path.
        for matcher in mine:
            for alternative in matcher.split("|"):
                with self.subTest(matcher=matcher, alternative=alternative):
                    self.assertIn(alternative, tools)

    def test_the_hook_runs_on_the_312_floor(self):
        commands = [
            h.get("command") or ""
            for entry in self.registered() for h in (entry.get("hooks") or [])
            if HOOK.name in (h.get("command") or "")
        ]
        self.assertTrue(commands)
        for command in commands:
            with self.subTest(command=command):
                self.assertIn("py -3.12", command)
                self.assertIn("CLAUDE_PROJECT_DIR", command)

    def test_the_argv_guard_is_still_registered_beside_it(self):
        # A second entry under the same event is the shape most likely to be
        # written as a replacement rather than an addition, and the git guard
        # going quiet would be invisible: its own suite judges it directly too.
        commands = [
            h.get("command") or ""
            for entry in self.registered() for h in (entry.get("hooks") or [])
        ]
        self.assertTrue(any("guard-git-argv.py" in c for c in commands),
                        f"the argv guard is no longer registered: {commands}")


if __name__ == "__main__":
    unittest.main()
