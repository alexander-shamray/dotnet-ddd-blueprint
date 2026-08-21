using System.Globalization;
using Catalog.Pricing.V1;
using Grpc.Core;

namespace Web.Bff.TestSupport;

/// <summary>
/// What <c>Web.Bff</c> needs Catalog's pricing RPC (§9.7) to do — written by the
/// consumer, honoured by the consumer's stub, and verified against the real
/// provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is Appendix C's PR-26, and it is not Pact.</b> Pact expresses a
/// contract in one artefact the consumer authors and the provider verifies, and
/// that property is the whole of its value — the broker, the wire format and the
/// Rust core are how Pact ships it across repository boundaries this monorepo
/// does not have. The one consumer relationship here that is contentious is
/// gRPC, and Pact's .NET binding cannot express gRPC at all — ADR-023 records
/// it. So the property is taken and the machinery is not: one file, linked into
/// both suites, exactly as <c>pricing.proto</c> is.
/// </para>
/// <para>
/// <b>The syntactic contract and the semantic one are now shared the same
/// way.</b> <c>pricing.proto</c> is Catalog's, because Catalog serves the RPC;
/// this file is Web.Bff's, because only a consumer can say what it needs. Both
/// are linked rather than referenced, so no assembly crosses a service boundary
/// and §4.3 stays true with <c>Common.Contracts</c> as its one exception.
/// </para>
/// <para>
/// <b>Only what the consumer needs is in here.</b> Catalog refuses a malformed
/// product id and an anonymous caller, and neither is an interaction below:
/// <c>CheckoutEndpoints</c> always sends canonical ids and always presents a
/// token, so an expectation about either would be a promise nobody is relying
/// on. Those stay <c>PricingServiceTests</c>' — provider-owned behaviour, tested
/// where it is decided. A consumer-driven contract that lists everything the
/// provider does is a second provider suite wearing the consumer's name.
/// </para>
/// </remarks>
public static class PricingContract
{
    /// <summary>The consumer that authored these expectations.</summary>
    public const string Consumer = "Web.Bff";

    /// <summary>The provider they are verified against.</summary>
    public const string Provider = "Catalog.Api";

    /// <summary>
    /// The largest basket the consumer expects to be served.
    /// </summary>
    /// <remarks>
    /// <b>The consumer states the ceiling here and nowhere else, and that is not
    /// a contradiction of <c>CheckoutEndpoints</c>' refusal to hold one.</b>
    /// Production code holding a copy would refuse requests Catalog would have
    /// served, which is the drift that comment rules out. A contract holding one
    /// is the consumer saying which number it is relying on — so lowering
    /// <c>GetPricesValidator.MaxProductIds</c> breaks a verification run rather
    /// than a checkout screen, which is the entire point of writing the
    /// expectation down.
    /// </remarks>
    public const int MaxProductIds = 100;

    private static readonly ContractProduct Chair = new("chair", "Chair", 49.99m, "GBP");
    private static readonly ContractProduct Desk = new("desk", "Desk", 120.50m, "GBP");
    private static readonly ContractProduct Lamp = new("lamp", "Lamp", 18.00m, "EUR");

    /// <summary>
    /// Every expectation the consumer has of the provider, each verified on both
    /// sides of the hop.
    /// </summary>
    public static IReadOnlyList<PricingInteraction> Interactions { get; } =
    [
        new PricingInteraction(
            "every product the basket names is priced",
            [Chair, Desk],
            ["chair", "desk"],
            0,
            "GBP",
            PricingOutcome.Prices("chair", "desk")),

        // §6.4's read path is the one that has to be right about money; this is
        // the screen, and a product priced in another currency has to reach the
        // caller as unpriced rather than as free. `Unpriced` in QuoteResponse is
        // computed from what came back, so an entry of zero would be totalled.
        new PricingInteraction(
            "a product priced in another currency is absent rather than zero",
            [Chair, Lamp],
            ["chair", "lamp"],
            0,
            "GBP",
            PricingOutcome.Prices("chair")),

        // A basket assembled from a stale page names products Catalog has since
        // withdrawn. The consumer needs the rest of the basket priced anyway —
        // a whole-request failure would blank a checkout screen over one line.
        new PricingInteraction(
            "a product Catalog has never heard of is absent rather than an error",
            [Chair],
            ["chair"],
            1,
            "GBP",
            PricingOutcome.Prices("chair")),

        // The currency reaches this hop from the caller's own query string, so
        // the consumer cannot promise a case. What it needs is that the answer
        // is the same one either way — and this is the interaction that puts a
        // reply whose currency is spelled differently from the request through
        // the consumer's OrdinalIgnoreCase comparison, which nothing did before
        // this contract existed.
        new PricingInteraction(
            "a currency spelled in another case prices the same products",
            [Chair],
            ["chair"],
            0,
            "gbp",
            PricingOutcome.Prices("chair")),

        // The two interactions below bracket the ceiling, and neither is
        // sufficient alone. A provider that quietly lowered its limit to fifty
        // would still refuse a hundred and one, so the refusal cannot say the
        // consumer's basket is safe; a provider that raised it would still serve
        // a hundred, so the answer cannot say a refusal arrives as
        // InvalidArgument. What the consumer needs is both edges, which is why
        // it states a number at all.
        //
        // A change to GetPricesValidator.MaxProductIds in EITHER direction fails
        // verification, and that is the agreement being renegotiated rather than
        // drifting. Pact pins an interaction the same way, and for the same
        // reason: a provider free to change behaviour a consumer wrote down has
        // a contract nobody is holding.
        new PricingInteraction(
            "a basket at the ceiling is served rather than refused",
            [],
            [],
            MaxProductIds,
            "GBP",
            PricingOutcome.Prices()),

        // CheckoutEndpoints deliberately holds no ceiling of its own and relies
        // on this refusal to become the caller's 400 (UpstreamExceptionHandler).
        // Served in part instead, a basket past the ceiling would quote a total
        // that silently omitted lines.
        new PricingInteraction(
            "a basket past the ceiling is refused rather than served in part",
            [],
            [],
            MaxProductIds + 1,
            "GBP",
            PricingOutcome.Refused(StatusCode.InvalidArgument))
    ];

    /// <summary>The interactions the contract says are answered.</summary>
    /// <remarks>
    /// Descriptions rather than interactions, because both suites feed them to
    /// an xUnit <c>[MemberData]</c> and the datum has to be serialisable. Here
    /// rather than in each suite so that the two sides cannot come to verify
    /// different subsets of one contract.
    /// </remarks>
    public static IEnumerable<string> Answered =>
        Interactions
            .Where(interaction => interaction.Then is PricingOutcome.Priced)
            .Select(interaction => interaction.Description);

    /// <summary>The interactions the contract says are refused.</summary>
    public static IEnumerable<string> Refusals =>
        Interactions
            .Where(interaction => interaction.Then is PricingOutcome.Refusal)
            .Select(interaction => interaction.Description);

    /// <summary>The interaction with this description.</summary>
    /// <remarks>
    /// Both suites drive their theories from <see cref="Interactions"/> by
    /// description rather than by index, because xUnit renders the datum in the
    /// test name and §12.8 asks for a name readable without opening the file. An
    /// index would render <c>[3]</c>.
    /// </remarks>
    public static PricingInteraction Named(string description) =>
        Interactions.SingleOrDefault(i => i.Description == description)
        ?? throw new PricingContractException(
            $"No interaction is described as '{description}'.");

    /// <summary>
    /// An id no product will ever have, so a request can name one deliberately.
    /// </summary>
    /// <remarks>
    /// Derived from the index rather than drawn from a list, because the ceiling
    /// interaction needs a hundred and one of them. Catalog mints product ids
    /// with <c>Guid.CreateVersion7()</c>, whose version nibble is 7 and whose
    /// leading bytes are a timestamp, so nothing it publishes can collide with
    /// these — and they are canonical D-form, which is what the request itself
    /// has to be.
    /// </remarks>
    public static Guid UnknownId(int index) =>
        new($"00000000-0000-0000-0000-{index:D12}");

    /// <summary>
    /// The ids this interaction's request names, in order: the published
    /// products first, then the ids that name nothing.
    /// </summary>
    public static IReadOnlyList<Guid> RequestedIds(
        PricingInteraction interaction,
        IReadOnlyDictionary<string, Guid> published)
    {
        List<Guid> ids = new(interaction.Ask.Count + interaction.PlusUnknownIds);

        foreach (string alias in interaction.Ask)
        {
            if (!published.TryGetValue(alias, out Guid id))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}' asks about '{alias}', which the caller did not publish.");
            }

            ids.Add(id);
        }

        for (int index = 1; index <= interaction.PlusUnknownIds; index++)
            ids.Add(UnknownId(index));

        return ids;
    }

    /// <summary>
    /// This interaction as a <c>GetPricesRequest</c> — the <b>provider</b>
    /// suite's message, and only its.
    /// </summary>
    /// <remarks>
    /// <b>What both sides share is <see cref="RequestedIds"/>, not this.</b> The
    /// question — which products, in which currency — is built once and asked of
    /// the stub and the real service alike. The gRPC message is not: the
    /// consumer suite hands the same ids to the screen as a query string and
    /// lets <c>CheckoutEndpoints</c> construct its own, because its whole job is
    /// to establish that the request the ENDPOINT builds is the one this
    /// contract describes. Building it here for that side too would verify the
    /// contract against itself.
    /// </remarks>
    public static GetPricesRequest Request(
        PricingInteraction interaction,
        IReadOnlyDictionary<string, Guid> published)
    {
        GetPricesRequest request = new() { Currency = interaction.Currency };
        request.ProductId.AddRange(RequestedIds(interaction, published).Select(id => id.ToString()));

        return request;
    }

    /// <summary>
    /// Whether <paramref name="reply"/> is one the consumer can work with, for
    /// the interaction that produced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the consumer's tolerance and not the provider's behaviour, and
    /// the difference is what keeps the contract from over-specifying.</b> The
    /// amount is parsed and compared numerically rather than matched as text,
    /// because <c>pricing.proto</c> says a consumer must parse it — Catalog's
    /// column is <c>decimal(19,4)</c> and answers <c>"49.9900"</c>, and a
    /// migration that changed the scale would change nothing a customer can see.
    /// The currency is compared case-insensitively for the same reason: which
    /// spelling Catalog canonicalises to is its own business, and the consumer
    /// needs only that the two agree.
    /// </para>
    /// <para>
    /// <b>Four of these checks are also defences in <c>CheckoutEndpoints</c>,
    /// and the rest are expectations of Catalog that no screen enforces.</b> The
    /// endpoint refuses a malformed amount, a negative one, a currency that
    /// disagrees with the request, and an id it did not ask about or has already
    /// been answered — each a 500, because each would otherwise produce a wrong
    /// quote. Those four are the same tolerance stated as an expectation instead
    /// of as a defence, which is what lets the provider be held to it before a
    /// screen is.
    /// </para>
    /// <para>
    /// <b>The others have no counterpart, and that is the half a contract is
    /// for.</b> A product the contract says is absent and the reply prices, one
    /// it says is priced and the reply omits, a name that is not the published
    /// one, an amount that is not the published price — the endpoint answers 200
    /// to every one of them, quoting a line or reporting it in
    /// <c>QuoteResponse.Unpriced</c>. It has no published price to compare
    /// against and no reason to refuse a customer a quote over it. Only a test
    /// that knows what was published can see any of it, which is exactly why the
    /// contract carries them and the screen does not.
    /// </para>
    /// </remarks>
    /// <exception cref="PricingContractException">
    /// The reply is one the consumer could not use.
    /// </exception>
    public static void Verify(
        PricingInteraction interaction,
        IReadOnlyDictionary<string, Guid> published,
        GetPricesReply reply)
    {
        if (interaction.Then is not PricingOutcome.Priced priced)
        {
            throw new PricingContractException(
                $"'{interaction.Description}' expects a refusal, so it has no reply to verify.");
        }

        HashSet<Guid> outstanding = [.. RequestedIds(interaction, published)];
        Dictionary<Guid, ContractProduct> expected = [];

        foreach (string alias in priced.Aliases)
            expected.Add(published[alias], Product(interaction, alias));

        foreach (ProductPrice price in reply.Price)
        {
            // Canonical D-form, which is what the request carried and what
            // pricing.proto states — the rule PricingService already enforces on
            // the way in, on the stated grounds that accepting more than the
            // contract says is how two ends stop agreeing about what it is.
            //
            // STRICTER THAN THE ENDPOINT, deliberately. CheckoutEndpoints parses
            // the echo with Guid.Parse, which also takes the N, B and P forms, so
            // a braced id would price correctly there and fail here. That
            // asymmetry is the design rather than a gap: the endpoint refuses
            // what would make a quote WRONG and tolerates what would merely make
            // it unusual, because a customer losing a basket is a worse outcome
            // than a log line in an odd shape. The contract is where an odd shape
            // is caught, one release before it costs anything.
            //
            // Tightening the endpoint to match was the alternative and is
            // rejected: it converts a detectable contract violation into a 500
            // for a value that is not even wrong.
            if (!Guid.TryParseExact(price.ProductId, "D", out Guid productId))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': product_id '{price.ProductId}' is not a canonical GUID.");
            }

            // Removing rather than testing membership, for CheckoutEndpoints'
            // own reason: one operation answers both "was this asked about" and
            // "has it been answered already", and both faults end as one wrong
            // total.
            if (!outstanding.Remove(productId))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': {productId} was either not asked about or priced twice.");
            }

            if (!expected.TryGetValue(productId, out ContractProduct? product))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': {productId} was priced, and the contract says it is absent.");
            }

            // NOT NumberStyles.Number: AllowThousands makes "12,50" parse under
            // the invariant culture as twelve hundred and fifty, which is the
            // hundredfold error the invariant culture was chosen to rule out.
            // The same two styles CheckoutEndpoints uses, for the same reason.
            if (!decimal.TryParse(
                    price.Amount,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out decimal amount))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': '{price.Amount}' is not a decimal in the invariant form " +
                    "pricing.proto specifies.");
            }

            if (amount < 0)
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': {productId} was priced at '{price.Amount}', and " +
                    "pricing.proto states the amount is never negative.");
            }

            if (amount != product.Amount)
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': {productId} was priced at {amount} and was published " +
                    $"at {product.Amount}.");
            }

            if (!string.Equals(price.Currency, interaction.Currency, StringComparison.OrdinalIgnoreCase))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': {productId} was priced in '{price.Currency}' for a " +
                    $"'{interaction.Currency}' request.");
            }

            if (!string.Equals(price.Name, product.Name, StringComparison.Ordinal))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': {productId} came back named '{price.Name}' and was " +
                    $"published as '{product.Name}'.");
            }
        }

        // The direction a loop over the reply cannot catch: a product the
        // contract says is priced and that the reply simply left out. Unchecked,
        // an empty reply satisfies every assertion above.
        foreach ((Guid productId, ContractProduct product) in expected)
        {
            if (outstanding.Contains(productId))
            {
                throw new PricingContractException(
                    $"'{interaction.Description}': '{product.Alias}' ({productId}) was not priced, and " +
                    "the contract says it is.");
            }
        }
    }

    /// <summary>The product this interaction publishes under <paramref name="alias"/>.</summary>
    public static ContractProduct Product(PricingInteraction interaction, string alias) =>
        interaction.Given.SingleOrDefault(p => p.Alias == alias)
        ?? throw new PricingContractException(
            $"'{interaction.Description}' names no product '{alias}'.");
}

/// <summary>One expectation the consumer has of the provider.</summary>
/// <param name="Description">
/// What the interaction is for, in the consumer's words. It becomes the test
/// name on both sides of the hop, so it reads as a sentence about the screen
/// rather than about the RPC.
/// </param>
/// <param name="Given">
/// The products that exist in Catalog when the request is made. Each side
/// realises this its own way — the stub takes them into a dictionary, the
/// provider suite publishes them through the real endpoint — and both bind the
/// alias to the id that came back.
/// </param>
/// <param name="Ask">The aliases the request names, in order.</param>
/// <param name="PlusUnknownIds">
/// How many ids naming nothing to append to the request. It is what makes the
/// withdrawn-product and past-the-ceiling interactions expressible without
/// either side inventing ids of its own.
/// </param>
/// <param name="Currency">The currency the request asks for, in the caller's spelling.</param>
/// <param name="Then">What the consumer needs to happen.</param>
public sealed record PricingInteraction(
    string Description,
    IReadOnlyList<ContractProduct> Given,
    IReadOnlyList<string> Ask,
    int PlusUnknownIds,
    string Currency,
    PricingOutcome Then);

/// <summary>A product the provider is holding when an interaction runs.</summary>
/// <param name="Alias">
/// How the contract names it. Ids are minted by whichever side realises the
/// state, so nothing in this file can hold one.
/// </param>
public sealed record ContractProduct(string Alias, string Name, decimal Amount, string Currency);

/// <summary>What the consumer needs the provider to do with a request.</summary>
/// <remarks>
/// A closed hierarchy — the constructor is private, so the only two cases are
/// the ones nested below. An outcome is either an answer or a refusal, and a
/// single record carrying both a list and a status would admit a third state
/// that means nothing.
/// </remarks>
public abstract record PricingOutcome
{
    private PricingOutcome()
    {
    }

    /// <summary>The provider answers, pricing exactly these aliases and no others.</summary>
    public static PricingOutcome Prices(params string[] aliases) => new Priced([.. aliases]);

    /// <summary>The provider refuses, with this status.</summary>
    public static PricingOutcome Refused(StatusCode status) => new Refusal(status);

    /// <summary>An answer.</summary>
    public sealed record Priced(IReadOnlyList<string> Aliases) : PricingOutcome;

    /// <summary>A refusal.</summary>
    public sealed record Refusal(StatusCode Status) : PricingOutcome;
}

/// <summary>
/// A reply, or a request, that the contract does not permit.
/// </summary>
/// <remarks>
/// Its own exception rather than an assertion-library failure, because this file
/// is compiled into a library that has no assertion library and into a suite
/// that has Shouldly — so a type of its own is the only thing that means the
/// same on both sides. The message is the contract's own words either way.
/// <para>
/// The three constructors are CA1032's, exactly as
/// <c>ContractMappingException</c>'s are.
/// </para>
/// </remarks>
public sealed class PricingContractException : Exception
{
    public PricingContractException()
    {
    }

    public PricingContractException(string message)
        : base(message)
    {
    }

    public PricingContractException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
