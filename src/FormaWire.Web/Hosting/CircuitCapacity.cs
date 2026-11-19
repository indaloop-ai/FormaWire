namespace FormaWire.Web.Hosting;

// A slot belongs to a circuit, not a transport connection. Retained disconnected
// circuits still consume memory and keep their slot until Blazor disposes them.
public sealed class CircuitCapacity(WebSettings settings)
{
    private readonly object _gate = new();
    private readonly HashSet<string> _circuits = new(StringComparer.Ordinal);

    public bool IsFull { get { lock (_gate) return _circuits.Count >= settings.MaxConcurrentCircuits; } }
    public int Count { get { lock (_gate) return _circuits.Count; } }

    public bool TryAcquire(string circuitId)
    {
        lock (_gate)
        {
            if (_circuits.Contains(circuitId)) return true;
            if (_circuits.Count >= settings.MaxConcurrentCircuits) return false;
            return _circuits.Add(circuitId);
        }
    }

    public void Release(string circuitId) { lock (_gate) _circuits.Remove(circuitId); }
}
