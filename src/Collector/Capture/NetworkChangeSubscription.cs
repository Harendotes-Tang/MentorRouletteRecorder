using System.Net.NetworkInformation;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// Owns both static .NET notification subscriptions. The callback only invalidates the controller's
/// cached discovery; all enumeration and capture lifecycle work remains on its serialized follow poll.
/// </summary>
internal sealed class NetworkChangeSubscription : IDisposable
{
    private Action? _changed;

    public NetworkChangeSubscription(Action changed)
    {
        _changed = changed;
        NetworkChange.NetworkAddressChanged += AddressChanged;
        try { NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged; }
        catch
        {
            NetworkChange.NetworkAddressChanged -= AddressChanged;
            _changed = null;
            throw;
        }
    }

    private void AddressChanged(object? sender, EventArgs args) => Volatile.Read(ref _changed)?.Invoke();
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args) => Volatile.Read(ref _changed)?.Invoke();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _changed, null) is null) return;
        NetworkChange.NetworkAddressChanged -= AddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged;
    }
}
