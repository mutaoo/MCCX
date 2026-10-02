using MinecraftClient.Inventory;
using MinecraftClient.Mapping;
using MinecraftClient.Scripting;

namespace MCCX.Core;

/// <summary>
/// 复杂鼠标按键控制 Bot（需求 3.2）。左键、右键各一套独立状态机，参数互不干扰，可以同时触发。模式：
/// <list type="bullet">
/// <item>长按：按住不松手——右键每 tick 重发“使用物品”，左键持续挖掘准星方块（挖掘时长由 MCC
/// 自动计算）。按住/间隔两个参数在长按模式下不生效，界面也会隐藏这两行</item>
/// <item>间隔点击：按下 + 释放 → 等待 IntervalMs → 重复</item>
/// <item>间隔长按：按下 → 保持/蓄力 HoldMs → 释放 → 冷却 IntervalMs → 重复</item>
/// </list>
/// 间隔类模式的时长都带 ±JitterPercent% 的随机抖动，避免固定节奏。
///
/// 行为约定（需求 5）：
/// <list type="bullet">
/// <item>作用方块只有“当前准星所对的方块”：沿视线做体素步进，取视线最先撞上的非空气方块，
/// 不挑别的目标，也绝不回退去挖脚下地板（会挖穿立足点导致持续坠落）</item>
/// <item>探测距离由 MouseOptions.AimReach 决定（1-7 格，默认 5），左右键共用</item>
/// <item>不能扭动视角：挖掘、交互都不发 LookAt（lookAtBlock 恒为 false）</item>
/// <item>不管准星处有没有方块物品，都要触发鼠标动作：左键没目标就挥手（Animation），
/// 右键没目标就用手中物品（UseItem），本次点击绝不跳过</item>
/// </list>
///
/// 数据包映射：
/// <list type="bullet">
/// <item>左键 = 挖掘方块（StartDigging → 保持 → StopDigging），释放包由 MCC 的挖掘计时器自动补发；
/// 空处点击 = 挥手（Animation）</item>
/// <item>右键 = 视线指着方块时发 UseItemOn（开箱/按按钮/放方块），空处发 UseItem（使用手中物品），
/// 每次点击都会挥手；按住期间每个 tick 重发，与原版按住右键一致；
/// 协议里没有独立的“右键释放”包，停止发送即视为释放</item>
/// </list>
/// </summary>
internal sealed class MouseControlBot : ChatBot
{
    private enum Phase
    {
        Press,
        Hold,
        Cooling,
    }

    /// <summary>
    /// 长按模式左键的兜底重按间隔（毫秒，带抖动）：正常情况下方块挖完会通过 OnBlockChange
    /// 立刻重新按下，但服务端可能在不通知客户端的情况下中止挖掘（超距、反作弊等），
    /// 没有兜底就会永远卡在按住状态。2 分钟足够任何生存挖掘完成，重按一次代价可忽略。
    /// </summary>
    private const int HoldRepressFallbackMs = 120_000;

    /// <summary>长按模式左键在“准星空处”的挥手节奏（毫秒，带抖动）：与原版按住左键的连续挥击一致。</summary>
    private const int HoldAirSwingMs = 250;

    /// <summary>
    /// 长按模式右键的重复交互节奏（tick）：原版持续按住右键时客户端约每 4 tick 决策一次
    /// （对准方块发 UseItemOn = 连续放置/交互，空处发 UseItem），这里对齐同样的节奏。
    /// </summary>
    private const int HoldRightRepeatTicks = 4;

    /// <summary>单个按键的运行状态：左键、右键各一份，节奏完全独立。</summary>
    private sealed class ButtonState
    {
        public required MouseSide Side { get; init; }

        public Phase Phase = Phase.Press;
        public int TicksRemaining;
        public Location? LastDigTarget;
        public DateTime LastTrace = DateTime.MinValue;
        public DateTime LastBreakTrace = DateTime.MinValue;

        /// <summary>最近一次点击走的路径（指向方块 / 空处），只用于日志。</summary>
        public string ClickPath = string.Empty;
    }

    private readonly ButtonState _left = new() { Side = MouseSide.Left };
    private readonly ButtonState _right = new() { Side = MouseSide.Right };

    private MouseOptions _options;
    private readonly Random _random = new();

    /// <summary>
    /// 是否已经走完服务器的 Configuration 阶段。MCC 的 Login() 在“登录成功”那一刻就返回，
    /// 此时服务器还在配置阶段，发 Play 阶段的数据包会被直接踢掉，所以必须等 AfterGameJoined。
    /// </summary>
    private bool _inGame;

    public MouseControlBot(MouseOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>运行中可直接替换参数（引用赋值原子），无需卸载重挂 Bot。</summary>
    public MouseOptions Options
    {
        get => _options;
        set
        {
            MouseOptions old = _options;
            _options = value;

            // 模式切换会改变状态机语义（长按=一直按住不松手）：
            // 不复位的话，从长按切走后状态机会永远停在“按住”阶段，所以按侧复位。
            if (old.Left.Mode != value.Left.Mode)
                Reset(_left);
            if (old.Right.Mode != value.Right.Mode)
                Reset(_right);
        }
    }

    public override void Initialize()
    {
        Reset(_left);
        Reset(_right);

        MouseOptions o = _options;
        List<string> parts = [];
        if (o.Left.Enabled)
            parts.Add(Describe(MouseSide.Left, o.Left));
        if (o.Right.Enabled)
            parts.Add(Describe(MouseSide.Right, o.Right));

        if (parts.Count == 0)
        {
            LogToConsole("§8[鼠标] 左键、右键都没启用，不做任何点击。");
            return;
        }

        LogToConsole($"§a[鼠标] 已开启：{string.Join("；", parts)}（探测距离 {o.AimReach:0.#} 格）");

        if (o.Left.Enabled && !GetTerrainEnabled())
            LogToConsole("§8[鼠标] 地形数据未开启：左键只挥手，不挖方块。");
    }

    private static void Reset(ButtonState st)
    {
        st.Phase = Phase.Press;
        st.TicksRemaining = 0;
        st.LastDigTarget = null;
        st.LastTrace = DateTime.MinValue;
        st.LastBreakTrace = DateTime.MinValue;
        st.ClickPath = string.Empty;
    }

    public override void AfterGameJoined()
    {
        // 服务器 Configuration 阶段结束、真正进入 Play 阶段后才允许发包
        _inGame = true;
    }

    public override void Update()
    {
        if (!_inGame)
            return;

        MouseOptions o = _options;
        Tick(_left, o.Left);
        Tick(_right, o.Right);
    }

    /// <summary>推进单个按键的状态机；未启用的按键复位不动（不影响另一个按键）。</summary>
    private void Tick(ButtonState st, MouseButtonOptions cfg)
    {
        if (!cfg.Enabled)
        {
            // 关掉就回到“未按下”：重新启用时从一次完整的按下开始（右键不会莫名其妙处于按住状态）
            Reset(st);
            return;
        }

        switch (st.Phase)
        {
            case Phase.Hold:
                if (st.Side == MouseSide.Right)
                {
                    if (cfg.Mode == MouseMode.Hold)
                    {
                        // 长按 = 一直按住不松手（按住/间隔参数不生效）。
                        // 持续按住的原版语义 = 周期性重新决策：对着方块 UseItemOn（连续放置/交互），
                        // 空处 UseItem；成功才挥手。协议里没有“右键释放”包，停止发送即释放。
                        if (--st.TicksRemaining > 0)
                            return;

                        st.TicksRemaining = HoldRightRepeatTicks;
                        if (!Press(st, cfg))
                        {
                            st.Phase = Phase.Cooling;
                            st.TicksRemaining = 20;
                            return;
                        }

                        Trace(st, PressLabel(st, "按下"));
                        return;
                    }

                    // 间隔长按：按住期间每个 tick 重发“使用物品”，与原版按住右键的行为一致
                    UseItemInHand();

                    if (--st.TicksRemaining > 0)
                        return;

                    Trace(st, $"{SideName(st.Side)} 松开");
                    st.Phase = Phase.Cooling;
                    st.TicksRemaining = Ticks(cfg.IntervalMs, cfg.JitterPercent);
                    return;
                }

                // 左键长按：目标没变就一直等；需要时立刻重新按下（挖完/目标挪走/空处节拍/兜底超时）
                if (cfg.Mode == MouseMode.Hold)
                {
                    if (LeftHoldShouldRepress(st))
                    {
                        st.Phase = Phase.Press;
                        break;
                    }

                    return;
                }

                if (--st.TicksRemaining > 0)
                    return;

                Trace(st, $"{SideName(st.Side)} 松开");

                // 左键的“释放”数据包由挖掘计时器（DigBlock duration）自动发出
                st.Phase = Phase.Cooling;
                st.TicksRemaining = Ticks(cfg.IntervalMs, cfg.JitterPercent);
                return;

            case Phase.Cooling:
                if (--st.TicksRemaining > 0)
                    return;

                st.Phase = Phase.Press;
                break;
        }

        // Phase.Press
        if (!Press(st, cfg))
        {
            // 发包失败（不常发生）：退避 1 秒再试，避免空转刷日志
            st.Phase = Phase.Cooling;
            st.TicksRemaining = 20;
            return;
        }

        if (cfg.Mode == MouseMode.IntervalClick)
        {
            // 间隔点击：按下即完成、直接进冷却；按住 ms 是“间隔长按”的参数，这里绝不参与
            Trace(st, PressLabel(st, "点击"));
            st.Phase = Phase.Cooling;
            st.TicksRemaining = Ticks(cfg.IntervalMs, cfg.JitterPercent);
        }
        else
        {
            Trace(st, PressLabel(st, "按下"));
            st.Phase = Phase.Hold;
            st.TicksRemaining = cfg.Mode == MouseMode.Hold
                // 长按：这个计时只当“重新按下”的节拍/兜底，不是按住时长（按住=永久）
                ? (st.Side == MouseSide.Right
                    ? HoldRightRepeatTicks
                    : st.LastDigTarget is not null
                        ? Ticks(HoldRepressFallbackMs, cfg.JitterPercent)
                        : Ticks(HoldAirSwingMs, cfg.JitterPercent))
                : Ticks(cfg.HoldMs, cfg.JitterPercent);
        }
    }

    /// <summary>
    /// 长按模式下左键是否需要重新“按下”：准星目标变了（挖完/被换/挪走）、
    /// 空处挥手节拍到、或兜底超时。返回 true 时状态机回到 Press 立刻重新执行一次按下。
    /// </summary>
    private bool LeftHoldShouldRepress(ButtonState st)
    {
        // 空处长按：按节拍重复挥手（原版按住左键在没目标时也会连续挥）
        if (st.LastDigTarget is null)
            return --st.TicksRemaining <= 0;

        if (!TryGetAimBlock(out Location current, out _))
            return true; // 准星已空：回到空处路径重新按下（挥手）

        if (!SameCell(current, st.LastDigTarget.Value))
            return true; // 方块挖完/被替换/准星挪走：重新拾取目标

        // 目标没变：正常要等 OnBlockChange（挖完）触发重按；这里只留兜底超时，
        // 应对服务端悄悄中止挖掘这类“客户端不知道”的情况。
        return --st.TicksRemaining <= 0;
    }

    /// <summary>两个 Location 是否指向同一格。</summary>
    private static bool SameCell(Location a, Location b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;

    public override void OnBlockChange(Location location, Block block)
    {
        if (block.Type is not (Material.Air or Material.CaveAir or Material.VoidAir))
            return;

        // 只有左键挖掘才可能“破坏方块”，右键模式不报
        if (!_options.Left.Enabled)
            return;

        // 优先按“我刚挖的那块”匹配，不依赖客户端自身坐标（避免位置抖动导致误判）
        if (_left.LastDigTarget is Location target)
        {
            if (target.Distance(location) > 1.0)
                return;
        }
        else if (GetCurrentLocation().Distance(location) > 4)
        {
            return;
        }

        // 长按模式：准星目标挖完 → 立刻重新按下打下一块（正常挖掘循环的衔接点，与日志节流无关）
        if (_options.Left.Enabled && _options.Left.Mode == MouseMode.Hold
            && _left.Phase == Phase.Hold)
        {
            _left.Phase = Phase.Press;
            _left.TicksRemaining = 0;
        }

        // 破坏事件独立节流：不能和“点击”共用 Trace 的 2 秒槽，否则几乎总被点击抢走
        DateTime now = DateTime.UtcNow;
        if ((now - _left.LastBreakTrace).TotalMilliseconds < 1000)
            return;

        _left.LastBreakTrace = now;
        LogToConsole($"§7[鼠标] 破坏方块 {block.Type}（{location.X:0}, {location.Y:0}, {location.Z:0}）");
    }

    /// <summary>执行一次“按下”。</summary>
    private bool Press(ButtonState st, MouseButtonOptions cfg)
    {
        if (st.Side == MouseSide.Right)
        {
            // 与原版一致：视线指着方块就发 UseItemOn（开箱、按按钮、放方块都靠它），
            // 只发 UseItem 的话“对着方块右键”永远没反应；空处照样用手中物品，本次点击不跳过。
            bool hasTarget = TryGetAimBlock(out Location blockTarget, out Direction blockFace);
            bool ok = hasTarget
                ? SendPlaceBlock(blockTarget, blockFace, Hand.MainHand, lookAtBlock: false)
                : UseItemInHand();

            st.ClickPath = hasTarget ? "指向方块" : "空处";
            if (ok)
                SendAnimation(Hand.MainHand);

            return ok;
        }

        // 左键：准星所对的第一个方块就挖它；准星空着也照常点击（挥手），本次绝不跳过
        if (TryGetAimBlock(out Location target, out Direction face))
        {
            st.ClickPath = "指向方块";
            st.LastDigTarget = target;

            // 间隔点击 = 立刻按下并释放；长按 = 交给 MCC 按方块+工具自动算挖掘时长（按住参数不生效）；
            // 间隔长按 = 按住 HoldMs 后由 MCC 计时补发释放包
            double seconds = cfg.Mode switch
            {
                MouseMode.IntervalHold => Math.Max(cfg.HoldMs, 1) / 1000.0,
                _ => 0, // IntervalClick / Hold：duration<=0 → MCC 自动计算（生存/冒险），创造模式=立即
            };

            // lookAtBlock: false —— 绝不扭视角；发包失败（如未开地形）也照样走下面的挥手
            if (DigBlock(target, face, swingArms: true, lookAtBlock: false, duration: seconds))
                return true;
        }

        st.ClickPath = "空处";
        st.LastDigTarget = null;
        SendAnimation(Hand.MainHand);
        return true;
    }

    /// <summary>
    /// 沿视线做体素步进（DDA），命中“当前准星所对”的第一个非空气方块。
    /// 顺序步进保证拿到的一定是视线最先撞上的方块（不隔山打牛、不跳格子）；
    /// 最远 <see cref="MouseOptions.AimReach"/> 格（1-7，默认 5），空处返回 false
    /// （左键改挥手、右键改用物品，照常触发）。
    /// </summary>
    private bool TryGetAimBlock(out Location target, out Direction face)
    {
        target = default;
        face = Direction.Up;

        // 准星探测距离：UI 滑动条限制 1-7，这里再夹一道，脏数据不能放飞
        double reach = _options.AimReach;
        if (reach is < 1 or > 7)
            reach = Math.Clamp(reach, 1, 7);

        Location eye = GetCurrentLocation().EyesLocation();

        double yawRad = GetYaw() * Math.PI / 180.0;
        double pitchRad = GetPitch() * Math.PI / 180.0;
        double horizontal = Math.Cos(pitchRad);
        double dirX = -Math.Sin(yawRad) * horizontal;
        double dirY = -Math.Sin(pitchRad);
        double dirZ = Math.Cos(yawRad) * horizontal;

        int x = (int)Math.Floor(eye.X);
        int y = (int)Math.Floor(eye.Y);
        int z = (int)Math.Floor(eye.Z);

        int stepX = dirX > 0 ? 1 : dirX < 0 ? -1 : 0;
        int stepY = dirY > 0 ? 1 : dirY < 0 ? -1 : 0;
        int stepZ = dirZ > 0 ? 1 : dirZ < 0 ? -1 : 0;

        double tDeltaX = stepX == 0 ? double.PositiveInfinity : Math.Abs(1.0 / dirX);
        double tDeltaY = stepY == 0 ? double.PositiveInfinity : Math.Abs(1.0 / dirY);
        double tDeltaZ = stepZ == 0 ? double.PositiveInfinity : Math.Abs(1.0 / dirZ);

        double tMaxX = stepX == 0 ? double.PositiveInfinity
            : stepX > 0 ? (x + 1 - eye.X) / dirX : (eye.X - x) / -dirX;
        double tMaxY = stepY == 0 ? double.PositiveInfinity
            : stepY > 0 ? (y + 1 - eye.Y) / dirY : (eye.Y - y) / -dirY;
        double tMaxZ = stepZ == 0 ? double.PositiveInfinity
            : stepZ > 0 ? (z + 1 - eye.Z) / dirZ : (eye.Z - z) / -dirZ;

        // 眼睛所在的格子本身就是方块（脸贴墙/嵌在方块里）：它就是准星所对的目标
        Location here = new Location(x, y, z);
        if (!IsAirBlock(here))
        {
            target = here;
            face = FaceToward(-dirX, -dirY, -dirZ);
            return true;
        }

        while (true)
        {
            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                if (tMaxX > reach)
                    return false;
                x += stepX;
                tMaxX += tDeltaX;
                // 向 +X 走 = 从西面进入方块 → 点的是它的西面（面朝玩家）
                face = stepX > 0 ? Direction.West : Direction.East;
            }
            else if (tMaxY <= tMaxZ)
            {
                if (tMaxY > reach)
                    return false;
                y += stepY;
                tMaxY += tDeltaY;
                face = stepY > 0 ? Direction.Down : Direction.Up;
            }
            else
            {
                if (tMaxZ > reach)
                    return false;
                z += stepZ;
                tMaxZ += tDeltaZ;
                face = stepZ > 0 ? Direction.North : Direction.South;
            }

            Location probe = new Location(x, y, z);
            if (!IsAirBlock(probe))
            {
                target = probe;
                return true;
            }
        }
    }

    /// <summary>方块是否为空气（未加载的区块按空气处理）。</summary>
    private bool IsAirBlock(Location location)
    {
        try
        {
            World world = GetWorld();
            if (world is null)
                return true;

            Material material = world.GetBlock(location).Type;
            return material is Material.Air or Material.CaveAir or Material.VoidAir;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>根据“方块指向玩家”的向量算出该点击哪个面。</summary>
    private static Direction FaceToward(double backX, double backY, double backZ)
    {
        double absX = Math.Abs(backX);
        double absY = Math.Abs(backY);
        double absZ = Math.Abs(backZ);

        if (absX >= absY && absX >= absZ)
            return backX > 0 ? Direction.East : Direction.West;
        if (absY >= absZ)
            return backY > 0 ? Direction.Up : Direction.Down;
        return backZ > 0 ? Direction.South : Direction.North;
    }

    private int Ticks(int ms, int jitterPercent)
        => AutomationJitter.MsToTicks(AutomationJitter.Apply(ms, jitterPercent, _random));

    /// <summary>节流日志：每个按键各自最长每 2 秒输出一条，避免长时间挂机刷屏。</summary>
    private void Trace(ButtonState st, string message)
    {
        DateTime now = DateTime.UtcNow;
        if ((now - st.LastTrace).TotalMilliseconds < 2000)
            return;

        st.LastTrace = now;
        LogToConsole($"§7[鼠标] {message}");
    }

    private static string SideName(MouseSide side)
        => side == MouseSide.Left ? "左键" : "右键";

    /// <summary>日志文案：标明本次点击走的是方块路径还是空处路径。</summary>
    private static string PressLabel(ButtonState st, string action)
        => st.ClickPath.Length > 0
            ? $"{SideName(st.Side)} {action}（{st.ClickPath}）"
            : $"{SideName(st.Side)} {action}";

    private static string Describe(MouseSide side, MouseButtonOptions o)
    {
        string mode = o.Mode switch
        {
            MouseMode.Hold => "长按",
            MouseMode.IntervalClick => "间隔点击",
            _ => "间隔长按",
        };

        // 长按模式下按住/间隔参数不生效（界面也隐藏），日志里就不提这些参数，避免误导
        string detail = o.Mode switch
        {
            MouseMode.Hold => side == MouseSide.Left
                ? "持续挖掘准星方块（时长自动计算）"
                : "按住不松手",
            MouseMode.IntervalClick => $"每 {o.IntervalMs} ms 点一次",
            _ => $"按住 {o.HoldMs} ms，冷却 {o.IntervalMs} ms",
        };

        string jitter = o.Mode == MouseMode.Hold ? string.Empty : $"，抖动 ±{o.JitterPercent}%";
        return $"{SideName(side)} / {mode}，{detail}{jitter}";
    }
}
