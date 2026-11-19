using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.JSInterop;

namespace FormaWire.Web.Hosting;

public sealed class CapacityCircuitHandler(CircuitCapacity capacity, IJSRuntime js) : CircuitHandler, IDisposable
{
    private string? _circuitId;

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (capacity.TryAcquire(circuit.Id))
        {
            _circuitId = circuit.Id;
            return;
        }

        // The HTTP check handles the usual case. This also handles simultaneous
        // arrivals competing for the last slot, before any editor is initialized.
        try { await js.InvokeVoidAsync("formawireHosting.serverBusy", TimeSpan.FromSeconds(2)); }
        catch (Exception ex) when (ex is JSException or OperationCanceledException) { }
        // An unhandled circuit lifecycle error makes Blazor dispose this circuit.
        throw new InvalidOperationException("Server occupato. Riprova tra poco.");
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        var id = Interlocked.Exchange(ref _circuitId, null);
        if (id is not null) capacity.Release(id);
    }
}
