using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MCCX_App;

/// <summary>
/// 命令建议框里的一条候选（2026-10-14 需求；2026-10-15 改配色）。
/// 显示为"命中前缀（强调色、加粗）+ 剩余（正文色）"，选中行给主题的次级填充底色。
/// 每次重算/换选中项都重建整份列表，所以 x:Bind 单次绑定（OneTime）也够用。
/// </summary>
public sealed class SuggestionItem
{
    public SuggestionItem(string matched, string rest, bool isSelected)
    {
        Matched = matched;
        Rest = rest;
        RowBrush = isSelected ? SelectedRowBrush() : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    /// <summary>命中当前输入的前缀部分（强调色）。</summary>
    public string Matched { get; }

    /// <summary>候选剩余部分（正文色）。</summary>
    public string Rest { get; }

    /// <summary>在候选列表中的下标（鼠标点选时回填用；渲染方构造后修正）。</summary>
    public int Index { get; set; } = -1;

    /// <summary>
    /// 行底色：选中取主题的"次级半透明填充"（SubtleFillColorSecondaryBrush），浅色/深色下都压得住，
    /// 也比原先写死的 25% 白纱更贴合背景。取不到时退回 12% 黑纱，保证总能看见选中行。
    /// </summary>
    public Brush RowBrush { get; }

    private static Brush SelectedRowBrush()
    {
        if (Application.Current?.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out object? v) == true &&
            v is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0x00, 0x00));
    }
}
