using System.ComponentModel;

namespace MCCX_App.ViewModels;

/// <summary>
/// 一条界面日志。连续重复的日志合并进同一条，展示文本就地变成“原文 xN”
/// （首条不带后缀；第 2 条相同日志合并后显示 x1，第 3 条显示 x2，依此类推——
/// N = 合并进去的重复条数），避免同一行刷屏。
/// </summary>
public sealed class LogEntry : INotifyPropertyChanged
{
    private string _text;
    private int _mergedCount;

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

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>合并一条“与本条完全相同”的后续重复日志：计数 +1，展示文本追加 “ xN”。</summary>
    public void MergeDuplicate() => Text = $"{BaseText} x{++_mergedCount}";

    public override string ToString() => Text;
}
