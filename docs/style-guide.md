# Style guide

**The dialect this repository's prose, C# and SQL are written in, and which
rules the build enforces rather than a reviewer.** This was `CLAUDE.md`'s
*Prose conventions*, *C# style — samples now, source later* and *SQL style*
sections. **One dialect, both phases**: these rules govern the blueprint's
fenced samples and the compiled solution alike, so a sample and its
implementation read identically, and `.editorconfig` is the file PR-01 ships
rather than a documentation convenience.

It is load-bearing whenever you write or review prose, C# or SQL — which is
most changes, not every change, and *most* is what earns it a file. A rule you
are about to apply is a rule you can afford to open a file for; a rule you are
about to break without knowing it is the case `CLAUDE.md`'s short list under
*Style* exists for.

**The content is verbatim in its arguments**, on
[`pr-decision-log.md`](pr-decision-log.md)'s terms and for its reason — and
this is the file where that failure has a name. **The rules marked
review-carried are why it exists**: no analyser reaches them, so this file and
a reviewer are the only things that do, and a summary of one is how it gets
"corrected" back into the corpus. The headings kept their levels under this
file's title, so nothing moved. And one **self-reference was rebased**: a
sentence noting that `CLAUDE.md` makes the same argument about its own line
count said "the top of this file", which was true in its old home and points at
the wrong document here. Not one argument was shortened and no paragraph was
dropped.

**Two claims were then corrected in the same pull request, by review rather
than by the move**, and they are listed here because the sentence above would
otherwise read as covering them: the fence inventory was missing `promql`,
which §13 had been using throughout, and the whitespace rule overstated what
the build enforces — `.editorconfig` permits an aligned `=` in an initialiser
on purpose. Neither is an argument shortened; both are a claim brought back to
what is measurable. **A file that says "verbatim" earns the right to say it by
listing what it is not.**

**A short list of these rules stays in `CLAUDE.md`**, under *Style*, because
they have to be true of an edit made before anyone opens this file. **This file
is the master copy**: where the two disagree, the disagreement is a bug report
against the shorter one, and the change that moves a rule has to reach both.

**`/style-pass` records a corrected form here**, in the C# style or SQL style
section below, in the established voice — the rule, an example, and the reason
it is not arbitrary — and then reconciles `.editorconfig` in the same change.
State the exceptions with the rule: half this file's value is the carve-outs,
and a rule recorded without its exceptions gets applied to the exceptions next
time.

## Prose conventions

- **Wrap prose at 80 columns.** Tables, links and code blocks may exceed it.
- **British spelling in prose** — `behaviour`, `serialisation`, `licence`,
  `normalise`, `organised`. **Identifiers keep their real spelling**:
  `IPipelineBehavior`, `AddAuthorization`, `[Authorize]`. Never "correct" a type
  name into British spelling, and never Americanise the surrounding prose to
  match a type name.
- **Chapter headings are numbered**: `# 9. Messaging`, `## 9.1 Integration
  events`. Third-level headings are unnumbered prose titles
  (`### Handler contracts`). Appendices use `# Appendix A — <title>` and
  `## ADR-001 — <title>`; `docs/pr-decision-log.md` follows the ADR form.
- **Cross-references use the section sign**: `§9.3`, and link the first mention
  in a passage — `[§9.3](09-messaging.md)`. Within a chapter, `(§6.5)` bare is
  fine. Cite the section that actually states the claim; a reference to a
  section that only mentions the topic is a defect.
- **Callouts are blockquotes whose opening sentence is bold**, no emoji, no
  admonition syntax. Two forms are named and recurring — `**Trap — …**`
  (21) for a mistake worth naming, and `**Decision — …**` (10), which always
  points at the ADR that records it:

  ```markdown
  > **Trap — projecting everything by default.** Each projection is a second
  > copy of the truth, with its own bugs and its own rebuild procedure.

  > **Decision — no mediator library.** See [ADR-004](appendix-a-adrs.md#adr-004--no-mediator-library).
  ```

  The rest are a bold assertion followed by its argument —
  `> **Unregistered, this fails silently and completely.** …`. That is the
  default; reach for `Trap` or `Decision` only when the callout genuinely is
  one. `**Decision.** / **Why.** / **Consequences.**` are the ADR body form,
  not callouts.

  **The total used to be written here and no longer is, which is the third
  fix and the only one that holds.** It said 120 for eight PRs, then 161, then
  186; PR-24 recounted it to 189 mid-branch and its own later commits made
  that 194 before the pull request merged. **A number that goes stale twice
  inside one PR is not a number that recounting fixes.**

  What holds is the two named counts, and the branch that added a sixteenth
  `Trap` was the first test of that; a seventeenth has since landed and was
  reconciled the same way. This paragraph used to say *what never
  drifted is 15 and 8*; `Trap` has now moved twice, and the lesson survives the
  movement rather than being refuted by it — those are the figures a reader
  checks, so the change was caught and reconciled inside the PR that caused it,
  where the residual nobody looks at is the one that rots unnoticed. Keep them
  current when a callout is added; do not bring the total back —
  `CLAUDE.md` makes the same argument about its own line count at the top of
  that file, and `deploy/observability/README.md` about its rule counts.

  If you do need the figure, it is `grep -h '^> \*\*' *.md | wc -l` over the
  twenty blueprint files, minus the three definitional entries below — and
  **not** `grep -c`, which over many files prints a count *per file* and has
  to be summed, which is one more place to be wrong.

  **Three callouts spell the dash outside the bold and are not counted above**:
  §1.3's two glossary entries, which *define* `Trap` and `Decision` rather than
  being instances of them, and one `**Decision** —` in §14. Counting a
  definition as an instance is what made an earlier revision of this sentence
  say 16 and 10 — an arithmetic that added to 120 only by including two entries
  that fail the predicates beside them, since a glossary line is not "a mistake
  worth naming" and does not "point at the ADR that records it".
- **Em dashes** (`—`) for asides, **en dashes** (`–`) for ranges. Both are
  literal Unicode, not `--`.
- **Every chapter ends with a rule and a nav footer**, in this exact shape:

  ```markdown
  ---

  [← §5 Tactical DDD](05-tactical-ddd.md) · [Index](README.md) · [§7 Persistence →](07-persistence.md)
  ```

  Separator is ` · ` (U+00B7). First chapter omits the `←` link, last appendix
  omits the `→` link. One `---` only — a doubled rule before the footer is a
  regression that has been fixed before.
- **Tables carry the summary data**; prose carries the argument. A two-column
  borderless table (`| | |`) is the established form for metadata blocks.

## C# style — samples now, source later

**One dialect, both phases.** The rules below govern the fenced samples and the
compiled solution alike, so a sample and its implementation read identically.
The repo `.editorconfig` is not a documentation convenience — it is the file
PR-01 ships. Change it deliberately, and reconcile any change with the samples
already written against it.

Each rule below says whether the build enforces it. **The ones marked
review-carried are why this section exists**: no analyser reaches them, so this
file and a reviewer are the only things that do.

### Layout and naming

- Four-space indent, spaces not tabs. CRLF line endings. Newline at end of file.
- Pascal case for types, properties, methods and events; `I` prefix on
  interfaces; namespace matches folder.
- **`using` directives outside the namespace**, no blank lines between
  consecutive usings (IDE0065, enforced). This binds source only — a sample is
  an excerpt rather than a compilable unit, so **do not "complete" one by
  adding the block it would need to build.** The blueprint carries exactly two
  `using` lines, both `using static` (§9.6's saga imports `Endpoints`, §12.4's
  subject suite imports `Principals`), because an unqualified
  `Authenticated(caller)` reads as a member of the type being shown unless
  something says otherwise. A third is fine on those terms; a plain `using` is
  not.
- **No unused `using` directives** — *review-carried*. A file that stops needing
  one drops it in the change that stopped needing it: a stale using is a claim
  that the file depends on something it does not, and the reader who trusts it
  looks in the wrong assembly first. **Nothing catches this.** IDE0005 is not
  reported by the build at all unless `GenerateDocumentationFile` is on, so
  `dotnet build` is silent even with `TreatWarningsAsErrors`, and so is
  `dotnet format style --diagnostics IDE0005` — both checked against a
  deliberately injected using. Turning it on costs a fourth entry in
  `Directory.Build.props`, because it also enables CS1591 against **1,178**
  public members with no XML comment, across `src/` and the test projects
  alike. That figure said 62 until this branch measured it — `dotnet build
  Platform.slnx -p:GenerateDocumentationFile=true -p:TreatWarningsAsErrors=false
  --no-incremental`, counting unique diagnostic sites, run twice for the same
  answer. It has grown roughly nineteenfold since it was written, which makes
  the fourth-entry argument stronger rather than weaker. **`--no-incremental`
  is load-bearing in that command**: an ordinary build recompiles nothing and
  reports zero, which reads exactly like a clean result. The greyed-out using
  in the IDE is the only live signal there is.
- **A blank line always follows the namespace declaration** (IDE0055,
  enforced). `namespace X;` is a statement about the whole file, not the first
  line of the type below it:

  ```csharp
  namespace Common.Application;

  public interface IDispatcher
  ```

### Statements and types

- **A single statement may omit braces; two or more always take them — and so
  does one that wraps.** The statement goes on the following line, never beside
  the condition, and if it does not finish on that line the body takes braces:

  ```csharp
  // Wrong — the body wraps, so the reader has to count indentation to find
  // where the condition's reach ends.
  if (!Known.Contains(message.Reason))
      throw new ContractMappingException(
          $"Unknown review reason '{message.Reason}'.");

  // Right.
  if (!Known.Contains(message.Reason))
  {
      throw new ContractMappingException(
          $"Unknown review reason '{message.Reason}'.");
  }
  ```

  **This rule tightened in PR-21 and cost a 49-site sweep**, because the
  sentence used to end "and it may wrap". It is *review-carried*: no analyser
  reaches it — IDE0055 governs the indentation of whatever shape is there and
  has no opinion on which shape it is — so the corpus is the only enforcement,
  and a new braceless wrapped body is a review comment rather than a failed
  build.

  A single statement that fits on its line still omits them:

  ```csharp
  if (amount < 0)
      throw new DomainException("Money cannot be negative.");

  foreach (IDomainEvent domainEvent in events.Where(projections.HasHandler))
      await publisher.StageAsync(domainEvent, OutboxLane.Local, ct);
  ```

  This holds across all 49 braceless bodies in the blueprint, counted over
  ` ```csharp ` fences after PR-21 braced the 18 that wrapped. **The figure it
  replaces was 53 and does not reconcile**: the same count run before that sweep
  gives 67, so 53 was neither the old value nor the new one under this
  definition. Which definition produced it is not recoverable, and inventing one
  would be worse than saying so — the number below is the one a rerun of
  `count-braceless` reproduces.
  `csharp_preserve_single_line_statements = false` keeps a format run from
  pulling any of them back up onto the condition's line. The one exception is a
  **wrapped** condition, which takes braces — see below.
- **Explicit types for locals**, except where the right-hand side names the
  type. A reader of a fenced code block has no hover and no go-to-definition,
  and this blueprint's job is to teach types and contracts. Code blocks may run
  past 80 columns, so length alone is never a reason to hide a type — if a
  declaration turns unwieldy, split the expression.

  ```csharp
  IReadOnlyDictionary<ProductId, Money> priceList =
      await prices.GetAsync(productIds, command.Currency, ct);
  ProductId[] missing = [.. productIds.Where(id => !priceList.ContainsKey(id))];
  ```

  Four cases keep `var`, and only these four:

  | | Example |
  |---|---|
  | The RHS names the type | `var order = Order.Place(…)`, `var id = Guid.CreateVersion7()` |
  | Anonymous types | `var args = new { OrderId = orderId.Value };` |
  | Tuple deconstruction | `foreach (var (product, qty, price) in items)` |
  | Fluent resource DSLs | The whole Aspire AppHost block in §14.2 — eleven of its thirteen locals are an `IResourceBuilder<T>` whose name only repeats what the `Add*` call already said. Explicit types are possible there and read worse; keep the block uniform |

- **Target-typed `new()` only where the type is already named beside it** —
  half enforced, half *review-carried*. This is the `var` rule applied to the
  right-hand side. Where the declaration names the type, `new()` repeats
  nothing and `.editorconfig` asks for it:

  ```csharp
  ServiceCollection services = new();
  private static readonly ConcurrentDictionary<Type, Invoker> Cache = new();
  public static Result Success() => new(null);
  ```

  Where nothing beside it names the type, spell the type out. The positions
  that hide it are **an argument**, **a collection expression in argument
  position**, and **the target of an indexer or property assignment**:

  ```csharp
  // Wrong — the reader cannot see what is being constructed in any of these.
  .MapHealthChecks("/health/live", new() { Predicate = _ => false })
  .AddAttributes([new("deployment.environment", builder.Environment.EnvironmentName)])
  scrubbed[i] = new(attribute.Key, "[redacted]");

  // Right.
  .MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
  .AddAttributes([new KeyValuePair<string, object>("deployment.environment", builder.Environment.EnvironmentName)])
  scrubbed[i] = new KeyValuePair<string, object?>(attribute.Key, "[redacted]");
  ```

  A collection expression assigned to a typed declaration is **not** one of
  them — the element type is on the left, so the elements stay bare:

  ```csharp
  KeyValuePair<string, object?>[] state =
  [
      new("NewPassword", "a"),      // fine — the array's type is on the left
      new("card_number", "b")
  ];
  ```

  **No analyser reaches the banned half**: IDE0090 fires only when the type *is*
  apparent, so it polices the first block and has nothing to say about the
  second. **Where naming the type breaks the 120-column budget, name it in a
  local — do not fall back to `new()`.** A local declaration carries the type,
  so the `new` beside it needs none and both lines fit.
- Prefer collection expressions, `is null` over `ReferenceEquals`, null
  propagation, compound assignment, simplified interpolation, primary
  constructors.
- **Materialise with a spread, not a terminal `.ToArray()` or `.ToList()`** —
  *review-carried*. The spread states the target and the terminal call states a
  conversion, and only one of those is what the line is for:

  ```csharp
  ProductId[] missing = [.. productIds.Where(id => !priceList.ContainsKey(id))];
  ```

  Two consequences: dropping `.ToArray()` often leaves a **single** call, and a
  single call is not a broken chain — join it. And a spread frequently brings
  the statement back under 120, in which case the one-line rule applies and the
  `[` does not get its own line.

  **A `ToArray` that is not a sequence materialisation is outside this rule.**
  `MemoryStream.ToArray()` is a stream accessor — the type implements no
  `IEnumerable`, so `[.. buffer]` does not compile (CS9212). The rule is about
  the *terminal LINQ operator*, where the spread and the call are two spellings
  of one thing. This was narrowed after a reviewer read "there are no
  `.ToArray()` calls left in the corpus" as the grep it literally is — **a rule
  whose stated test is a string match will be enforced as one.**

### Wrapping

- **A list is on one line, or one element per line. Never a ragged middle.**
  "List" means anything comma-separated inside brackets — parameters,
  arguments, collection expressions, initialisers, tuple members. The budget is
  **120 columns** (`max_line_length`); within it the list stays on one line,
  past it every element gets its own.
- **`[` and `{` each take a line of their own**, at the column of the construct
  they open, and their closers do too. **`(` is the single exception**: it ends
  the line it opens, and `)` trails the last element — `);`, not a line of its
  own:

  ```csharp
  string[] forbidden =
  [
      "Microsoft.EntityFrameworkCore",
      "MassTransit",
      "StackExchange.Redis",
      "Microsoft.AspNetCore"
  ];

  _deliveryLag = meter.CreateHistogram<double>(
      "messaging.delivery.lag",
      unit: "s",
      description: "OccurredAt to consumer start.");
  ```

  The exception is not arbitrary. A parenthesised argument list is *part of* the
  invocation, so it hugs the call at both ends; a braced or bracketed body is a
  *container* of elements, and giving it its own opening and closing line puts
  its extent in a column the eye can scan.

  **The two halves are enforced very differently.** `{` is not a review rule at
  all: the C# default for `csharp_new_line_before_open_brace` is `all`, IDE0055
  reports a trailing one, and ADR-019 turns that into a failed build. `[` has no
  such backing — Roslyn has no opinion on bracket placement and `dotnet format`
  neither introduces nor removes the break — so that half is *review-carried*.
- **A trailing lambda is an argument like any other**, and does not hang off the
  opening line. An argument list holding a lambda has exactly two legal shapes,
  tried in this order:

  ```csharp
  // 1. One line, if it fits inside 120. Always preferred.
  Publish(payload, type, c => { … }, ct);

  // 2. Otherwise one argument per line — the lambda included, braces and all.
  Publish(
      payload,
      type,
      c => { … },
      ct);

  // And if the lambda itself will not fit on its line, its braces open under
  // the rule that governs braces, at the argument's own column.
  Publish(
      payload,
      type,
      c =>
      {
          c.MessageId = message.MessageId;
          c.CorrelationId = message.CorrelationId;
      },
      ct);
  ```

  **There is no carve-out, and a braced body does not earn one.** One was tried
  — leading arguments stayed up whenever the trailing lambda had a braced body —
  and removed, because it made the rule undecidable from the call site: whether
  a leading argument could stay depended on the *last* argument's body kind and
  on whether anything followed it, and a reviewer performing only the first
  lookahead got `Publish(payload, type, c => { … }, ct)` wrong. The cost is
  real and accepted: every builder DSL in the blueprint now breaks across four
  or five lines. It buys a rule with no lookahead.

  A **single**-argument call is untouched — `AddRateLimiter(options => { … })`
  keeps its shape. Nor is a lambda the only thing that can hang:
  `WriteAsJsonAsync(new ProblemDetails { … }, ct)` is the same shape with an
  object initialiser in the lambda's place.

  **Two greps narrow this down and neither closes it.** The arrow —
  `\(.+,\s*\w+\s*=>\s*$` — catches a lambda hanging off a call with a leading
  argument. The closer — `^\s*[]})],\s*\S` — catches a bracket closing at the
  head of a line with an element still after it. **Neither sees the plain
  one**: a continuation line carrying two ordinary arguments has no arrow and
  no leading bracket. Treat them as a sieve, not a proof.

  **Write the closer for the tool you are running it in.** Ripgrep reads `\]`
  as an escaped bracket and builds the class `}` `]` `)`; POSIX `grep` treats a
  backslash inside a bracket expression as literal, so the class closes at the
  first `]` and the pattern matches nothing, ever — reporting zero and exiting
  1, which reads exactly like a clean sweep. Put the `]` first — `[]})]` — and
  it means the same thing in both.
- Continuations indent **four**, never to a bracket column. A list wrapped under
  its opening bracket is a leftover from an older style. Two things keep this
  from being mechanical: a line comment on an element forces the broken form
  regardless of width, and the four `var` cases still apply inside the elements.
- **A broken fluent chain puts every call on its own line**, at head + 4, never
  aligned under the receiver's dot — including the *first* call. If the chain
  breaks at all, nothing stays on the head's line:

  ```csharp
  builder.Services
      .AddReverseProxy()
      .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
  ```

  Unlike a list, a chain is **never joined back up to fit** — it is broken for
  reading order, not for width. One exception: a short qualifier and its
  subject count as one call (`.That().ResideInNamespaceContaining(…)` —
  NetArchTest's idiom reads as pairs).

  **The head is the line the chain starts on**, which is not always the
  statement's first line. When a declaration's initialiser wraps, that wrapped
  line is the head and the chain indents four past *it* — eight from the
  declaration. A spread element is a head the same way, so `.. x` takes the
  chain at + 4. A `})` closing a lambda mid-chain is a continuation, not a head.

  **"Contains no invocation" means no *dotted* call, and a receiver never
  outranks that.** `Types`, `app`, `_lines` and `Enumerable` all sit alone as
  heads, static classes and fields alike. What stays on the head is whatever has
  nothing to strand in front of it: object creation (`new MsSqlBuilder()`), a
  call with no receiver (`When(OrderPlaced)`), and a parenthesised expression
  (`(await Task.WhenAll(…))`).
- **A lambda body that is itself a call goes on its own line**, at + 4, rather
  than trailing after the `=>`. A bare parameter re-mention is not a call and
  stays — `p => p` heading its own chain is the fluent-DSL idiom:

  ```csharp
  builder.Services
      .AddCors(o =>
          o.AddDefaultPolicy(p => p
              .WithOrigins(builder.Configuration.GetRequiredSection("Cors:Origins").Get<string[]>()!)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()));
  ```

  So `o => o.AddDefaultPolicy(…)` breaks and `p => p` does not, in the same
  statement. This applies only when the construct is already broken across
  lines; inside 120 columns it stays on one.

  An **expression-bodied member** is not a lambda for this purpose:
  `public Money Total => _lines.Aggregate(…)` keeps its body on the `=>` line,
  because there is no nesting for the break to clarify. When such a member's
  body *does* need its own line, `=>` still trails the signature, and the body
  sits at the **declaration's** indent + 4 — not necessarily the `=>` line's.
- **Break at the outermost bracket, never a nested one.** Reaching past the
  outer parenthesis leaves the outer call glued to its argument:

  ```csharp
  // Wrong — the break is inside OrderPlacedDomainEvent, and Raise( is stranded.
  order.Raise(new OrderPlacedDomainEvent(
      order.Id, customerId, order.Total, order.SnapshotLines(), now));

  // Right.
  order.Raise(
      new OrderPlacedDomainEvent(order.Id, customerId, order.Total, order.SnapshotLines(), now));
  ```

  The argument moves to the next line **whole**; break it further only if it
  still does not fit. This composes with the lambda rule rather than competing:
  a call whose last argument is a lambda breaks at its **own** parenthesis,
  which puts the lambda on a line of its own, and never after the `=>`.
- **A multi-line condition takes trailing operators, four-space continuations,
  and braces on its body.** The braces are what make four safe: without them
  the last `&&` line and the body sit in the same column.

  ```csharp
  if (!command.IsSystemInitiated &&
      (!currentUser.IsAuthenticated ||
          (order.CustomerId.Value != currentUser.Id &&
              !currentUser.HasPermission("orders:admin"))))
  {
      return Result.Failure(OrderErrors.NotFound);
  }
  ```

  This is the **one exception to the braces rule above**. Prefer not to wrap at
  all; joining is the better fix and has been applied everywhere it fits. The
  block above is the corpus: one wrapped header, in §11.4, kept because an
  ownership check that fails closed does not join inside 120. A second one
  appearing is a signal to join, not a precedent. **A parenthesised group that
  breaks indents a further four**, so nesting depth is visible. The check above
  gained its second level when the guard was rewritten to fail closed; the
  earlier form led with `currentUser.IsAuthenticated &&`, which read as a guard
  and behaved as an exemption, admitting every caller arriving with no
  principal at all.
- **Operators go at the end of the line they continue from**
  (`dotnet_style_operator_placement_when_wrapping = end_of_line`). Each line
  then ends by announcing that more is coming. This holds for `&&`, `||`, `??`
  and `+`, in conditions and expressions alike; a leading `&&` or `??` is a
  leftover. It governs wrapped lambda predicates as much as `if` headers.
- A base-type list is the one continuation covered by none of the above: no
  bracket to hang off, no operator, already one entry per line. Leave it
  aligned under the `:`.

### Whitespace, suppressions and secrets

- **One space before `=`, `=>` and `{` — never a column of them.** Padding a
  token out to line up with the one above is the house form this rule
  refuses.

  **How much of it the build actually catches is narrower than this rule,
  and the gap is deliberate on the build's side.** IDE0055 reports the
  padded member declaration below and ADR-019 makes that an error — but
  `.editorconfig` sets `csharp_space_around_declaration_statements =
  ignore`, arguing in its own comment that extra spaces before `=` are
  *alignment, not accident*, and naming the initialiser members that rely
  on it. So an aligned `=` column in an object initialiser compiles green,
  and the corpus holds live examples in `OutboxDispatcher` and Catalog's
  query handlers.

  **The two statements were contradictory for as long as both existed**,
  and this is the documentation reconciled to the measurement rather than
  the rule relaxed: what the build enforces is one thing, what this file
  asks of a reviewer is another, and only the first was overclaimed.
  Flipping that setting to `false` would collapse every aligned initialiser
  in the corpus, which is a `/style-pass` decision about the whole corpus
  and not one to take inside an extraction.

  ```csharp
  // Fails the build. Every line but the longest carries the diagnostic.
  public required Guid MessageId       { get; init; }
  public required decimal TotalAmount  { get; init; }

  // Correct.
  public required Guid MessageId { get; init; }
  public required decimal TotalAmount { get; init; }
  ```

  **A trailing `//` comment is the carve-out, and it is a real one.** IDE0055
  does not govern whitespace in front of a comment, so a comment may sit in
  whatever column the block reads best in. The carve-out stops at the end of
  the line: a trailing comment too long for one line cannot wrap into a second
  `//` line aligned under the first, because a line *starting* with `//` is a
  whole-line comment whose leading whitespace is indentation, and indentation
  IDE0055 does govern. Shorten it, or move it above the statement whole.

  `dotnet format` agrees with both halves — it collapses the code padding and
  leaves the comment column untouched — so a format run neither introduces this
  nor undoes it.

  **Two places the analyser does not reach, and they are opposites.** Padding
  between a type and its identifier —
  `private readonly Counter<long>   _placed;` —
  is *not* reported, and is swept anyway — *review-carried*, because one
  dialect beats a rule that stops halfway. **SQL is the other way round and
  keeps its alignment**: it lives inside raw string literals, which no analyser
  or formatter reads, and the SQL section argues its columns on their own terms.
- **No `#pragma` suppressions** — there are none in the corpus, and a sample
  that needs one is a sample whose design is wrong. A genuinely warranted
  suppression belongs in `Directory.Build.props` with a comment, never inline.
- **No real credentials** — no production connection string, key or certificate
  path, in a sample or in source. Bind configuration or read the environment;
  deployed secrets come from a vault (§15.4).

  The deliberate exception is local development, and it is not a defect to be
  tidied away: §14.1's Compose file carries
  `${SQL_PASSWORD:-Local_Dev_Pa55w0rd!}` and
  `${BFF_CLIENT_SECRET:-local-dev-secret}`, and documents Keycloak as
  `admin/admin`. Those defaults are what make `docker compose up` work with
  no prior setup; the environment variable in front of each is the seam that
  keeps them out of anything deployed.

  **The broker is the one that is NOT fronted by a variable, and since
  ADR-036 it is not `guest/guest` either.** Each service carries a literal
  `{service}-svc` / `local-dev-{service}`, because those credentials are
  imported into the image from `definitions.json` and a `${...}` in front of
  the connection string would front only one half of a pair — the broker
  would still expect the compiled-in password. Rotating them locally is an
  edit to that file and a `docker compose down -v`, which is stated in
  `deploy/compose/README.md` rather than implied by a seam that is not
  there.

### Settled choices — do not "fix" them

| | |
|---|---|
| Namespaces | File-scoped (`namespace X;`), never block-scoped |
| Extension declarations | C# 14 `extension(T receiver)` blocks where a class groups several extensions on one receiver — `Common.Application.DependencyInjection` is the worked example. **The corpus is currently split**: every extension class in `Common.Web` still uses the classic `this`-parameter form. Four extend a receiver nothing else does — `ProblemDetailsExtensions` (`IServiceCollection`), `HealthCheckExtensions` (`IEndpointRouteBuilder`), `AuthorizationPolicyExtensions` (`AuthorizationPolicyBuilder`) and `ResultExtensions` (`Result`) — while two groups share one: `CorrelationIdExtensions` and `SecurityHeadersExtensions` on `IApplicationBuilder`, and `ObservabilityExtensions`, `AuthenticationExtensions` and the `CommonWebDefaultsExtensions` that composes them on `IHostApplicationBuilder`. Whether to group those three is open and deliberately unsettled — they are separate files because one composes the other two, and merging would put a caller-facing entry point in the same block as the pieces it calls. The receivers are listed rather than counted because a bare count is what went stale here — twice now: this cell said six and four from before PR-16 added `AuthenticationExtensions`, then eight and five until `SecurityHeadersExtensions` landed on a receiver `CorrelationIdExtensions` already had, which is the split that makes "extends a receiver nothing else does" the wrong shape for a tally. Converting anything is a decision about the whole corpus |
| Expression-bodied members | Used for one-line members, not for constructors |
| Braces | Optional for a single statement, required for two or more |
| Target framework | .NET 10 (LTS), C# 14 |

Each of these is a house style a reviewer might otherwise read as an oversight
and "correct". They are consistent across all ~150 existing C# blocks, and the
solution is written the same way. Changing one is a decision about the whole
corpus, not about the file in front of you.

**Fence languages in use:** `csharp`, `sql`, `yaml`, `bash`, `json`, `mermaid`,
`dockerfile`, `xml`, `promql`, and bare ``` for trees and console output.
Always tag a fence that contains a real language.

  `promql` was missing from that list for as long as the list existed, and
  §13's alert expressions have been fenced with it throughout — the inventory
  was written from the languages somebody remembered rather than from the
  corpus. The predicate is one command, so run it rather than trusting the
  sentence: grep the blueprint for lines opening a fence with a language tag,
  then `sort | uniq -c`. It returns exactly these nine.

  **That command is described rather than quoted, and the reason is a rule
  this file states elsewhere.** A fence marker cannot sit inside a
  single-backtick span, and a fenced block quoting one has to be opened with
  four backticks — so the literal form is a rendering trap in a paragraph
  whose whole subject is fence markers.

## SQL style

Most of the SQL lives inside C# raw string literals rather than `sql` fences, so
"the statement's own left margin" below means the literal's indent, not column
zero.

**Clause keywords start their own line at that margin, one space before what
follows; continuations indent four.** Continuations are the connectors that
extend a clause — `AND`, `OR`, and a `MERGE`'s `ON`. Each predicate gets its own
line: a chain packed onto one line hides the shape of the condition, which in
this blueprint is usually the point being made.

**An `AND` extending an `ON` aligns with the `ON`**, rather than indenting
another four. `ON` is itself a continuation — of `MERGE … USING` or of a
`JOIN` — so its predicates sit at the level it already occupies, and a second
indent would imply a nesting that is not there:

```sql
MERGE ordering.ProductPrices AS target
USING (SELECT ProductId = @ProductId, Currency = @Currency) AS source
    ON target.ProductId = source.ProductId
    AND target.Currency = source.Currency
```

This is the one place `AND` does not indent past the keyword it extends; under
`WHERE`, which is a clause keyword at the margin, it still does.

```sql
UPDATE ordering.OrderSummaries
SET FulfilmentCounted = 1
OUTPUT inserted.PlacedAt, inserted.ConfirmedAt
WHERE OrderId = @OrderId
    AND PlacedAt IS NOT NULL
    AND ConfirmedAt IS NOT NULL
    AND FulfilmentCounted = 0;
```

One space means one, `WHERE` included — it used to be written `WHERE  ` and no
longer is.

`UPDATE <table>` and `SET` are separate lines. The exception is `MERGE`'s
`UPDATE SET`, which names no table and stays one token.

**A `SET` that breaks keeps nothing on the keyword's line.** Like a fluent
chain, if the assignment list does not fit it goes below whole — the first
assignment does not stay up beside `SET` with the rest hanging under it:

```sql
UPDATE SET
    CustomerId  = @CustomerId,
    TotalAmount = @Total,
    Currency    = @Currency,
    LineCount   = @LineCount,
```

The `=` signs line up in a column here, and that alignment is deliberate. It is
**not** justified by parity with the C# initialisers — IDE0055 forbids the C#
form and the section above says so. SQL keeps the column on its own merits: a
statement inside a raw string literal is invisible to every analyser and
formatter in the toolchain, so nothing will fight it, and one assignment per
line with the names in a column is what makes `SET` read as the row shape it
produces rather than as a wrapped list.

**A SQL list obeys the same rule as a C# one: one line, or one element per
line, never a ragged middle.** That covers the column list after `INSERT`, the
values after `VALUES`, the columns after `OUTPUT` and `GROUP BY`, and a
function's arguments. The budget is the same 120 columns, measured from the
literal's margin:

```sql
INSERT (OrderId, CustomerId, Status, TotalAmount, Currency, LineCount, Products, PlacedAt, UpdatedAt)
VALUES (@OrderId, @CustomerId, @Status, @Total, @Currency, @LineCount, @Products, @PlacedAt, @UpdatedAt)
```

Past the budget the `(` ends its line and each element takes one, indented four
— **not** aligned under the first argument. `DATEADD(second, …)` wrapping its
arguments into a column under `second` is the keyword river again, one scope in.

The single exception is a **DDL body**, where alignment is a table rather than a
wrapped list: `CREATE TABLE` and `CREATE INDEX` keep their aligned type and
constraint columns, because there the columns carry the meaning.

A parenthesised sub-expression short enough for one line stays on one line;
`AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())` is one
predicate, not two. A parenthesised group that *does* break indents its `OR`s a
further four, so nesting depth is visible:

```sql
WHERE o.CustomerId = @CustomerId
    AND (@AfterPlacedAt IS NULL
        OR o.PlacedAt < @AfterPlacedAt
        OR (o.PlacedAt = @AfterPlacedAt AND o.Id < @AfterId))
```

**Column aliases are assignments, not `AS`** — `Total = SUM(…)`, never
`SUM(…) AS Total`. The name being defined then starts the line, so a
projection reads as the row shape it produces, and the `=` column lines up the
way `SET`'s does. The running example is §6.5's history query, whose total is
summed from the order's lines: `ordering.Orders` stores no total, because the
aggregate computes one and §7.2 declines to keep a second copy of it. This is
the SELECT list only: `MERGE … AS target`,
`USING (…) AS source`, `WITH claimable AS (` and `CAST(x AS varchar(10))` are
required syntax and keep `AS`.

**`INNER JOIN` is spelled in full, and its `ON` gets its own line** at + 4. The
join condition is a predicate and belongs where predicates go, not trailing off
the end of a table name:

```sql
FROM ordering.Orders o
INNER JOIN ordering.OrderLines l
    ON l.OrderId = o.Id
```

An alias follows its table after one space. Padding table names into a column
(`FROM ordering.Orders      o`) is the old keyword river in another costume.

This replaced a right-aligned keyword river (`FROM   `, `WHERE  `, `  AND  `,
every argument at column 7). If you find one, it is a leftover — convert it.
