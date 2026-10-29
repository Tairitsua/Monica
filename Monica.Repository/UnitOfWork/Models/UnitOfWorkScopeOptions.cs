using System.Data;

namespace Monica.Repository.UnitOfWork.Models;

/// <summary>Settings for one local transaction or a nested participant.</summary>
/// <param name="DbContextTypes">
/// Explicit participant types. Null selects the single UnitOfWork-registered context (or no context).
/// Multiple contexts require this explicit selection, the same DbConnection instance and relational provider.
/// Independent databases and shards cannot participate in one local transaction.
/// </param>
/// <param name="IsolationLevel">Null uses the database provider's default isolation.</param>
/// <param name="CommandTimeout">Optional command timeout applied to every participant.</param>
/// <param name="IsReadOnly">
/// Declares a Query boundary. A read-only root may own a consistency transaction but does not automatically
/// drain domain events or save changes. A nested Query still joins the caller's physical transaction;
/// handled business rejections do not mark it rollback-only. This is a contract, not SQL write detection.
/// </param>
public sealed record UnitOfWorkScopeOptions(
    IReadOnlyList<Type>? DbContextTypes = null,
    IsolationLevel? IsolationLevel = null,
    TimeSpan? CommandTimeout = null,
    bool IsReadOnly = false);
