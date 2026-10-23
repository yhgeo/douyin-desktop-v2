namespace DouyinDesktop.App;

/// <summary>
/// 登录健康度：靠"能不能读到评论"来判断。
///
/// 抖音对过期的登录态不返回错误码，只返回空 body，所以没法直接问。
/// 做法是：每次视频加载好后，后台拉一小页评论——
/// 有数据就是有效，空的就说明登录态大概率失效了。
/// 结果存在这里，设置页读它显示。
/// </summary>
public static class LoginHealth
{
    public enum State
    {
        Unknown,     // 还没检查过
        Ok,          // 有效
        Expired,     // 可能已过期
        Guest,       // 没登录
    }

    public static State Current { get; private set; } = State.Unknown;

    public static DateTime? CheckedAt { get; private set; }

    public static event Action? Changed;

    public static void Set(State state)
    {
        Current = state;
        CheckedAt = DateTime.Now;
        Changed?.Invoke();
    }

    public static string Describe()
    {
        return Current switch
        {
            State.Ok => "正常",
            State.Expired => "可能已过期，建议重新扫码",
            State.Guest => "未登录",
            _ => "尚未检查",
        };
    }

    public static string DescribeTime()
        => CheckedAt is { } t ? $"上次检查 {t:HH:mm}" : "加载视频后会自动检查";
}
