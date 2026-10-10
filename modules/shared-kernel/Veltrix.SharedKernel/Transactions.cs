using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Veltrix.SharedKernel;

/// <summary>A transaction that is either owned (started here) or joined (an ambient transaction already exists).
/// Only the owner commits, so composite workflows (e.g. POS exchange = return + sale) stay atomic.</summary>
public sealed class UnitOfWorkTransaction(IDbContextTransaction? owned) : IAsyncDisposable
{
    public bool Owned => owned is not null;
    public Task CommitAsync(CancellationToken ct = default) => owned?.CommitAsync(ct) ?? Task.CompletedTask;
    public Task RollbackAsync(CancellationToken ct = default) => owned?.RollbackAsync(ct) ?? Task.CompletedTask;
    public ValueTask DisposeAsync() => owned?.DisposeAsync() ?? ValueTask.CompletedTask;
}

public static class TransactionExtensions
{
    public static async Task<UnitOfWorkTransaction> BeginOrJoinAsync(this DatabaseFacade database, CancellationToken ct = default) =>
        database.CurrentTransaction is not null ? new UnitOfWorkTransaction(null) : new UnitOfWorkTransaction(await database.BeginTransactionAsync(ct));
}
