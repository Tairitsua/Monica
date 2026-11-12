using System.Collections.Immutable;
using Monica.Core.TypeDiscovery.Abstractions;

namespace Monica.Framework.Seeder.Models.Internal;

internal sealed record SeederDescriptor(
    Type SeederType,
    string SeederName,
    string SeederTypeName,
    SeederExecutionMode ExecutionMode,
    SeederPolicySource ExecutionModeSource,
    SeederCriticality Criticality,
    SeederPolicySource CriticalitySource,
    SeederFailureBehavior FailureBehavior,
    SeederPolicySource FailureBehaviorSource,
    int MaxAttempts,
    SeederPolicySource MaxAttemptsSource,
    ImmutableArray<Type> Dependencies,
    ActivationMode Activation = ActivationMode.Automatic);
