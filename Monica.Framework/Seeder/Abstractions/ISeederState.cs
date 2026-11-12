using Monica.Framework.Seeder.Models;

namespace Monica.Framework.Seeder.Abstractions;

/// <summary>
/// Exposes the current host-owned seeder state without permitting consumers to mutate scheduler state.
/// </summary>
public interface ISeederState
{
    /// <summary>
    /// Captures one internally consistent snapshot of every discovered seeder.
    /// </summary>
    SeederStateSnapshot GetSnapshot();

    /// <summary>
    /// Reports whether a discovered prerequisite has actually succeeded in this process. Held, disabled, unknown,
    /// failed, or currently executing seeders return false. Dependent APIs can use this guard to report unavailable
    /// prerequisites without starting work implicitly.
    /// </summary>
    bool IsSucceeded(Type seederType);
}
