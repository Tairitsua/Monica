namespace Monica.Core.Modularity.Abstractions;

/// <summary>Exposes the shared host-owned input and extension state to framework composition extensions.</summary>
public interface IMonicaCompositionContext
{
    /// <summary>Gets the same composition state used by this host's Monica application.</summary>
    MonicaCompositionState Composition { get; }
}
