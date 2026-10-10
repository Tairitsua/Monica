using Monica.Configuration.Annotations;

namespace Monica.Configuration.Models;

/// <summary>
/// Describes the structural role of a configuration schema node.
/// </summary>
public enum ConfigurationNodeKind
{
    Object,
    Dictionary,
    List,
    Scalar
}

/// <summary>
/// Describes the scalar value category exposed by a configuration node.
/// </summary>
public enum ConfigurationValueKind
{
    String,
    Boolean,
    Integer,
    Decimal,
    Floating,
    Enum,
    DateTime,
    TimeSpan,
    Uri,
    Json
}

/// <summary>
/// Describes specialized text semantics for scalar string configuration nodes.
/// </summary>
public enum ConfigurationTextSemantic
{
    /// <summary>
    /// Treats the string as ordinary operator-facing text.
    /// </summary>
    PlainText,

    /// <summary>
    /// Treats the string as a regular expression pattern whose non-ASCII characters should be displayed and
    /// persisted with regex-compatible Unicode escapes.
    /// </summary>
    RegexPattern
}

/// <summary>
/// Describes when a configuration value can be applied by a running process.
/// </summary>
public enum ConfigurationReloadBehavior
{
    /// <summary>
    /// Inherits the reload behavior from the owning definition or parent node.
    /// </summary>
    Inherit,

    /// <summary>
    /// Monica cannot prove whether the value is observed dynamically by the running process.
    /// </summary>
    Unknown,

    /// <summary>
    /// The value can be applied by reloading configuration in the running process.
    /// </summary>
    OnlineReloadable,

    /// <summary>
    /// The value is read dynamically but requires a process restart before it is observed safely.
    /// </summary>
    RequiresRestart,

    /// <summary>
    /// The value is intentionally fixed after startup and should not be treated as hot-reloadable.
    /// </summary>
    StaticAfterStartup
}

/// <summary>
/// Describes how the current service obtained its reload-behavior observation for a configuration definition.
/// </summary>
public enum ConfigurationReloadBehaviorObservationKind
{
    /// <summary>
    /// The definition declares a concrete reload behavior in developer-authored metadata.
    /// </summary>
    Declared,

    /// <summary>
    /// Monica inferred a concrete reload behavior from how the current service consumes the options type.
    /// </summary>
    Inferred,

    /// <summary>
    /// The current service may consume the definition, but Monica cannot prove its reload behavior.
    /// </summary>
    Unresolved,

    /// <summary>
    /// Monica inspected the current service and found no consumer of the definition.
    /// This observation is neutral when reload behavior is aggregated across services.
    /// </summary>
    NotConsumed
}

/// <summary>
/// Defines how Monica derives a Microsoft configuration section path when
/// <see cref="ConfigurationAttribute.SectionPath"/> is not set explicitly.
/// </summary>
public enum ConfigurationSectionPathConvention
{
    /// <summary>
    /// Uses the short CLR type name, for example <c>K8SOptions</c>.
    /// </summary>
    ShortTypeName,

    /// <summary>
    /// Uses the CLR full type name with dots converted to configuration separators.
    /// </summary>
    ClrFullName
}

/// <summary>
/// Defines how Monica reacts when two managed configuration definitions resolve to the same section path.
/// </summary>
public enum ConfigurationDuplicateSectionPathBehavior
{
    /// <summary>
    /// Throws during module startup so ambiguous configuration binding is fixed before the host runs.
    /// </summary>
    FailFast,

    /// <summary>
    /// Logs a warning and allows both definitions to be registered.
    /// </summary>
    Warning
}

/// <summary>
/// Defines whether runtime validation findings are exposed as diagnostics or enforced as application failures.
/// </summary>
public enum ConfigurationRuntimeValidationBehavior
{
    /// <summary>
    /// Reports invalid effective values through logging and the runtime validation API while allowing application
    /// startup and managed options resolution to continue.
    /// </summary>
    DiagnosticOnly,

    /// <summary>
    /// Rejects application startup when invalid effective values are found and registers Microsoft options validators
    /// that reject later resolution of invalid managed options.
    /// </summary>
    FailFast
}

/// <summary>
/// Describes where a configuration definition was resolved from for the current process.
/// </summary>
public enum ConfigurationDefinitionOrigin
{
    /// <summary>
    /// The definition was scanned from a local CLR options type in this process.
    /// </summary>
    LocalScan,

    /// <summary>
    /// The definition was loaded from published metadata in the configured metadata store.
    /// </summary>
    PublishedMetadata
}

/// <summary>
/// Identifies the backing technology of a configuration store.
/// </summary>
public enum ConfigurationStoreKind
{
    File,
    Database
}

/// <summary>
/// Describes the lifecycle state of a stored configuration value.
/// </summary>
public enum ConfigurationValueState
{
    Active,
    Removed
}

/// <summary>
/// Describes the coarse mutation requested by a caller.
/// </summary>
public enum ConfigurationMutationKind
{
    Set,
    Remove
}

/// <summary>
/// Describes whether a mutation targets one scalar leaf or a whole container subtree.
/// </summary>
public enum ConfigurationMutationGranularity
{
    Scalar,
    Container
}

/// <summary>
/// Describes the lifecycle state of a persisted mutation group.
/// </summary>
public enum ConfigurationMutationGroupStatus
{
    /// <summary>
    /// All mutations in the group were applied successfully.
    /// </summary>
    Applied,

    /// <summary>
    /// Only part of the group was applied successfully.
    /// </summary>
    PartiallyApplied,

    /// <summary>
    /// A later rollback group was applied for this group.
    /// </summary>
    RolledBack
}

/// <summary>
/// Describes the final persistence state of a submitted mutation group.
/// </summary>
public enum ConfigurationMutationGroupApplyStatus
{
    /// <summary>
    /// Every submitted command was applied.
    /// </summary>
    Applied,

    /// <summary>
    /// At least one command was applied and at least one command failed or was skipped.
    /// </summary>
    PartiallyApplied,

    /// <summary>
    /// Validation rejected the group before values, history, or a mutation-group row were persisted.
    /// </summary>
    Rejected
}

/// <summary>
/// Describes the result of one command inside a mutation group.
/// </summary>
public enum ConfigurationMutationOutcomeStatus
{
    /// <summary>
    /// The command was durably applied.
    /// </summary>
    Applied,

    /// <summary>
    /// The command failed at its persistence boundary.
    /// </summary>
    Failed,

    /// <summary>
    /// The command was not attempted because an earlier persistence boundary failed.
    /// </summary>
    Skipped
}

/// <summary>
/// Describes a failure that happened after durable configuration persistence.
/// </summary>
public enum ConfigurationPostCommitIssueKind
{
    /// <summary>
    /// The current process could not reload its configuration projection.
    /// </summary>
    LocalReload,

    /// <summary>
    /// A distributed invalidation notifier failed.
    /// </summary>
    DistributedNotification,

    /// <summary>
    /// Mutation-group audit finalization failed after values were applied.
    /// </summary>
    AuditFinalization,

    /// <summary>
    /// Unified-version capture failed after a non-transactional persistence boundary.
    /// </summary>
    UnifiedVersionCapture,

    /// <summary>
    /// Persisted values reloaded successfully, but the resulting effective configuration did not match the
    /// operation's reviewed postcondition.
    /// </summary>
    EffectiveValueVerification
}

/// <summary>
/// Describes the work requested by a distributed reload signal.
/// </summary>
public enum ConfigurationReloadSignalKind
{
    /// <summary>
    /// Reloads the supplied changed definitions.
    /// </summary>
    DefinitionsChanged,

    /// <summary>
    /// Reloads every locally known Monica definition.
    /// </summary>
    ReloadAll
}
