using MinecraftClient.CommandHandler;
using MinecraftClient.Inventory;
using MinecraftClient.Scripting;

namespace MCCX.Core;

/// <summary>
/// 自动指令（2026-10-22 用户需求）：按各自间隔定时把一段文本（<c>/spawn</c> 这类服务器指令、
/// <c>/useitem</c> 这类 MCC 内部命令、或普通聊天）发出去；每条自己决定"进服立即执行"、
/// 执行方式（循环 / 只执行一次）、循环次数与自己那条过程日志开关。
///
/// <para>
/// 发送顺序与命令栏回车完全一致（<c>McClient.HandleCommandPromptText</c>）：
/// 以 <c>/</c> 开头先试 MCC 内部命令（<c>PerformInternalCommand</c>，例如 <c>/useitem</c>）；
/// 不是内部命令才把整行原样交给 <c>Handler.SendText</c> 当服务器指令 / 聊天发。
/// <c>//</c> 开头不进内部命令分支（与命令栏同口径）。
/// </para>
///
/// <para>
/// 生命周期与自动行走同款 <c>_joined</c> 门闩：断线时 MCC 会把本 Bot 经静态 botsOnHold
/// 挂到新连接上（那时还没进服），<c>AfterGameJoined</c> 置真、<c>OnDisconnect</c> 置假，
/// <b>没进服不发</b>。进服后先空等 <see cref="JoinGraceTicks"/>（1 秒）再发首条，
/// 等 MCC 把"可以发消息"的状态真正打开，免得白丢一条。
/// </para>
///
/// <para>
/// 指令表由界面改一次就整份下发（<see cref="SetCommands"/>），这里只存快照：
/// 计数归零重排（编辑中途改间隔不会让某条永远不发），运行中改表也不会有半新半旧的状态。
/// 次数也用同一份快照重建：每次下发 / 每次进服都从"配置的次数"重新算，
/// 所以"只执行一次"就是每进服一次；无限循环永远发。
/// </para>
/// </summary>
public sealed class AutoCommandBot : ChatBot
{
    /// <summary>进服后等多久再执行"进服立即执行"那几条（tick，20 TPS → 20 = 1 秒）。</summary>
    private const int JoinGraceTicks = 20;

    /// <summary>指令条数上限（防止误操作/坏数据把界面撑爆；弹层里也滚得动）。</summary>
    public const int MaxCommands = 100;

    /// <summary>间隔下限（秒）：再短就是刷屏，服务端会按聊天频率踢人。</summary>
    public const double MinIntervalSeconds = 1.0;

    /// <summary>间隔上限（秒）：24 小时。</summary>
    public const double MaxIntervalSeconds = 86_400.0;

    /// <summary>循环次数上限（防止误填天文数字；0 = 无限）。</summary>
    public const int MaxRepeatCount = 1_000_000;

    /// <summary>剩余次数里表示"无限"的哨兵（0 = 已经执行完，&gt;0 = 还剩几次）。</summary>
    private const int InfiniteCount = -1;

    private static readonly AutoCommandDefinition[] Empty = [];

    private readonly object _gate = new();
    private readonly string _accountId;

    /// <summary>当前指令快照（按界面顺序）。</summary>
    private AutoCommandDefinition[] _commands = Empty;

    /// <summary>每条指令离下次执行还有多少 tick；与 <see cref="_commands"/> 等长。</summary>
    private int[] _remainingTicks = [];

    /// <summary>每条指令还剩几次可执行（<see cref="InfiniteCount"/> = 无限，0 = 已执行完）。</summary>
    private int[] _leftCounts = [];

    /// <summary>是否已真正进服（门闩，见类注释）。</summary>
    private bool _joined;

    /// <summary>这次进服的"立即执行"是否已经处理过（重连/重生只做一次）。</summary>
    private bool _joinHandled;

    /// <summary>进服后的宽限 tick 数。</summary>
    private int _joinGrace;

    /// <param name="accountId">账号 Id：多开时每个账号一个子进程，这里只是认领标记。</param>
    public AutoCommandBot(string accountId) => _accountId = accountId ?? string.Empty;

    /// <summary>重连认领用：MCCSession 只认领属于本账号的已挂载实例。</summary>
    public string AccountId => _accountId;

    /// <summary>是否已按"进服"初始化过（门闩状态，供会话层判断要不要补 <see cref="NotifyGameJoined"/>）。</summary>
    public bool IsJoined
    {
        get
        {
            lock (_gate)
                return _joined;
        }
    }

    /// <summary>
    /// 进服之后才被挂上时的补偿（用户先连上、再打开"自动指令"开关）：
    /// MCC 只在<b>进服那一刻</b>回调一次 <see cref="AfterGameJoined"/>，进服后新挂的 Bot 收不到，
    /// 不补这一下就永远 <c>_joined = false</c>，表现为"开关开了但一条都不发，重连一次才好"。
    /// </summary>
    public void NotifyGameJoined() => AfterGameJoined();

    /// <summary>
    /// 整份替换指令表（界面每次改动都会下发）。空文本的条目直接丢掉；
    /// 间隔夹到 <see cref="MinIntervalSeconds"/>~<see cref="MaxIntervalSeconds"/>、次数夹到 0~<see cref="MaxRepeatCount"/>；
    /// 条数截到 <see cref="MaxCommands"/>。
    /// </summary>
    public void SetCommands(IReadOnlyList<AutoCommandDefinition>? commands)
    {
        AutoCommandDefinition[] snapshot = commands is null
            ? Empty
            : [.. commands
                .Where(command => command is not null && !string.IsNullOrWhiteSpace(command.Text))
                .Take(MaxCommands)
                .Select(command => new AutoCommandDefinition
                {
                    Text = command.Text.Trim(),
                    IntervalSeconds = Math.Clamp(command.IntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds),
                    RunOnJoin = command.RunOnJoin,
                    Loop = command.Loop,
                    RepeatCount = Math.Clamp(command.RepeatCount, 0, MaxRepeatCount),
                    LogEnabled = command.LogEnabled,
                })];

        lock (_gate)
        {
            _commands = snapshot;
            _remainingTicks = FillTicks(snapshot);
            _leftCounts = FillCounts(snapshot);
        }
    }

    public override void Initialize()
    {
        int count;
        lock (_gate)
            count = _commands.Length;

        LogToConsole(count == 0
            ? "§7[自动指令] 已就绪：还没有指令，在弹层里点「新建指令」加一条。"
            : $"§7[自动指令] 已就绪：{count} 条指令按各自间隔执行。");
    }

    public override void AfterGameJoined()
    {
        lock (_gate)
        {
            _joined = true;
            _joinHandled = false;
            _joinGrace = JoinGraceTicks;
            _remainingTicks = FillTicks(_commands);
            _leftCounts = FillCounts(_commands);
        }
    }

    public override bool OnDisconnect(ChatBot.DisconnectReason reason, string message)
    {
        lock (_gate)
        {
            _joined = false;
            _joinHandled = false;
            _joinGrace = 0;
            _remainingTicks = FillTicks(_commands);
            _leftCounts = FillCounts(_commands);
        }

        return base.OnDisconnect(reason, message);
    }

    public override void Update()
    {
        List<DueCommand> due = [];

        lock (_gate)
        {
            // 门闩也在锁里读：OnDisconnect 在别的线程置假，锁外读可能拿到旧值
            if (!_joined)
                return;

            AutoCommandDefinition[] commands = _commands;
            if (commands.Length == 0)
                return;

            if (_remainingTicks.Length != commands.Length || _leftCounts.Length != commands.Length)
            {
                _remainingTicks = FillTicks(commands);
                _leftCounts = FillCounts(commands);
            }

            // 进服首轮：先让 MCC 把"可以发消息"打开（1 秒），再执行勾了"进服立即执行"的几条
            if (!_joinHandled)
            {
                if (_joinGrace > 0)
                {
                    _joinGrace--;
                    return;
                }

                _joinHandled = true;
                for (int i = 0; i < commands.Length; i++)
                {
                    if (!commands[i].RunOnJoin || _leftCounts[i] == 0)
                        continue;

                    due.Add(new DueCommand(commands[i], "进服执行", Consume(i)));
                    _remainingTicks[i] = ToTicks(commands[i].IntervalSeconds);
                }
            }

            // 定时轮：每条各自倒计时，到点执行并重置自己的间隔
            for (int i = 0; i < commands.Length; i++)
            {
                if (_leftCounts[i] == 0)
                    continue; // 次数用完：不再排队

                if (--_remainingTicks[i] > 0)
                    continue;

                due.Add(new DueCommand(commands[i], "定时执行", Consume(i)));
                _remainingTicks[i] = ToTicks(commands[i].IntervalSeconds);
            }
        }

        // 发送放在锁外：SendText / PerformInternalCommand 要进 MCC 的队列，别在锁里做
        foreach (DueCommand item in due)
            Send(item.Command, item.Reason, item.Finished);
    }

    /// <summary>扣一次剩余次数；返回 true 表示"这一条执行完就结束了"（只执行一次 / 次数刚好用完）。</summary>
    private bool Consume(int index)
    {
        int left = _leftCounts[index];
        if (left == InfiniteCount)
            return false;

        if (left > 1)
        {
            _leftCounts[index] = left - 1;
            return false;
        }

        _leftCounts[index] = 0;
        return true;
    }

    /// <summary>按 <see cref="_commands"/> 造一份"满间隔"的倒计时数组。</summary>
    private static int[] FillTicks(AutoCommandDefinition[] commands)
    {
        int[] ticks = new int[commands.Length];
        for (int i = 0; i < commands.Length; i++)
            ticks[i] = ToTicks(commands[i].IntervalSeconds);

        return ticks;
    }

    /// <summary>按配置造"剩余次数"数组：循环 + 次数 0 = 无限，循环 + N = N，只执行一次 = 1。</summary>
    private static int[] FillCounts(AutoCommandDefinition[] commands)
    {
        int[] counts = new int[commands.Length];
        for (int i = 0; i < commands.Length; i++)
        {
            counts[i] = !commands[i].Loop
                ? 1
                : commands[i].RepeatCount > 0 ? commands[i].RepeatCount : InfiniteCount;
        }

        return counts;
    }

    /// <summary>秒 → tick（20 TPS，至少 1 tick）。</summary>
    private static int ToTicks(double seconds) => Math.Max(1, (int)Math.Ceiling(seconds * 20.0));

    /// <summary>
    /// 发一条：与命令栏回车同一条路 —— 先试 MCC 内部命令（<c>/useitem</c>、<c>/send</c>…），
    /// 不是内部命令才原样当服务器指令 / 聊天发。过程日志受<b>这一条</b>的开关控制。
    /// 会断连接/退客户端的内部命令（<c>/quit</c> 等）与命令栏一样**直接跳过**，不当服务器指令发。
    /// </summary>
    private void Send(AutoCommandDefinition command, string reason, bool finished)
    {
        string text = command.Text.Trim();
        if (text.Length == 0)
            return;

        string? commandName = IsInternalCommandText(text) ? CommandNameOf(text) : null;
        if (MCCSession.IsBlockedInternalCommand(commandName))
        {
            LogProcess($"§e[自动指令] 已跳过 /{commandName}：这条 MCC 命令会断开连接/退出客户端，界面里不执行。");
            return;
        }

        bool internalCommand = false;
        string? internalNote = null;
        if (commandName == "useitem")
        {
            try
            {
                // /useitem 走专门通路（见 UseItemInHand 注释）；带坐标的返回 false，交回 MCC 处理
                internalCommand = UseItemCommand(text);
            }
            catch (Exception ex)
            {
                LogProcess($"§e[自动指令] 使用物品失败：{ex.Message}");
                internalCommand = true; // 已经认领：别再当服务器指令发出去
            }
        }

        if (commandName is not null && !internalCommand)
        {
            try
            {
                // ChatBot.PerformInternalCommand 收的是去掉前导 / 的命令（与 McClient.HandleCommandPromptText 一致）
                CmdResult result = new();
                internalCommand = PerformInternalCommand(text[1..], ref result);

                // 执行了但没成功（缺背包/地形处理、参数不对…）：把 MCC 的说明补到日志里，
                // 否则界面上只看到"已执行"，实际什么都没发生（2026-10-23 用户报的那类现象）
                if (internalCommand && result.status != CmdResult.Status.Done && result.result is { Length: > 0 } note)
                    internalNote = note;
            }
            catch (Exception ex)
            {
                LogProcess($"§e[自动指令] 执行 MCC 命令失败：{ex.Message}");
            }
        }

        if (!internalCommand)
        {
            try
            {
                SendText(text);
            }
            catch (Exception ex)
            {
                LogProcess($"§e[自动指令] 发送失败：{ex.Message}");
            }
        }

        if (command.LogEnabled)
        {
            LogProcess($"§7[自动指令] {reason}{(internalCommand ? "（MCC 命令）" : string.Empty)}：{text}");
            if (internalNote is not null)
                LogProcess($"§e[自动指令] MCC 返回：{internalNote}");

            if (finished)
                LogProcess($"§7[自动指令] 已执行完设置次数，停止：{text}");
        }
    }

    /// <summary>
    /// 自动指令里的 <c>/useitem</c>（不带坐标）：直接把手上的物品"用"出去。
    /// <para>
    /// MCC 命令栏的 <c>/useitem</c> 在启用地形处理时是"瞄准方块就放方块"
    /// （<c>UseItem.DoUseItem</c> 会先做射线检测再 <c>PlaceBlock</c>）：手上拿的是食物 / 末影珍珠 /
    /// 烟花这类非方块物品时，服务端收到的是"对方块使用"，物品不会被消耗，日志却照样报成功
    /// （2026-10-23 用户实测：日志说执行了，背包里物品还在）。
    /// </para>
    /// <para>
    /// 所以这里与 <c>AutoEat</c> 一样直接发 UseItem 包；带坐标的形式（<c>/useitem x y z</c>）
    /// 仍然交回 MCC —— 那是明确要放方块。
    /// </para>
    /// </summary>
    /// <returns>true 表示已经按"使用物品"处理；false 表示不认识这个写法，交回 MCC。</returns>
    private bool UseItemCommand(string text)
    {
        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // parts[0] = "/useitem"；带参数时只认 mainhand / offhand，其余（坐标等）交回 MCC
        if (parts.Length > 2)
            return false;

        Hand hand = Hand.MainHand;
        if (parts.Length == 2)
        {
            if (parts[1].Equals("offhand", StringComparison.OrdinalIgnoreCase))
                hand = Hand.OffHand;
            else if (!parts[1].Equals("mainhand", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        bool used = hand == Hand.OffHand ? UseItemInLeftHand() : UseItemInHand();

        try
        {
            SendAnimation(hand);
        }
        catch
        {
            // 挥手动画只是观感，发不出去不影响使用
        }

        if (!used)
            LogProcess("§e[自动指令] 使用物品的包没发出去（连接可能已经断开）。");

        return true; // 已经认领这条指令：不要再当服务器指令发出去
    }

    /// <summary>与命令栏同口径：<c>/</c> 开头且不是 <c>//</c> 才算"可能是 MCC 内部命令"。</summary>
    private static bool IsInternalCommandText(string text) =>
        text.Length >= 2 && text[0] == '/' && text[1] != '/';

    /// <summary>取内部命令名（<c>/useitem 1</c> → <c>useitem</c>）。</summary>
    private static string CommandNameOf(string text)
    {
        int end = 1;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
            end++;

        return text[1..end].ToLowerInvariant();
    }

    private readonly record struct DueCommand(AutoCommandDefinition Command, string Reason, bool Finished);
}
