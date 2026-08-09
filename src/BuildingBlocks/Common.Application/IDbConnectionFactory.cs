using System.Data;

namespace Common.Application;

/// <summary>
/// Creates a connection for Dapper reads (§6.5). Queries bypass the domain
/// model and the unit of work entirely — this port is what a query handler
/// injects instead of a repository, and it must never appear inside §6.3's
/// transaction (§9's outbox note: it belongs to queries and to projections).
/// </summary>
/// <remarks>
/// The caller owns the connection it is handed — <c>using</c> at the call
/// site, as every §6.5 handler shows. Dapper opens a closed connection itself,
/// so the factory only constructs.
/// </remarks>
public interface IDbConnectionFactory
{
    IDbConnection Create();
}
