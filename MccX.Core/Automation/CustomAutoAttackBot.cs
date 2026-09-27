using MinecraftClient.Inventory;
using MinecraftClient.Mapping;
using MinecraftClient.Scripting;

namespace MccX.Core;

/// <summary>
/// 自定义自动砍怪 Bot（需求 3.2：不使用 MCC 内置的 AutoAttack）。
///
/// 每个 tick 扫描实体，挑出攻击距离内最近的敌对生物，发送“攻击数据包 + 挥手动画”；
/// 攻击冷却在 [CooldownMinMs, CooldownMaxMs] 内随机，避免固定节奏被判定为机器人。
/// 伤害日志来自服务端回包（<see cref="OnEntityHealth"/>），可直接验证打怪是否真实生效。
/// </summary>
internal sealed class CustomAutoAttackBot : ChatBot
{
    private AttackOptions _options;
    private readonly Random _random = new();
    private readonly HashSet<int> _attacked = [];

    private int _cooldownTicks;
    private int? _lastTargetId;

    /// <summary>只有见过非零生命值，才认为实体生命字段可靠，用于跳过“尸体”。</summary>
    private bool _sawHealth;

    /// <summary>
    /// 是否已经走完服务器的 Configuration 阶段。MCC 的 Login() 在“登录成功”那一刻就返回，
    /// 此时服务器还在配置阶段，发 Play 阶段的数据包会被直接踢掉，所以必须等 AfterGameJoined。
    /// </summary>
    private bool _inGame;

    public CustomAutoAttackBot(AttackOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>运行中可直接替换参数（引用赋值原子），无需卸载重挂 Bot。</summary>
    public AttackOptions Options
    {
        get => _options;
        set => _options = value;
    }

    public override void Initialize()
    {
        if (!GetEntityHandlingEnabled())
        {
            LogToConsole("§c[砍怪] 需要实体处理支持，当前未开启，已自动停用。");
            UnloadBot();
            return;
        }

        AttackOptions o = _options;
        LogToConsole($"§a[砍怪] 已开启：距离 {o.Range:0.#} 格，冷却 {o.CooldownMinMs}-{o.CooldownMaxMs} ms（随机）。");
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

        if (_cooldownTicks > 0)
        {
            _cooldownTicks--;
            return;
        }

        AttackOptions o = _options;
        Location player = GetCurrentLocation();

        int targetId = -1;
        double targetDist = double.MaxValue;
        string targetName = string.Empty;

        foreach (KeyValuePair<int, Entity> pair in GetEntities())
        {
            Entity entity = pair.Value;

            // 只打敌对生物，玩家/掉落物/被动生物一律跳过
            if (!entity.Type.IsHostile())
                continue;

            if (_sawHealth && entity.Health <= 0)
                continue;

            double distance = player.Distance(entity.Location);
            if (distance > o.Range || distance >= targetDist)
                continue;

            targetId = pair.Key;
            targetDist = distance;
            targetName = entity.GetTypeString();
        }

        if (targetId < 0)
        {
            if (_lastTargetId is not null)
            {
                LogToConsole("§8[砍怪] 目标已离开范围，回到待命。");
                _lastTargetId = null;
            }

            _cooldownTicks = 4; // 无目标时每 0.2 秒扫一次，成本可忽略
            return;
        }

        if (InteractEntity(targetId, InteractType.Attack))
        {
            SendAnimation(Hand.MainHand);
            _attacked.Add(targetId);

            if (_lastTargetId != targetId)
            {
                _lastTargetId = targetId;
                LogToConsole($"§7[砍怪] 攻击 {targetName}（{targetDist:0.0} 格）");
            }
        }

        _cooldownTicks = RandomCooldownTicks(o.CooldownMinMs, o.CooldownMaxMs);
    }

    public override void OnEntityHealth(Entity entity, float health)
    {
        if (health > 0)
            _sawHealth = true;

        if (!_attacked.Contains(entity.ID))
            return;

        if (health <= 0)
        {
            _attacked.Remove(entity.ID);
            LogToConsole($"§a[砍怪] {entity.GetTypeString()} 已被消灭。");
        }
        else
        {
            LogToConsole($"§7[砍怪] {entity.GetTypeString()} 受到伤害，剩余生命 {health:0.#}");
        }
    }

    public override void OnEntityDespawn(Entity entity)
    {
        bool wasAttacked = _attacked.Remove(entity.ID);
        if (wasAttacked && _lastTargetId == entity.ID)
        {
            _lastTargetId = null;
            LogToConsole($"§8[砍怪] {entity.GetTypeString()} 已消失。");
        }
    }

    /// <summary>把冷却毫秒数（含随机抖动）换算成 tick。</summary>
    private int RandomCooldownTicks(int minMs, int maxMs)
    {
        if (minMs < 50)
            minMs = 50;
        if (maxMs < minMs)
            maxMs = minMs;

        int ms = minMs == maxMs ? minMs : _random.Next(minMs, maxMs + 1);
        return AutomationJitter.MsToTicks(ms);
    }
}
