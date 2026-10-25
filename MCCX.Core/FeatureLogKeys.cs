namespace MCCX.Core;

/// <summary>
/// 各功能“过程日志”开关的功能标识（存进 <c>AccountProfile.FeatureLogSwitches</c> 的键，
/// 也是父子进程 <c>logswitch</c> 命令里的 <c>Key</c>）。
///
/// 2026-10-08 需求：Bot 日志发送过于频繁 → 每个功能一个独立的日志开关。
///
/// 2026-10-20 改法（用户要求）：开关<b>控制日志的发出</b>，不再“先发出去、界面再按文本过滤”。
/// 键从界面经管道下发到子进程，子进程把对应 Bot 的
/// <c>ChatBot.ProcessLogEnabled</c> 置 false ——
/// 该 Bot 的过程日志连控制台/日志文件/管道都不会进，跟语言、标签文本、颜色码无关。
/// （旧实现靠日志行里的 <c>[标签]</c> 认功能，MCC 汉化后标签从 <c>AutoFishing</c> 变成
/// <c>自动钓鱼</c>，匹配失败开关就"看起来失效"了。）
/// </summary>
public static class FeatureLogKeys
{
    public const string Attack = "attack";
    public const string Mouse = "mouse";
    public const string Fishing = "fishing";
    public const string Refill = "refill";
    public const string Walk = "walk";
    public const string View = "view";
    public const string Reconnect = "reconnect";

    // 自动指令不在这里：2026-10-22 第二轮按用户要求改成"每条指令自己的日志开关"，
    // 开关存在 AutoCommandDefinition.LogEnabled 里，由 AutoCommandBot 逐条判断。
}
