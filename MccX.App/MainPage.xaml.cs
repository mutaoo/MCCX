using System.Collections.Specialized;
using MccX.Core;
using MccX_App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace MccX_App;

/// <summary>
/// 主界面。仅负责控件交互（回车发送、日志滚动），业务逻辑都在 ViewModel 里。
/// </summary>
public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    public MainPage()
    {
        // x:Bind 在加载前读取 ViewModel，必须先于 InitializeComponent 赋值
        ViewModel = new MainViewModel(DispatcherQueue, new MccSession());
        InitializeComponent();

        ViewModel.Logs.CollectionChanged += OnLogsCollectionChanged;
    }

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (LogList.Items.Count == 0)
            return;

        LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    private void CommandBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        if (ViewModel.SendCommand.CanExecute(null))
            ViewModel.SendCommand.Execute(null);

        e.Handled = true;
    }
}
