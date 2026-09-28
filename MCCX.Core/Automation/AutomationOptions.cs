namespace MCCX.Core;

/// <summary>
/// 自动砍怪参数（需求 3.2）。时间单位毫秒；攻击冷却在 [Min, Max] 内随机取值，
/// 避免固定节奏被服务端判定为机器人。
/// </summary>
public sealed record AttackOptions
{
    /// <summary>攻击距离（格）。原版服务端对攻击距离有校验，取 3 格左右最稳。</summary>
    public double Range { get; init; } = 3.0;

    /// <summary>攻击冷却下限（毫秒）。</summary>
    public int CooldownMinMs { get; init; } = 800;

    /// <summary>攻击冷却上限（毫秒）。</summary>
    public int CooldownMaxMs { get; init; } = 1600;
}

/// <summary>鼠标控制模式（需求 3.2）。</summary>
public enum MouseMode
{
    /// <summary>长按：按下 → 保持 → 释放 → 立刻再次按下。</summary>
    Hold = 0,

    /// <summary>间隔点击：按下+释放 → 等待 → 重复。</summary>
    IntervalClick = 1,

    /// <summary>间隔长按：按下 → 保持/蓄力 → 释放 → 冷却 → 重复。</summary>
    IntervalHold = 2,
}

/// <summary>鼠标按键。</summary>
public enum MouseSide
{
    Left = 0,
    Right = 1,
}

/// <summary>鼠标按键控制参数。时间单位毫秒，JitterPercent 为 ± 抖动比例。</summary>
public sealed record MouseOptions
{
    public MouseMode Mode { get; init; } = MouseMode.IntervalClick;

    public MouseSide Side { get; init; } = MouseSide.Right;

    /// <summary>保持（按住/蓄力）时长。</summary>
    public int HoldMs { get; init; } = 1000;

    /// <summary>点击间隔 / 间隔长按的冷却时长。</summary>
    public int IntervalMs { get; init; } = 600;

    /// <summary>随机抖动比例（0-90，百分比）。0 表示完全固定节奏。</summary>
    public int JitterPercent { get; init; } = 20;
}

/// <summary>断线自动重连参数（需求 3.2）。延迟按 DelayMs ± JitterPercent 抖动。</summary>
public sealed record ReconnectOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>连续失败的最大重连次数。</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>每次重连前的等待时长（毫秒）。</summary>
    public int DelayMs { get; init; } = 3000;

    /// <summary>随机抖动比例（0-90，百分比）。</summary>
    public int JitterPercent { get; init; } = 30;
}

/// <summary>时间抖动工具：把固定毫秒数变成 ±percent% 的随机值。</summary>
internal static class AutomationJitter
{
    /// <summary>返回 baseMs ±percent% 的随机毫秒数（percent 截断到 0-90）。</summary>
    public static int Apply(int baseMs, int percent, Random? random = null)
    {
        if (baseMs < 0)
            baseMs = 0;

        percent = Math.Clamp(percent, 0, 90);
        if (percent == 0 || baseMs == 0)
            return baseMs;

        int span = baseMs * percent / 100;
        if (span <= 0)
            return baseMs;

        Random source = random ?? Random.Shared;
        return baseMs + source.Next(-span, span + 1);
    }

    /// <summary>毫秒 → MCC 的 20 TPS tick 数（至少 1 tick）。</summary>
    public static int MsToTicks(int ms) => Math.Max(1, (ms + 49) / 50);
}
