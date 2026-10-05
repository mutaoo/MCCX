using MCCX.Core.Dialogs;

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

    /// <summary>
    /// 服务器弹出对话框（MCC 的 Dialog 系统）。可能从任意线程触发。
    /// 界面据此弹输入框收集取值，再用 <see cref="SubmitDialog"/> 交回。
    /// </summary>
    event Action<MccDialogInfo>? DialogRequested;

    /// <summary>服务器关闭了编号为该值的对话框。可能从任意线程触发。</summary>
    event Action<int>? DialogClosed;

    /// <summary>
    /// 把界面填好的对话框取值写回并点击指定动作（编号从 1 开始）。
    /// 不走聊天/命令通道：密码不进日志、不会被当成公屏消息发出去。返回是否成功。
    /// </summary>
    bool SubmitDialog(IReadOnlyDictionary<string, string> values, int actionIndex);

    /// <summary>
    /// 取消服务器弹出的对话框（没有进行中的对话框时返回 false 且不打日志）。返回是否成功。
    /// 取消成功后 60 秒内的那次断开不自动重连（点服务器的“取消”类动作同理），否则服务器
    /// 踢人会触发“取消 → 断开 → 自动重连 → 又弹框”的死循环；点“连接”可恢复正常。
    /// </summary>
    bool CancelDialog();

    /// <summary>连接服务器；返回时状态要么是 Connected，要么已回到 Disconnected。</summary>
    Task ConnectAsync(MCCConnectionOptions options, CancellationToken cancellationToken = default);

    /// <summary>把一行输入（聊天或内部命令）送进会话，返回是否已投递。</summary>
    bool SendInput(string text);

    /// <summary>主动断开；也可用于取消待执行的自动重连。</summary>
    void Disconnect();

    void ConfigureAttack(bool enabled, AttackOptions options);

    void ConfigureMouse(bool enabled, MouseOptions options);

    /// <summary>自动钓鱼：开关 + 收杆检测/抛竿超时/重抛间隔参数（默认值 = MCC 配置默认值）。</summary>
    void ConfigureFishing(bool enabled, FishingOptions options);

    /// <summary>自动补充（2026-10-03 第五批需求）：手持格用完时自动从背包补同款。</summary>
    void ConfigureAutoRefill(bool enabled);

    /// <summary>自动行走（2026-10-04 需求）：一直朝当前朝向前进，没有参数，只有开关。</summary>
    void ConfigureWalk(bool enabled);

    /// <summary>
    /// 服务器信息过滤（2026-10-04 需求）：全屏蔽 / 只屏蔽玩家消息 / 只开放特定前缀。
    /// 只影响服务器消息进不进日志，不影响 Bot 收到的内容。
    /// <paramref name="showPrefix"/> 是"只显示指定前缀"用的，<paramref name="blockPrefix"/> 是
    /// "只屏蔽指定前缀"用的——<b>两份相互独立</b>（2026-10-05 用户要求：以前共用一个输入框，
    /// 切方式就得出另一份前缀、要反复重填）。
    /// </summary>
    void ConfigureServerFilter(ServerFilterMode mode, string showPrefix, string blockPrefix);

    void ConfigureReconnect(ReconnectOptions options);

    /// <summary>视角移动（需求 4）：把视角立刻转向指定方向，一次性生效。没进服时写一行提示。</summary>
    void LookAt(MccLookDirection direction);
}
