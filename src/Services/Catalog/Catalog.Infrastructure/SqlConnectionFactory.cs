using System.Data;
using Common.Application;
using Microsoft.Data.SqlClient;

namespace Catalog.Infrastructure;

/// <summary>
/// The implementation behind §6.5's port, over the RUNTIME identity of §7.1 —
/// a query has no business on the migrator's connection. Service-local rather
/// than a building block: <c>Common.Infrastructure</c> does not exist, and
/// inventing it early is the move CLAUDE.md forbids; when its PR arrives,
/// moving these lines is that PR's business.
/// </summary>
/// <remarks>
/// <c>Create</c> only constructs — Dapper opens a closed connection itself,
/// and the caller disposes (§6.5's <c>using</c> at every call site).
/// </remarks>
internal sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public IDbConnection Create() => new SqlConnection(connectionString);
}
