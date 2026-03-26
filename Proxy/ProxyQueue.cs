using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Server.Proxy;

public sealed class ProxyQueue
{
    private readonly ConcurrentDictionary<uint, Channel<ProxyFrame>> _queues = new();

    public void Enqueue(uint beaconId, ProxyFrame frame)
    {
        var channel = _queues.GetOrAdd(beaconId, _ => Channel.CreateUnbounded<ProxyFrame>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false }));

        channel.Writer.TryWrite(frame);
    }

    public IReadOnlyList<ProxyFrame> Drain(uint beaconId)
    {
        if (!_queues.TryGetValue(beaconId, out var channel))
            return [];

        var frames = new List<ProxyFrame>();

        while (channel.Reader.TryRead(out var frame))
            frames.Add(frame);

        return frames;
    }
}