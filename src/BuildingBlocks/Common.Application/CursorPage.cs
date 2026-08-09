namespace Common.Application;

/// <summary>
/// The pagination envelope every collection query returns (§6.5). Pagination
/// is mandatory on collection endpoints and cursor-based by default (ADR-016):
/// a null <see cref="NextCursor"/> is the last page, anything else is the
/// opaque token <see cref="Cursor"/> minted from the last row of
/// <see cref="Items"/> — the next page's seek is strictly past it.
/// </summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);
