using Microsoft.Extensions.Logging;
using Monica.Core.Results;
using Monica.Locker.Leases;
using Monica.Locker.Leases.Diagnostics;
using Monica.Locker.Services;

namespace Monica.Locker.Facades;

/// <summary>Authorized boundary for explicitly configured lease evidence and administrative revocation.</summary>
/// <remarks>Registered scoped by AddLeaseDiagnostics. No HTTP endpoints or automatic provider discovery are enabled.</remarks>
public sealed class LeaseDiagnosticsFacade
{
    private readonly LeaseInventoryReader _reader;
    private readonly ILeaseDiagnosticsAccess _access;
    private readonly ILogger<LeaseDiagnosticsFacade> _logger;

    internal LeaseDiagnosticsFacade(LeaseInventoryReader reader, ILeaseDiagnosticsAccess access,
        ILogger<LeaseDiagnosticsFacade> logger)
    {
        _reader = reader;
        _access = access;
        _logger = logger;
    }

    /// <summary>Reads cached inventories; each authority preserves its own clock, failure and last successful evidence.</summary>
    public Task<Result<IReadOnlyList<LeaseAuthorityEvidence>>> GetInventoryAsync(CancellationToken cancellationToken = default) =>
        RunAsync(token => _reader.ReadAsync(token), cancellationToken);

    /// <summary>Reads one exact permitted resource without enumerating other roles.</summary>
    public Task<Result<LeaseResourceEvidence>> GetResourceAsync(string authorityId, LeaseResource resource,
        CancellationToken cancellationToken = default) => RunAsync(token => _reader.ReadResourceAsync(authorityId, resource, token), cancellationToken);

    /// <summary>Revokes whichever grant exists at execution, not necessarily the displayed holder.</summary>
    /// <remarks>Requires current access, an opted-in binding and ILeaseAdministration. Does not stop existing work.</remarks>
    public Task<Result<bool>> RevokeAsync(string authorityId, LeaseResource resource,
        CancellationToken cancellationToken = default) => RunAsync(token => _reader.RevokeAsync(authorityId, resource, token), cancellationToken);

    private async Task<Result<T>> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        try
        {
            if (!await _access.IsAuthorizedAsync(token)) return Result.Failure<T>("Lease diagnostics access denied.", ResultStatus.Forbidden);
            token.ThrowIfCancellationRequested();
            return Result.Success(await operation(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return Result.Failure<T>("Lease administration is not permitted.", ResultStatus.Forbidden); }
        catch (ArgumentException) { return Result.Failure<T>("Unknown authority or resource outside its configured scope."); }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Lease observation or administration could not be confirmed");
            return Result.Failure<T>("Lease observation or administration could not be confirmed.", ResultStatus.InternalError);
        }
    }
}
