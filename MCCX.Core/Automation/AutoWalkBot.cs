using MinecraftClient.Mapping;
using MinecraftClient.Scripting;

namespace MCCX.Core;

/// <summary>
/// 自动行走（用户 2026-10-04 需求）：只管一直朝前走。
///
/// 做法：每走完一段（或卡住重试）就按<b>当前视角朝向</b>重新算一个前方目标点，
/// 交给 MCC 的寻路（<see cref="MoveToLocation"/> → Handler.MoveTo）去走——碰撞、台阶、
/// 绕墙都由 MCC 负责，MCCX 不自己造位移包（直接造会被服务端反作弊当移动作弊踢）。
///
/// 朝向约定（MCC yaw）：0=南(+Z) 90=西(-X) 180=北(-Z) 270=东(+X)，
/// 前方向量 = (-sin(yaw), 0, cos(yaw))。
///
/// 生命周期与 ViewControlBot 同款 <c>_joined</c> 门闩：断线时 MCC 会把本 Bot 经静态
/// botsOnHold 挂到新连接上（还没进服），那段时间 handler/位置数据都是新的，
/// 不能跑寻路——AfterGameJoined 置真、OnDisconnect 置假，<b>没进服就不动</b>。
///
/// 不取 BotMovementLock：MCCX 里只有本 Bot 会移动（钓鱼 Enable_Move 默认关，
/// 砍怪/鼠标/补充/视角都不动位），而锁在断线时不释放（Bot 进 botsOnHold 不走 OnUnload），
/// 持有者名字又固定，重连回来反而会把自己锁在门外。
/// </summary>
public sealed class AutoWalkBot : ChatBot
{
    /// <summary>每段路的目标点距当前位置的前方距离（格）：太长寻路变慢易失败，太短频繁重算。</summary>
    private const double LegDistance = 3.0;

    /// <summary>寻路失败（墙/悬崖/区块没加载）后的重试间隔（tick，20 TPS → 60 = 3 秒）。</summary>
    private const int BlockedRetryTicks = 60;

    /// <summary>位置包还没到时的等待间隔（tick）。</summary>
    private const int IdleWaitTicks = 20;

    /// <summary>
    /// 进服后等朝向落地再种方向锚点的 tick 数（20 TPS → 60 = 3 秒）。
    /// <para>
    /// 2026-10-05 用户现场"进服后视角自动向南"的根因：登录位置包在 <c>JoinGame</c> <b>之后</b>才到，
    /// 服务器越远/登录越慢越容易超过原来那 1 秒（<see cref="IdleWaitTicks"/>）的等待；
    /// 这时 <c>GetYaw()</c> 读到的仍是默认 0（= 正南），锚点一旦种下就永久把行走方向、
    /// 以及 <c>SetInputToward</c> 每 tick 压写的客户端朝向一起钉在南。
    /// </para>
    /// <para>
    /// 所以这里改成：进服先空等这段时间（期间不寻路），到期再取 <c>GetYaw()</c> 当锚点，
    /// 那时登录朝向必然已经写进 <c>playerYaw</c>。取值偏保守（3 秒）只影响开始行走的时机，
    /// 不影响行走本身；朝向若仍为 0 也确实是服务器发来的 0，不是竞态误判。
    /// </para>
    /// </summary>
    private const int AnchorSettleTicks = 60;

    /// <summary>
    /// 卡死看门狗：连续"寻路中"走满这么久（tick，300 = 15 秒）就强制重新算一段。
    /// 正常一段路只走 1~2 秒；一直显示寻路中说明被反作弊拉回/路堵死，
    /// 原地续着永远不会重试，必须主动重新规划（顺带按最新朝向改目的地）。
    /// </summary>
    private const int StallDetectTicks = 300;

    /// <summary>
    /// 视角点击后延迟几个 tick 再规划（2026-10-04 现场反馈修复）：
    /// 视角包的 _yaw 要等下一个物理 tick 才同步进 GetYaw()，
    /// 取消旧路径后立刻规划会拿旧朝向算出反方向的路。2 tick ≈ 100ms，足够同步。
    /// </summary>
    private const int ReplanDelayTicks = 2;

    /// <summary>目标点 Y 吸附：向下找地面的最大深度（格）。超出 = 没底（深坑/虚空），维持原目标。</summary>
    private const int SnapDownLimit = 128;

    /// <summary>目标点 Y 吸附：向上爬障碍/山顶的最大高度（格）。更高的墙视为走不过去。</summary>
    private const int SnapUpLimit = 8;

    private readonly string _accountId;

    private bool _joined;
    private int _cooldown;
    private int _stallTicks;
    private bool _complained;

    /// <summary>
    /// "改走直接下落"提示的独立标志位：不跟 <see cref="_complained"/> 共用——
    /// 共用的话，开局若先来一次"区块没加载/无路"的临时提示就会把它抢掉，
    /// 真正下落时永远不再播报（g_walk D3 实测踩过）。
    /// </summary>
    private bool _complainedUnsafe;

    /// <summary>
    /// 请求重规划（任意线程可写、MCC 主线程消费）：视角点击后由 MCCSession 调用。
    /// 在途路径每 tick 都经 SetInputToward 把朝向压回旧路点方向——不掐掉旧路径，
    /// 点"向北"也会被立刻拉回南（2026-10-04 现场反馈的根因之一）。
    /// </summary>
    private volatile bool _replanPending;

    /// <summary>最近一次成功规划时的位置：看门狗日志算这段窗口内真实位移用。</summary>
    private Location _planStart;

    /// <summary>
    /// 规划方向锚点（2026-10-04 G7 根因修复）：段末重规划若直接读 <c>GetYaw()</c>，
    /// 会把上一段路收尾路点的局部朝向（绕障时可偏出 90°+）当成新前进方向，
    /// 行走方向就一段段"随机游走"漂走——实测北向计划的绕障收尾朝向 307°，之后整条轨迹偏东南。
    /// 锚点规则：首段取当前朝向（保持"进服后持续朝当前朝向前进"的语义），
    /// 视角按钮点击时由 <see cref="RequestReplan"/> 显式更新，段末/看门狗重规划一律按锚点走。
    /// </summary>
    private float? _headingYaw;

    /// <summary>
    /// 视角按钮带来的目标 yaw（跨线程：IPC 线程先写本字段、再置 <c>_replanPending</c> 的 volatile 位，
    /// 主线程先读位再读本字段，按 .NET 内存模型可见性安全）。
    /// </summary>
    private float _replanYawTarget;

    /// <summary>
    /// 进服后还需等待多少 tick 才种方向锚点（见 <see cref="AnchorSettleTicks"/>）。
    /// 走完归零；断开/重新进服由 <see cref="AfterGameJoined"/> 重置。
    /// </summary>
    private int _anchorSettle;

    public AutoWalkBot(string accountId) => _accountId = accountId ?? string.Empty;

    /// <summary>重连认领用：MCCSession 只认领属于本账号的已挂载实例。</summary>
    public string AccountId => _accountId;

    public override void Initialize()
    {
        if (!GetTerrainEnabled())
            LogToConsole("§c[自动行走] 需要地形数据才能寻路（MCCX 已默认开启地形）。");
        else
            LogToConsole("§7[自动行走] 已就绪：持续朝当前朝向前进，关掉开关或断开即停。");
    }

    public override void AfterGameJoined()
    {
        _joined = true;
        _cooldown = IdleWaitTicks; // 等服务端把位置发下来再寻路
        _stallTicks = 0;
        _complained = false;
        _complainedUnsafe = false;
        _headingYaw = null; // 新连接重新取进服时的朝向（服务器记忆的视角）当锚点
        _anchorSettle = AnchorSettleTicks; // 但先等朝向落地（AnchorSettleTicks），别把默认 0（正南）当锚点
    }

    public override bool OnDisconnect(ChatBot.DisconnectReason reason, string message)
    {
        _joined = false;
        _cooldown = 0;
        _stallTicks = 0;
        _headingYaw = null;
        _anchorSettle = 0;
        return base.OnDisconnect(reason, message);
    }

    public override void OnUnload()
    {
        // 关开关要"立刻"停：卸载时把还没走完的那段路彻底取消（含残留路点），
        // 否则玩家会继续走到当前路点才停（"关了还在动一截"的错觉）。
        CancelActivePath();
    }

    /// <summary>
    /// 视角点击后由 <see cref="MCCSession.LookAt"/> 调用（任意线程，只置标志位）：
    /// 下一个 tick 在 MCC 主线程上取消在途路径，稍后按 <paramref name="targetYaw"/> 锚点重算一段。
    /// 锚点显式传值（按钮方向的固定 yaw），不依赖当时可能被路点带偏的 <c>GetYaw()</c>。
    /// </summary>
    public void RequestReplan(float targetYaw)
    {
        // 先写值再置 volatile 位；读侧先读位再读值（见 _replanYawTarget 注释）
        _replanYawTarget = targetYaw;
        _replanPending = true;
    }

    /// <summary>
    /// 彻底掐断在途移动。MCC 的 <c>CancelMovement()</c> <b>只清 path、不清 pathTarget</b>，
    /// 而 <c>UpdatePathfindingInput</c> 只要看得到 pathTarget 就会继续 SetInputToward——
    /// 残留路点既会把朝向每 tick 压回旧方向（视角点击被拉回南的根因），
    /// 也会让关掉开关后"还在走向下个路点"。
    /// 先 <c>MoveTo(当前位置)</c>（进锁后第一件事就是 <c>pathTarget = null</c>），
    /// 再 <c>CancelMovement()</c> 清掉 path，两步缺一不可。
    /// </summary>
    private void CancelActivePath()
    {
        // 两步各自兜底：MoveTo 抛异常也必须继续把 path 清掉，缺了哪步都停不干净
        try
        {
            Handler.MoveTo(GetCurrentLocation(), allowUnsafe: false, allowDirectTeleport: false,
                timeout: TimeSpan.FromSeconds(1));
        }
        catch
        {
            // 连接正在拆（handler 已经没了）：本来也走不到了
        }

        try
        {
            Handler.CancelMovement();
        }
        catch
        {
        }
    }

    public override void Update()
    {
        if (!_joined || !GetTerrainEnabled())
            return;

        // 视角点击触发的重规划：先把按钮方向立成方向锚点，再掐掉旧路径（含残留路点），
        // 延迟两个 tick 让视角保持期的转向包先落地，然后按锚点规划——
        // 规划不再读 GetYaw()（段末读它会被绕障收尾路点的局部朝向带偏，见 _headingYaw），
        // 掐断旧路径则保证规划窗口里没有每 tick 压朝向的写手，视角与行进天然对齐。
        if (_replanPending)
        {
            _replanPending = false;
            _headingYaw = _replanYawTarget;
            CancelActivePath();
            _stallTicks = 0;
            _cooldown = ReplanDelayTicks;
        }

        // 进服后先空等朝向落地（AnchorSettleTicks）再种锚点：这段时间不寻路，
        // 但上面的"视角点击重规划"照常生效（用户可能在进服瞬间就点方向，那种锚点是显式给的，不受影响）。
        if (_anchorSettle > 0)
        {
            _anchorSettle--;
            if (_anchorSettle == 0)
                _headingYaw ??= GetYaw(); // 此刻 GetYaw() 已是服务器发来的朝向（可能是 0=南，那就是真实的南）
            return;
        }

        if (_cooldown > 0)
        {
            _cooldown--;
            return;
        }

        if (ClientIsMoving())
        {
            _stallTicks++;
            if (_stallTicks < StallDetectTicks)
                return;

            // 卡死看门狗到点：落到下面按当前位置 + 最新朝向重算一段（MoveToLocation 会重算路径）。
            // 附带诊断（2026-10-04 现场反馈"平坦地面却不走"）：这段窗口的位移 + 身位方块——
            // 位移≈0 且身位=实心 → MCC 世界与服务端不同步（幻影方块，物理被挡）；
            // 位移≈0 且身位=空 → 服务端在回拉位置（反作弊/移动校验把每次移动打回）；
            // onGround=False → 物理认为悬空（永远落不回地）。真服复测就把这行发回来定位。
            Location here = GetCurrentLocation();
            string moved = IsZeroLocation(_planStart) || IsZeroLocation(here)
                ? "?"
                : Distance3(_planStart, here).ToString("0.00");
            bool inBody = GetWorld().GetBlock(here).Type.IsSolid()
                || GetWorld().GetBlock(here + new Location(0, 1, 0)).Type.IsSolid();
            string body = inBody ? "实心" : "空";
            bool ground = Movement.IsOnGround(GetWorld(), here);
            LogToConsole($"§e[自动行走] 长时间未能前进，重新规划路线。（这段位移 {moved} 格，身位={body}，onGround={ground}）");
            _stallTicks = 0;
        }
        else
        {
            _stallTicks = 0;
        }

        Location current = GetCurrentLocation();
        if (current.X == 0 && current.Y == 0 && current.Z == 0)
        {
            _cooldown = IdleWaitTicks;
            return;
        }

        // "前方" = 方向锚点（首段取当前朝向；段末/看门狗重规划都按锚点，不继承绕障路点的
        // 局部朝向——2026-10-04 G7 实测：北段绕障收尾 307° 被当成新方向，轨迹随后漂向东南）
        _headingYaw ??= GetYaw();
        double yawRad = _headingYaw.Value * Math.PI / 180.0;
        Location goal = new(
            current.X - Math.Sin(yawRad) * LegDistance,
            current.Y,
            current.Z + Math.Cos(yawRad) * LegDistance);

        // 目标点 Y 吸附（2026-10-04 用户反馈"站高空方块不走"的根因修复，见 SnapGoalY）
        goal = SnapGoalY(current, goal);

        if (!Movement.CheckChunkLoading(GetWorld(), current, goal))
        {
            _cooldown = BlockedRetryTicks;
            ComplainOnce("前方区块还没加载，稍后自动重试。");
            return;
        }

        // 不给 maxOffset（2026-10-04 实测教训）：maxOffset>0 时 A* 只要"网格上距目标 <= maxOffset"
        // 就算到达。玩家恰好站在目标的斜对角外一格时，寻路会立刻返回一个离脚下 0.1 格的"路径"——
        // 原地抖一下就"走完"，然后 3~6 Hz 无限重发，位移恒为 0（g_walk B2 实测）。
        // 目标不可达就让它失败：1 秒超时后走"受阻"分支，3 秒后带日志重试。
        if (MoveToLocation(goal, allowUnsafe: false, allowDirectTeleport: false,
                timeout: TimeSpan.FromSeconds(1)))
        {
            _stallTicks = 0;
            _planStart = current;
            _complained = false;
            _complainedUnsafe = false;
        }
        else if (MoveToLocation(goal, allowUnsafe: true, allowDirectTeleport: false,
                   timeout: TimeSpan.FromSeconds(1)))
        {
            // 安全路线没有（落差 > 3 格、孤立高柱等——MCC 的 IsSafe 只肯走落差 <=3 的地方）
            // → 直接下落穿过去。用户 2026-10-04 拍板：不限落差、不判落点是否安全，一直行走即可
            // （会摔伤甚至摔死，属预期表现）。
            _stallTicks = 0;
            _planStart = current;
            ComplainOnce(ref _complainedUnsafe, "没有安全路线，改走直接下落的路线继续前进。");
        }
        else
        {
            _cooldown = BlockedRetryTicks;
            ComplainOnce("前方无路可走（墙/死胡同），稍后自动重试。");
        }
    }

    /// <summary>
    /// 目标点 Y 吸附（2026-10-04 用户反馈"角色站在高空方块上纹丝不动"的根因修复）。
    /// <para>
    /// MCC 的 A*（maxOffset=0）要求目标点坐标与某个可达站位<b>完全相等</b>，而原逻辑把目标 Y
    /// 固定成当前脚底高度——前方是下坡/上坡/障碍/悬空时，目标点悬在半空或嵌在实心方块里，
    /// A* 永远解不出来：1 秒后失败、反复"受阻"重试，角色站着一动不动（站 1x1 高柱上必现）。
    /// </para>
    /// <para>
    /// 规则（按目标列判断）：
    /// ① 脚底高度就有实心（山体/障碍/墙的面）→ 沿连续实心爬到顶，目标挪到顶面；
    ///    顶到扫描上限还实心 = 超高墙，维持原目标（走不过去）。
    /// ② 脚底高度是空气（平地/下坡/悬空）→ 向下找第一块实心地面（找到的第一块上方必然是空气），
    ///    目标挪到地面上方；深处的洞穴地板也算——掉进去照样继续走（用户拍板不判落点）。
    /// ③ 上下都找不到可站立处（深坑/虚空）→ 维持原目标，交给受阻重试分支。
    /// </para>
    /// </summary>
    private Location SnapGoalY(Location current, Location goal)
    {
        World world = GetWorld();
        int topY = (int)Math.Floor(current.Y);
        Location column = new(goal.X, topY, goal.Z);

        // ① 脚底高度有实心：爬山/爬障碍顶
        if (world.GetBlock(column).Type.IsSolid())
        {
            int top = topY;
            while (top < topY + SnapUpLimit
                   && world.GetBlock(new Location(goal.X, top + 1, goal.Z)).Type.IsSolid())
            {
                top++;
            }

            if (!world.GetBlock(new Location(goal.X, top + 1, goal.Z)).Type.IsSolid())
                return new Location(goal.X, top + 1, goal.Z);

            return goal;
        }

        // ② 空中：向下找第一块实心（第一块的上方必然还是空气 = 可站）
        for (int y = topY - 1; y >= topY - SnapDownLimit; y--)
        {
            if (world.GetBlock(new Location(goal.X, y, goal.Z)).Type.IsSolid())
                return new Location(goal.X, y + 1, goal.Z);
        }

        // ③ 找不到地面
        return goal;
    }

    /// <summary>坐标是否还是默认零点（位置包没到 / 尚未规划过）。</summary>
    private static bool IsZeroLocation(Location l) => l.X == 0 && l.Y == 0 && l.Z == 0;

    /// <summary>三维直线距离（看门狗位移诊断用）。</summary>
    private static double Distance3(Location a, Location b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        double dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>受阻提示只喊一次：成功走起来后清掉，换地方再受阻还会再提示。</summary>
    private void ComplainOnce(string detail) => ComplainOnce(ref _complained, detail);

    /// <summary>同上，但用调用方自己的标志位——不同类别的提示不互相抢"只喊一次"的机会。</summary>
    private void ComplainOnce(ref bool flag, string detail)
    {
        if (flag)
            return;

        flag = true;
        LogToConsole($"§e[自动行走] {detail}");
    }
}
