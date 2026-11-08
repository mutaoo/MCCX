using System.ComponentModel;
using MCCX_App.Logging;

namespace MCCX_App.ViewModels;

/// <summary>
/// 一条界面日志。连续重复的日志合并进同一条，展示文本就地变成“原文 xN”。
/// **N = 这条内容至今累计出现的总次数**（首条不带后缀；出现第 2 条就显示 x2，
/// 第 3 条显示 x3，依此类推）——口径与用户 2026-10-03 的要求一致：
/// “当出现第 1 条重复信息时，此时有两条一样的信息了，应该是 x2”。
/// </summary>
public sealed class LogEntry : INotifyPropertyChanged
{
    private string _text;
    private int _totalCount = 1;
    private IReadOnlyList<LogRun>? _coloredRuns;

    public LogEntry(string text, string raw)
    {
        BaseText = text;
        _text = text;
        Raw = raw;
    }

    /// <summary>本条日志的展示原文（不含合并计数后缀），合并判断也用它比较。</summary>
    public string BaseText { get; }

    /// <summary>
    /// 展示文本：被后续重复日志合并时就地更新为 “BaseText xN”。
    /// 带变更通知，模板以 OneWay 绑定时合并计数会即时刷新。
    /// </summary>
    public string Text
    {
        get => _text;
        private set
        {
            if (string.Equals(_text, value, StringComparison.Ordinal))
                return;

            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    /// <summary>MCC 输出的原始文本（带 § 颜色码），供后续按级别过滤使用。</summary>
    public string Raw { get; }

    /// <summary>
    /// 按服务器推送的 § 颜色码拆好的分段（2026-10-31 需求：日志按服务器颜色显示）。
    /// <para>
    /// 懒解析：只有真正显示出来的那几十行才会走到这里；拼起来的纯文本与
    /// <see cref="BaseText"/> 逐字符一致（渲染前还会再校验一次，见 MainPage.Log.cs）。
    /// 合并出来的 " xN" 后缀不在 <see cref="Raw"/> 里，由渲染方补一段默认色。
    /// </para>
    /// </summary>
    public IReadOnlyList<LogRun> ColoredRuns => _coloredRuns ??= LogLineFormat.Parse(Raw);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 合并一条“与本条完全相同”的后续重复日志：累计次数 +1，展示文本追加 “ xN”。
    /// 第 2 条出现时 N=2（不是 1）——首条本身就是第 1 次。
    /// </summary>
    public void MergeDuplicate() => Text = $"{BaseText} x{++_totalCount}";

    public override string ToString() => Text;
}
