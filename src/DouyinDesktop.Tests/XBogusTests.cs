using DouyinDesktop.Core.Signing;

namespace DouyinDesktop.Tests;

/// <summary>
/// x_bogus 签名。
///
/// 这个算法没有官方文档，全靠移植开源实现。所以必须用**别人仓库里跑通的参考向量**
/// 钉死——否则改了某个字节顺序、base64 字母表，本地编译通过但请求全被拒，
/// 排查起来非常痛苦（搜索接口只会回一句"请先登录"）。
///
/// 向量来源：https://github.com/smalls0098/xb 的 xb_test.go
/// </summary>
public class XBogusTests
{
    private const string ReferenceParams =
        "device_platform=webapp&aid=6383&channel=channel_pc_web&aweme_id=7346145148188904742" +
        "&pc_client_type=3&version_code=190500&version_name=19.5.0&cookie_enabled=true" +
        "&screen_width=1792&screen_height=1120&browser_language=zh-CN&browser_platform=MacIntel" +
        "&browser_name=Chrome&browser_version=104.0.5112.102&browser_online=true" +
        "&engine_name=Blink&engine_version=104.0.5112.102&os_name=Mac+OS&os_version=10.15.7" +
        "&cpu_core_num=12&device_memory=8&platform=PC&downlink=9.6&effective_type=4g" +
        "&round_trip_time=0&webid=7209940127916459575" +
        "&msToken=191ZZJ5jsfzviU7JMotGDULg3e8hVIh9B9CqqeNmFOMF7qHrbmi_44Vri00ssGlSI46oyGcOPMVtIFqdze7bYWGm40CDtmqJHV_YterqNVoC220dvjMP";

    private const string ReferenceUa =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "douyin/3.4.0 Chrome/104.0.5112.102 Electron/20.1.0-tt.4.release.douyin.155 " +
        "TTElectron/20.1.0-tt.4.release.douyin.155 Safari/537.36 awemePcClient/3.4.0 " +
        "buildId/11509980 osName/Mac";

    [Fact]
    public void 与参考实现逐字符一致()
    {
        string actual = XBogus.Sign(ReferenceParams, "", ReferenceUa, 1710927309);
        Assert.Equal("DFSzswVO/72ANVartLGTYN7TlqCM", actual);
    }

    [Fact]
    public void 长度固定28字符()
    {
        // 21 字节 → 28 字符（21 能被 3 整除，没有 padding）
        string xb = XBogus.Sign("a=1", "", "UA", 1710927309);
        Assert.Equal(28, xb.Length);
    }

    [Fact]
    public void 时间戳不同结果就不同()
    {
        string a = XBogus.Sign("a=1", "", "UA", 1710927309);
        string b = XBogus.Sign("a=1", "", "UA", 1710927310);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void 参数不同结果就不同()
    {
        string a = XBogus.Sign("a=1", "", "UA", 1710927309);
        string b = XBogus.Sign("a=2", "", "UA", 1710927309);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void UA不同结果就不同()
    {
        string a = XBogus.Sign("a=1", "", "UA-1", 1710927309);
        string b = XBogus.Sign("a=1", "", "UA-2", 1710927309);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void 只用到自定义字母表里的字符()
    {
        const string alphabet = "Dkdpgh4ZKsQB80/Mfvw36XI1R25-WUAlEi7NLboqYTOPuzmFjJnryx9HVGcaStCe";

        string xb = XBogus.Sign(ReferenceParams, "", ReferenceUa, 1710927309);
        Assert.All(xb, c => Assert.Contains(c, alphabet));
    }
}
