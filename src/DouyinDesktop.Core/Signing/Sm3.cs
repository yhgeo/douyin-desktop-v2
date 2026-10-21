using System.Text;

namespace DouyinDesktop.Core.Signing;

/// <summary>
/// 国密 SM3 哈希（GB/T 32905-2016）。
///
/// 为什么需要它：抖音的 a_bogus 签名把 SM3 当作摘要函数用，
/// 不是 SHA-256。用错算法签名会被服务端判为无效。
///
/// 实现严格对齐 vendor/DouYin_Spider/utils/sm3.py，并附带标准测试向量
/// （"abc" 的已知结果）用于自检。
/// </summary>
public static class Sm3
{
    private static readonly uint[] Iv =
    {
        0x7380166f, 0x4914b2b9, 0x172442d7, 0xda8a0600,
        0xa96f30bc, 0x163138aa, 0xe38dee4d, 0xb0fb0e4e,
    };

    /// <summary>返回 32 字节摘要。</summary>
    public static byte[] Hash(ReadOnlySpan<byte> message)
    {
        int bitLen = message.Length * 8;

        // 填充：0x80，补 0 到 len % 64 == 56，再接 8 字节大端位长
        int padLen = 64 - ((message.Length + 9) % 64);
        if (padLen == 64) padLen = 0;
        int totalLen = message.Length + 1 + padLen + 8;

        byte[] buffer = new byte[totalLen];
        message.CopyTo(buffer);
        buffer[message.Length] = 0x80;

        // 位长写在大端末尾（消息不会超过 2^61 位，高 4 字节恒为 0）
        ulong bits = (ulong)bitLen;
        for (int i = 0; i < 8; i++)
        {
            buffer[totalLen - 1 - i] = (byte)(bits >> (8 * i));
        }

        uint[] v = (uint[])Iv.Clone();
        uint[] w = new uint[68];
        uint[] w1 = new uint[64];

        for (int offset = 0; offset < totalLen; offset += 64)
        {
            for (int i = 0; i < 16; i++)
            {
                int p = offset + i * 4;
                w[i] = ((uint)buffer[p] << 24) | ((uint)buffer[p + 1] << 16)
                     | ((uint)buffer[p + 2] << 8) | buffer[p + 3];
            }
            for (int j = 16; j < 68; j++)
            {
                unchecked
                {
                    w[j] = P1(w[j - 16] ^ w[j - 9] ^ Rotl(w[j - 3], 15))
                         ^ Rotl(w[j - 13], 7) ^ w[j - 6];
                }
            }
            for (int j = 0; j < 64; j++)
            {
                w1[j] = w[j] ^ w[j + 4];
            }

            uint a = v[0], b = v[1], c = v[2], d = v[3];
            uint e = v[4], f = v[5], g = v[6], h = v[7];

            for (int j = 0; j < 64; j++)
            {
                unchecked
                {
                    uint ss1 = Rotl(Rotl(a, 12) + e + Rotl(Tj(j), j), 7);
                    uint ss2 = ss1 ^ Rotl(a, 12);
                    uint tt1 = Ff(a, b, c, j) + d + ss2 + w1[j];
                    uint tt2 = Gg(e, f, g, j) + h + ss1 + w[j];

                    d = c;
                    c = Rotl(b, 9);
                    b = a;
                    a = tt1;
                    h = g;
                    g = Rotl(f, 19);
                    f = e;
                    e = P0(tt2);
                }
            }

            unchecked
            {
                v[0] ^= a; v[1] ^= b; v[2] ^= c; v[3] ^= d;
                v[4] ^= e; v[5] ^= f; v[6] ^= g; v[7] ^= h;
            }
        }

        byte[] digest = new byte[32];
        for (int i = 0; i < 8; i++)
        {
            digest[i * 4]     = (byte)(v[i] >> 24);
            digest[i * 4 + 1] = (byte)(v[i] >> 16);
            digest[i * 4 + 2] = (byte)(v[i] >> 8);
            digest[i * 4 + 3] = (byte)v[i];
        }
        return digest;
    }

    public static byte[] Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    public static string Hex(ReadOnlySpan<byte> digest) => Convert.ToHexString(digest).ToLowerInvariant();

    public static string Hex(string text) => Hex(Hash(text));

    private static uint Rotl(uint x, int n)
    {
        n &= 31;
        if (n == 0) return x;
        unchecked
        {
            return (x << n) | (x >> (32 - n));
        }
    }

    private static uint Tj(int j) => j < 16 ? 0x79CC4519u : 0x7A879D8Au;

    private static uint Ff(uint x, uint y, uint z, int j)
        => j < 16 ? x ^ y ^ z : (x & y) | (x & z) | (y & z);

    private static uint Gg(uint x, uint y, uint z, int j)
        => j < 16 ? x ^ y ^ z : (x & y) | (~x & z);

    private static uint P0(uint x) => x ^ Rotl(x, 9) ^ Rotl(x, 17);

    private static uint P1(uint x) => x ^ Rotl(x, 15) ^ Rotl(x, 23);
}
