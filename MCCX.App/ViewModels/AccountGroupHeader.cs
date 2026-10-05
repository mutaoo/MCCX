namespace MCCX_App.ViewModels;

/// <summary>
/// 左侧账号列表的一个"服务器分类"表头（2026-10-05 用户要求：把同一个服务器的账号移到一个分类里）。
///
/// WinUI 3 没有 UWP 的 <c>IGroupable</c> 分组接口，所以分组是"把表头也当成一项塞进列表"，
/// 由 <c>AccountTemplateSelector</c> 按类型选不同模板渲染成组头。
///
/// 继承 <see cref="ObservableObject"/>：组头要显示服务器地址与账号个数，
/// XAML 里是 OneWay 绑定，类型得能报属性变化通知（否则编译器报 WMC1506）。
/// </summary>
public sealed class AccountGroupHeader : ObservableObject
{
    /// <summary>组标题：服务器地址（域名或 IP，端口不参与判断）。</summary>
    public string Title { get; }

    /// <summary>组内账号数（显示成"3 个账号"，组头一眼能看出多少）。</summary>
    public int Count { get; }

    public AccountGroupHeader(string title, int count)
    {
        Title = title;
        Count = count;
    }
}