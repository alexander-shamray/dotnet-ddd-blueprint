namespace Catalog.Domain;

/// <summary>
/// The <c>typeof</c> anchor for this assembly. The architecture gates (§4.2)
/// need a type to reach the assembly through, and the blueprint's idiom is a
/// real type doing double duty — <c>OrderRepository</c> is also Ordering's
/// Infrastructure marker (Appendix D.5). An empty skeleton has no real type to
/// borrow, so the marker is explicit; PR-10's first aggregate can take over as
/// the anchor and delete it.
/// </summary>
public static class AssemblyMarker;
