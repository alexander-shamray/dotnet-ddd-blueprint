# The secret-scan allow-list

Findings this repository accepts, one per line, as

```
path | rule-id | sha256 fingerprint | reason
```

All four fields are required and the reason is checked for being one. An entry
that matches no finding **fails the build**: a suppression whose finding has
gone is a decision nobody has re-read, and this repository already decided what
to do about a list of known exceptions — gate it, so the day one clears, the
build says which. `deploy/observability/alerts` does the same thing to its
unloaded alerts, for the same reason.

## One file per tree

The allow-list is this directory rather than a file in it. Each `.txt` here
declares the tree it speaks for:

```
# covers: deploy/
```

exactly once, before its first entry, and every entry in that file must name a
path the prefix covers. No two files may declare the same prefix. Placement is
therefore mechanical rather than a judgement — an entry goes in the file whose
prefix covers its path — and two agents suppressing findings in two trees never
edit one file. That is `docs/change-locality.md`'s rule applied to the gate's
own paperwork: a single file every agent has to edit is a mutex, and the
entries were already grouped by tree, so the grouping is what became the
boundary.

Two rules decide what "covers" means, and both are enforced, so an entry that
ignores either is a red build rather than a preference:

**A trailing `/` is what makes a prefix a tree.** `deploy/` covers everything
beneath `deploy/`; a prefix without the slash names one exact path and nothing
else. Without that rule the check is a string comparison with no path boundary
in it — `# covers: d` would own entries from `docs/` *and* `deploy/`, and
`# covers: deploy` would own `deployment/`, so one file could span two trees
while satisfying every other rule here.

**Where prefixes nest, the longest one owns the entry.** Splitting `deploy/`
into a `deploy/compose/` of its own is allowed, and then an entry for
`deploy/compose/x` belongs to the child file — the parent may not keep it, and
the gate says so by name. Without this, an entry under a split tree would
satisfy both files and have two homes, which is two places for a reader to fail
to find it. `tools/new-service` routes the entries it writes by the same rule,
asking the gate rather than reimplementing it.

**What did not move is where a suppression lives.** Every file is still under
`.github/secret-scan/`, which is the whole of the argument for having had one
file: a suppression has to travel away from the credential it accepts, so
somebody looks at it twice. A per-tree file sitting *beside* the code it
suppresses would be the inline pragma this gate refuses, spelt as a path
instead of a comment.

A file that declares no `covers:` is refused outright, entries or not. Judging
it per entry let an empty or comment-only `.txt` through in silence, which is
an ownerless file somebody meant to fill in — and the moment they do, it
inherits whatever the reader assumed it covered. The directive also has to sit
above the first entry, because below one it reads as though the lines over it
were covered by something else.

## What an entry may say

**The path is exact and never a glob.** A glob is how a suppression arrives for
a file nobody has written yet, and a file that arrives pre-suppressed is this
repository's most-repeated failure — a gate that quietly stops covering the
newest surface — with the paperwork already filled in.

**The rule is named**, so an entry silences one class and not the file. A
Compose unit is allowed to carry §14.1's local database default; it is not
thereby allowed to carry a GitHub token.

**The fingerprint is a hash and not the value**, and the reason is not
confidentiality — every value here is already in the tree in plain sight. It is
that these files must not become a *second* place a credential is written. A
second copy is the copy that outlives the rotation of the first, and a
credential in two places is a credential nobody can retire. It is also why a
reason must not quote the value it is about: these files are walked like every
other, and a reason carrying the literal would report itself.

The fingerprint is printed beside every finding, so adding an entry is reading
the failure and writing down why — never guessing at a digest.

**There is no inline pragma and there never will be.** `CLAUDE.md`'s argument
against an inline suppression is that it is a decision written where nobody
re-reads it; a suppression that has to travel to this directory is one somebody
had to look at twice. Splitting the list one file per tree did not weaken
that: every one of them is still here, under the gate, and none sits beside the
code it accepts.
