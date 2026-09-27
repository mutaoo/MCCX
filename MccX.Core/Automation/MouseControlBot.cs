using MinecraftClient.Inventory;
using MinecraftClient.Mapping;
using MinecraftClient.Scripting;

namespace MccX.Core;

/// <summary>
/// 复杂鼠标按键控制 Bot（需求 3.2）。三种模式：
/// <list type="bullet">
/// <item>长按：按下 → 保持 HoldMs → 释放 → 立刻再次按下（连续长按）</item>
/// <item>间隔点击：按下 + 释放 → 等待 IntervalMs → 重复</item>
/// <item>间隔长按：按下 → 保持/蓄力 HoldMs → 释放 → 冷却 IntervalMs → 重复</item>
/// </list>
/// 所有时长都带 ±JitterPercent% 的随机抖动，避免固定节奏。
///
/// 数据包映射：
/// <list type="bullet">
/// <item>左键 = 挖掘方块（StartDigging → 保持 → StopDigging），目标取视线方向上的方块（与真实玩家把鼠标指向方块一致）；
/// 视线中没有可挖掘方块时本次直接跳过 —— 绝不回退去挖脚下地板，否则会把立足点挖穿导致持续坠落</item>
/// <item>右键 = 视线指着方块时发 UseItemOn（开箱/按按钮/放方块），没指着方块才发 UseItem（使用手中物品），
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

    /// <summary>视线探测距离（格），模拟鼠标指针指向的方块。</summary>
    private static readonly double[] ProbeDistances = [0.6, 1.2, 1.8, 2.4, 3.0];

    private MouseOptions _options;
    private readonly Random _random = new();

    private Phase _phase = Phase.Press;
    private int _ticksRemaining;
    private DateTime _lastTrace = DateTime.MinValue;
    private DateTime _lastBreakTrace = DateTime.MinValue;
    private Location? _lastDigTarget;
    private bool _warnedNoTarget;

    /// <summary>
    /// 是否已经走完服务器的 Configuration 阶段。MCC 的 Login() 在“登录成功”那一刻就返回，
    /// 此时服务器还在配置阶段，发 Play 阶段的数据包会被直接踢掉，所以必须等 AfterGameJoined。
    /// </summary>
    private bool _inGame;

    /// <summary>最近一次右键走的路径（指向方块 / 空处），只用于日志。</summary>
    private string _rightClickPath = string.Empty;

    public MouseControlBot(MouseOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>运行中可直接替换参数（引用赋值原子），无需卸载重挂 Bot。</summary>
    public MouseOptions Options
    {
        get => _options;
        set => _options = value;
    }

    public override void Initialize()
    {
        MouseOptions o = _options;
        if (o.Side == MouseSide.Left && !GetTerrainEnabled())
        {
            LogToConsole("§c[鼠标] 左键挖掘需要地形数据，当前未开启，已自动停用。");
            UnloadBot();
            return;
        }

        _phase = Phase.Press;
        _ticksRemaining = 0;
        _warnedNoTarget = false;
        _lastDigTarget = null;
        _lastBreakTrace = DateTime.MinValue;

        LogToConsole($"§a[鼠标] 已开启：{Describe(o)}");
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

        switch (_phase)
        {
            case Phase.Hold:
                // 右键按住期间每个 tick 重发“使用物品”，与原版按住右键的行为一致
                if (o.Side == MouseSide.Right)
                    UseItemInHand();

                if (--_ticksRemaining > 0)
                    return;

                Trace($"{SideName(o)} 松开");

                // 左键的“释放”数据包由挖掘计时器（DigBlock duration）自动发出
                _phase = Phase.Cooling;
                _ticksRemaining = o.Mode == MouseMode.Hold ? 1 : Ticks(o.IntervalMs, o.JitterPercent);
                return;

            case Phase.Cooling:
                if (--_ticksRemaining > 0)
                    return;

                _phase = Phase.Press;
                break;
        }

        // Phase.Press
        if (!Press(o))
        {
            // 没找到目标或发送失败：退避 1 秒再试，避免空转刷日志
            _phase = Phase.Cooling;
            _ticksRemaining = 20;
            return;
        }

        if (o.Mode == MouseMode.IntervalClick)
        {
            Trace(PressLabel(o, "点击"));
            _phase = Phase.Cooling;
            _ticksRemaining = Ticks(o.IntervalMs, o.JitterPercent);
        }
        else
        {
            Trace(PressLabel(o, "按下"));
            _phase = Phase.Hold;
            _ticksRemaining = Ticks(o.HoldMs, o.JitterPercent);
        }
    }

    public override void OnBlockChange(Location location, Block block)
    {
        if (block.Type is not (Material.Air or Material.CaveAir or Material.VoidAir))
            return;

        // 只有左键挖掘才可能“破坏方块”，右键模式不报
        if (_options.Side != MouseSide.Left)
            return;

        // 优先按“我刚挖的那块”匹配，不依赖客户端自身坐标（避免位置抖动导致误判）
        if (_lastDigTarget is Location target)
        {
            if (target.Distance(location) > 1.0)
                return;
        }
        else if (GetCurrentLocation().Distance(location) > 4)
        {
            return;
        }

        // 破坏事件独立节流：不能和“点击”共用 Trace 的 2 秒槽，否则几乎总被点击抢走
        DateTime now = DateTime.UtcNow;
        if ((now - _lastBreakTrace).TotalMilliseconds < 1000)
            return;

        _lastBreakTrace = now;
        LogToConsole($"§7[鼠标] 破坏方块 {block.Type}（{location.X:0}, {location.Y:0}, {location.Z:0}）");
    }

    /// <summary>执行一次“按下”。</summary>
    private bool Press(MouseOptions o)
    {
        if (o.Side == MouseSide.Right)
        {
            // 与原版一致：视线指着方块就发 UseItemOn（开箱、按按钮、放方块都靠它），
            // 只发 UseItem 的话“对着方块右键”永远没反应。
            bool hasTarget = TryGetDigTarget(out Location blockTarget, out Direction blockFace);
            bool ok = hasTarget
                ? SendPlaceBlock(blockTarget, blockFace, Hand.MainHand, lookAtBlock: false)
                : UseItemInHand();

            _rightClickPath = hasTarget ? "指向方块" : "空处";
            if (ok)
                SendAnimation(Hand.MainHand);

            return ok;
        }

        if (!TryGetDigTarget(out Location target, out Direction face))
        {
            if (!_warnedNoTarget)
            {
                LogToConsole("§8[鼠标] 视线中没有可挖掘的方块，稍后重试。");
                _warnedNoTarget = true;
            }

            return false;
        }

        _warnedNoTarget = false;
        _lastDigTarget = target;

        // 间隔点击 = 立刻按下并释放；其余模式 = 按下后保持 HoldMs，由 MCC 计时补发释放包
        double seconds = o.Mode == MouseMode.IntervalClick ? 0 : Math.Max(o.HoldMs, 1) / 1000.0;
        return DigBlock(target, face, swingArms: true, lookAtBlock: true, duration: seconds);
    }

    /// <summary>找出该挖的方块：沿视线方向由近及远探测，没有方块就返回 false（本次跳过）。</summary>
    private bool TryGetDigTarget(out Location target, out Direction face)
    {
        target = default;
        face = Direction.Up;

        Location player = GetCurrentLocation();
        Location eye = player.EyesLocation();

        double yawRad = GetYaw() * Math.PI / 180.0;
        double pitchRad = GetPitch() * Math.PI / 180.0;
        double horizontal = Math.Cos(pitchRad);
        double dirX = -Math.Sin(yawRad) * horizontal;
        double dirY = -Math.Sin(pitchRad);
        double dirZ = Math.Cos(yawRad) * horizontal;

        foreach (double distance in ProbeDistances)
        {
            Location probe = new Location(
                eye.X + dirX * distance,
                eye.Y + dirY * distance,
                eye.Z + dirZ * distance).ToFloor();

            if (!IsAirBlock(probe))
            {
                target = probe;
                face = FaceToward(-dirX, -dirY, -dirZ);
                return true;
            }
        }

        // 不回退去挖脚下：那会挖穿立足点，玩家踩空坠落后继续挖，一路掉到矿洞/岩浆/虚空。
        return false;
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

    /// <summary>节流日志：最长每 2 秒输出一条，避免长时间挂机刷屏。</summary>
    private void Trace(string message)
    {
        DateTime now = DateTime.UtcNow;
        if ((now - _lastTrace).TotalMilliseconds < 2000)
            return;

        _lastTrace = now;
        LogToConsole($"§7[鼠标] {message}");
    }

    private static string SideName(MouseOptions o)
        => o.Side == MouseSide.Left ? "左键" : "右键";

    /// <summary>日志文案；右键额外标明本次走的是 UseItemOn（指向方块）还是 UseItem（空处）。</summary>
    private string PressLabel(MouseOptions o, string action)
        => o.Side == MouseSide.Right && _rightClickPath.Length > 0
            ? $"{SideName(o)} {action}（{_rightClickPath}）"
            : $"{SideName(o)} {action}";

    private static string Describe(MouseOptions o)
    {
        string mode = o.Mode switch
        {
            MouseMode.Hold => "长按",
            MouseMode.IntervalClick => "间隔点击",
            _ => "间隔长按",
        };

        string detail = o.Mode switch
        {
            MouseMode.Hold => $"连续长按，每次按住 {o.HoldMs} ms",
            MouseMode.IntervalClick => $"每 {o.IntervalMs} ms 点一次",
            _ => $"按住 {o.HoldMs} ms，冷却 {o.IntervalMs} ms",
        };

        return $"{SideName(o)} / {mode}，{detail}，抖动 ±{o.JitterPercent}%";
    }
}
