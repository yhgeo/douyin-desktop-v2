using System.Text.Json;
using System.Text.Json.Serialization;
using DouyinDesktop.Core.Signing;

namespace DouyinDesktop.Tests;

/// <summary>
/// a_bogus 移植的正确性验收。
///
/// 基准来自 tools/golden/abogus_vectors.json —— 那是用 Python 参考实现
/// （ABogusPureSigner(fixed=True)，时间/随机数/指纹全部钉死）生成的，
/// 同一输入必然产出同一签名。所以这里可以逐字节比对，而不是"看着像就行"。
/// </summary>
public class ABogusGoldenTests
{
    public class Vector
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("note")] public string Note { get; set; } = "";
        [JsonPropertyName("host")] public string Host { get; set; } = "";
        [JsonPropertyName("query")] public string Query { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("ua")] public string Ua { get; set; } = "";
        [JsonPropertyName("now_ms")] public long NowMs { get; set; }
        [JsonPropertyName("rand")] public double Rand { get; set; }
        [JsonPropertyName("counter")] public int Counter { get; set; }
        [JsonPropertyName("expected")] public string Expected { get; set; } = "";
    }

    public class GoldenFile
    {
        [JsonPropertyName("vectors")] public List<Vector> Vectors { get; set; } = new();
    }

    private static List<Vector> LoadVectors()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "abogus_vectors.json");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"找不到基准向量 {path}。先运行 tools/golden/gen_abogus_vectors.py 生成。");

        var file = JsonSerializer.Deserialize<GoldenFile>(File.ReadAllText(path));
        return file?.Vectors ?? new List<Vector>();
    }

    [Fact]
    public void 向量文件存在且有内容()
    {
        var vectors = LoadVectors();
        Assert.True(vectors.Count >= 30, $"向量数量异常：{vectors.Count}");
        Assert.All(vectors, v => Assert.False(string.IsNullOrEmpty(v.Expected)));
    }

    [Fact]
    public void 全部向量逐字节一致()
    {
        var vectors = LoadVectors();
        var failures = new List<string>();

        foreach (var v in vectors)
        {
            // 每个向量独立构造 signer：counter 与随机序列都从头开始，
            // 否则前一个向量的调用会影响后一个（Python 版也是这么做的）
            var signer = new ABogus(
                @fixed: true,
                userAgent: v.Ua,
                fixedNowMs: v.NowMs,
                fixedCounter: v.Counter,
                random: new FixedRandomSource(v.Rand));

            string actual = signer.SignQuery(v.Query, v.Body, v.Host);

            if (actual != v.Expected)
            {
                failures.Add(
                    $"{v.Id} ({v.Note})\n" +
                    $"  期望: {v.Expected}\n" +
                    $"  实际: {actual}\n" +
                    $"  首个差异位: {FirstDiff(v.Expected, actual)}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{vectors.Count} 组不一致：\n\n" + string.Join("\n\n", failures));
    }

    private static string FirstDiff(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i]) return $"index {i}: '{a[i]}' vs '{b[i]}'";
        return a.Length == b.Length ? "长度相同但内容不同" : $"长度 {a.Length} vs {b.Length}";
    }
}

/// <summary>SM3 的标准测试向量，确保基础摘要函数本身是对的。</summary>
public class Sm3Tests
{
    [Fact]
    public void Abc标准向量()
    {
        // GB/T 32905-2016 附录 A 的示例
        Assert.Equal(
            "66c7f0f462eeedd9d1f2d46bdc10e4e24167c4875cf2f7a2297da02b8f4ba8e0",
            Sm3.Hex("abc"));
    }

    [Fact]
    public void 长输入标准向量()
    {
        // "abcd" 重复 16 次，512 位
        Assert.Equal(
            "debe9ff92275b8a138604889c18e5a4d6fdb70e5387e5765293dcba39c0c5732",
            Sm3.Hex(string.Concat(Enumerable.Repeat("abcd", 16))));
    }

    [Fact]
    public void 空输入()
    {
        // SM3("") 的已知值
        Assert.Equal(
            "1ab21d8355cfa17f8e61194831e81a8f22bec8c728fefb747ed035eb5082aa2b",
            Sm3.Hex(""));
    }

    [Fact]
    public void 摘要长度恒为32字节()
    {
        foreach (int len in new[] { 0, 1, 55, 56, 63, 64, 65, 1000 })
        {
            byte[] digest = Sm3.Hash(new string('x', len));
            Assert.Equal(32, digest.Length);
        }
    }
}
