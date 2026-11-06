using Monica.AI.AgentCapabilities.Abstractions;
using Monica.AI.AgentCapabilities.Models;
using Monica.AI.Mcp.Abstractions;
using Monica.AI.Mcp.Models;
using Monica.AI.Mcp.Services;
using Monica.Core.Extensions;
using Monica.Core.Results;

namespace Monica.AI.Mcp.Facades;

/// <summary>
/// UI-facing entry point for MCP connectivity, user-managed external client profiles, and agent skill pack export.
/// </summary>
public sealed class McpFacade
{
    private readonly IExternalMcpClientProfileStore profileStore;
    private readonly MonicaMcpCatalog catalog;
    private readonly IAgentCapabilityService capabilityService;
    private readonly McpSkillPackExporter skillPackExporter;

    internal McpFacade(
        IExternalMcpClientProfileStore profileStore,
        MonicaMcpCatalog catalog,
        IAgentCapabilityService capabilityService,
        McpSkillPackExporter skillPackExporter)
    {
        this.profileStore = profileStore;
        this.catalog = catalog;
        this.capabilityService = capabilityService;
        this.skillPackExporter = skillPackExporter;
    }

    /// <summary>
    /// Tests connectivity or local readiness for an MCP catalog entry.
    /// </summary>
    public async Task<Result<McpConnectivityTestResult>> TestConnectivityAsync(
        string name,
        CancellationToken ct = default)
    {
        return await ExecuteAsync(async () =>
            await catalog.TestConnectivityAsync(name, ct)
            ?? throw new KeyNotFoundException($"MCP entry '{name}' was not found."));
    }

    /// <summary>
    /// Tests a draft external MCP client profile without saving it.
    /// </summary>
    public Task<Result<McpConnectivityTestResult>> TestExternalProfileAsync(
        ExternalMcpClientProfile profile,
        CancellationToken ct = default)
    {
        return ExecuteAsync(() =>
        {
            ArgumentNullException.ThrowIfNull(profile);
            return catalog.TestExternalProfileAsync(
                profile.Normalize(ExternalMcpClientProfileOrigin.User),
                ct);
        });
    }

    /// <summary>
    /// Saves a user-managed external MCP client profile and invalidates cached clients and tools.
    /// </summary>
    public Task<Result<AgentCapabilityManagementInfo>> SaveExternalProfileAsync(
        ExternalMcpClientProfile profile,
        CancellationToken ct = default)
    {
        return ExecuteAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(profile);
            var normalized = profile.Normalize(ExternalMcpClientProfileOrigin.User);
            normalized.Validate();
            var management = await capabilityService.GetManagementInfoAsync(ct);
            var conflictingEntry = management.McpEntries.FirstOrDefault(entry =>
                string.Equals(entry.Name, normalized.Name, StringComparison.OrdinalIgnoreCase)
                && !entry.IsUserManaged);
            if (conflictingEntry is not null)
            {
                throw new InvalidOperationException(
                    $"MCP entry '{normalized.Name}' is code-defined or local and cannot be overwritten from the UI.");
            }

            await profileStore.SaveAsync(normalized, ct);
            return await capabilityService.RefreshAsync(ct);
        });
    }

    /// <summary>
    /// Deletes a user-managed external MCP client profile and invalidates cached clients and tools.
    /// </summary>
    public Task<Result<AgentCapabilityManagementInfo>> DeleteExternalProfileAsync(
        string name,
        CancellationToken ct = default)
    {
        return ExecuteAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (!await profileStore.DeleteAsync(name, ct))
            {
                throw new KeyNotFoundException($"External MCP profile '{name}' was not found.");
            }

            await catalog.InvalidateExternalEntriesAsync(ct);
            return await capabilityService.RemoveEntryOverrideAsync(AgentCapabilityKind.Mcp, name, ct);
        });
    }

    /// <summary>
    /// Renders the agent skill pack for one local HTTP MCP server and writes it below a target skills directory.
    /// </summary>
    /// <param name="serverName">Logical name of a local HTTP MCP server in the catalog.</param>
    /// <param name="endpointUrl">Absolute HTTP endpoint the pack's helper scripts call by default.</param>
    /// <param name="targetRoot">Directory that receives <c>targetRoot/&lt;skill name&gt;</c>; must not already contain it.</param>
    public Result<McpSkillPackExportResult> ExportSkillPack(string serverName, string endpointUrl, string targetRoot)
    {
        return Execute(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
            ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);
            ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
            if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException($"Endpoint URL '{endpointUrl}' must be an absolute HTTP or HTTPS URL.");
            }

            var pack = skillPackExporter.Render(serverName, uri);
            return skillPackExporter.WriteToDirectory(pack, targetRoot);
        });
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

    private static Result<T> Execute<T>(Func<T> operation)
    {
        try
        {
            return Result.Success<T>(operation());
        }
        catch (Exception ex)
        {
            return Result.Failure<T>(ex.GetMessageRecursively());
        }
    }
}
