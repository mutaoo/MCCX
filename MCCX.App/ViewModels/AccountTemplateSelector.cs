namespace MCCX_App.ViewModels;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

/// <summary>
/// 左侧账号列表的模板选择器：列表里既有账号、也有分组表头，按类型给不同模板。
///
/// 为什么需要：WinUI 3 移除了 UWP 的 <c>IGroupable</c>/<c>GroupStyle</c> 分组接口，
/// 想按服务器分类就只能把"组头"也当成列表项，靠这里分流成两种外观。
/// </summary>
public sealed class AccountTemplateSelector : DataTemplateSelector
{
    private readonly DataTemplate? _accountTemplate;
    private readonly DataTemplate? _groupHeaderTemplate;

    public AccountTemplateSelector(DataTemplate? accountTemplate, DataTemplate? groupHeaderTemplate)
    {
        _accountTemplate = accountTemplate;
        _groupHeaderTemplate = groupHeaderTemplate;
    }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        AccountGroupHeader => _groupHeaderTemplate,
        _ => _accountTemplate,
    };
}