using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ReolinkMailBridge.Baichuan;

/// <summary>
/// Reolink's UDP transport ("BcUdp") for battery cameras on the LAN. Three packet kinds, all little-endian:
/// <list type="bullet">
/// <item>discovery 0x2a87cf3a: size, 1, tid, crc, XML encrypted with <see cref="BcCrypto.UdpXor"/>;</item>
/// <item>data 0x2a87cf10: connection id, 0, packet id, size, payload (a slice of the Baichuan byte stream);</item>
/// <item>ack 0x2a87cf20: connection id, 0, group id, last packet id, latency, size, bitmap.</item>
/// </list>
/// The client asks for the camera by UID (C2D_C to ports 2015/2018), the camera answers D2C_C_R with its connection
/// id, then both sides exchange data packets addressed with the peer's id.
/// </summary>
public sealed class BcUdpConnection : IAsyncDisposable
{
    const uint MagicDiscovery = 0x2a87cf3a, MagicData = 0x2a87cf10, MagicAck = 0x2a87cf20;
    const int Mtu = 1350, MaxChunk = Mtu - 20;
    static readonly int[] DiscoveryPorts = [2015, 2018];

    readonly UdpClient _udp;
    readonly ILogger _logger;
    readonly CancellationTokenSource _cts = new();
    readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    readonly Dictionary<uint, byte[]> _received = [];
    readonly Dictionary<uint, byte[]> _unacked = [];
    readonly Lock _lock = new();
    readonly uint _tid;
    readonly int _clientId;

    IPEndPoint? _camera;
    int _cameraId;
    uint _nextReceive, _nextSend;
    Task? _receiveLoop, _maintenanceLoop;

    BcUdpConnection(ILogger logger)
    {
        _logger = logger;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        _tid = (uint)Random.Shared.Next(1, 256);
        _clientId = Random.Shared.Next(int.MinValue, int.MaxValue);
    }

    /// <summary>Byte stream from the camera, in order.</summary>
    public ChannelReader<byte[]> Incoming => _incoming.Reader;

    public static async Task<BcUdpConnection> ConnectAsync(IPAddress cameraIp, string uid, ILogger logger, TimeSpan timeout, CancellationToken ct)
    {
        var conn = new BcUdpConnection(logger);
        try
        {
            await conn.DiscoverAsync(cameraIp, uid, timeout, ct);
            conn._receiveLoop = conn.ReceiveLoopAsync();
            conn._maintenanceLoop = conn.MaintenanceLoopAsync();
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    async Task DiscoverAsync(IPAddress cameraIp, string uid, TimeSpan timeout, CancellationToken ct)
    {
        var port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        var request = DiscoveryPacket(
            $"<C2D_C>\n<uid>{uid}</uid>\n<cli>\n<port>{port}</port>\n</cli>\n<cid>{_clientId}</cid>\n<mtu>{Mtu}</mtu>\n<debug>0</debug>\n<p>WIN</p>\n</C2D_C>");
        // Unicast to the known IP first; broadcast as well, as the Reolink app does.
        IPEndPoint[] targets = [.. DiscoveryPorts.Select(p => new IPEndPoint(cameraIp, p)),
                                .. DiscoveryPorts.Select(p => new IPEndPoint(IPAddress.Broadcast, p))];

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var sender = Task.Run(async () =>
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                foreach (var t in targets)
                    await _udp.SendAsync(request, t, timeoutCts.Token);
                await Task.Delay(500, timeoutCts.Token);
            }
        }, timeoutCts.Token);

        try
        {
            while (true)
            {
                var result = await _udp.ReceiveAsync(timeoutCts.Token);
                var xml = TryReadDiscovery(result.Buffer);
                if (xml is null) continue;
                _logger.LogDebug("Discovery from {From}: {Xml}", result.RemoteEndPoint, xml);
                if (!xml.Contains("<D2C_C_R>") || XmlInt(xml, "cid") != _clientId) continue;
                if (XmlInt(xml, "rsp") is { } rsp && rsp != 0)
                    throw new IOException($"Camera refused the connection (rsp {rsp})");

                _camera = result.RemoteEndPoint;
                _cameraId = XmlInt(xml, "did") ?? throw new IOException("D2C_C_R without did");
                _logger.LogDebug("Camera {Uid} answered from {EndPoint}, connection id {Did}", uid, _camera, _cameraId);
                return;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No answer from camera {uid} at {cameraIp} within {timeout.TotalSeconds:0} s (asleep or wrong UID?)");
        }
        finally
        {
            await timeoutCts.CancelAsync();
            try { await sender; } catch (OperationCanceledException) { }
        }
    }

    public async Task SendAsync(byte[] data, CancellationToken ct)
    {
        var packets = new List<byte[]>();
        lock (_lock)
        {
            for (var offset = 0; offset < data.Length; offset += MaxChunk)
            {
                var chunk = data.AsSpan(offset, Math.Min(MaxChunk, data.Length - offset));
                var packet = new byte[20 + chunk.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(packet, MagicData);
                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4), _cameraId);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), _nextSend);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), (uint)chunk.Length);
                chunk.CopyTo(packet.AsSpan(20));
                _unacked[_nextSend++] = packet;
                packets.Add(packet);
            }
        }
        foreach (var p in packets)
            await _udp.SendAsync(p, _camera!, ct);
    }

    async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await _udp.ReceiveAsync(_cts.Token);
                var b = result.Buffer;
                if (b.Length < 20) continue;
                var magic = BinaryPrimitives.ReadUInt32LittleEndian(b);
                var connectionId = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4));

                if (magic == MagicData && connectionId == _clientId)
                {
                    var packetId = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12));
                    var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16));
                    lock (_lock)
                    {
                        if (packetId >= _nextReceive)
                            _received[packetId] = b.AsSpan(20, Math.Min(size, b.Length - 20)).ToArray();
                        while (_received.Remove(_nextReceive, out var chunk))
                        {
                            _incoming.Writer.TryWrite(chunk);
                            _nextReceive++;
                        }
                    }
                    await SendAckAsync();
                }
                else if (magic == MagicAck && connectionId == _clientId && b.Length >= 28)
                {
                    var lastId = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16));
                    if (lastId != uint.MaxValue)
                        lock (_lock)
                            foreach (var id in _unacked.Keys.Where(id => id <= lastId).ToList())
                                _unacked.Remove(id);
                }
                else if (magic == MagicDiscovery && TryReadDiscovery(b) is { } xml && xml.Contains("<D2C_DISC>"))
                {
                    _logger.LogDebug("Camera closed the connection");
                    _incoming.Writer.TryComplete(new IOException("Camera closed the connection"));
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _incoming.Writer.TryComplete(ex);
        }
    }

    /// <summary>Resends unacknowledged packets every 500 ms and sends a heartbeat every second.</summary>
    async Task MaintenanceLoopAsync()
    {
        var tick = 0;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(500, _cts.Token);
                byte[][] resend;
                lock (_lock) resend = [.. _unacked.Values];
                foreach (var p in resend)
                    await _udp.SendAsync(p, _camera!, _cts.Token);
                if (++tick % 2 == 0)
                    await _udp.SendAsync(DiscoveryPacket($"<C2D_HB>\n<cid>{_clientId}</cid>\n<did>{_cameraId}</did>\n</C2D_HB>"), _camera!, _cts.Token);
                await SendAckAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning("UDP maintenance stopped: {Message}", ex.Message);
        }
    }

    async Task SendAckAsync()
    {
        var ack = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(ack, MagicAck);
        BinaryPrimitives.WriteInt32LittleEndian(ack.AsSpan(4), _cameraId);
        lock (_lock)
        {
            // Nothing received yet: group and packet id 0xffffffff.
            var last = _nextReceive == 0 ? uint.MaxValue : _nextReceive - 1;
            BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(12), _nextReceive == 0 ? uint.MaxValue : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(ack.AsSpan(16), last);
        }
        await _udp.SendAsync(ack, _camera!, _cts.Token);
    }

    byte[] DiscoveryPacket(string innerXml)
    {
        var encrypted = BcCrypto.UdpXor(Encoding.UTF8.GetBytes($"<P2P>\n{innerXml}\n</P2P>\n"), _tid);
        var packet = new byte[20 + encrypted.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, MagicDiscovery);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)encrypted.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), _tid);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), BcCrypto.UdpCrc(encrypted));
        encrypted.CopyTo(packet.AsSpan(20));
        return packet;
    }

    static string? TryReadDiscovery(byte[] b)
    {
        if (b.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(b) != MagicDiscovery) return null;
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
        var tid = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(12));
        if (b.Length < 20 + size) return null;
        return Encoding.UTF8.GetString(BcCrypto.UdpXor(b.AsSpan(20, size), tid));
    }

    static int? XmlInt(string xml, string element)
    {
        var m = Regex.Match(xml, $"<{element}>\\s*(-?\\d+)\\s*</{element}>");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_camera is not null && !_cts.IsCancellationRequested)
        {
            var disc = DiscoveryPacket($"<C2D_DISC>\n<cid>{_clientId}</cid>\n<did>{_cameraId}</did>\n</C2D_DISC>");
            for (var i = 0; i < 3; i++)
                try { await _udp.SendAsync(disc, _camera); } catch { }
        }
        await _cts.CancelAsync();
        foreach (var t in new[] { _receiveLoop, _maintenanceLoop })
            if (t is not null) try { await t; } catch { }
        _udp.Dispose();
        _cts.Dispose();
    }
}
