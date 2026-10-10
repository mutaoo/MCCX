using System.Collections.Generic;

namespace MCCX_App.ViewModels;

/// <summary>
/// 各功能“过程日志”开关的功能标识（存进 <c>AccountProfile.FeatureLogSwitches</c> 的键）。
///
/// 2026-10-08 需求：Bot 日志发送过于频繁 → 每个功能一个独立的日志开关。
/// 开关按「功能」而不是按「某一条日志」存，所以这里要给出
/// <b>日志行里的 [标签] → 功能</b> 的对照表，供 AccountViewModel.AppendLog 认行。
/// </summary>
internal static class FeatureLogKeys
{
    public const string Attack = "attack";
    public const string Mouse = "mouse";
    public const string Fishing = "fishing";
    public const string Refill = "refill";
    public const string Walk = "walk";
    public const string View = "view";
    public const string Reconnect = "reconnect";

    /// <summary>
    /// 日志行开头的 <c>[标签]</c> → 功能标识。
    ///
    /// <b>没进这张表的标签永远不受开关影响</b>：[MCCX] 是应用自己的提示、[公告] 是服务器消息，
    /// 把它们漏进映射会让“关掉砍怪日志”顺手把服务器公告也吃掉。
    ///
    /// 同一功能可以有多个标签：MCC 的内置 Bot 用英文名（AutoFishing/AutoRelog），
    /// MCCX 自己的 Bot 用中文名，而 ViewControlBot 的自检日志带后缀（[视角-test]）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TagToKey =
        new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["砍怪"] = Attack,
            ["鼠标"] = Mouse,
            ["视角"] = View,
            ["视角-test"] = View,
            ["自动补充"] = Refill,
            ["自动行走"] = Walk,
            ["AutoFishing"] = Fishing,
            ["AutoRelog"] = Reconnect,
        };
}
