namespace Common.Contracts;

/// <summary>
/// A contract that forgot its version namespace — the shape §9.2 forbids, and
/// the one shape <see cref="Platform.IntegrationTests.ContractTests"/>'s
/// discovery could not see until a review asked about the trailing dot in
/// <c>StartsWith("Common.Contracts.")</c>.
/// </summary>
/// <remarks>
/// <b>In this test assembly, deliberately, and in the contracts' namespace all
/// the same.</b> The defect is a type declared straight into
/// <c>Common.Contracts</c>, so proving the filter catches it needs such a type
/// to exist — and putting one in the real contract assembly would be
/// committing the defect in order to demonstrate that it can be found. A
/// namespace is not an assembly, so this sits where the check has to look
/// without ever shipping in the assembly the check runs over.
/// <para>
/// It is therefore <em>never</em> in <c>Contracts</c>: that array is built from
/// <c>typeof(OrderPlaced).Assembly</c>, which is not this one. Only the
/// predicate is asked about this type, which is exactly the seam under test.
/// </para>
/// </remarks>
public sealed record UnversionedProbe(Guid Id);
