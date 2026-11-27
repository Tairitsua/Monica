namespace Monica.DependencyInjection.Models;

/// <summary>Describes the activation recorded for a finalized service descriptor.</summary>
public enum DependencyInjectionActivation
{
    /// <summary>The descriptor uses an implementation type or an existing instance.</summary>
    Direct,
    /// <summary>A conventional factory forwards to another exposure of the implementation.</summary>
    Forward,
    /// <summary>A conventional factory assigns the cached service provider after constructing the service.</summary>
    MonicaActivated,
    /// <summary>The descriptor uses a factory delegate with no recorded Monica activation contract.</summary>
    Factory
}
