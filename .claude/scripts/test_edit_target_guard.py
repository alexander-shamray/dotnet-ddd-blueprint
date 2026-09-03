"""What `.claude/hooks/guard-edit-target.py` refuses, and what it must not.

**The gate-coverage lesson is the reason this file is shaped the way it is.**
Every case below judges the hook directly, which says nothing about whether the
harness ever calls it — so the last class here has the registration itself as
its subject, and it is the one that fails if the matcher stops naming a tool
that writes.

**The link cases run against a real link, and against EVERY primitive the
platform grants rather than the first one found.** A symbolic link where the
session may make one; a directory junction on Windows, which is all an
unprivileged process gets there — measured, `WinError 1314` — and which is the
same property one component up: a path whose spelling is inside an allowed tree
and whose resolution is not. Taking the first would have left the junction
fallback unexercised on the one platform that needs it, since the GitHub
Windows runner turns out to hold `SeCreateSymbolicLinkPrivilege` and reports
`symlink, junction`.

**Neither is a skip.** A skip on a missing capability reports a pass, which is
the fail-open this repository refused when `dotnet test` was made to need a
real Docker daemon; where a platform grants no primitive at all this module
fails rather than passing quietly.

**Three platform properties are asserted as the platform's own**, because the
guard follows each rather than picking one: `..` after a link, which POSIX
resolves against the link's target and Windows collapses first; case folding,
which is the *filesystem's* answer rather than the platform's; and which link
primitives exist. CI runs this module on Linux, Windows and macOS for exactly
that reason.
"""

import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unicodedata
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parent
HOOK = SCRIPTS.parent / "hooks" / "guard-edit-target.py"
SETTINGS = SCRIPTS.parent / "settings.json"

SYMLINKS = False
JUNCTIONS = None


def setUpModule():
    """Find every link primitive this platform grants, not the first one.

    **Every case below runs against all of them**, and taking the first was a
    defect rather than a simplification: a Windows runner whose account holds
    `SeCreateSymbolicLinkPrivilege` gets symbolic links, so the junction
    fallback — the only primitive an unprivileged Windows session has — would
    have gone unexercised on the one platform that needs it. Raised by Copilot
    against the CI coverage; found by the fix.
    """
    global SYMLINKS, JUNCTIONS
    # Removed at the end of this function rather than left behind: every run,
    # local or CI, was leaving one populated tree in the temp directory.
    # Raised by Copilot, beside the same defect in the per-case fixtures.
    probe = tempfile.mkdtemp()
    target = os.path.join(probe, "target")
    os.mkdir(target)
    try:
        os.symlink(target, os.path.join(probe, "link"),
                   target_is_directory=True)
        SYMLINKS = True
    except (OSError, NotImplementedError, AttributeError):
        SYMLINKS = False
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

    # **Say what was exercised, because a green run does not.** `unittest`
    # prints a subtest's name only when it fails, so a Windows job that had
    # symbolic links and never reached the junction fallback is indexed
    # identically to one that ran both — and which of the two happened is the
    # whole reason that job exists. One line in the log answers it.
    print(f"link primitives exercised: {', '.join(linkers())}", file=sys.stderr)
    shutil.rmtree(probe, ignore_errors=True)


def linkers():
    """The link primitives available here, by name. Never empty."""
    names = []
    if SYMLINKS:
        names.append("symlink")
    if JUNCTIONS is not None:
        names.append("junction")
    return names


# **The two platforms disagree about `..` after a link, and the discriminator
# is the PLATFORM rather than the primitive.** POSIX resolves `..` against the
# link's target; Windows' path parser collapses it before the filesystem is
# consulted, through a junction and through a symbolic link alike. Reading that
# off `SYMLINKS` — as the first version did — is right only while symbolic
# links and POSIX coincide, which a privileged Windows runner breaks.
DOTDOT_IS_LEXICAL = os.name == "nt"


class GuardCase(unittest.TestCase):
    """A scratch checkout, the shapes a link can take in it, and the verdict."""

    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="guard-root-")
        self.outside = tempfile.mkdtemp(prefix="guard-outside-")
        # **Both fixtures are removed, and the order is load-bearing.** Every
        # case here makes links from the checkout into `outside`, so the
        # checkout goes first: `addCleanup` runs last-registered-first, which
        # is why `outside` is registered before it. `ignore_errors` because a
        # link left dangling by the other order is not a test failure worth
        # reporting, and a link that `rmtree` declines to follow is the
        # behaviour we want rather than an error. Raised by Copilot, against a
        # suite that had been leaving two directories per case behind.
        self.addCleanup(shutil.rmtree, self.outside, ignore_errors=True)
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)
        # **The fixture is a real checkout, and the `.git` is load-bearing.**
        # An anchor is a checkout root, so a scratch tree without one has no
        # root to derive from `cwd` — and the case below that stands the
        # session inside a linked directory would then pass because the anchor
        # was dropped rather than because the guard refused. A marker directory
        # is all `checkout_root` looks for.
        for tree in ("docs", os.path.join(".claude", "scripts"), ".git"):
            os.makedirs(os.path.join(self.root, tree), exist_ok=True)
        self.write(os.path.join(self.root, "docs", "chapter.md"), "prose\n")
        self.write(
            os.path.join(self.root, ".claude", "scripts", "helper.sh"), "ok\n")
        self.write(os.path.join(self.outside, "loot.txt"), "secret\n")

    @staticmethod
    def write(path, text):
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(text)

    def link_to(self, name, real_target, linker):
        """A path spelled under `docs/` whose resolution is `real_target`.

        With symbolic links the link IS the target's spelling. With junctions —
        which is all Windows grants an unprivileged process, and which take a
        directory only — the link is the target's *directory* and the file is
        named beneath it. Both produce a path inside an allowed tree that
        resolves outside it, which is the property every case here is about;
        neither is a weaker form of the other, and running every case against
        each primitive the platform grants is what keeps the Windows run from
        being a different suite.
        """
        linkpath = os.path.join(self.root, "docs", f"{name}-{linker}")
        if linker == "symlink":
            os.symlink(real_target, linkpath,
                       target_is_directory=os.path.isdir(real_target))
            return linkpath
        if os.path.isdir(real_target):
            JUNCTIONS(real_target, linkpath)
            return linkpath
        JUNCTIONS(os.path.dirname(real_target), linkpath)
        return os.path.join(linkpath, os.path.basename(real_target))

    def link_dir(self, name, real_target, linker):
        """The same for a directory target, where both primitives agree."""
        linkpath = os.path.join(self.root, "docs", f"{name}-{linker}")
        if linker == "symlink":
            os.symlink(real_target, linkpath, target_is_directory=True)
        else:
            JUNCTIONS(real_target, linkpath)
        return linkpath

    def judge(self, file_path, tool="Edit", cwd=None, key="file_path",
              project=None):
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
            env={**os.environ,
                 "CLAUDE_PROJECT_DIR": self.root if project is None else project},
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

    def guard_module(self):
        """The hook imported directly, for the predicates a verdict hides."""
        spec = importlib.util.spec_from_file_location("guard_edit_target", HOOK)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module


class ALinkIsNotTheFileItIsSpelledAs(GuardCase):

    def test_a_link_into_a_denied_tree_is_refused(self):
        # #181 exactly: `/review-grok` holds `Edit` for `docs/` and denies
        # `.claude/**`, and both denies are matched on the spelling. A link
        # under `docs/` pointing into the machinery is a path the deny never
        # sees and a write the machinery receives.
        target = os.path.join(self.root, ".claude", "scripts", "helper.sh")
        for linker in linkers():
            with self.subTest(link=linker):
                reason = self.assertRefused(
                    self.link_to("pwned", target, linker))
                self.assertIn("resolves elsewhere in it", reason)
                self.assertIn("helper.sh", reason)

    def test_a_link_out_of_the_checkout_is_refused(self):
        # The other half of the issue's sentence, and the one a tree deny could
        # never have covered: no repository-relative pattern says anything
        # about a path that stops being repository-relative on resolution.
        loot = os.path.join(self.outside, "loot.txt")
        for linker in linkers():
            with self.subTest(link=linker):
                reason = self.assertRefused(
                    self.link_to("escape", loot, linker))
                self.assertIn("outside the checkout", reason)

    def test_a_link_in_the_middle_of_the_path_is_refused(self):
        # The component that is a link need not be the last one, and a guard
        # that only stats the leaf would admit this. Under junctions this is
        # the shape every case takes; under symlinks it is a case of its own,
        # which is why it is written out rather than left to the helper.
        machinery = os.path.join(self.root, ".claude")
        for linker in linkers():
            with self.subTest(link=linker):
                linkdir = self.link_dir("tree", machinery, linker)
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
        # says it is. **The discriminator is `os.name` and not which primitive
        # exists**: the first version read it off `SYMLINKS`, which is the same
        # answer only while symbolic links and POSIX coincide — a privileged
        # Windows runner has both and would have failed this case for a
        # difference that is the platform's rather than the guard's.
        real = os.path.join(self.root, ".claude", "scripts")
        for linker in linkers():
            with self.subTest(link=linker):
                linkdir = self.link_dir("updir", real, linker)
                spelled = os.path.join(linkdir, "..", "settings.json")
                if DOTDOT_IS_LEXICAL:
                    self.assertAdmitted(spelled)
                else:
                    self.assertIn("resolves elsewhere in it",
                                  self.assertRefused(spelled))

    def test_a_cwd_inside_a_link_does_not_excuse_that_link(self):
        # **The bypass an anchor becomes when it is not a checkout root.** An
        # anchor excuses exactly one link traversal — the one on its own root
        # prefix — so an anchor at `docs/tree`, where `tree` links into
        # `.claude/scripts`, excuses precisely the traversal this guard exists
        # to refuse: re-anchoring `docs/tree/helper.sh` on that directory makes
        # the spelling and the resolution agree. The first form took the
        # event's `cwd` as an anchor whatever it pointed at, and admitted this.
        # Raised by Copilot.
        #
        # Two changes close it and the case is written to fail if either is
        # reverted: `cwd` is walked up to its checkout root, and every anchor
        # containing the target must agree rather than any one of them.
        real = os.path.join(self.root, ".claude", "scripts")
        for linker in linkers():
            with self.subTest(link=linker):
                linkdir = self.link_dir("standing-in", real, linker)
                reason = self.assertRefused(
                    os.path.join(linkdir, "helper.sh"), cwd=linkdir)
                self.assertIn("resolves elsewhere in it", reason)

    def test_a_device_prefixed_spelling_is_refused(self):
        # **Windows' extended-length and device prefixes exist to SKIP the path
        # normalisation a permission matcher depends on**, which makes them a
        # spelling that names a denied target and is judged by nothing.
        # Measured in the real checkout with `.claude/sandbox/**` denied: a
        # `Write` to the extended-length spelling of a file under it was
        # CREATED, where the plain spelling of the same file is refused.
        #
        # Refused rather than resolved, because a hook can only allow or deny —
        # it cannot hand the matcher the plain spelling it would have judged.
        # Asserted on every platform because the check is textual: a POSIX file
        # whose name begins with those characters is not a real caller.
        # **The whole family, not the two prefixes that were found first.** The
        # UNC form reaches the same disk through an administrative share, and
        # it was measured the same way: a `Write` to
        # `\\localhost\C$\...\.claude\sandbox\probe-share.txt` was created in a
        # denied directory. A list of prefixes would have missed it, which is
        # the deny-list shape this repository has rejected twice.
        guard = self.guard_module()
        plain = os.path.join(self.root, "docs", "chapter.md")
        spellings = ["\\\\?\\" + plain, "\\\\.\\" + plain, "//?/" + plain,
                     "\\\\localhost\\C$\\dev\\x\\docs\\chapter.md"]
        if os.name != "nt":
            # No second alphabet exists here, and the predicate says so; `//x`
            # on POSIX is an ordinary path and refusing it would be a rule
            # about nothing.
            for spelling in spellings:
                self.assertFalse(guard.alternate_alphabet(spelling))
            return
        for spelling in spellings:
            with self.subTest(spelling=spelling):
                self.assertTrue(guard.alternate_alphabet(spelling))
                self.assertIn("other path grammar",
                              self.assertRefused(spelling))

        # Two controls. The same file named the ordinary way is admitted, so
        # the case is about the grammar rather than about the path — and a
        # session whose own checkout is `\\`-spelled is not refused wholesale,
        # which is the one legitimate use of that grammar.
        self.assertAdmitted(plain)
        self.assertTrue(guard.alternate_alphabet("\\\\nas\\projects\\repo"))

    def test_an_eight_dot_three_spelling_is_refused_where_one_exists(self):
        # The same class in Windows' other alphabet: `CLAUDE~1` is a different
        # string from `.claude`, so a matcher comparing strings does not see
        # the denied tree — and this guard refuses it without a special case,
        # because `realpath` answers with the long name and the spelling
        # therefore disagrees with the file.
        #
        # 8.3 alias creation can be disabled per volume, so the case reports
        # when the platform gave it nothing to test rather than pretending to
        # have tested it. Measured in the real checkout, where it is enabled:
        # `.claude` has the alias `CLAUDE~1`, and the guard refuses a write
        # through it.
        if os.name != "nt":
            return
        import ctypes
        buffer = ctypes.create_unicode_buffer(1024)
        long_name = os.path.join(self.root, "documentation-directory")
        os.makedirs(long_name, exist_ok=True)
        size = ctypes.windll.kernel32.GetShortPathNameW(
            long_name, buffer, len(buffer))
        short = buffer.value if size else long_name
        if short == long_name:
            print("8.3 aliases are disabled on this volume; case has no "
                  "subject", file=sys.stderr)
            return
        # **Which refusal fires depends on how much of the path the volume
        # aliases, and the case asserts the outcome rather than the wording.**
        # Where only the leaf is shortened the prefix still matches an anchor
        # and the ordinary spelling-versus-resolution test refuses it; where
        # `GetShortPathNameW` shortens the whole path — which is what the CI
        # runner does — no anchor recognises the spelling and the refusal comes
        # from the rule for a name that lands inside a checkout it cannot be
        # placed in. Pinning one message made this case red on the runner for a
        # difference that is the volume's.
        reason = self.assertRefused(os.path.join(short, "a.md"))
        self.assertIn(os.path.basename(long_name), reason)

    def test_a_directory_may_fold_differently_from_its_root(self):
        """Windows sets case sensitivity per directory, and the traits do not.

        **The finding was that a child can disagree with the root; what the
        measurement shows is that the disagreement is benign for a link.** A
        case-sensitive `docs/` really does keep `Sub` and `sub` apart — checked
        here with `fsutil file setCaseSensitiveInfo`, which needs no
        privilege — so the anchor's folded key calls two spellings one where
        that directory does not. For the guard to be fooled by it, a link's
        resolution would have to differ from its own path only in case, and a
        link's resolution IS its target: writing through `docs/Sub/x.md` lands
        on exactly the file that path names. `samefile` says so, and this case
        asserts it rather than asserting a bypass that does not exist.

        The guard carries the identity check anyway, for the shape this
        argument does not cover — a sub-mount whose equivalences differ from
        the root's — and it costs one `stat` on paths that agree only after
        folding.
        """
        if os.name != "nt":
            print("per-directory case sensitivity is Windows'; case has no "
                  "subject here", file=sys.stderr)
            return
        # A fresh, EMPTY directory: the flag will not take on one that already
        # holds entries, and `docs/` in this fixture does. Found by the flag
        # silently not applying — `BETA` and `beta` stayed one directory.
        docs = os.path.join(self.root, "mixed")
        os.makedirs(docs, exist_ok=True)
        made = subprocess.run(
            ["fsutil", "file", "setCaseSensitiveInfo", docs, "enable"],
            capture_output=True, text=True)
        probe_lower = os.path.join(docs, "probe")
        probe_upper = os.path.join(docs, "PROBE")
        os.makedirs(probe_lower, exist_ok=True)
        if made.returncode != 0 or os.path.isdir(probe_upper):
            print("this volume will not take a case-sensitive directory; case "
                  "has no subject here", file=sys.stderr)
            return

        # **The pair has to differ ONLY in case**, or the guard refuses it for
        # the ordinary reason and the case says nothing about folding. One pair
        # per primitive, since two links cannot share a name.
        names = {"symlink": "alpha", "junction": "beta"}
        for linker in linkers():
            with self.subTest(link=linker):
                lower = os.path.join(docs, names[linker])
                os.makedirs(lower, exist_ok=True)
                self.write(os.path.join(lower, "x.md"), "lower\n")
                upper = os.path.join(docs, names[linker].upper())
                if linker == "symlink":
                    os.symlink(lower, upper, target_is_directory=True)
                else:
                    JUNCTIONS(lower, upper)
                spelled = os.path.join(upper, "x.md")
                self.assertTrue(
                    os.path.samefile(spelled, os.path.join(lower, "x.md")),
                    "the link and its target are one file, which is why "
                    "admitting this is correct")
                self.assertAdmitted(spelled)

    def test_a_notebook_path_is_judged_too(self):
        # `NotebookEdit` carries its target under another key, and a guard that
        # reads only `file_path` would wave the whole tool through while the
        # matcher says it is covered.
        target = os.path.join(self.root, ".claude", "scripts", "helper.sh")
        for linker in linkers():
            with self.subTest(link=linker):
                self.assertRefused(self.link_to("book", target, linker),
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

    def test_a_dotdot_into_a_denied_tree_is_the_deny_lists_subject_not_this_one(self):
        # **Stated as a passing case because the argument for the other verdict
        # is a good one and rests on a premise this harness does not have.**
        # `docs/../.claude/scripts/helper.sh` carries no `.claude/**` spelling,
        # so a matcher reading the string would not deny it — and the harness
        # does not read the string. Measured in the real checkout, with
        # `.claude/sandbox/**` denied: a `Write` to
        # `docs/../.claude/sandbox/probe-tmp.txt` was refused with the
        # harness's own "denied by your permission settings", while
        # `docs/../docs/probe-tmp.txt` was created — the path is normalised and
        # then matched, and `..` is not what was rejected.
        #
        # So this guard admits it, because the file it lands on is the file the
        # path names once the shell of `..` is gone, and no link was traversed.
        # Refusing every `..` would buy nothing against the deny list and would
        # refuse the second of those two spellings, which is innocent traffic.
        # Raised by Copilot; if the harness ever stops normalising, this case
        # is the one to invert and the paragraph in the hook is the one to
        # rewrite.
        self.assertAdmitted(os.path.join(
            self.root, "docs", "..", ".claude", "scripts", "helper.sh"))

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
        for linker in linkers():
            with self.subTest(link=linker):
                alias = os.path.join(self.outside, f"checkout-{linker}")
                if linker == "symlink":
                    os.symlink(self.root, alias, target_is_directory=True)
                else:
                    JUNCTIONS(self.root, alias)
                self.assertAdmitted(
                    os.path.join(alias, "docs", "chapter.md"), cwd=alias)

    def test_case_folding_is_asked_of_the_filesystem_not_the_platform(self):
        # `os.path.normcase` folds on Windows and nowhere else, which is a
        # statement about the platform where what matters is the filesystem:
        # macOS mounts APFS case-insensitively by default. The hook probes
        # instead, and this case checks the probe against the same measurement
        # taken here — one file, one device and inode, under two spellings.
        guard = self.guard_module()
        directory, name = os.path.split(self.root)
        flipped = os.path.join(directory, name.swapcase())
        try:
            measured = (os.stat(self.root).st_dev == os.stat(flipped).st_dev
                        and os.stat(self.root).st_ino == os.stat(flipped).st_ino)
        except OSError:
            measured = False
        self.assertEqual(measured, guard.case_insensitive(self.root))

    def test_a_spelling_no_anchor_recognises_is_refused_if_it_lands_inside(self):
        # **The general form of three separate findings**, and the case that
        # went red on CI having passed locally: a Windows runner's
        # `GetShortPathNameW` shortens the whole prefix, so
        # `C:\Users\RUNNER~1\...\GUARD-~1\DOCUME~1\a.md` matched no anchor and
        # fell through to the residual while resolving squarely inside a
        # checkout. Case folding and Unicode composition each closed one
        # spelling by teaching the comparison an equivalence; this closes the
        # class, because the residual is for a file genuinely outside every
        # checkout and not for one inside under a name the anchors cannot
        # place.
        #
        # Measured against the commit that shipped it: admitted there, refused
        # here. The alias below stands in for the short prefix, which cannot be
        # produced on a volume with 8.3 creation disabled.
        for linker in linkers():
            with self.subTest(link=linker):
                alias = os.path.join(self.outside, f"alias-{linker}")
                if linker == "symlink":
                    os.symlink(self.root, alias, target_is_directory=True)
                else:
                    JUNCTIONS(self.root, alias)
                through = os.path.join(alias, "docs", "chapter.md")
                self.assertIn("not a spelling any checkout here recognises",
                              self.assertRefused(through))

                # The control, and it is the worktree case rather than a
                # loophole: a session STANDING in that alias makes it a
                # checkout root of its own, and then the spelling is one the
                # anchors recognise.
                self.assertAdmitted(through, cwd=alias)

    def test_a_composed_and_a_decomposed_spelling_are_one_key(self):
        # **A case-insensitive APFS volume is also insensitive to Unicode
        # normalisation**, so `é` composed and `e` followed by a combining
        # accent name one directory there while they are two strings in
        # Python. A checkout prefix spelled in the other form therefore matched
        # no anchor and reached the branch that admits. Raised by Copilot.
        #
        # `key` composes where the anchor's traits say the mount does, so the
        # predicate is assertable on every platform by passing the traits
        # explicitly; the end-to-end half needs a mount that agrees, and says
        # so when it has none rather than reporting a pass for it.
        guard = self.guard_module()
        name = "caf\u00e9"  # built rather than typed: an editor that normalises
        # this file would otherwise make the two spellings one and the case vacuous.
        composed = os.path.join(self.root, unicodedata.normalize("NFC", name))
        decomposed = os.path.join(self.root, unicodedata.normalize("NFD", name))
        self.assertNotEqual(composed, decomposed)

        # **Composed only where the mount composes**, which is the half that
        # arrived a round late: composing everywhere folds two names that can
        # COEXIST on ext4 into one key, so a link resolving into the sibling
        # compares equal to a path inside the checkout. Both directions are
        # asserted here because each was a bypass in its turn.
        for folded in (False, True):
            with self.subTest(folded=folded):
                self.assertEqual(guard.key(composed, (folded, True)),
                                 guard.key(decomposed, (folded, True)))
                self.assertNotEqual(guard.key(composed, (folded, False)),
                                    guard.key(decomposed, (folded, False)))

        os.makedirs(composed, exist_ok=True)
        try:
            here, there = os.stat(composed), os.stat(decomposed)
            one_directory = (here.st_dev, here.st_ino) == (there.st_dev,
                                                           there.st_ino)
        except OSError:
            one_directory = False
        if not one_directory:
            print("this filesystem distinguishes NFC from NFD; the end-to-end "
                  "half of the normalisation case has no subject here",
                  file=sys.stderr)
            return

        target = os.path.join(self.root, ".claude", "scripts", "helper.sh")
        for linker in linkers():
            with self.subTest(link=linker):
                link = self.link_to("composed", target, linker)
                through = os.path.join(
                    decomposed, "..", os.path.relpath(link, self.root))
                self.assertRefused(through)

    def test_two_names_that_can_coexist_are_not_folded_into_one(self):
        # **The other direction of the same question, and the argument that
        # skipped it was the bypass.** This file once composed to NFC
        # unconditionally on the reasoning that composing "can never make two
        # paths look like one". On a normalisation-SENSITIVE filesystem — NTFS
        # and ext4 among them — a composed and a decomposed name are two
        # directories that coexist, so a link inside the checkout resolving to
        # the SAME relative path under the sibling compared equal to a path
        # inside it, and the escape was admitted. Raised by Copilot.
        #
        # Measured against the commit that shipped it, on NTFS: admitted there,
        # refused here. Where the mount equates the two names the sibling
        # cannot exist, and the case says so rather than pretending to have
        # tested it.
        name = "caf\u00e9"
        checkout = os.path.join(self.outside,
                                unicodedata.normalize("NFC", name))
        sibling = os.path.join(self.outside,
                               unicodedata.normalize("NFD", name))
        os.makedirs(os.path.join(checkout, ".git"), exist_ok=True)
        os.makedirs(os.path.join(sibling, "docs"), exist_ok=True)
        self.write(os.path.join(sibling, "docs", "a.md"), "secret\n")
        try:
            here, there = os.stat(checkout), os.stat(sibling)
            coexist = (here.st_dev, here.st_ino) != (there.st_dev, there.st_ino)
        except OSError:
            coexist = False
        if not coexist:
            print("this filesystem equates NFC and NFD, so the sibling cannot "
                  "exist and this case has no subject here", file=sys.stderr)
            return

        for linker in linkers():
            with self.subTest(link=linker):
                link = os.path.join(checkout, f"docs-{linker}")
                if linker == "symlink":
                    os.symlink(os.path.join(sibling, "docs"), link,
                               target_is_directory=True)
                else:
                    JUNCTIONS(os.path.join(sibling, "docs"), link)
                self.assertRefused(os.path.join(link, "a.md"),
                                   cwd=checkout, project=checkout)

    def test_a_checkout_whose_name_has_no_letters_is_still_asked(self):
        # **The probe has to flip something, and the basename is not always
        # flippable.** A checkout at `/Users/me/123` has no cased character in
        # its last component, so a probe that only flips the basename falls to
        # the platform default — `False` on macOS, where the mount folds — and
        # a linked target spelled `/users/me/123/...` matches no anchor and
        # falls through unjudged. Raised by Copilot.
        #
        # The assertion ties the numeric root to a lettered one on the same
        # filesystem rather than to a platform: whatever the mount answers for
        # the parent, it must answer for the child, because they are the same
        # mount. A platform-default fallback breaks that on macOS and on any
        # case-insensitive mount, which is where it mattered.
        guard = self.guard_module()
        numeric = os.path.join(self.outside, "123456")
        os.makedirs(os.path.join(numeric, "docs"), exist_ok=True)
        self.assertEqual(guard.case_insensitive(self.outside),
                         guard.case_insensitive(numeric))
        self.assertEqual(guard.case_insensitive(numeric),
                         guard.case_insensitive(os.path.join(numeric, "docs")))

    def test_a_differently_cased_checkout_prefix_is_still_judged(self):
        # **The branch that admits is the one a case difference reaches.** On a
        # folding filesystem `/Users/x/Repo` and `/users/x/repo` are one
        # directory, so a target spelled with the other case is inside the
        # checkout — and a comparison that folds only on Windows finds it under
        # no anchor at all and falls through to `None`. A link edit spelled
        # that way would have bypassed the guard on a default macOS checkout.
        # Raised by Copilot.
        #
        # Where the filesystem does NOT fold, the same spelling names a path
        # that does not exist and is not this guard's subject, so the assertion
        # is the filesystem's answer rather than one platform's.
        guard = self.guard_module()
        directory, name = os.path.split(self.root)
        other_case = os.path.join(directory, name.swapcase())
        folds = guard.case_insensitive(self.root)

        plain = os.path.join(other_case, "docs", "chapter.md")
        target = os.path.join(self.root, ".claude", "scripts", "helper.sh")
        for linker in linkers():
            with self.subTest(link=linker, folds=folds):
                link = self.link_to("cased", target, linker)
                through = os.path.join(
                    other_case, os.path.relpath(link, self.root))
                if folds:
                    self.assertAdmitted(plain)
                    self.assertIn("resolves elsewhere in it",
                                  self.assertRefused(through))
                else:
                    self.assertAdmitted(plain)
                    self.assertAdmitted(through)

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
        for linker in linkers():
            with self.subTest(link=linker):
                link = self.link_to("read-me", target, linker)
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
