using Monica.Core.Modularity.Diagnostics.Facades;

namespace Monica.UI.UIModuleSystem.State;

/// <summary>
/// Creates one explicitly page-owned module diagnostics session without extending its lifetime through dependency injection.
/// </summary>
public sealed class ModuleSystemWorkbenchSessionFactory
{
    private readonly ModuleDiagnosticsCalls _calls;

    /// <summary>Creates a factory backed by the host-local diagnostics facade.</summary>
    public ModuleSystemWorkbenchSessionFactory(ModuleDiagnosticsFacade diagnostics)
        : this(new ModuleDiagnosticsCalls(
            diagnostics.GetSnapshot,
            diagnostics.GetAssemblyInventory,
            diagnostics.GetModuleOptions,
            diagnostics.CreateExport,
            diagnostics.GetResourceActivations))
    {
    }

    internal ModuleSystemWorkbenchSessionFactory(ModuleDiagnosticsCalls calls)
    {
        _calls = calls;
    }

    /// <summary>
    /// Creates a fresh workbench session owned by one rendered page instance.
    /// </summary>
    /// <param name="authorize">
    /// Re-evaluates the current circuit before every facade boundary so authentication changes take effect immediately.
    /// </param>
    public ModuleSystemWorkbenchSession Create(Func<Task<bool>> authorize) => new(_calls, authorize);
}
