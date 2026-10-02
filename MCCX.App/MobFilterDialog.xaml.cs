using MCCX_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MCCX_App;

/// <summary>
/// “生物过滤”弹窗：和“添加账号”一样的模态窗口，入口在“砍怪参数”里。
/// 窗口只管展示与展开/收回，勾选状态直接写在选中账号的 ViewModel 上（立即下发并随账号落盘）。
/// </summary>
public sealed partial class MobFilterDialog : ContentDialog
{
    /// <summary>绑定源：主面板的 ViewModel（它代理当前选中账号的过滤参数）。</summary>
    public MainViewModel VM { get; }

    public MobFilterDialog(MainViewModel viewModel)
    {
        VM = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        // x:Bind 在加载前读取 VM，必须先于 InitializeComponent 赋值
        InitializeComponent();
    }

    /// <summary>
    /// 分类右侧的展开/收回按钮：切换该分类生物列表的显示，箭头跟着换方向。
    /// 列表收起时那一片生物会整个移出 UI 树（页面里也就找不到了），窗口高度跟着缩。
    /// </summary>
    private void CategoryExpand_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button)
            return;

        bool expanded = button.IsChecked == true;
        button.Content = expanded ? "▾" : "▸";

        FrameworkElement? list = (button.Tag as string) switch
        {
            "hostile" => HostileList,
            "neutral" => NeutralList,
            "friendly" => FriendlyList,
            _ => null,
        };

        // XAML 里给 IsChecked="True" 时事件可能早于控件字段生成，判空跳过即可
        if (list is not null)
            list.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
    }
}
