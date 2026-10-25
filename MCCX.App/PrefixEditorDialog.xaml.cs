using Microsoft.UI.Xaml.Controls;

namespace MCCX_App;

/// <summary>
/// 服务器信息过滤的前缀列表编辑器（2026-10-21 需求）：从参数弹层里输入框右端的小展开图标打开，
/// 多行显示方便看长列表。只负责取文本，保存与下发由 MainPage 完成。
/// <para>
/// 单行输入框装不下换行（多行文本塞进去只显示第一行，用户再改一个字，后面几行就悄悄丢了），
/// 所以保存时把换行合并成逗号：解析器本来就按 换行/逗号/顿号/竖线 切分，语义完全一致，
/// 回到窄框里还能一眼看全。
/// </para>
/// </summary>
public sealed partial class PrefixEditorDialog : ContentDialog
{
    public PrefixEditorDialog()
    {
        InitializeComponent();
    }

    /// <summary>编辑中的文本（打开前赋值，点“保存”后读回；换行已合并成 “, ”）。</summary>
    public string Text
    {
        get => NormalizeLines(Editor.Text);
        set => Editor.Text = value ?? string.Empty;
    }

    /// <summary>换行（含行首尾空白、空行）合并成 “, ”，与过滤器的分隔符一致。</summary>
    private static string NormalizeLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string[] parts = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        var items = new List<string>(parts.Length);
        foreach (string part in parts)
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0)
                items.Add(trimmed);
        }

        return string.Join(", ", items);
    }
}
