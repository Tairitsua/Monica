using Microsoft.AspNetCore.Components.Server.Circuits;

namespace Monica.Application.UI.Services;

internal sealed class ApplicationCircuitState
{
    public bool IsCircuit { get; set; }
}

internal sealed class ApplicationCircuitHandler(ApplicationCircuitState state) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        state.IsCircuit = true;
        return Task.CompletedTask;
    }
}
