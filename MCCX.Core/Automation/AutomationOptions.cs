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

    /// <summary>攻击生物过滤模式（默认不过滤 = 只打敌对生物）。</summary>
    public MobFilterMode FilterMode { get; init; } = MobFilterMode.Off;

    /// <summary>过滤名单：<see cref="MobCandidate.Key"/>（EntityType 名，如 "Zombie"）。</summary>
    public IReadOnlyList<string> Mobs { get; init; } = Array.Empty<string>();
}

/// <summary>鼠标控制模式（需求 3.2）。</summary>
public enum MouseMode
{
    /// <summary>
    /// 长按：按住不松手。按住/间隔两个参数不生效（界面也隐藏）：
    /// 右键持续发“使用物品”，左键持续挖掘准星所对方块（挖掘时长由 MCC 按方块与工具自动计算）。
    /// </summary>
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

/// <summary>
/// 单个鼠标按键的参数（需求 3.2：左键、右键设置相互独立，可以只开一个，也可以同时触发）。
/// 时间单位毫秒，JitterPercent 为 ± 抖动比例。
/// </summary>
public sealed record MouseButtonOptions
{
    /// <summary>这个按键是否启用（左右键各自独立，互不影响）。</summary>
    public bool Enabled { get; init; }

    public MouseMode Mode { get; init; } = MouseMode.IntervalClick;

    /// <summary>保持（按住/蓄力）时长。长按模式下不生效（长按=一直按住，没有时长概念）。</summary>
    public int HoldMs { get; init; } = 1000;

    /// <summary>点击间隔 / 间隔长按的冷却时长。长按模式下不生效。</summary>
    public int IntervalMs { get; init; } = 600;

    /// <summary>随机抖动比例（0-90，百分比）。0 表示完全固定节奏。</summary>
    public int JitterPercent { get; init; } = 20;
}

/// <summary>鼠标按键控制参数：左键、右键两套独立配置，状态互不干扰。</summary>
public sealed record MouseOptions
{
    /// <summary>左键（挖掘准星所对的方块；准星空时挥手）。</summary>
    public MouseButtonOptions Left { get; init; } = new();

    /// <summary>右键（使用物品 / 交互）。默认启用，与旧版默认按键“右键”一致。</summary>
    public MouseButtonOptions Right { get; init; } = new() { Enabled = true };

    /// <summary>
    /// 准星探测距离（格，1-7，默认 5）：左键挖掘、右键交互只作用于视线方向这个距离内的方块。
    /// 注意原版服务端对交互距离有校验（生存约 4.5 格），调得过大时远端点击可能被服务端拒绝。
    /// </summary>
    public double AimReach { get; init; } = 5.0;
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
