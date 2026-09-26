namespace MccX_App.ViewModels;

/// <summary>一条界面日志。</summary>
public sealed class LogEntry
{
    public LogEntry(string text, string raw)
    {
        Text = text;
        Raw = raw;
    }

    /// <summary>去掉颜色码后的展示文本。</summary>
    public string Text { get; }

    /// <summary>MCC 输出的原始文本（带 § 颜色码），供后续按级别过滤使用。</summary>
    public string Raw { get; }

    public override string ToString() => Text;
}
