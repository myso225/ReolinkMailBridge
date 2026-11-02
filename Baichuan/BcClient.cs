using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ReolinkMailBridge.Baichuan;

public sealed record BatteryInfo(int? Percent, string? ChargeStatus, string? AdapterStatus, int? Temperature, int? Voltage, bool? LowPower, string Xml);

/// <summary>
/// Baichuan session with one battery camera over <see cref="BcUdpConnection"/>: login, then single requests.
/// Login: (1) msg 1, class 0x6514, response code 0xdc12 (= "AES please"), empty body; the camera answers 0xddXX with
/// the chosen cipher and a nonce. (2) msg 1, class 0x6414, LoginUser XML with MD5(user+nonce) / MD5(password+nonce),
/// XOR-encrypted; answer 200 with DeviceInfo. Afterwards XML is AES-encrypted with MD5(nonce-password)[0..16].
/// </summary>
public sealed class BcClient(BcUdpConnection connection, ILogger logger) : IAsyncDisposable
{
    const string XmlHeader = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>\n";

    readonly List<byte> _buffer = [];
    BcEncryption _encryption = BcEncryption.None;
    byte[]? _aesKey;
    ushort _nextMsgNum;
    string _username = "admin";

    /// <summary>Every message received, decrypted, including unsolicited ones (e.g. 252 battery pushes).</summary>
    public List<BcMessage> Received { get; } = [];

    public static async Task<BcClient> ConnectAsync(IPAddress ip, string uid, ILogger logger, CancellationToken ct)
    {
        var connection = await BcUdpConnection.ConnectAsync(ip, uid, logger, TimeSpan.FromSeconds(10), ct);
        return new BcClient(connection, logger);
    }

    /// <summary>Logs in and returns the DeviceInfo XML.</summary>
    public async Task<string> LoginAsync(string username, string password, CancellationToken ct)
    {
        _username = username;
        var msgNum = _nextMsgNum++;
        var nonceReply = await RequestAsync(new BcMessage(BcMessage.MsgLogin, msgNum, 0x6514, ResponseCode: 0xdc12), ct);
        var nonce = XmlValue(nonceReply.PayloadText, "nonce")
                    ?? throw new IOException($"No nonce in login reply (code 0x{nonceReply.ResponseCode:x4}): {nonceReply.PayloadText}");
        var cipher = nonceReply.ResponseCode & 0xff;
        _encryption = cipher switch { 0x00 => BcEncryption.None, 0x01 => BcEncryption.Xor, 0x02 or 0x12 => BcEncryption.Aes,
            _ => throw new IOException($"Unknown encryption 0x{nonceReply.ResponseCode:x4}") };
        if (_encryption == BcEncryption.Aes)
            _aesKey = BcCrypto.AesKey(nonce, password);
        logger.LogDebug("Camera chose encryption {Encryption} (0x{Code:x4})", _encryption, nonceReply.ResponseCode);

        var login = XmlHeader +
            $"<body>\n<LoginUser version=\"1.1\">\n<userName>{BcCrypto.Md5(username + nonce)}</userName>\n" +
            $"<password>{BcCrypto.Md5(password + nonce)}</password>\n<userVer>1</userVer>\n</LoginUser>\n" +
            "<LoginNet version=\"1.1\">\n<type>LAN</type>\n<udpPort>0</udpPort>\n</LoginNet>\n</body>\n";
        var reply = await RequestAsync(new BcMessage(BcMessage.MsgLogin, msgNum, 0x6414, Payload: Encoding.UTF8.GetBytes(login)), ct);
        if (reply.ResponseCode != 200)
            throw new UnauthorizedAccessException($"Login refused with code {reply.ResponseCode} (wrong username or password?)");
        return reply.PayloadText;
    }

    /// <summary>Version info XML: name, model, serial, firmware.</summary>
    public async Task<string> GetVersionAsync(CancellationToken ct) =>
        (await RequestAsync(new BcMessage(BcMessage.MsgVersion, _nextMsgNum++, 0x6414), ct)).PayloadText;

    /// <summary>
    /// Battery state; the camera refuses (400) the request unless the extension names the logged-in user. If it refuses
    /// anyway, the BatteryList (msg 252) the camera pushes on its own after login is used instead. Right after login a
    /// camera sometimes answers 400 too, so a refusal is retried once after a second.
    /// </summary>
    public async Task<BatteryInfo> GetBatteryAsync(byte channel, CancellationToken ct)
    {
        var ext = XmlHeader + $"<Extension version=\"1.1\">\n<userName>{_username}</userName>\n<channelId>{channel}</channelId>\n</Extension>\n";
        BcMessage reply = null!;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (attempt > 1)
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            reply = await RequestAsync(new BcMessage(BcMessage.MsgBattery, _nextMsgNum++, 0x6414, Channel: channel,
                Extension: Encoding.UTF8.GetBytes(ext)), ct);
            if (reply.ResponseCode == 200)
                return ParseBattery(reply.PayloadText);
            logger.LogDebug("Battery request refused with code {Code} (attempt {Attempt})", reply.ResponseCode, attempt);
        }
        if (Received.LastOrDefault(m => m.MsgId == BcMessage.MsgBatteryList && m.PayloadText.Contains("<batteryPercent>")) is { } pushed)
        {
            logger.LogDebug("Battery request refused with code {Code}, using the pushed battery list", reply.ResponseCode);
            return ParseBattery(pushed.PayloadText);
        }
        throw new IOException($"Battery request refused with code {reply.ResponseCode}");
    }

    public static BatteryInfo ParseBattery(string xml) => new(
        XmlInt(xml, "batteryPercent"), XmlValue(xml, "chargeStatus"), XmlValue(xml, "adapterStatus"),
        XmlInt(xml, "temperature"), XmlInt(xml, "voltage"), XmlInt(xml, "lowPower") is { } low ? low != 0 : null, xml);

    async Task<BcMessage> RequestAsync(BcMessage request, CancellationToken ct)
    {
        var encrypted = request with
        {
            Extension = Encrypt(request, request.Extension),
            Payload = Encrypt(request, request.Payload),
        };
        logger.LogDebug("-> msg {Id} #{Num} class 0x{Class:x4} code 0x{Code:x4}: {Ext}{Payload}",
            request.MsgId, request.MsgNum, request.Class, request.ResponseCode, request.ExtensionText, request.PayloadText);
        await connection.SendAsync(encrypted.ToBytes(), ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var message = await ReadMessageAsync(timeout.Token);
                if (message.MsgId == request.MsgId && message.MsgNum == request.MsgNum)
                    return message;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No reply to message {request.MsgId}");
        }
    }

    async Task<BcMessage> ReadMessageAsync(CancellationToken ct)
    {
        while (true)
        {
            if (BcMessage.TryRead(_buffer) is { } raw)
            {
                var message = raw with { Extension = Decrypt(raw, raw.Extension, isPayload: false), Payload = Decrypt(raw, raw.Payload, isPayload: true) };
                Received.Add(message);
                logger.LogDebug("<- msg {Id} #{Num} class 0x{Class:x4} code 0x{Code:x4}: {Ext}{Payload}",
                    message.MsgId, message.MsgNum, message.Class, message.ResponseCode, message.ExtensionText, message.PayloadText);
                return message;
            }
            _buffer.AddRange(await connection.Incoming.ReadAsync(ct));
        }
    }

    /// <summary>Login messages always use XOR; everything else the negotiated cipher.</summary>
    byte[]? Encrypt(BcMessage m, byte[]? data)
    {
        if (data is not { Length: > 0 }) return data;
        var cipher = m.MsgId == BcMessage.MsgLogin && _encryption == BcEncryption.Aes ? BcEncryption.Xor : _encryption;
        if (m.MsgId == BcMessage.MsgLogin && _encryption == BcEncryption.None) cipher = BcEncryption.Xor;
        return Apply(cipher, m.Channel, data, encrypt: true);
    }

    byte[]? Decrypt(BcMessage m, byte[]? data, bool isPayload)
    {
        if (data is not { Length: > 0 }) return data;
        var cipher = _encryption;
        if (m.MsgId == BcMessage.MsgLogin && isPayload)
        {
            if (m.ResponseCode >> 8 == 0xdd)
                cipher = (m.ResponseCode & 0xff) == 0 ? BcEncryption.None : BcEncryption.Xor;
            else if (_encryption == BcEncryption.Aes)
                cipher = BcEncryption.Xor;
        }
        return Apply(cipher, m.Channel, data, encrypt: false);
    }

    byte[] Apply(BcEncryption cipher, byte channel, byte[] data, bool encrypt) => cipher switch
    {
        BcEncryption.Xor => BcCrypto.Xor(data, channel),
        BcEncryption.Aes => encrypt ? BcCrypto.AesEncrypt(_aesKey!, data) : BcCrypto.AesDecrypt(_aesKey!, data),
        _ => data,
    };

    static string? XmlValue(string xml, string element)
    {
        var m = Regex.Match(xml, $"<{element}>\\s*([^<]*?)\\s*</{element}>");
        return m.Success ? m.Groups[1].Value : null;
    }

    static int? XmlInt(string xml, string element) => int.TryParse(XmlValue(xml, element), out var v) ? v : null;

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
