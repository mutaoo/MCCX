using MCCX.Core.Containers;
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
    /// 服务器命令列表（declare_commands / tab-complete 合并结果）更新。
    /// 载荷是根命令名列表（不含前导 '/'），供输入框做命令建议。可能从任意线程触发。
    /// </summary>
    event Action<IReadOnlyList<string>>? ServerCommandsChanged;

    /// <summary>
    /// 服务器打开/刷新了一个容器窗口（箱子、商店 GUI 等）。载荷里 <c>Full</c> 为 true 时是
    /// 全量快照，否则只有变化的格子。可能从任意线程触发。
    /// </summary>
    event Action<MccWindowInfo>? WindowChanged;

    /// <summary>服务器关掉了编号为该值的容器窗口。可能从任意线程触发。</summary>
    event Action<int>? WindowClosed;

    /// <summary>
    /// 玩家血量 / 饥饿值（都是 0-20）更新，2026-10-31 需求：状态区显示血量与饥饿。
    /// <para>
    /// 只在服务器发来 UpdateHealth 包（以及登录后补发的当前值）时触发；没收到过任何数据之前
    /// 界面不应显示这两个数字（0/0 不代表真实状态）。断开后界面自己隐藏。
    /// 可能从任意线程触发。
    /// </para>
    /// </summary>
    event Action<float, int>? VitalsChanged;

    /// <summary>
    /// 自动重连从 SRV 记录解析出与账号里不同的端口（服务器换端口后自动跟随）。
    /// 界面据此更新端口框并写回账号库。可能从任意线程触发。
    /// </summary>
    event Action<ushort>? ServerPortChanged;

    /// <summary>
    /// 点容器窗口里的一个格子（或关掉窗口，<see cref="MccWindowAction.Close"/>）。
    /// 与对话框一样不走聊天/命令通道：这是协议层的窗口点击，不会进日志也不会被当成聊天发出去。
    /// 返回是否已投递。
    /// </summary>
    bool WindowClick(MccWindowClick click);

    /// <summary>
    /// 开 / 收"玩家背包"面板：打开后会话把玩家背包（MCC 的窗口 0：装备 + 背包 + 快捷栏）
    /// 当普通窗口内容推过来（<see cref="WindowChanged"/>，<c>Player</c> 为 true），
    /// 收起后停止推送。重连后由会话自己接着推，界面不用重发。返回是否已投递。
    /// </summary>
    bool WatchPlayerInventory(bool watch);

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
    /// 自动指令（2026-10-22 需求）：总开关 + 整份指令表。每条指令各自带间隔（秒）与
    /// "进服立即执行"；已连接时改表立即生效，不用重连。
    /// </summary>
    void ConfigureAutoCommands(bool enabled, IReadOnlyList<AutoCommandDefinition> commands);

    /// <summary>
    /// 服务器信息过滤（2026-10-04 需求）：全屏蔽 / 只屏蔽玩家消息 / 只开放特定前缀。
    /// 只影响服务器消息进不进日志，不影响 Bot 收到的内容。
    /// <paramref name="showPrefix"/> 是"只显示指定前缀"用的，<paramref name="blockPrefix"/> 是
    /// "只屏蔽指定前缀"用的——<b>两份相互独立</b>（2026-10-05 用户要求：以前共用一个输入框，
    /// 切方式就得出另一份前缀、要反复重填）。
    /// </summary>
    void ConfigureServerFilter(ServerFilterMode mode, string showPrefix, string blockPrefix);

    void ConfigureReconnect(ReconnectOptions options);

    /// <summary>
    /// 某功能的“过程日志”开关（2026-10-20 用户要求：开关控制日志的<b>发出</b>）：
    /// 下发到会话后，对应 Bot 的 <c>ProcessLogEnabled</c> 立即跟着变——关掉后该功能的过程日志
    /// 根本不产生，而不是先发到界面再按文本过滤。状态提示（已开启/已就绪…）不受影响。
    /// </summary>
    void SetFeatureLog(string key, bool enabled);

    /// <summary>视角移动（需求 4）：把视角立刻转向指定方向，一次性生效。没进服时写一行提示。</summary>
    void LookAt(MccLookDirection direction);
}
