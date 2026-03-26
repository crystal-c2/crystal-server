using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Server.Proxy;

public sealed class ProxyBroker
{
    private readonly ConcurrentDictionary<uint, Entry> _connections = new();

    public uint? GetBeaconId(uint connectionId) =>
        _connections.TryGetValue(connectionId, out var e) ? e.BeaconId : null;

    public void Publish(uint connectionId, ProxyFrame frame)
    {
        if (_connections.TryGetValue(connectionId, out var e))
            e.Writer.TryWrite(frame);
    }

    public IDisposable Register(uint connectionId, uint beaconId, ChannelWriter<ProxyFrame> writer)
    {
        _connections[connectionId] = new Entry(beaconId, writer);
        return new Registration(() => _connections.TryRemove(connectionId, out _));
    }

    private sealed record Entry(uint BeaconId, ChannelWriter<ProxyFrame> Writer);

    private sealed class Registration(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}