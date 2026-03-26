using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using System.Threading.Channels;

namespace Server.Proxy;

[Authorize]
public sealed class ProxyProtoService(ProxyQueue proxyQueue, ProxyBroker proxyBroker) : ProxyService.ProxyServiceBase
{
    public override async Task ProxyStream(IAsyncStreamReader<ProxyFrame> requestStream,
        IServerStreamWriter<ProxyFrame> responseStream, ServerCallContext context)
    {
        var ct = context.CancellationToken;

        // One channel per connection - all proxy replies for this client flow through here
        var clientChannel = Channel.CreateUnbounded<ProxyFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var readLoop = ReadFramesAsync(requestStream, clientChannel, ct);

        await foreach (var frame in clientChannel.Reader.ReadAllAsync(ct))
            await responseStream.WriteAsync(frame, ct);

        await readLoop;
    }

    private async Task ReadFramesAsync(IAsyncStreamReader<ProxyFrame> requestStream,
        Channel<ProxyFrame> clientChannel, CancellationToken ct)
    {
        var registrations = new List<IDisposable>();
        var registered = new HashSet<uint>();

        try
        {
            while (await requestStream.MoveNext(ct))
            {
                var frame = requestStream.Current;

                // register the first time we see each connection ID so the ProxyBroker
                // knows where to send replies from the agent.
                if (registered.Add(frame.ConnectionId))
                    registrations.Add(proxyBroker.Register(frame.ConnectionId, frame.BeaconId, clientChannel.Writer));

                proxyQueue.Enqueue(frame.BeaconId, frame);
            }
        }
        finally
        {
            foreach (var reg in registrations)
                reg.Dispose();

            clientChannel.Writer.TryComplete();
        }
    }
}