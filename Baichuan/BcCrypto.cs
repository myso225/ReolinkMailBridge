using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ReolinkMailBridge.Baichuan;

public enum BcEncryption { None, Xor, Aes }

/// <summary>
/// The ciphers of the Reolink "Baichuan" protocol, written from the protocol notes of the Neolink and reolink_aio
/// projects: a fixed-key XOR for login messages, AES-128-CFB for everything after login, and a separate XOR for the
/// UDP discovery XML.
/// </summary>
public static class BcCrypto
{
    static readonly byte[] XorKey = [0x1F, 0x2D, 0x3C, 0x4B, 0x5A, 0x69, 0x78, 0xFF];
    static readonly uint[] UdpKey = [0x1f2d3c4b, 0x5a6c7f8d, 0x38172e4b, 0x8271635a, 0x863f1a2b, 0xa5c6f7d8, 0x8371e1b4, 0x17f2d3a5];
    static readonly byte[] AesIv = "0123456789abcdef"u8.ToArray();

    /// <summary>Baichuan XOR; <paramref name="offset"/> is the message's channel id. Encrypts and decrypts.</summary>
    public static byte[] Xor(ReadOnlySpan<byte> data, uint offset)
    {
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ XorKey[(int)((offset + (uint)i) % 8)] ^ (byte)offset);
        return result;
    }

    /// <summary>XOR of UDP discovery XML; <paramref name="tid"/> is the packet's transaction id. Encrypts and decrypts.</summary>
    public static byte[] UdpXor(ReadOnlySpan<byte> data, uint tid)
    {
        Span<byte> key = stackalloc byte[32];
        for (var i = 0; i < UdpKey.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(key[(i * 4)..], unchecked(UdpKey[i] + tid));
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
            result[i] = (byte)(data[i] ^ key[i % 32]);
        return result;
    }

    /// <summary>CRC-32 (reflected, polynomial 0x04C11DB7) with initial value 0 and no final XOR.</summary>
    public static uint UdpCrc(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc;
    }

    /// <summary>Uppercase MD5 hex of the text, cut to 31 characters, as the camera expects for credentials.</summary>
    public static string Md5(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text)))[..31];

    public static byte[] AesKey(string nonce, string password) => Encoding.ASCII.GetBytes(Md5($"{nonce}-{password}")[..16]);

    /// <summary>AES-128-CFB128; each buffer is ciphered on its own, starting from the fixed IV.</summary>
    public static byte[] AesEncrypt(byte[] key, ReadOnlySpan<byte> data) => AesCfb(key, data, encrypt: true);

    public static byte[] AesDecrypt(byte[] key, ReadOnlySpan<byte> data) => AesCfb(key, data, encrypt: false);

    /// <summary>
    /// CFB built on ECB, because .NET's own CFB only accepts whole 16-byte blocks and Baichuan messages have any
    /// length (the last block is simply cut short).
    /// </summary>
    static byte[] AesCfb(byte[] key, ReadOnlySpan<byte> data, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        var result = new byte[data.Length];
        Span<byte> feedback = stackalloc byte[16];
        Span<byte> keystream = stackalloc byte[16];
        AesIv.CopyTo(feedback);
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            aes.EncryptEcb(feedback, keystream, PaddingMode.None);
            var n = Math.Min(16, data.Length - offset);
            for (var i = 0; i < n; i++)
                result[offset + i] = (byte)(data[offset + i] ^ keystream[i]);
            (encrypt ? result.AsSpan(offset, n) : data.Slice(offset, n)).CopyTo(feedback);
        }
        return result;
    }
}
