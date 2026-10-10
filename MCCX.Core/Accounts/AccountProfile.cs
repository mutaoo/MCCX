using System.Text.Json.Serialization;

namespace MCCX.Core;

/// <summary>
/// 一个已保存的历史账号（对应需求 3.1「账号持久化」的本地记录）。
/// 离线模式没有真正的密码，<see cref="Credential"/> 用于预留在线模式 Token，
/// 无论是否有值都会随整体数据一起加密落盘。
/// </summary>
public sealed class AccountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>离线模式玩家名。</summary>
    public string Username { get; set; } = string.Empty;

    public string ServerHost { get; set; } = "127.0.0.1";

    public ushort Port { get; set; } = 25565;

    /// <summary>Minecraft 版本，"auto" 表示连接时 Ping 探测。</summary>
    public string MinecraftVersion { get; set; } = "auto";

    /// <summary>列表里显示的名字，保存时若为空会回填为 <see cref="Username"/>。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>凭据（离线模式为 null，加密存储）。</summary>
    public string? Credential { get; set; }

    /// <summary>自动砍怪的攻击生物过滤模式：0 不过滤 / 1 白名单 / 2 黑名单（对应 MobFilterMode）。</summary>
    public int AttackFilterMode { get; set; }

    /// <summary>勾选的攻击生物名单，存 EntityType 名（如 "Zombie"），加密存储。</summary>
    public List<string> AttackFilterMobs { get; set; } = [];

    // ---- 各功能参数（用户 2026-10-03 要求：参数必须跟随账号存取）----
    // 说明：这些是"面板上的参数"，与服务器/用户名同属账号资料。
    // 老账号库没有这些字段时反序列化会用默认值，等价于"参数回到默认"，不会读失败。

    /// <summary>自动砍怪参数（距离 / 冷却区间 / 过滤模式与名单）。</summary>
    public AttackOptions? Attack { get; set; }

    /// <summary>鼠标控制参数（左右键各自独立 + 准星探测距离）。</summary>
    public MouseOptions? Mouse { get; set; }

    /// <summary>自动钓鱼开关。</summary>
    public bool FishingEnabled { get; set; }

    /// <summary>自动钓鱼参数（收杆检测声音/实体速度、抛竿超时、重抛间隔；默认值 = MCC 配置默认值）。</summary>
    public FishingOptions? Fishing { get; set; }

    /// <summary>自动补充开关（手持用完/损坏时从背包补同款，2026-10-03 第五批需求）。</summary>
    public bool AutoRefillEnabled { get; set; }

    /// <summary>自动行走开关（一直朝当前朝向前进，2026-10-04 需求）。</summary>
    public bool AutoWalkEnabled { get; set; }

    /// <summary>服务器信息过滤模式（0 不过滤 / 1 全屏蔽 / 2 只屏蔽玩家消息 / 3 只显示指定前缀 / 4 只屏蔽指定前缀，2026-10-04 需求）。</summary>
    public int ServerFilterMode { get; set; }

    /// <summary>
    /// 历史遗留字段（2026-10-05 起不再使用）：以前"只显示指定前缀"和"只屏蔽指定前缀"
    /// 共用这一个前缀输入框。现在两种方式<b>各存各的</b>，见下面两个字段；
    /// 老账号库读到这里的值会自动迁移过去（见 <c>AccountViewModel.LoadParameters</c>），
    /// 字段本身保留只为不再反序列化失败。
    /// </summary>
    public string ServerFilterPrefix { get; set; } = string.Empty;

    /// <summary>
    /// "只显示指定前缀"模式用的前缀列表（多项用逗号/顿号/竖线分隔，空 = 不过滤）。
    /// 与 <see cref="ServerFilterBlockPrefix"/> 相互独立，切模式不用重填。
    /// </summary>
    public string ServerFilterShowPrefix { get; set; } = string.Empty;

    /// <summary>"只屏蔽指定前缀"模式用的前缀列表（与 <see cref="ServerFilterShowPrefix"/> 相互独立）。</summary>
    public string ServerFilterBlockPrefix { get; set; } = string.Empty;

    /// <summary>断线自动重连参数（开关 / 次数 / 间隔）。次数 0 = 无限。</summary>
    public ReconnectOptions? Reconnect { get; set; }

    /// <summary>
    /// 各功能的"过程日志"开关（2026-10-08 需求：Bot 日志刷屏，每个功能可单独关）。
    /// <para>
    /// 键 = 功能标识（<c>MCCX_App.ViewModels.FeatureLogKeys</c>），值 = <b>是否显示该功能的过程日志</b>。
    /// 没有记录 = 显示（老账号库与新账号默认全开），所以老库读进来不会失败。
    /// </para>
    /// <para>
    /// 关掉只屏蔽"过程日志"（攻击了谁、破坏了哪块方块这类高频行）；
    /// <b>功能的开关状态日志照常推送</b>，否则用户看不出功能开没开（见 AccountViewModel.IsStateLog）。
    /// 每个账号各存一份。
    /// </para>
    /// </summary>
    public Dictionary<string, bool> FeatureLogSwitches { get; set; } = [];

    /// <summary>
    /// 视角恢复开关。历史遗留字段：功能已于 2026-10-04 移除（进服沿用服务器记住的朝向、
    /// 本地不再存视角，与 MCC 一致；2026-10-05 起另有"进服头 10 秒的纠正包守卫"，
    /// 那不依赖任何账号字段），代码不再读写它的语义，仅保留序列化兼容（老账号库里有这个字段）。
    /// </summary>
    public bool ViewRestoreEnabled { get; set; } = true;

    /// <summary>
    /// 历史遗留：上次退出服务器时的视角（水平偏航角，度）。同样是 2026-10-04 移除的
    /// 进服恢复功能留下的字段——只保留序列化兼容，新的进服流程不再读它
    ///（朝向由服务器在玩家数据里记住）。
    /// </summary>
    public float? LastYaw { get; set; }

    /// <summary>上次退出服务器时的视角（俯仰角，度）。见 <see cref="LastYaw"/>。</summary>
    public float? LastPitch { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>列表副标题，不参与序列化。</summary>
    [JsonIgnore]
    public string ServerSummary => $"{ServerHost}:{Port}";

    /// <summary>
    /// 把另一个记录的字段整体拷到本条（Id 除外）。
    /// <para>
    /// 用"一处列全"的写法替代散落各处的逐字段赋值：以前 <c>Upsert</c> 逐字段拷贝，
    /// 新增字段忘了加就会"改了不落盘"（功能参数就是这么丢的）。
    /// </para>
    /// <para>
    /// <b>注意</b>：调用方必须传一条**填完整**的记录。传稀疏记录会把没填的字段清成默认值
    /// （例如只改服务器地址却把砍怪参数抹掉）。<see cref="LastYaw"/>/<see cref="LastPitch"/> 不在此拷贝
    /// （历史遗留的视角字段，进服恢复功能已移除，保持原值不动即可）。
    /// </para>
    /// </summary>
    public void CopyFrom(AccountProfile other)
    {
        ArgumentNullException.ThrowIfNull(other);

        Username = other.Username;
        ServerHost = other.ServerHost;
        Port = other.Port;
        MinecraftVersion = other.MinecraftVersion;
        DisplayName = other.DisplayName;
        Credential = other.Credential;
        AttackFilterMode = other.AttackFilterMode;
        AttackFilterMobs = other.AttackFilterMobs;
        Attack = other.Attack;
        Mouse = other.Mouse;
        FishingEnabled = other.FishingEnabled;
        Fishing = other.Fishing;
        AutoRefillEnabled = other.AutoRefillEnabled;
        AutoWalkEnabled = other.AutoWalkEnabled;
        ServerFilterMode = other.ServerFilterMode;
        ServerFilterPrefix = other.ServerFilterPrefix;
        ServerFilterShowPrefix = other.ServerFilterShowPrefix;
        ServerFilterBlockPrefix = other.ServerFilterBlockPrefix;
        Reconnect = other.Reconnect;
        FeatureLogSwitches = other.FeatureLogSwitches;
        ViewRestoreEnabled = other.ViewRestoreEnabled;
        LastUsedAt = other.LastUsedAt;
    }
}
