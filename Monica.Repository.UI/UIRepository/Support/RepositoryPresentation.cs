using System.Globalization;
using Microsoft.Extensions.Localization;
using Monica.Repository.Persistence.Models;
using Monica.UI.Shared.Components.Visual;

namespace Monica.Repository.UI.UIRepository.Support;

internal static class RepositoryPresentation
{
    internal static readonly string[] Standings = ["disconnected", "error", "pending", "healthy"];
    internal static readonly string[] Providers = ["PostgreSql", "OpenGauss", "Sqlite", "SqlServer", "MySql", "Other"];
    internal static readonly TimeSpan LongActive = TimeSpan.FromSeconds(30);

    internal static string Standing(RepositoryDbContextSnapshot context) => !context.CanConnect ? "disconnected"
        : Incomplete(context) ? "error" : context.PendingMigrationCount > 0 ? "pending" : "healthy";

    internal static bool Incomplete(RepositoryDbContextSnapshot context) =>
        context.HasError || context.PendingMigrationCount is null || context.MigrationCount is null;

    internal static MoVisualRole StandingRole(string standing) => standing switch
    {
        "disconnected" or "error" => MoVisualRole.Error,
        "pending" => MoVisualRole.Warning,
        _ => MoVisualRole.Success
    };

    internal static string Provider(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Other";
        if (Matches(name, "gauss", "vastbase")) return "OpenGauss";
        if (Matches(name, "npgsql", "postgre")) return "PostgreSql";
        if (Matches(name, "sqlite")) return "Sqlite";
        if (Matches(name, "sqlserver")) return "SqlServer";
        if (Matches(name, "mysql")) return "MySql";
        return "Other";
    }

    internal static string Target(RepositoryDbContextSnapshot context) =>
        !string.IsNullOrWhiteSpace(context.Connection?.DataSource) && !string.IsNullOrWhiteSpace(context.Connection.Database)
            ? $"{context.Connection.DataSource.Trim()}::{context.Connection.Database.Trim()}"
            : "unresolved";

    internal static bool SharesTarget(RepositoryDbContextSnapshot first, RepositoryDbContextSnapshot second) =>
        Target(first) != "unresolved" && Target(first) == Target(second);

    internal static (int? Min, int? Max, string Mode, MoVisualRole Role) Pool(RepositoryDbContextSnapshot context)
    {
        var pool = context.Connection?.Pool;
        if (pool is null) return (null, null, "unknown", MoVisualRole.Neutral);
        if (pool.Pooling == false) return (null, null, "disabled", MoVisualRole.Neutral);
        var commonDefaults = Provider(context.ProviderName) is "PostgreSql" or "SqlServer" or "MySql";
        var min = pool.MinPoolSize ?? (commonDefaults ? RepositoryConnectionPoolInfo.DefaultMinPoolSize : (int?)null);
        var max = pool.MaxPoolSize ?? (commonDefaults ? RepositoryConnectionPoolInfo.DefaultMaxPoolSize : (int?)null);
        if (pool.MaxPoolSize is not null || pool.MinPoolSize is not null) return (min, max, "explicit", MoVisualRole.Primary);
        return commonDefaults ? (min, max, "default", MoVisualRole.Third) : (null, null, "unspecified", MoVisualRole.Neutral);
    }

    internal static string PoolLabel(RepositoryDbContextSnapshot context, IStringLocalizer localizer)
    {
        var (min, max, mode, _) = Pool(context);
        return mode switch
        {
            "explicit" => localizer["Persistence:PoolExplicit", min?.ToString() ?? "—", max?.ToString() ?? "—"],
            "default" => localizer["Persistence:PoolDefault", min!.Value, max!.Value],
            "disabled" => localizer["Persistence:PoolDisabled"],
            "unspecified" => localizer["Persistence:PoolUnspecified"],
            _ => localizer["Persistence:Unavailable"]
        };
    }

    internal static string SessionState(RepositoryActivityRow row) => row.State?.Trim().ToLowerInvariant() switch
    {
        "active" => "active",
        "idle" => "idle",
        { } state when state.StartsWith("idle in transaction", StringComparison.Ordinal) => "idleTx",
        _ => "other"
    };

    internal static bool IsLongActive(RepositoryActivityRow row) => SessionState(row) == "active" && row.Runtime >= LongActive;

    internal static MoVisualRole SessionRole(RepositoryActivityRow row) => SessionState(row) switch
    {
        "active" when row.Waiting == true => MoVisualRole.Error,
        "idleTx" => MoVisualRole.Warning,
        _ when row.Waiting == true || IsLongActive(row) => MoVisualRole.Warning,
        "active" => MoVisualRole.Info,
        _ => MoVisualRole.Neutral
    };

    internal static bool InDatabase(RepositoryActivityRow row, string? database) =>
        string.IsNullOrWhiteSpace(database) || string.Equals(row.DatabaseName?.Trim(), database.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static string? Client(RepositoryActivityRow row)
    {
        var address = string.IsNullOrWhiteSpace(row.ClientAddress) ? row.ClientHostname : row.ClientAddress;
        if (string.IsNullOrWhiteSpace(address)) return null;
        return row.ClientPort is { } port ? $"{address}:{port}" : address;
    }

    internal static double AgePosition(TimeSpan? age) =>
        Math.Clamp(100 * Math.Log10(Math.Clamp(age?.TotalMilliseconds ?? 100, 100, 3_600_000) / 100) / Math.Log10(36_000), 2, 98);

    internal static string CssPercent(double value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    internal static string Duration(TimeSpan? duration, IStringLocalizer localizer)
    {
        if (duration is not { } span) return "—";
        if (span.TotalMilliseconds < 1000) return localizer["Persistence:Milliseconds", Math.Max(0, Math.Round(span.TotalMilliseconds))];
        if (span.Days > 0) return localizer["Persistence:DaysHours", span.Days, span.Hours];
        if (span.Hours > 0) return localizer["Persistence:HoursMinutes", span.Hours, span.Minutes];
        if (span.Minutes > 0) return localizer["Persistence:MinutesSeconds", span.Minutes, span.Seconds];
        return localizer["Persistence:Seconds", span.TotalSeconds < 10 ? span.TotalSeconds.ToString("0.0") : span.Seconds.ToString()];
    }

    internal static MoVisualRole MigrationRole(RepositoryMigrationApplicationState state) => state switch
    {
        RepositoryMigrationApplicationState.Applied => MoVisualRole.Success,
        RepositoryMigrationApplicationState.Pending => MoVisualRole.Warning,
        _ => MoVisualRole.Info
    };

    internal static string Group(RepositoryOptionEntry option) => option.Path.Split('.', 2)[0];

    internal static string OptionLabel(RepositoryOptionEntry option, IStringLocalizer<Localization.RepositoryUIResource> localizer) => option.Path switch
    {
        "Registration.ProviderType" => localizer["Persistence:OptionNames:Registration:ProviderType"],
        "Registration.AssemblyName" => localizer["Persistence:OptionNames:Registration:AssemblyName"],
        "DbContext.QueryTrackingBehavior" => localizer["Persistence:OptionNames:DbContext:QueryTrackingBehavior"],
        "ModuleRepository.UseDbFunction" => localizer["Persistence:OptionNames:ModuleRepository:UseDbFunction"],
        "ModuleRepository.EnableSensitiveDataLogging" => localizer["Persistence:OptionNames:ModuleRepository:EnableSensitiveDataLogging"],
        "ModuleRepository.EnableEfCoreConnectionMetrics" => localizer["Persistence:OptionNames:ModuleRepository:EnableEfCoreConnectionMetrics"],
        "ModuleRepository.DisableEntitySelfConfiguration" => localizer["Persistence:OptionNames:ModuleRepository:DisableEntitySelfConfiguration"],
        "ModuleRepository.DisableEntitySeparateConfiguration" => localizer["Persistence:OptionNames:ModuleRepository:DisableEntitySeparateConfiguration"],
        _ => option.Label
    };

    internal static string OptionValue(string value, IStringLocalizer localizer)
    {
        if (bool.TryParse(value, out var flag))
            return localizer[flag ? "Persistence:Enabled" : "Persistence:Disabled"];
        return value == "Environment default" ? localizer["Persistence:EnvironmentDefault"].Value : value;
    }

    internal static bool Matches(string? haystack, params string[] needles) =>
        haystack is not null && needles.Any(needle => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase));

    internal static bool Search(string? query, params string?[] values) =>
        string.IsNullOrWhiteSpace(query) || string.Join(' ', values).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
}
