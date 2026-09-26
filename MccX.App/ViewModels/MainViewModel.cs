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
    private readonly AccountStore _accountStore = new();

    private string _serverHost = "127.0.0.1";
    private string _serverPort = "25565";
    private string _username = "Player";
    private string _minecraftVersion = "auto";
    private string _commandInput = string.Empty;
    private string _stateText = "未连接";
    private bool _isConnected;
    private AccountProfile? _selectedAccount;

    public MainViewModel(DispatcherQueue dispatcherQueue, MccSession session)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _session = session ?? throw new ArgumentNullException(nameof(session));

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
        SendCommand = new RelayCommand(SendInput, () => IsConnected);
        ClearLogCommand = new RelayCommand(ClearLog);
        SaveAccountCommand = new RelayCommand(SaveCurrentAccount);
        DeleteAccountCommand = new RelayCommand(DeleteSelectedAccount, () => SelectedAccount is not null);

        _session.LogReceived += OnLogReceived;
        _session.StateChanged += OnStateChanged;

        ReloadAccounts();

        AppendLog("§8[MccX] 就绪：填写服务器与用户名后点击“连接”。（离线模式）");
        if (!string.IsNullOrEmpty(_accountStore.LastError))
            AppendLog($"§e账号列表解密失败，已按空列表启动：{_accountStore.LastError}");
    }

    public ObservableCollection<LogEntry> Logs { get; } = [];

    /// <summary>历史账号列表（已解密，按最近使用排序）。</summary>
    public ObservableCollection<AccountProfile> Accounts { get; } = [];

    public AsyncRelayCommand ConnectCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand SendCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    /// <summary>把当前连接参数保存/更新为一个历史账号。</summary>
    public RelayCommand SaveAccountCommand { get; }

    public RelayCommand DeleteAccountCommand { get; }

    /// <summary>
    /// 左侧列表当前选中的账号。选中后自动回填连接参数（不自动连接）。
    /// </summary>
    public AccountProfile? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (!SetProperty(ref _selectedAccount, value))
                return;

            DeleteAccountCommand.RaiseCanExecuteChanged();

            if (value is null)
                return; // 列表重载或点击空白处时不要清空输入框

            ServerHost = value.ServerHost;
            ServerPort = value.Port.ToString();
            Username = value.Username;
            MinecraftVersion = string.IsNullOrWhiteSpace(value.MinecraftVersion) ? "auto" : value.MinecraftVersion;
        }
    }

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
        _accountStore.Dispose();
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

    #region 账号持久化（需求 3.1）

    private void ReloadAccounts(string? selectId = null)
    {
        IReadOnlyList<AccountProfile> list = _accountStore.Load();

        Accounts.Clear();
        foreach (AccountProfile profile in list)
            Accounts.Add(profile);

        SelectedAccount = selectId is null ? null : list.FirstOrDefault(p => p.Id == selectId);
    }

    private void SaveCurrentAccount()
    {
        AccountProfile? candidate = BuildProfileFromInputs();
        if (candidate is null)
            return;

        try
        {
            AccountProfile saved = _accountStore.Upsert(candidate);
            ReloadAccounts(saved.Id);
            AppendLog($"§8已保存账号 {saved.DisplayName}（{saved.ServerSummary}）。");

            if (!string.IsNullOrEmpty(_accountStore.LastError))
                AppendLog($"§c写入账号文件失败：{_accountStore.LastError}");
        }
        catch (Exception ex)
        {
            AppendLog($"§c保存账号失败：{ex.Message}");
        }
    }

    private void DeleteSelectedAccount()
    {
        AccountProfile? target = SelectedAccount;
        if (target is null)
            return;

        try
        {
            _accountStore.Remove(target.Id);
            ReloadAccounts();
            AppendLog($"§8已删除账号 {target.DisplayName}。");
        }
        catch (Exception ex)
        {
            AppendLog($"§c删除账号失败：{ex.Message}");
        }
    }

    /// <summary>连接成功后把当前参数静默入库，实现“历史登录过的账号”自动沉淀。</summary>
    private void AutoSaveConnectedAccount()
    {
        AccountProfile? candidate = BuildProfileFromInputs(silent: true);
        if (candidate is null)
            return;

        try
        {
            AccountProfile saved = _accountStore.Upsert(candidate);
            ReloadAccounts(saved.Id);
        }
        catch (Exception ex)
        {
            AppendLog($"§c自动保存账号失败：{ex.Message}");
        }
    }

    private AccountProfile? BuildProfileFromInputs(bool silent = false)
    {
        string host = ServerHost.Trim();
        string username = Username.Trim();

        if (host.Length == 0 || username.Length == 0)
        {
            if (!silent)
                AppendLog("§c服务器地址与用户名不能为空，无法保存账号。");
            return null;
        }

        if (!ushort.TryParse(ServerPort.Trim(), out ushort port) || port == 0)
        {
            if (!silent)
                AppendLog("§c端口无效，无法保存账号。");
            return null;
        }

        return new AccountProfile
        {
            Username = username,
            ServerHost = host,
            Port = port,
            MinecraftVersion = MinecraftVersion.Trim(),
            DisplayName = username,
            CreatedAt = DateTimeOffset.Now,
            LastUsedAt = DateTimeOffset.Now,
        };
    }

    #endregion

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

            if (state == MccConnectionState.Connected)
                AutoSaveConnectedAccount();
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
