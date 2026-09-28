using Microsoft.UI.Xaml.Controls;

namespace MCCX_App;

/// <summary>
/// “添加账号”弹窗：只负责收集与校验输入，业务（入库、开进程、连接）由 MainViewModel 完成。
/// </summary>
public sealed partial class AddAccountDialog : ContentDialog
{
    public AddAccountDialog()
    {
        InitializeComponent();
    }

    public string ServerHost => NewServerBox.Text?.Trim() ?? string.Empty;

    public string Port => NewPortBox.Text?.Trim() ?? string.Empty;

    public string Username => NewUserBox.Text?.Trim() ?? string.Empty;

    public string MinecraftVersion => NewVersionBox.Text?.Trim() ?? string.Empty;

    /// <summary>添加后是否立刻连接（默认勾选）。</summary>
    public bool AutoConnect => NewAutoConnectBox.IsChecked ?? false;

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (ServerHost.Length == 0 || Username.Length == 0)
        {
            NewErrorBox.Text = "服务器地址与游戏名不能为空。";
            args.Cancel = true; // 关不掉弹窗，用户继续填
            return;
        }

        if (Port.Length > 0 && (!ushort.TryParse(Port, out ushort port) || port == 0))
        {
            NewErrorBox.Text = $"端口“{Port}”无效，请输入 1-65535 之间的数字。";
            args.Cancel = true;
            return;
        }

        NewErrorBox.Text = string.Empty;
    }
}
