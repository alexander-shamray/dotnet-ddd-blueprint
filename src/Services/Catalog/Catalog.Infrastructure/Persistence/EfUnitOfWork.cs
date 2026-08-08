using Common.Application;
using Common.Domain;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// §6.3's transaction boundary over the Catalog context. Internal — nothing
/// outside this assembly resolves it by type, only through
/// <see cref="IUnitOfWork"/>.
/// </summary>
internal sealed class EfUnitOfWork(CatalogDbContext db) : IUnitOfWork
{
    public bool HasActiveTransaction => db.Database.CurrentTransaction is not null;

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken ct)
    {
        IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction tx =
                await db.Database.BeginTransactionAsync(ct);
            TResult result = await operation(ct);

            // The commit decision belongs with the commit. §6.3's behaviour
            // declines to SaveChanges on a failed Result, which is enough for
            // tracked changes — but ExecuteRawAsync writes on this
            // transaction's connection immediately, and only a rollback undoes
            // that. Returning without committing disposes the transaction,
            // which rolls it back.
            if (result is Result { IsFailure: true })
                return result;

            await tx.CommitAsync(ct);
            return result;
        });
    }

    public Task<int> SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    // Owned children are not roots and do not count — that is the difference
    // between an aggregate and a table (§6.3, principle 3).
    public int ModifiedAggregateCount => db.ChangeTracker
        .Entries()
        .Count(e => e.Entity is IAggregateRoot &&
                    e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

    // The transaction's own connection and transaction, explicitly passed —
    // this is what makes a raw write part of the command rather than beside it.
    public Task ExecuteRawAsync(string sql, object parameters, CancellationToken ct) =>
        db.Database.GetDbConnection().ExecuteAsync(
            new CommandDefinition(
                sql,
                parameters,
                transaction: db.Database.CurrentTransaction?.GetDbTransaction(),
                cancellationToken: ct));
}
