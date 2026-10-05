using MinecraftClient.Scripting;

namespace MCCX.Core;

/// <summary>
/// 视角控制 Bot（原「视角恢复/记录」；2026-10-04 用户拍板：移除进服恢复开关，
/// 进服视角体验与 MCC 完全一致）。
///
/// <list type="bullet">
/// <item><b>进服沿用服务器记住的朝向</b>：服务器在玩家数据（playerdata）里记住上次退出时的朝向，
/// 登录时原样发回来、MCC 照单应用；MCCX 不本地存视角、不在进服时推别的朝向，
/// 和原版客户端退出再进的体验一致。</item>
/// <item><b>进服视角守卫（10 秒窗口）</b>：现场抓包证实登录之后服务器还会再推一个
/// <c>PlayerPositionAndLook</c>、yaw 常是 0（正南），于是"刚进服还沿用上次朝向、几秒后变正南"；
/// 那个包拦不住（协议收包路径在只读的 MCCSource 里），所以只在进服
/// <see cref="GuardTicks"/> tick 内把登录包的朝向钉住，被纠正就改回来。
/// 锚点认的是"窗口内第一个<b>非正南</b>朝向"（登录包几乎总是非零、纠正包才是 0）——
/// 2026-10-05 现场踩过：按"与进服那一拍不同"认锚点，会被紧随其后的纠正包抢走，
/// 反而把"视角变正南"钉成基准。窗口过后完全放手；用户点按钮/自动行走都视为"有意为之"，不改。</item>
/// <item><b>视角移动（需求 4）</b>：界面点"向东/抬头看天"等按钮 → 经 IPC 到这边 →
/// 下一个 tick 转向，随后 <see cref="OverrideHoldTicks"/> 内每 tick 保持。
/// 改视角的写手不止我们：自动行走的 SetInputToward 每 tick 直压 playerYaw，
/// 行走路点头跟随（MoveHeadWhileWalking）还会把路点方位写进 _yaw 发给服务端
///（后者已在 <c>MCCRuntime</c> 关闭，这是"视角被行走自动改变"的根子）；
/// 一次性写打不过每 tick 的写手，现场表现为"点了按钮视角纹丝不动"，
/// 2026-10-04 按保持期模型重新实现：保持期内每 tick 重写目标视角，
/// 配合 AutoWalkBot 的方向锚点掐断在途路径、按锚点重规划。</item>
/// </list>
///
/// 生命周期：Bot 连上就挂着（SyncView 挂载/认领，没有开关）；没进服
/// （<see cref="_joined"/> 为假）不读、不发，防止在登录阶段发包、防止 handler 未就绪时抛 NRE。
/// MCC 断线重连时实例经静态 botsOnHold 挂进新连接，由 SyncView 认领同一个实例。
/// </summary>
internal sealed class ViewControlBot : ChatBot
{
    /// <summary>视角移动的保持期（tick，20 TPS → 40 = 2 秒）。见类注释里的"保持期模型"。</summary>
    private const int OverrideHoldTicks = 40;

    /// <summary>
    /// 进服后延迟多少 tick 报客户端朝向（20 TPS → 5 = 0.25 秒）。
    /// 取 5 而不是 0：登录位置包在 JoinGame 之后才到、且要等 McClient 物理块把它写进
    /// playerYaw，太早读只会读到默认 0（正南），那样的日志没有诊断价值。
    /// </summary>
    private const int JoinReportDelayTicks = 5;

    /// <summary>
    /// 进服视角守卫窗口（tick，20 TPS → 200 = 10 秒）。
    /// 只在进服后这段时间内生效：现场实测服务器会在登录后又推一个
    /// <c>PlayerPositionAndLook</c>，其 yaw 常是 0（正南）——领地/反作弊类插件重定向，
    /// 于是"刚进服沿用上次朝向、几秒后变正南"。协议收包路径不可配置（<c>MCCSource</c> 只读），
    /// 拦不住这个包，只能有限度地改回来。
    /// </summary>
    private const int GuardTicks = 200;

    /// <summary>
    /// 守卫锚点的落地延迟（tick）：采样从第 0 tick 就开始（见 <c>_guardFirstYaw</c>），
    /// 但"钉住"的判定要等登录位置包落地后再开始，否则会把还没到货的默认 0 当成锚点。
    /// 必须<b>晚于</b> <see cref="SimulateCorrectionTicks"/>——测试钩子要能在锚点判定之前
    /// 把朝向打成 0，好盯住"纠正包抢走锚点"这个现场时序。
    /// </summary>
    private const int GuardAnchorDelayTicks = 8;

    /// <summary>
    /// 测试钩子从第几个 tick 开始按（仅 <c>MCCX_TEST_VIEW_CORRECTION=1</c>）。
    /// 取 6：晚于 <see cref="JoinReportDelayTicks"/>（5，否则会污染"进服后客户端朝向"那行诊断，
    /// A7 就没法拿它和服务端 Rotation 对账），早于锚点判定。
    /// </summary>
    private const int SimulateCorrectionTicks = 6;

    /// <summary>
    /// 测试钩子连按多少拍。必须连按而不是按一拍拍：本地测试服登录后<b>自己还会再同步一次旋转</b>，
    /// 一拍就被覆盖掉了（客户端 yaw 立刻回到服务器的值，守卫自然看不到偏离）。
    /// 连按能同时钉住两件事——锚点必须取登录包的朝向（不能被按成 0 的这一拍抢走），
    /// 以及锚定之后守卫要把 0 钉回去。
    /// </summary>
    private const int SimulateHoldTicks = 8;

    private readonly string _accountId;

    /// <summary>保持期剩余 tick 与保持的视角（只在 MCC 主线程 Update 里读写；LookTo 不碰）。</summary>
    private int _overrideTicks;
    private float _overrideYaw;
    private float _overridePitch;

    /// <summary>发送失败的日志去重：只提示一次，成功后重新武装。</summary>
    private bool _sendErrorLogged;

    /// <summary>界面点下的"视角移动"：等下一个 tick 在 MCC 主线程上执行；未进服时先排队。</summary>
    private MccLookDirection? _pendingLook;

    /// <summary>
    /// 是否已进服：AfterGameJoined 置真、OnDisconnect 置假。
    /// 为假时（重连/登录阶段）完全不动作：不发包、不读朝向。
    /// </summary>
    private bool _joined;

    /// <summary>
    /// 进服后延迟 <see cref="JoinReportDelayTicks"/> tick 再报一次客户端朝向（诊断"进服朝南"现场：
    /// 能区分"服务器/玩家数据本来就是南"与"落地后被谁改成南"）。-1 = 本次连接已报过或不报。
    /// </summary>
    private int _joinReportTicks = -1;

    /// <summary>
    /// 进服视角守卫的剩余 tick（&lt;= 0 = 不守）。锚点见 <see cref="_guardYaw"/>。
    /// </summary>
    private int _guardTicks;

    /// <summary>锚点落地延迟倒计时（<see cref="GuardAnchorDelayTicks"/>）。</summary>
    private int _guardAnchorDelay;

    /// <summary>
    /// 进服那一拍（<c>AfterGameJoined</c>）读到的朝向。实测这个服上登录位置包在
    /// <c>AfterGameJoined</c> <b>之前</b>就落地了，所以这里往往已经就是登录包带来的朝向。
    /// </summary>
    private float _joinYaw;

    /// <summary>与 <see cref="_joinYaw"/> 同一拍的俯仰。</summary>
    private float _joinPitch;

    /// <summary>
    /// 进服窗口内采到的<b>第一个非正南朝向</b>及其俯仰，也就是服务器登录包带来的那个朝向。
    ///
    /// 为什么不能靠"与进服那一拍不同"来认锚点（2026-10-05 踩过的坑）：那个服上
    /// 服务器在登录后几百毫秒还会再推一个 yaw=0 的纠正包，若纠正包恰好是第一个变化，
    /// 就会被当成锚点，把"视角变正南"当成基准钉死——现场日志正是
    /// <c>守卫锚定 yaw 0</c>。而登录包几乎总是带非零朝向、纠正包才是 0，
    /// 所以"第一个非零"才是可靠的识别依据。
    /// <see cref="float.NaN"/> = 窗口内还没出现过非正南朝向。
    /// </summary>
    private float _guardFirstYaw = float.NaN;

    /// <summary>与 <see cref="_guardFirstYaw"/> 同一拍的俯仰。</summary>
    private float _guardFirstPitch;

    /// <summary>
    /// 守卫锚点（纯内存，不落盘）：服务器登录包带来的朝向。进服 <see cref="GuardTicks"/> tick 内，
    /// 没有用户点击、也没有自动行走时，若客户端朝向偏离它，就认为是被服务器/插件纠正包改走的，改回锚点。
    /// <see cref="float.NaN"/> = 还没锚定。
    /// </summary>
    private float _guardYaw = float.NaN;

    /// <summary>锚点对应的俯仰（与 <see cref="_guardYaw"/> 一起恢复）。</summary>
    private float _guardPitch;

    /// <summary>
    /// 本次连接已钉回多少次。用来在窗口结束时回答"服务器只是推一次，还是一直在纠正"——
    /// 后者说明 <see cref="GuardTicks"/> 可能不够长。
    /// </summary>
    private int _guardPinCount;

    /// <summary>窗口结束小结是否已打过（每次连接只打一次）。</summary>
    private bool _guardSummaryLogged;

    /// <summary>
    /// 测试钩子倒计时（仅 <c>MCCX_TEST_VIEW_CORRECTION=1</c> 时启用，否则恒为 0）：
    /// 锚定之后主动把朝向改成 0（正南）一次（<b>全进程只做一次</b>，见 <see cref="s_simulateDone"/>），
    /// 模拟真实服务器登录后再推的纠正包，好让 e2e 能断言"守卫确实会钉回来"。正常运行时这条路径完全不走。
    /// </summary>
    private int _simulateTicks;

    /// <summary>测试钩子还要按多少拍（见 <see cref="SimulateHoldTicks"/>）。</summary>
    private int _simulateHold;

    /// <summary>本次连接是否已开始那次模拟（每次连接只做一次）。</summary>
    private bool _simulated;

    /// <summary>
    /// 测试钩子的<b>全进程一次</b>闸门：<c>AfterGameJoined</c> 每连接（以及重连）都会触发，
    /// 若按连接重复模拟，e2e 后面的小节（点方向按钮、行走）会被那包 0|0 打断。
    /// </summary>
    private static bool s_simulateDone;

    public ViewControlBot(string accountId)
    {
        _accountId = accountId ?? string.Empty;
    }

    public override void Initialize() => AnnounceStatus();

    /// <summary>
    /// 把当前状态写进日志：首次挂载由 <see cref="Initialize"/> 触发；
    /// 重连时 SyncView 认领 botsOnHold 恢复回来的旧实例（不会再走 Initialize），
    /// 由那边显式调一次，保证每次进服都能在日志里看到。
    /// </summary>
    public void AnnounceStatus()
    {
        LogToConsole($"§8[视角] 进服沿用服务器记住的朝向（与 MCC 一致）；进服头 {GuardTicks / 20} 秒内若被服务器纠正包改走会自动钉回；「视角移动」按钮照常可用。");
    }

    /// <summary>账号 id：SyncView 认领旧实例时用来确认是同一账号的 Bot。</summary>
    internal string AccountId => _accountId;

    /// <summary>
    /// 视角移动（需求 4）：记下来，下一个 tick 执行（执行即进入保持期，见 <c>OverrideHoldTicks</c>）。
    /// <see cref="Update"/> 每 tick 跑在 MCC 主线程上，这样发包才安全；没进服时先排队，进服后立即生效。
    /// </summary>
    public void LookTo(MccLookDirection direction) => _pendingLook = direction;

    /// <summary>登录/重生/换维度都会走到这里：开闸，视角完全跟随服务器发来的朝向（不干预）。</summary>
    public override void AfterGameJoined()
    {
        _joined = true;
        _overrideTicks = 0; // 新连接不继承上个连接的转向保持期
        _joinReportTicks = JoinReportDelayTicks; // 延迟几 tick 报一次进服朝向（见字段注释）

        // 进服视角守卫：给 GuardTicks 的窗口把登录包带来的朝向钉住（见 RunViewGuard）
        _guardTicks = GuardTicks;
        _guardAnchorDelay = GuardAnchorDelayTicks;
        _guardYaw = float.NaN;
        _guardFirstYaw = float.NaN;
        _guardFirstPitch = 0f;
        _guardPinCount = 0;
        _guardSummaryLogged = false;
        _joinYaw = GetYaw(); // GetYaw 只读 playerYaw，登录阶段也安全
        _joinPitch = GetPitch();
        _simulateTicks = Environment.GetEnvironmentVariable("MCCX_TEST_VIEW_CORRECTION") == "1" ? SimulateCorrectionTicks : 0;
        _simulateHold = 0;
        _simulated = false;
    }

    /// <summary>
    /// 断线/退出：清掉在途状态，<b>不在本地保存视角</b>（2026-10-04 起）。
    /// 下次进服的朝向由服务器在玩家数据里记住，与 MCC 行为一致。
    /// </summary>
    public override bool OnDisconnect(ChatBot.DisconnectReason reason, string message)
    {
        if (_joined)
        {
            _joined = false;
            _pendingLook = null;
            _overrideTicks = 0;
        }

        return base.OnDisconnect(reason, message);
    }

    public override void Update()
    {
        // 未进服（重连 / 登录阶段）：不读、不发。
        if (!_joined)
            return;

        // 进服朝向报告（只报一次，见 _joinReportTicks）：现场再遇到"进服朝南"时，
        // 这行能直接回答"服务器发来的就是南"还是"发来之后被改成了南"，不必再插桩重跑。
        if (_joinReportTicks >= 0 && --_joinReportTicks < 0)
            LogToConsole($"§8[视角] 进服后客户端朝向 yaw {GetYaw():0.#}（服务器记住的朝向；此后 {GuardTicks / 20} 秒内若被纠正包改走会自动钉回）。");

        // 0) 界面点的"视角移动"：落地并进入保持期
        if (_pendingLook is { } wanted)
        {
            _pendingLook = null;
            ApplyLook(wanted);
        }

        // 1) 转向保持期：每 tick 把目标视角重写一遍，压过其余每 tick 写手
        //（自动行走的 SetInputToward 跑在物理块里、排在本步之后，会直压 playerYaw；
        // MoveHeadWhileWalking 已在 MCCRuntime 关闭，服务端朝向的写手只剩这里）。
        if (_overrideTicks > 0)
        {
            TrySetView(_overrideYaw, _overridePitch);
            _overrideTicks--;
        }

        // 2) 进服视角守卫：窗口内把登录包带来的朝向钉住，服务器纠正包改走就改回来（见 RunViewGuard）
        RunViewGuard();
    }

    /// <summary>
    /// 进服视角守卫：进服 <see cref="GuardTicks"/> tick 内，把服务器<b>登录包</b>带来的朝向钉住；
    /// 若朝向在"用户没点视角按钮、自动行走也没在跑"的情况下偏离了锚点，就改回锚点。
    ///
    /// 为什么需要：现场抓包证实登录之后服务器还会再推一个 <c>PlayerPositionAndLook</c>（yaw 常为 0
    /// = 正南，领地/反作弊类插件重定向），MCC 忠实应用并回发，服务端记录的朝向就变成正南了。
    /// 协议收包路径在 <c>MCCSource</c>（只读）里、不可配置，拦不住这个包，只能有限度地改回来。
    ///
    /// 三条约束，避免守卫变成"跟服务器打架"：
    /// <list type="number">
    /// <item><b>只守进服窗口</b>：<see cref="GuardTicks"/> tick 之后完全放手。</item>
    /// <item><b>只守"没人要求过的改动"</b>：自动行走每 tick 用路点方位压朝向、用户点按钮都会
    /// <see cref="ApplyLook"/> 重设锚点，两者都不算被纠正。</item>
    /// <item><b>锚点必须来自服务器</b>：不落盘、不本地存视角，锚点就是登录包那个朝向本身
    /// （认法见 <see cref="_guardFirstYaw"/>）。</item>
    /// </list>
    /// </summary>
    private void RunViewGuard()
    {
        if (_guardTicks <= 0)
        {
            // 窗口结束小结：只钉回一次说明服务器就推了一包；反复钉回说明它在持续纠正，
            // 这时 GuardTicks 可能不够长（要调的话改这里即可）。
            if (_guardPinCount > 0 && !_guardSummaryLogged)
            {
                _guardSummaryLogged = true;
                LogToConsole(_guardPinCount == 1
                    ? $"§8[视角] 守卫窗口结束：期间钉回 1 次（服务器只纠正了一次，视角保持 yaw {_guardYaw:0.#}）。"
                    : $"§8[视角] 守卫窗口结束：期间钉回 {_guardPinCount} 次（服务器在持续纠正；若之后视角又变南，说明 {GuardTicks / 20} 秒窗口不够长）。");
            }

            return;
        }

        _guardTicks--;
        float yaw = GetYaw();

        // 认锚点：窗口内"第一个非正南朝向"就是服务器登录包带来的朝向。
        // 每 tick 都采，且先于锚点判定——登录包与纠正包的先后是不确定的
        // （有的服登录包在 AfterGameJoined 前就落地，有的在之后几百毫秒）。
        if (float.IsNaN(_guardFirstYaw) && Math.Abs(yaw) > 0.01f)
        {
            _guardFirstYaw = yaw;
            _guardFirstPitch = GetPitch();
        }

        // 测试钩子（仅 MCCX_TEST_VIEW_CORRECTION=1）：从 SimulateCorrectionTicks 拍起<b>连按</b>
        // SimulateHoldTicks 拍，每拍把朝向按成 0（正南），模拟"服务器持续纠正"。
        // 起点在锚点判定之前——正是 2026-10-05 现场把锚点抢走的那个时序。
        if (_simulateTicks > 0 && --_simulateTicks <= 0 && !_simulated && !s_simulateDone)
        {
            _simulated = true;
            s_simulateDone = true;
            _simulateHold = SimulateHoldTicks;
            LogToConsole("§8[视角-test] 开始模拟服务器纠正包（连续把朝向按成 yaw 0 正南），等守卫钉回。");
        }

        if (_simulateHold > 0)
        {
            _simulateHold--;
            TrySetView(0f, 0f);
        }

        // 锚点落地：优先用采到的"第一个非正南朝向"；没采到再看进服那一拍
        // （登录包在 AfterGameJoined 前落地的服就是这种）。两个都是 0 说明服务器记的就是正南，
        // 没有可守的东西，窗口走完即止。
        if (float.IsNaN(_guardYaw))
        {
            if (_guardAnchorDelay > 0)
            {
                _guardAnchorDelay--;
                return;
            }

            bool fromSample = !float.IsNaN(_guardFirstYaw);
            float candidate = fromSample ? _guardFirstYaw : _joinYaw;
            if (Math.Abs(candidate) < 0.01f)
                return;

            _guardYaw = candidate;
            _guardPitch = fromSample ? _guardFirstPitch : _joinPitch;
            LogToConsole($"§8[视角] 守卫锚定 yaw {_guardYaw:0.#}（服务器记住的朝向；{GuardTicks / 20} 秒窗口内被改走会自动钉回，除非你自己点了视角按钮或开了自动行走）。");
            return;
        }

        // 自动行走每 tick 用路点方位压朝向（SetInputToward），那是有意为之，守卫不介入。
        if (_overrideTicks > 0 || GetLoadedChatBots().OfType<AutoWalkBot>().Any())
            return;

        if (Math.Abs(yaw - _guardYaw) < 0.01f)
            return;

        // 偏离锚点且没人要求过 → 服务器/插件的纠正包，改回登录时的朝向。
        // 改回后如果服务器再推一次，本 tick 之后的采样又会看到偏离并再次改回（有窗口上限兜底）。
        if (TrySetView(_guardYaw, _guardPitch))
        {
            _guardPinCount++;
            if (_guardPinCount == 1)
                LogToConsole($"§8[视角] 进服 {GuardTicks / 20} 秒内朝向被改到 yaw {yaw:0.#}（非按钮、非自动行走所致），已钉回 yaw {_guardYaw:0.#}。");
        }
    }

    /// <summary>
    /// 真正转向：<b>水平方向用固定 yaw</b>（MC 约定 0=南、90=西、180=北、270=东），
    /// <b>抬头/低头保留当前朝向</b>只改俯仰（-90=正上方，90=正下方）。
    /// 发一次包后进入保持期（<see cref="OverrideHoldTicks"/>）：后续 tick 每次重写，
    /// 一次性写打不过自动行走这些每 tick 的写手。
    /// </summary>
    private void ApplyLook(MccLookDirection direction)
    {
        float yaw;
        float pitch;

        switch (direction)
        {
            case MccLookDirection.East:
                yaw = 270f;
                pitch = 0f;
                break;
            case MccLookDirection.South:
                yaw = 0f;
                pitch = 0f;
                break;
            case MccLookDirection.West:
                yaw = 90f;
                pitch = 0f;
                break;
            case MccLookDirection.North:
                yaw = 180f;
                pitch = 0f;
                break;
            case MccLookDirection.Up:
                yaw = GetYaw();
                pitch = -90f;
                break;
            case MccLookDirection.Down:
                yaw = GetYaw();
                pitch = 90f;
                break;
            default:
                return;
        }

        // 启动保持期（见 OverrideHoldTicks）：先存目标值，本 tick 发一次、后续每 tick 重发
        _overrideYaw = yaw;
        _overridePitch = pitch;
        _overrideTicks = OverrideHoldTicks;

        // 守卫窗口内：把锚点挪到用户刚选的方向，窗口剩下的时间里连服务器纠正包也不改它
        //（不挪的话保持期一结束，守卫会把用户刚点的方向当成"被纠正"而钉回旧锚点）。
        if (_guardTicks > 0)
        {
            _guardYaw = yaw;
            _guardPitch = pitch;
            _guardAnchorDelay = 0;
        }

        bool sent = TrySetView(yaw, pitch);

        // "当前 yaw" = 点击当拍的客户端朝向：诊断用（被行走压住的旧朝向会在这里现形）
        LogToConsole(sent
            ? $"§8[视角] 已转向{LabelOf(direction)}（yaw {yaw:0.#}，pitch {pitch:0.#}，当前 yaw {GetYaw():0.#}）。"
            : $"§e[视角] 转向{LabelOf(direction)}未送达（连接未就绪），保持期内会自动重试。");
    }

    private static string LabelOf(MccLookDirection direction) => direction switch
    {
        MccLookDirection.East => "东",
        MccLookDirection.South => "南",
        MccLookDirection.West => "西",
        MccLookDirection.North => "北",
        MccLookDirection.Up => "天上（抬头）",
        MccLookDirection.Down => "脚下（低头）",
        _ => direction.ToString(),
    };

    /// <summary>
    /// 设置视角并把包真正发出去，返回是否送达。
    /// 相比 <see cref="ChatBot.LookAtLocation"/>：能拿到 <see cref="McClient.SendLocationUpdate"/> 的
    /// bool 结果（未送达能知道），并且异常只在这里吞掉、按次去重提示，不给 MCC 的
    /// OnUpdate 异常兜底刷屏的机会。
    /// </summary>
    private bool TrySetView(float yaw, float pitch)
    {
        try
        {
            Handler.UpdateLocation(Handler.GetCurrentLocation(), yaw, pitch);
            bool sent = Handler.SendLocationUpdate();

            if (sent)
            {
                _sendErrorLogged = false;
                return true;
            }

            if (!_sendErrorLogged)
            {
                _sendErrorLogged = true;
                LogToConsole("§e[视角] 视角包本次未送达（连接尚未就绪），保持期内会自动重试，不再重复提示。");
            }

            return false;
        }
        catch (Exception e)
        {
            if (!_sendErrorLogged)
            {
                _sendErrorLogged = true;
                LogToConsole($"§e[视角] 视角发送失败（{e.GetType().Name}），保持期内会自动重试，不再重复提示。");
            }

            return false;
        }
    }
}
