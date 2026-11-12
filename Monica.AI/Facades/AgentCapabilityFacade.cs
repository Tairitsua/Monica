using Monica.AI.AgentCapabilities.Abstractions;
using Monica.AI.AgentCapabilities.Models;
using Monica.Core.Extensions;
using Monica.Core.Results;

namespace Monica.AI.Facades;

/// <summary>
/// UI-facing entry point for inspecting and toggling independently registered agent capabilities.
/// </summary>
public sealed class AgentCapabilityFacade(IAgentCapabilityService capabilityService)
{
    /// <summary>
    /// Gets the current management view for all registered capability sources.
    /// </summary>
    public Task<Result<AgentCapabilityManagementInfo>> GetManagementInfoAsync(CancellationToken ct = default)
    {
        return ExecuteAsync(() => capabilityService.GetManagementInfoAsync(ct));
    }

    /// <summary>
    /// Gets reference-completion candidates from enabled and disabled capabilities.
    /// </summary>
    public Task<Result<IReadOnlyList<AgentCapabilityReferenceCandidate>>> GetReferenceCandidatesAsync(
        string? query = null,
        CancellationToken ct = default)
    {
        return ExecuteAsync(() => capabilityService.GetReferenceCandidatesAsync(query, ct));
    }

    /// <summary>
    /// Sets the global runtime switch for a capability catalog.
    /// </summary>
    public Task<Result<AgentCapabilityManagementInfo>> SetCatalogEnabledAsync(
        AgentCapabilityKind kind,
        bool isEnabled,
        CancellationToken ct = default)
    {
        return ExecuteAsync(() => capabilityService.SetCatalogEnabledAsync(kind, isEnabled, ct));
    }

    /// <summary>
    /// Sets the runtime switch for one capability entry.
    /// </summary>
    public Task<Result<AgentCapabilityManagementInfo>> SetEntryEnabledAsync(
        AgentCapabilityKind kind,
        string name,
        bool isEnabled,
        CancellationToken ct = default)
    {
        return ExecuteAsync(() => capabilityService.SetEntryEnabledAsync(kind, name, isEnabled, ct));
    }

    /// <summary>
    /// Sets whether a skill should be exposed as an MCP server on the next host startup.
    /// </summary>
    public Task<Result<AgentCapabilityManagementInfo>> SetSkillMcpServerEnabledAsync(
        string skillName,
        bool isEnabled,
        CancellationToken ct = default)
    {
        return ExecuteAsync(() => capabilityService.SetSkillMcpServerEnabledAsync(skillName, isEnabled, ct));
    }

    private static async Task<Result<T>> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return Result.Success(await operation());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result.Failure<T>(ex.GetMessageRecursively());
        }
    }
}
