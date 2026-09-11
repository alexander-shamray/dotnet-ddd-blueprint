# Checkout quote — accept quantities

Design spec, frozen at write time. No PR number: this folder's `prNN` naming
stopped at PR-15 while the repository's pull requests ran on to #200, so this
is dated and named for its subject instead.

**Found by building a client.** The blueprint's reference frontend
(`blueprint-frontend`, Phase A) consumes `/bff/v1/checkout/quote` from a cart
screen. The endpoint is correct against its own contract and is not returning a
wrong number. It is returning a number a cart cannot use, and the reason is a
missing input rather than a bug.

## The problem

`GET /bff/v1/checkout/quote?productId=…&productId=…&currency=EUR` takes a **set
of product ids** and no quantities. `CheckoutEndpoints.cs` deduplicates them
(`productId.Distinct()`), asks Catalog for one price each, and answers:

```csharp
return Results.Ok(new QuoteResponse(
    currency,
    lines,
    lines.Sum(line => line.Amount),      // <- one unit of each distinct product
    [.. requested.Where(id => !priced.Contains(id))]));
```

`QuoteResponse.Total` is therefore *the sum of one unit of each distinct
product*. For a basket of two of product A and one of product B it is
`A + B`, not `2A + B`.

That would be unremarkable — the endpoint never promised a basket total — except
that `QuoteResponse`'s own summary states the purpose the total exists to serve:

> Shaped for a screen rather than for a resource: the total is computed here
> because every client would otherwise compute it, and two clients computing a
> total is two places to get rounding wrong.

The rule is right. The implementation cannot satisfy it, because the request
carries nothing to multiply by. A client with a cart has exactly two options,
and both defeat the rule:

1. **Multiply client-side** — precisely the second place computing money that
   the design forbids, and the place with the worst floating-point story.
2. **Not show a basket total at all** — which is what the reference client does
   today. It renders per-line unit prices and relabels the figure
   `Quoted unit prices:` rather than `Total:`.

Option 2 is honest and it is what shipped, with this comment standing over it in
`cart.page.ts`:

> `CheckoutEndpoints.cs` totals `lines.Sum(line => line.Amount)` over
> `productId.Distinct()`, and the request carries no quantities at all, so the
> reply's `total` is the sum of one unit of each distinct product — NOT the
> basket. Spec §5.2's rule that the client never sums money stands; what was
> wrong was calling that number a basket total.

So the platform currently ships a BFF whose stated job is to hand a screen the
number it renders, and a screen that cannot render the number it is handed. The
next client to want a basket total — a native app, a partner, a second
frontend — will either recompute money or ask this same question again.

## What this proposes

Give the quote the quantities it needs, and let the total mean what its own
documentation says it means.

## Decision 1 — the request carries quantities, and moves from GET to POST

**Decision.** Replace the repeated `productId` query parameter with a request
body of lines:

```jsonc
POST /bff/v1/checkout/quote
{
  "currency": "EUR",
  "lines": [
    { "productId": "…", "quantity": 2 },
    { "productId": "…", "quantity": 1 }
  ]
}
```

**Why not keep GET and add a parallel `quantity` array.** This is the obvious
cheap option and it should be rejected explicitly, because the endpoint already
contains the argument against it. ASP.NET binds `Guid[] productId` and
`int[] quantity` independently: nothing enforces equal length, and nothing
enforces that the *n*th quantity belongs to the *n*th id. A proxy that drops or
reorders one repeated parameter silently shifts every quantity onto the wrong
product. The symptom is a wrong total and nothing else — which is the exact
failure class this endpoint already grew a `HashSet<Guid> outstanding` to
prevent:

> The reply is checked against the question rather than trusted to have answered
> it. […] an id nobody asked about was priced and totalled, and an id answered
> twice was totalled twice. Neither appears in `Unpriced` […] so the only
> symptom was a wrong `Total`.

Having paid for that lesson on the *reply* side, the endpoint should not
reintroduce it on the *request* side. Positional pairing across two independently
bound collections is a contract that cannot be validated after the fact: if the
arrays arrive with equal length, there is no way to tell a correct pairing from a
shifted one.

**Why not a composite string** (`productId=<guid>:<qty>`). It re-implements
parsing and error reporting inside a query parameter, and the 400 it produces
cannot be keyed to a field, which is what `ValidationProblem` responses are for.

**The cost of POST, stated plainly.** A quote is a read, and POST makes it look
like a write. Two things make the trade acceptable here rather than merely
convenient:

- Nothing caches this today. The endpoint carries `RequireAuthorization()`, emits
  no cache headers, and a quote is keyed to one customer's basket — the hit rate
  of a shared cache on it is approximately zero.
- The request is already unbounded in the shape that matters. It is a list whose
  length is bounded by policy, not by URL length, and a body is the honest place
  for a list of records.

`GET` also has a latent ceiling the body does not: 100 product ids as repeated
query parameters is roughly 4.5 kB of URL, which is inside every common limit but
not far inside, and it is a limit nobody chose.

## Decision 2 — the response echoes the quantity and gives a line total

**Decision.** `QuoteLine` gains two fields:

```csharp
public sealed record QuoteLine(
    Guid ProductId,
    string Name,
    decimal Amount,      // unchanged: the UNIT price
    int Quantity,        // echoed from the request
    decimal LineTotal);  // Amount * Quantity, computed here
```

and `Total` becomes `lines.Sum(line => line.LineTotal)`.

**Why echo the quantity.** The same reason `Currency` is echoed today — "so the
response stands alone". A client that must hold its request alongside the reply
to render the reply has been handed half an answer.

**Why `LineTotal` rather than letting the client multiply.** Because otherwise
this change fixes the basket total and leaves every *line* total computed in the
client, which is the same defect one level down. A cart shows "2 × €12.50 =
€25.00"; if the BFF supplies the first two and not the third, the rule is still
broken and the fix is still incomplete. With `LineTotal` present, the reference
client's rule — the client never computes money — holds with no exceptions.

`Amount` keeps its meaning as the unit price and keeps its name. It is still
needed: a cart renders "€12.50 each".

## Decision 3 — the bounds are Ordering's bounds, exactly

**Decision.** Validate `quantity` as `> 0` and `<= 999`, and `lines` as non-empty
with `Count <= 100`.

**Why those numbers and not new ones.** They are already the bounds a customer
must satisfy to place the order this quote is for.
`PlaceOrderValidator.cs:56` reads:

```csharp
item.RuleFor(i => i.Quantity).GreaterThan(0).LessThanOrEqualTo(999);
```

and `MaxItems = 100` bounds the line count, for a reason that is about storage
rather than taste — `ProjectedPriceReader` expands product ids into one SQL
parameter each and SQL Server stops at 2,100.

A quote that accepts what the order will refuse hands the customer a price for a
basket they cannot buy, and moves the refusal from the cart screen (where the
number they typed is still in front of them) to the checkout screen (where it is
not). Any daylight between the two validators is a bug waiting for someone to
find it with a quantity of 1000.

**This is a new responsibility for the BFF, and that is the point.** The endpoint
currently declines to own the *product-count* ceiling, deliberately and correctly:

> No ceiling checked here, deliberately. Catalog's `GetPricesValidator` owns that
> number […] A second copy of the limit in this host would be a number that
> drifts from the one actually enforced.

That reasoning does not transfer to quantity. Catalog prices a *product*; it has
no opinion about how many of one a basket holds and no validator that could own
the bound. Quantity is a basket concept, the BFF is the only host that sees a
basket, and Ordering is the only other holder of the same rule. Two copies is one
more than ideal — see the open question below — but zero copies means no bound at
all.

## Decision 4 — a repeated product id is refused, not summed

**Decision.** Two lines naming the same `productId` produce a 400 keyed to
`lines`, naming the duplicated id.

**Why not sum them.** Silently summing repairs a caller's bug and hides it. A
basket has one line per product; a request with two is a client that has lost
track of its own state, and the kindest response is the one that says so while
the cause is still on screen.

**Why not keep `Distinct()`.** Under the current contract, dropping a duplicate
loses nothing — the second copy carries no information. Under this one it carries
a quantity, and dropping it silently discards part of the customer's basket.
`Distinct()` on a set of ids is a tidy-up; `DistinctBy(l => l.ProductId)` on a
list of quantified lines is data loss.

The dedup's second purpose survives: the existing comment notes it "keeps a
caller from spending the ceiling on the same id a hundred times", and refusing
duplicates outright does that at least as well.

**One test encodes the old behaviour and must change.**
`QuoteEndpointTests.A_repeated_product_is_asked_about_once()` asserts both halves
of the dedup: `quote.Lines.Count.ShouldBe(1)`, and — at the wire —
`_catalog.Calls.Single().ProductIds.ShouldBe([Chair.ToString()])`, with a comment
explaining that the upstream half is the one that matters, because it is what
stops a caller spending Catalog's id ceiling on one product repeated a hundred
times.

Its replacement asserts the 400. Note that the wire assertion does not survive in
its present form and should not be forced to: validation runs before the hop, so
a duplicated id now means Catalog is called **zero** times, not once. The
replacement should say so — `_catalog.Calls.ShouldBeEmpty()` — which is the shape
`A_quote_with_no_products_is_refused_without_a_hop` already uses, and which
preserves the original's actual concern rather than its literal assertion. The
ceiling is protected more cheaply than before: the request never leaves the host.

## Decision 5 — v1 changes in place, and the client lands with it

**Decision.** Evolve `/bff/v1/checkout/quote` rather than adding a v2.

**Why.** The response change is additive — two new fields on `QuoteLine`, and a
`Total` that becomes correct for a case no existing caller could express. The
request change is breaking. There is exactly one known consumer, it lives in a
repository we control, and it can land in the same window. A `/v2` whose `/v1`
has no remaining callers is a second code path maintained to serve nobody, and
this repository's own locality rules are unfriendly to that.

**This is the one decision here that is the repository owner's rather than
mine**, because it depends on facts outside the code: whether any consumer exists
that this proposal does not know about, and whether the blueprint wants to
*demonstrate* versioned BFF evolution as a teaching artefact even where the
pragmatics do not require it. That second consideration is real — this is a
reference implementation, and "how do you change a BFF contract" is a question
its readers will have. If the answer is yes, this becomes a `/v2` endpoint with
`/v1` delegating to it at quantity 1 per line, which is a clean adapter and about
fifteen lines.

## What changes

- `src/BFF/Web.Bff/Endpoints/CheckoutEndpoints.cs` — `MapGet` → `MapPost`, a
  request record, quantity-aware totals, duplicate refusal.
- `src/BFF/Web.Bff/Endpoints/QuoteResponse.cs` — `QuoteLine` gains `Quantity` and
  `LineTotal`; the `Total` summary is reworded to say what it now sums.
- A request record and its validator (new file, alongside the endpoint).
- `tests/Web.Bff.Tests/QuoteEndpointTests.cs` — `A_quote_prices_every_product_and_totals_them`
  gains a quantity and asserts `2A + B`; `A_repeated_product_is_asked_about_once`
  becomes a 400; new cases for quantity 0, quantity 1000, 101 lines, and an
  empty `lines`.

Every 500-on-contract-violation test in that file is unaffected and should stay
exactly as it is. They constrain the Catalog boundary, which this does not touch.

## What this does not change

- The `Unpriced` list, its meaning, or the decision to name unpriced products
  rather than drop them.
- Every check on Catalog's reply: the invariant-culture parse and its deliberate
  `AllowLeadingSign`, the negative refusal, the currency-label check, and the
  `outstanding` set. All of them are about the reply and none of them is about
  quantity.
- The deferral of the *product-count* ceiling to Catalog's `GetPricesValidator`.
  The new line-count bound is a bound on the request's own size, checked before
  the hop, and is not a second copy of Catalog's number.
- Anything in Ordering. `PlaceOrderCommand` already carries quantities.

## Open questions for the reviewer

1. **Where should the shared quantity bound live?** Decision 3 puts a second copy
   of `> 0 && <= 999` in the BFF, and this repository is explicitly hostile to a
   number that can drift from the one actually enforced. The options are: accept
   two copies with a comment in each naming the other; move the bound into
   `Common.Contracts.Ordering.V1` as a constant both validators read; or have the
   BFF not bound quantity at all and let the order refuse it later. The third is
   the status quo's failure mode and I would not take it. Between the first two,
   a shared constant is more correct and drags a contracts reference into the BFF
   — which may or may not be a boundary this repository wants crossed.

2. **Should `PlaceOrderValidator` also refuse duplicate product ids?** It does not
   today: `RuleForEach` checks each item and nothing checks the set.
   `PlaceOrderHandler` maps items through `priceList[id]`, so two lines for one
   product become two order lines at the same price rather than an error. That is
   out of scope here, but Decision 4 makes the quote stricter than the order it
   quotes for, and the two should probably agree.

3. **Is `Amount` the right name** for a field that is now unambiguously a unit
   price sitting beside a `LineTotal`? `UnitPrice` would read better. It is a
   breaking rename on a field every consumer reads, for clarity alone, which is
   why this proposal does not do it — but it is worth a moment's thought while
   the record is already being changed.

## How the client changes

For whoever picks this up: the reference client's cart is already structured for
it. `CartStore` holds `{ productId, quantity }` lines today, and
`CheckoutApi.quote()` currently discards the quantities on the way out:

```ts
quote(productIds: readonly string[], currency: string): Observable<QuoteResponse>
```

The client-side work is to pass the lines it already has, change one label from
`Quoted unit prices:` back to `Total:`, and delete the comment quoted at the top
of this document. The per-line unit price display stays — it is genuinely useful,
and `Amount` still supplies it.
