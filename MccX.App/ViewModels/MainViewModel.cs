using System.Collections.ObjectModel;
using System.Windows.Input;
using MccX.Core;
using MinecraftClient.Scripting;
using Microsoft.UI.Dispatching;

namespace MccX_App.ViewModels;

/// <summary>
/// 主界面 ViewModel：连接参数、日志列表、命令输入。
/// MCC 线程产生的事件统一经 DispatcherQueue 切回 UI 线程再改绑定数据。
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>日志最大保留行数，超出后按批裁剪，避免长时间挂机内存无限增长。</summary>
    private const int MaxLogLines = 1000;

    private const int LogTrimBatch = 100;

    private readonly MccSession _session;
    private readonly DispatcherQueue _dispatcherQueue;

    private string _serverHost = "127.0.0.1";
    private string _serverPort = "25565";
    private string _username = "Player";
    private string _minecraftVersion = "auto";
    private string _commandInput = string.Empty;
    private string _stateText = "未连接";
    private bool _isConnected;

    public MainViewModel(DispatcherQueue dispatcherQueue, MccSession session)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _session = session ?? throw new ArgumentNullException(nameof(session));

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
        SendCommand = new RelayCommand(SendInput, () => IsConnected);
        ClearLogCommand = new RelayCommand(ClearLog);

        _session.LogReceived += OnLogReceived;
        _session.StateChanged += OnStateChanged;

        AppendLog("§8[MccX] 就绪：填写服务器与用户名后点击“连接”。（离线模式）");
    }

    public ObservableCollection<LogEntry> Logs { get; } = [];

    public AsyncRelayCommand ConnectCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand SendCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    public string ServerHost
    {
        get => _serverHost;
        set => SetProperty(ref _serverHost, value);
    }

    public string ServerPort
    {
        get => _serverPort;
        set => SetProperty(ref _serverPort, value);
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    /// <summary>Minecraft 版本，auto 表示自动 Ping 探测。</summary>
    public string MinecraftVersion
    {
        get => _minecraftVersion;
        set => SetProperty(ref _minecraftVersion, value);
    }

    public string CommandInput
    {
        get => _commandInput;
        set => SetProperty(ref _commandInput, value);
    }

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
                SendCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public void Dispose()
    {
        _session.LogReceived -= OnLogReceived;
        _session.StateChanged -= OnStateChanged;
        _session.Dispose();
    }

    private async Task ConnectAsync()
    {
        if (!ushort.TryParse(ServerPort.Trim(), out ushort port) || port == 0)
        {
            AppendLog("§c端口无效，请输入 1-65535 之间的数字。");
            return;
        }

        try
        {
            await _session.ConnectAsync(new MccConnectionOptions
            {
                ServerHost = ServerHost,
                Port = port,
                Username = Username,
                MinecraftVersion = MinecraftVersion,
            });
        }
        catch (Exception ex)
        {
            AppendLog($"§c连接出错：{ex.Message}");
        }
    }

    private void Disconnect() => _session.Disconnect();

    private void SendInput()
    {
        string text = CommandInput;
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (!_session.SendInput(text))
            return;

        AppendLog($"§7> {text.Trim()}");
        CommandInput = string.Empty;
    }

    private void ClearLog() => Logs.Clear();

    private void OnLogReceived(string rawText)
    {
        // MCC 线程 → UI 线程
        _dispatcherQueue.TryEnqueue(() => AppendLog(rawText));
    }

    private void OnStateChanged(MccConnectionState state)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            IsConnected = state == MccConnectionState.Connected;
            StateText = state switch
            {
                MccConnectionState.Connecting => "连接中…",
                MccConnectionState.Connected => $"已连接 {ServerHost}:{ServerPort}",
                MccConnectionState.Disconnecting => "断开中…",
                _ => "未连接",
            };
        });
    }

    private void AppendLog(string rawText)
    {
        foreach (string line in rawText.Replace("\r\n", "\n").Split('\n'))
        {
            string text = ChatBot.GetVerbatim(line);
            Logs.Add(new LogEntry(text, line));
        }

        while (Logs.Count > MaxLogLines + LogTrimBatch)
            Logs.RemoveAt(0);
    }
}
