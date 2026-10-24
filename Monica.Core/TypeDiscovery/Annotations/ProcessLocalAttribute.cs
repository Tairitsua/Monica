namespace Monica.Core.TypeDiscovery.Annotations;

/// <summary>Marks finite initialization that changes only this process's memory and supports included passive behavior.</summary>
/// <remarks>
/// Applies only to seeder or hosted initialization, independently of role placement and AutonomousWork. Read-only
/// input is allowed; shared writes, distributed claims, publication and continuous work are not. The initializer's
/// owning capability must still enforce its dependency graph and readiness requirements.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ProcessLocalAttribute : Attribute;
