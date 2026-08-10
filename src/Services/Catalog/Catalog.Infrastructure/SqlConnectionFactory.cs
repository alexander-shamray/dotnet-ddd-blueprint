using System.Data;
using Common.Application;
using Microsoft.Data.SqlClient;

namespace Catalog.Infrastructure;

/// <summary>
/// The implementation behind §6.5's port, over the RUNTIME identity of §7.1 —
/// a query has no business on the migrator's connection. Service-local rather
/// than a building block, still: <c>Common.Infrastructure</c> exists since
/// PR-12 but takes no project references, and a factory that names
/// <c>IDbConnectionFactory</c> would draw the <c>Common.Application</c> edge
/// the Redis helpers deliberately do not have. It moves, if ever, with the
/// PR whose types need it there.
/// </summary>
/// <remarks>
/// <c>Create</c> only constructs — Dapper opens a closed connection itself,
/// and the caller disposes (§6.5's <c>using</c> at every call site).
/// </remarks>
internal sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public IDbConnection Create() => new SqlConnection(connectionString);
}
