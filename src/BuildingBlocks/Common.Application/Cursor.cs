using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Common.Application;

/// <summary>
/// The opaque cursor codec of §6.5: the sort key plus the tiebreaker ID,
/// Base64Url-encoded so the sort strategy stays an implementation detail
/// rather than a public contract. The tiebreaker is required — without it,
/// rows sharing a sort-key value straddle the page boundary unpredictably.
/// </summary>
public static class Cursor
{
    public static string Encode(DateTimeOffset sortKey, Guid id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{sortKey.UtcTicks}:{id:N}")));

    /// <summary>
    /// Null for null, and null for anything unreadable. The cursor is opaque
    /// by ADR-016, so a client that edits one has no contract to appeal to —
    /// answering the first page keeps the token an implementation detail,
    /// where a 400 would start documenting its insides and a 500 would make
    /// garbage input this service's fault.
    /// </summary>
    public static (DateTimeOffset SortKey, Guid Id)? Decode(string? cursor)
    {
        // IsValid first: TryDecodeFromChars' Try covers only the destination
        // size and still throws FormatException on an invalid character.
        if (cursor is null || !Base64Url.IsValid(cursor))
            return null;

        string payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
        string[] parts = payload.Split(':');
        if (parts.Length != 2 ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) ||
            ticks > DateTime.MaxValue.Ticks ||
            !Guid.TryParseExact(parts[1], "N", out Guid id))
        {
            return null;
        }

        return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
    }
}
