namespace MCCX.Core;

/// <summary>
/// 一个账号的会话抽象：进程内的 <see cref="MCCSession"/>（冒烟测试用），
/// 或多开子进程里的会话在主进程侧的代理 <see cref="Ipc.RunnerProcess"/>（界面用）。
/// 界面层只依赖本接口，换实现时 ViewModel 不用改。
/// </summary>
public interface IAccountSession : IDisposable
{
    /// <summary>当前连接状态（线程安全）。</summary>
    MCCConnectionState State { get; }

    /// <summary>是否已完全进入游戏（配置阶段结束，可发聊天）。</summary>
    bool IsGameJoined { get; }

    /// <summary>MCC 输出的日志行（可能带 § 颜色码）。可能从任意线程触发。</summary>
    event Action<string>? LogReceived;

    /// <summary>连接状态变化。可能从任意线程触发。</summary>
    event Action<MCCConnectionState>? StateChanged;

    /// <summary>服务器完全进入游戏。可能从任意线程触发。</summary>
    event Action? GameJoined;

    /// <summary>连接服务器；返回时状态要么是 Connected，要么已回到 Disconnected。</summary>
    Task ConnectAsync(MCCConnectionOptions options, CancellationToken cancellationToken = default);

    /// <summary>把一行输入（聊天或内部命令）送进会话，返回是否已投递。</summary>
    bool SendInput(string text);

    /// <summary>主动断开；也可用于取消待执行的自动重连。</summary>
    void Disconnect();

    void ConfigureAttack(bool enabled, AttackOptions options);

    void ConfigureMouse(bool enabled, MouseOptions options);

    void ConfigureFishing(bool enabled);

    void ConfigureReconnect(ReconnectOptions options);
}
