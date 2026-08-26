#!/usr/bin/env python3
"""Fail the build on a credential written into the working tree.

Section 15.1 puts "SCA + secret scan" at the head of the pipeline and argues the
position: neither half needs a build, and a scan downstream of one is a scan
that a build failure skips. The licence half landed with PR-01. This is the
other half, on the same terms — stdlib Python over text, no restore, no SDK, no
network — so it runs in the same job, ahead of everything.

WHAT THIS DOES NOT DO, stated here rather than inferred from a green run.

  * It reads the WORKING TREE, not git history. A credential committed and then
    deleted is still in the pack and is still compromised; nothing here looks
    at a single earlier revision. `docs/secrets.md` already states the rule this
    leaves standing — rotate first, rewrite history second — and this gate does
    not change it.
  * It is a PATTERN scanner, not an entropy oracle. It recognises shapes it has
    been shown: a PEM block, a provider's key prefix, a password inside a
    connection string, a credential-shaped name assigned a literal. A
    high-entropy string under a name nobody predicted passes. That is the same
    line Section 13.4's redactor draws for the same reason — an entropy test
    flags an id as readily as a secret, and a gate that cries wolf is a gate
    somebody turns off.
  * It knows nothing about whether a value is live. `not-a-real-password` and a
    production password are the same shape, which is why the accepted ones are
    ENUMERATED in allowed-secrets.txt rather than guessed at by the patterns.

So the honest claim is narrow: a credential of a recognised shape cannot reach
`main` through a pull request without somebody writing down why it is there.

    py -3.12 .github/secret-scan/secret_scan.py
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import sys
from pathlib import Path

GATE_DIR = Path(__file__).resolve().parent
REPO_ROOT = GATE_DIR.parents[1]

DEFAULT_ALLOWED = GATE_DIR / "allowed-secrets.txt"

# Directories never descended into. Build output and vendored trees are not
# reviewed, so a finding in one is a finding nobody would act on; `.git` is
# excluded because this gate is deliberately about the tree and not the history,
# and scanning the pack would be a claim to a coverage it does not have.
SKIP_DIRS = frozenset({
    ".git",
    ".vs",
    ".idea",
    "obj",
    "bin",
    "node_modules",
    "__pycache__",
    "TestResults",
})

# A file is binary when its first block holds a NUL. That is a heuristic and it
# is the right one here: every pattern below is ASCII, so a format that would
# hide a secret from a byte scan (a zip, a DLL, a PNG) is a format this gate
# could not read anyway, and refusing to guess further keeps the walk cheap.
PROBE_BYTES = 8192

# ------------------------------------------------------------------ values --

# A value that is a REFERENCE rather than a literal. `${SQL_PASSWORD}`,
# `$PGPASSWORD`, `%SQL_PASSWORD%`, `{{ .Values.db.password }}` and
# `<your-password-here>` all name a secret without carrying one.
REFERENCE = re.compile(
    r"^(?:"
    r"\$\{[^}]*\}"                       # ${VAR}, and ${VAR:-…} once unwrapped
    r"|\$[A-Za-z_][A-Za-z0-9_]*"         # $VAR
    r"|\$\([^)]*\)"                      # $(SqlCmdVariable), $(shell …)
    r"|%[A-Za-z0-9_]+%"                  # %VAR%
    r"|\{\{.*\}\}"                       # {{ .Values.x }} — Helm, Jinja, Go
    r"|\{[A-Za-z0-9_.\-]*\}"             # {Pwd} — a message template's hole
    r"|<[^>]*>"                          # <your-password-here>
    r")$")

# `${VAR:-default}` is NOT a reference. The default is a literal, committed to
# the tree, and the seam `docs/secrets.md` argues for — the variable in front of
# it — is a seam against DEPLOYING the value, not against writing it down. So
# the wrapper is peeled and the default is judged. Section 14.1's accepted
# local-development defaults then reach allowed-secrets.txt as decisions with
# reasons, which is where this repository has already said they belong, rather
# than disappearing into a pattern nobody re-reads.
DEFAULTED_REFERENCE = re.compile(r"^\$\{[A-Za-z_][A-Za-z0-9_]*:[-=]?(.*)\}$", re.S)

# Characters a mask is made of. A value composed only of these carries nothing.
MASK_CHARS = "*xX#.…_- \t"


def literal(value: str) -> str:
    """The literal a reader of this line would actually see, or "" for none.

    Structure only. This function decides whether a value CAN be a secret; it
    never decides whether a particular secret is acceptable — that is the
    allow-list's job and it is deliberately the only place such a decision can
    be made. CLAUDE.md's argument against `#pragma` is the same argument: a
    suppression written where the code is, is a suppression nobody re-reads.
    """
    value = value.strip()

    # Nested defaults occur in §14.1's compose file — a connection string whose
    # default embeds the password's own default. Bounded rather than recursive
    # so a pathological line cannot spin.
    for _ in range(5):
        match = DEFAULTED_REFERENCE.match(value)
        if not match:
            break
        value = match.group(1).strip()

    if not value or REFERENCE.match(value):
        return ""
    if not value.strip(MASK_CHARS):
        return ""

    # A value with no alphanumeric character at all is punctuation the pattern
    # ran into, not a credential — a stray backtick in prose, a bare `-`, a row
    # of asterisks somebody typed for a screenshot. This is the boundary at the
    # SHORT end of every value rule here; the long end is each rule's own
    # terminator. Both ends in the same change, because a constraint on one side
    # of a pattern is not a constraint.
    if not any(character.isalnum() for character in value):
        return ""
    return value


# ------------------------------------------------------------------- rules --

# Names that suggest the value beside them is a credential. Written once and
# shared by the two shape rules below, because two copies of a vocabulary is
# two vocabularies.
CREDENTIAL_WORDS = (
    r"passwd|password(?!less)|pwd|secret|token|api[_\-]?key|apikey|"
    r"client[_\-]?secret|connection[_\-]?string|conn[_\-]?str")

# A name CONTAINING one of those words, not equal to it. The field that leaks is
# never called `password` — it is `SQL_PASSWORD`, `ClientSecret`,
# `ConnectionStrings__Catalog`. Section 13.4's redactor reached the same
# conclusion from the other end and matches by substring for the same reason.
CREDENTIAL_NAME = rf"[A-Za-z0-9_.\-]*(?:{CREDENTIAL_WORDS})[A-Za-z0-9_.\-]*"

# The shortest value worth reporting under a NAME-based rule. Below eight
# characters the name is doing all the work and an ordinary codebase supplies
# endless `Token = "n/a"`; a prefix-based rule has no such floor because the
# prefix is the evidence.
MIN_NAME_RULE_VALUE = 8


class Rule:
    """One named shape, its sentence, and the group carrying the credential.

    One rule per shape, never one regex for all of them. A single pattern would
    report every class under one id, so a suppression for the boring class would
    silence the interesting one — which is the shape of this repository's
    most-repeated failure, a gate that quietly stops covering a surface.
    """

    def __init__(self, identifier: str, sentence: str, pattern: str, group: int | str = 1,
                 flags: int = 0, reject=None):
        self.id = identifier
        self.sentence = sentence
        self.pattern = re.compile(pattern, flags)
        self.group = group
        self.reject = reject

    def secrets(self, line: str) -> list[str]:
        """Every credential this rule finds on one line, unwrapped, in order."""
        found = []
        for match in self.pattern.finditer(line):
            if self.reject is not None and self.reject(match):
                continue
            value = literal(match.group(self.group))
            if value:
                found.append(value)
        return found


IDENTIFIER = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")


def names_itself(match: re.Match[str]) -> bool:
    """`self.secret = secret` — a constructor storing a parameter, not a value.

    The one false positive the bare-word rule below could not be narrowed out of
    by its value alone, because `secret` IS a bare word. What distinguishes it
    is that the value is an identifier the NAME already contains, which is what
    a parameter assigned to the field it backs looks like in every language here
    — and what a password never looks like, since a credential equal to the name
    of the field holding it is not a credential.
    """
    name, value = match.group("name"), match.group("value")
    return bool(IDENTIFIER.match(value)) and value.lower() in name.lower()


RULES: list[Rule] = [
    # The key material itself, not a reference to one. The optional algorithm
    # word is what makes this one rule rather than five, and the words are
    # enumerated rather than `\w+` so that BEGIN PUBLIC KEY and BEGIN
    # CERTIFICATE — both ordinary, both harmless — cannot reach it.
    Rule(
        "private-key-block",
        "a PEM private key block: the key material itself, not a reference to it",
        r"(-----BEGIN (?:RSA |DSA |EC |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY-----)"),

    # §14.1 normalises this exact shape with local defaults, which is precisely
    # why a real one would be pasted in unnoticed: the diff looks like every
    # other line around it. The value runs to the next `;` or quote, because
    # that is where a connection string's segment ends.
    #
    # WHITESPACE, PARENTHESES AND A BACKTICK END THE VALUE, and each end of
    # that was measured rather than guessed. The keyword occurs in this
    # repository's prose and in its own redactor's comments more often than it
    # occurs in a connection string, and there what follows is a sentence — 12
    # of the first 47 findings were English. A C# local of the same name
    # assigned the result of a method call is the same shape one language over,
    # which is what the parenthesis ends. The backtick is markdown's code
    # delimiter, which is what ends the value inside a chapter.
    #
    # A connection string's password may legally contain a space; that is the
    # stated cost, and it buys a rule people will still be reading in a year.
    Rule(
        "connection-string-password",
        "a connection string carries an inline password",
        r"(?<![A-Za-z0-9_.])(?:password|pwd)[ \t]*=[ \t]*([^;\"'()`\s]*)",
        flags=re.IGNORECASE),

    # AWS publishes the prefix and the length, so this needs no name beside it.
    # The trailing guard is a lookahead rather than `\b`: an id is exactly
    # twenty characters, and `\b` would still match one with a longer tail.
    Rule(
        "aws-access-key-id",
        "an AWS access key id",
        r"(?<![A-Za-z0-9])(AKIA[0-9A-Z]{16})(?![A-Za-z0-9])"),

    # The secret half carries no prefix, so the NAME is the only evidence there
    # is. Forty characters of base64 is the published length.
    Rule(
        "aws-secret-access-key",
        "an AWS secret access key assigned to a name that says so",
        r"aws[_\-. ]?secret[_\-. ]?access[_\-. ]?key[^A-Za-z0-9]{1,10}"
        r"([A-Za-z0-9/+=]{40})(?![A-Za-z0-9/+=])",
        flags=re.IGNORECASE),

    # `ghp_` personal, `gho_` OAuth, `ghu_` user-to-server, `ghs_` server-to-
    # server, `ghr_` refresh. The body is at least 36 characters.
    Rule(
        "github-token",
        "a GitHub token",
        r"(?<![A-Za-z0-9_])(gh[pousr]_[A-Za-z0-9]{36,255})(?![A-Za-z0-9])"),

    Rule(
        "slack-token",
        "a Slack token",
        r"(?<![A-Za-z0-9])(xox[abpr]-[A-Za-z0-9\-]{10,})"),

    # `sk_live_` only. `sk_test_` is a test-mode key by construction and firing
    # on it would train people to ignore this rule.
    Rule(
        "stripe-live-key",
        "a Stripe live secret key",
        r"(?<![A-Za-z0-9_])(sk_live_[A-Za-z0-9]{16,})(?![A-Za-z0-9])"),

    Rule(
        "google-api-key",
        "a Google API key",
        r"(?<![A-Za-z0-9_\-])(AIza[0-9A-Za-z_\-]{35})(?![0-9A-Za-z_\-])"),

    # A compact JWT: three base64url segments, the first starting `eyJ` because
    # that is `{"` encoded. Matched BARE rather than only in an assignment. The
    # brief asked for the assignment form and this is wider on purpose: a bearer
    # token pasted into a YAML list, a curl example or a test fixture is the way
    # one actually arrives, and `eyJ` plus two dotted segments is unambiguous
    # enough that the assignment adds nothing but a way to miss it.
    Rule(
        "json-web-token",
        "a JSON Web Token in compact serialisation",
        r"(?<![A-Za-z0-9_\-])(eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}"
        r"\.[A-Za-z0-9_\-]{10,})"),

    # This repository's own tooling holds one of each: `.claude/sandbox` builds
    # a reviewer around an xAI key, and the harness runs on an Anthropic one. A
    # gate that scans for everyone else's provider and not its own is a gate
    # written from a checklist.
    Rule(
        "model-provider-api-key",
        "an xAI or Anthropic API key",
        r"(?<![A-Za-z0-9_\-])((?:xai|sk-ant)-[A-Za-z0-9_\-]{20,})"),

    # The catch-all, and the only rule whose evidence is a name rather than a
    # shape. A quoted literal is required: an unquoted right-hand side in C# is
    # an expression, and `bool useToken = enabled` is not a finding.
    Rule(
        "credential-assignment",
        "a credential-shaped name is assigned a literal",
        rf"[\"']?\b(?:{CREDENTIAL_NAME})\b[\"']?\s*[:=]\s*"
        rf"([\"'])(?P<value>[^\"'\r\n]{{{MIN_NAME_RULE_VALUE},}})\1",
        group="value",
        flags=re.IGNORECASE),

    # The one shape the rule above structurally cannot see: a `.env` file, where
    # the value is NOT quoted. `SQL_PASSWORD=…` on its own line is a credential
    # written in the open and no other rule here reaches it.
    #
    # THE VALUE HAS TO BE A BARE WORD, and that constraint is the whole rule.
    # Without it this fires on every module-level assignment in every Python and
    # C# file in the tree — measured, 27 findings of which 21 were an expression
    # rather than a literal. Whitespace, brackets, quotes and a trailing `;` or
    # `,` all disqualify, which is a description of an expression and not of a
    # password. The quoted form is deliberately left to the rule above rather
    # than reported twice by both.
    Rule(
        "env-assignment",
        "an environment-style assignment gives a credential-shaped name a value",
        rf"^[ \t]*(?:export[ \t]+)?(?P<name>{CREDENTIAL_NAME})[ \t]*=[ \t]*"
        r"(?P<value>[^\s\"'()\[\]<>;,]+)[ \t]*$",
        group="value",
        flags=re.IGNORECASE,
        reject=names_itself),
]


def digest(secret: str) -> str:
    """A stable, short fingerprint of one credential.

    Twelve hex characters of SHA-256. This is what an allow-list entry names,
    and the reason it names a hash rather than the value is not confidentiality
    — these values are already in the tree in plain sight. It is that the
    suppression file must not become a SECOND place the credential is written.
    A second copy is a copy that outlives the rotation of the first, and the
    whole argument for this gate is that a credential in two places is a
    credential nobody can retire.
    """
    return hashlib.sha256(secret.encode("utf-8")).hexdigest()[:12]


def redact(secret: str) -> str:
    """A credential as it may appear in a CI log: three characters and a length.

    A gate that prints what it found has copied the secret into the log of every
    run that failed, where it is retained longer and read by more people than
    the branch ever was. Three characters is enough to find the line; the length
    is enough to tell two findings apart.
    """
    return f"{secret[:3]}... {len(secret)} chars"


# -------------------------------------------------------------- allow-list --


class Suppression:
    """One accepted finding: a path, a rule, a fingerprint and a reason."""

    def __init__(self, path: str, rule: str, fingerprint: str, reason: str, line: int):
        self.path = path
        self.rule = rule
        self.fingerprint = fingerprint
        self.reason = reason
        self.line = line
        self.used = False

    def key(self) -> tuple[str, str, str]:
        return (self.path, self.rule, self.fingerprint)


# A reason has to be a sentence somebody wrote, not a word somebody typed to get
# past the parser. Fifteen characters refuses "ok", "test" and "local"; it
# cannot make a reason true, and nothing can. What it does buy is that the
# cheapest way past this gate is still to write down why.
MIN_REASON = 15

# Exactly four, never at least four. A fifth field is a reason someone put a
# pipe in, and reading it as an entry would silently truncate what they wrote.
FIELDS = 4


def read_allowed(path: Path, known: set[str] | None = None) -> tuple[list[Suppression], list[str]]:
    """The allow-list, and every complaint about its own syntax.

    Four pipe-separated fields. Exact repository-relative paths, never globs:
    a glob is how a suppression arrives for a file nobody has written yet, and
    a file that arrives pre-suppressed is this repository's most-repeated
    failure with the paperwork already filled in.
    """
    entries: list[Suppression] = []
    problems: list[str] = []

    if not path.exists():
        return entries, [f"{path.name} is missing: the gate cannot judge what it may ignore"]

    for number, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue

        fields = [field.strip() for field in line.split("|")]
        if len(fields) != FIELDS:
            problems.append(
                f"{path.name}:{number}: expected `path | rule | fingerprint | reason`, "
                f"got {len(fields)} field(s)")
            continue

        entry_path, rule, fingerprint, reason = fields

        # A rule id nobody declares would otherwise surface as a stale entry,
        # which is the right verdict reported as the wrong diagnosis: the reader
        # goes looking for a finding that moved when what happened is a typo, or
        # a rule renamed without its entries. Say which.
        if known is not None and rule not in known:
            problems.append(
                f"{path.name}:{number}: `{rule}` is not a rule this scanner declares")
            continue

        if not entry_path or "*" in entry_path or "?" in entry_path:
            problems.append(
                f"{path.name}:{number}: `{entry_path}` is empty or a glob; "
                f"an entry names one exact path")
            continue
        if len(reason) < MIN_REASON:
            problems.append(
                f"{path.name}:{number}: the reason is {len(reason)} character(s). "
                f"An entry states WHY, in a sentence")
            continue

        entries.append(Suppression(entry_path, rule, fingerprint, reason, number))

    return entries, problems


# ---------------------------------------------------------------- scanning --


def is_binary(blob: bytes) -> bool:
    return b"\0" in blob[:PROBE_BYTES]


def walk(root: Path) -> list[Path]:
    """Every file worth reading, in a stable order.

    A filesystem walk rather than `git ls-files`, and the difference matters in
    the direction this gate cares about: the file that is about to be committed
    is untracked at the moment somebody wants to be told about it. Shelling out
    would also cost the property every gate here shares — that it runs over a
    plain checkout with nothing installed.
    """
    found: list[Path] = []
    for directory, subdirectories, filenames in os.walk(root):
        subdirectories[:] = sorted(name for name in subdirectories if name not in SKIP_DIRS)
        for name in sorted(filenames):
            found.append(Path(directory) / name)
    return found


class Finding:
    def __init__(self, path: str, line: int, rule: Rule, secret: str):
        self.path = path
        self.line = line
        self.rule = rule
        self.secret = secret
        self.fingerprint = digest(secret)

    def key(self) -> tuple[str, str, str]:
        return (self.path, self.rule.id, self.fingerprint)

    def __str__(self) -> str:
        return (f"{self.path}:{self.line}: {self.rule.id}: {self.rule.sentence} "
                f"[{redact(self.secret)}, sha256:{self.fingerprint}]")


def scan_text(path: str, text: str, rules: list[Rule]) -> list[Finding]:
    findings: list[Finding] = []
    for number, line in enumerate(text.splitlines(), start=1):
        for rule in rules:
            for secret in rule.secrets(line):
                findings.append(Finding(path, number, rule, secret))
    return findings


def scan_tree(root: Path, rules: list[Rule]) -> tuple[list[Finding], int]:
    findings: list[Finding] = []
    scanned = 0

    for file_path in walk(root):
        # The probe is read before the rest, so a large binary costs one block
        # rather than its whole length. PROBE_BYTES would otherwise be a comment
        # about a slice of something already in memory.
        try:
            with file_path.open("rb") as handle:
                head = handle.read(PROBE_BYTES)
                if is_binary(head):
                    continue
                blob = head + handle.read()
        except OSError:
            continue

        # errors="replace" rather than a guess at the encoding. Every pattern
        # here is ASCII, so a replacement character can only ever break a match
        # apart — it can never assemble one — and the failure direction is a
        # missed finding in a file that is not UTF-8, which is stated rather
        # than hidden.
        text = blob.decode("utf-8", errors="replace")
        scanned += 1
        relative = file_path.relative_to(root).as_posix()
        findings.extend(scan_text(relative, text, rules))

    return findings, scanned


def audit(findings: list[Finding], entries: list[Suppression]) -> list[str]:
    """Findings the allow-list does not cover, then entries that covered nothing.

    The second half is the part that keeps the first honest. A suppression whose
    finding has gone is a decision nobody has re-read, and this repository has
    already written down what to do about a list of known exceptions: gate it,
    so the day one clears, the build says so. `deploy/observability` does the
    same thing to its unloaded alerts and for the same reason.
    """
    by_key: dict[tuple[str, str, str], Suppression] = {}
    problems: list[str] = []

    for entry in entries:
        if entry.key() in by_key:
            problems.append(
                f"allowed-secrets.txt:{entry.line}: duplicates the entry on line "
                f"{by_key[entry.key()].line}")
            continue
        by_key[entry.key()] = entry

    unexplained: list[str] = []
    for finding in findings:
        entry = by_key.get(finding.key())
        if entry is None:
            unexplained.append(str(finding))
            continue
        entry.used = True

    stale = [
        f"allowed-secrets.txt:{entry.line}: `{entry.path}` no longer matches "
        f"{entry.rule} with sha256:{entry.fingerprint}. The finding is gone, "
        f"or the value changed. Re-read the entry and delete it or update it"
        for entry in entries if not entry.used
    ]

    return problems + unexplained + stale


def say(message: str, stream=None) -> None:
    """Print one line, with anything outside ASCII replaced.

    Every line this gate emits passes through here, and it is not decoration.
    Two of the three things a finding line carries come from the tree rather
    than from this file — the path and the redacted prefix of the value — so
    "the messages are written in ASCII" is a claim about source that says
    nothing about output. Runner stdout encoding is not ours to assume, and a
    gate whose job is to report a failure must not be the thing that fails.

    The stream is resolved on the call rather than defaulted in the signature.
    A default argument binds `sys.stdout` once, at import, and a caller that has
    redirected the stream afterwards then writes past the redirection into the
    real console -- which is exactly what a test capturing this output does.
    """
    print(message.encode("ascii", "replace").decode("ascii"),
          file=sys.stdout if stream is None else stream)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Scan the working tree for credentials.")
    parser.add_argument("--root", type=Path, default=REPO_ROOT)
    parser.add_argument("--allowed", type=Path, default=DEFAULT_ALLOWED)
    args = parser.parse_args(argv)

    entries, findings = read_allowed(args.allowed, {rule.id for rule in RULES})

    # THE GATE'S OWN SUBJECT, before anything that rests on it. `ci.yml` states
    # this as house policy at the pipeline gate: neither check trusts its own
    # parser, and each fails on an empty subject rather than reporting a
    # complete list it never read. A scan of no files and a scan with no rules
    # both print the same reassuring sentence as a clean tree, and that sentence
    # is the whole product — so the two ways of producing it dishonestly are
    # refused here rather than left to be noticed.
    if not RULES:
        findings.append("no rules are defined: the gate would clear any tree at all")

    matches, scanned = ([], 0) if findings else scan_tree(args.root, RULES)

    if not findings and scanned == 0:
        findings.append(
            f"scanned no files under {args.root}: a clean report over an empty subject")

    if not findings:
        findings = audit(matches, entries)

    if findings:
        say(f"Secret scan: {len(findings)} finding(s) across {scanned} file(s).\n", sys.stderr)
        for finding in findings:
            say(f"  {finding}", sys.stderr)
        say(f"\nA finding is cleared by fixing it, or by an entry in "
            f"{args.allowed.name} naming the path, the rule, the fingerprint above "
            f"and the reason.", sys.stderr)
        return 1

    # The accepted count is printed because on this repository it is non-zero,
    # which makes the summary a positive control: a scanner that had silently
    # stopped matching would report nothing accepted and fail above.
    say(f"Secret scan: {scanned} file(s), {len(RULES)} rule(s), "
        f"{len(entries)} accepted finding(s), 0 unexplained.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
