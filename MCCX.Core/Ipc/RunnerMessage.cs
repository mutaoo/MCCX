using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCCX.Core.Ipc;

/// <summary>
/// 多开父子进程之间的一行式协议（一行一个 JSON）。
///
/// 方向与类型：
/// 父 → 子（命令）：<c>connect</c> / <c>disconnect</c> / <c>input</c> / <c>attack</c> /
/// <c>mouse</c> / <c>fishing</c> / <c>autorefill</c> / <c>walk</c> / <c>autocmd</c> / <c>msgfilter</c> / <c>reconnect</c> /
/// <c>logswitch</c> / <c>view</c> / <c>look</c> / <c>dialog</c> / <c>dlgcancel</c> / <c>wclick</c> / <c>quit</c>
/// 子 → 父（事件）：<c>ready</c> / <c>log</c> / <c>state</c> / <c>joined</c> / <c>error</c> /
/// <c>dialog</c> / <c>dlgend</c> / <c>window</c> / <c>winend</c>
///
/// 字段名用短名是为了让高频日志行尽量短（子进程日志逐行走管道）。
/// </summary>
public sealed class RunnerMessage
{
    public const string CmdConnect = "connect";
    public const string CmdDisconnect = "disconnect";
    public const string CmdInput = "input";
    public const string CmdAttack = "attack";
    public const string CmdMouse = "mouse";
    public const string CmdFishing = "fishing";
    public const string CmdAutoRefill = "autorefill";

    /// <summary>自动行走开关（2026-10-04 需求：一直朝当前朝向前进）。</summary>
    public const string CmdWalk = "walk";

    /// <summary>
    /// 自动指令（2026-10-22 需求）：<c>On</c> = 总开关，<c>AutoCommands</c> = 整份指令表。
    /// 界面改一条也整份下发（子进程只存快照，不做增量合并）。
    /// </summary>
    public const string CmdAutoCommands = "autocmd";

    /// <summary>服务器信息过滤（2026-10-04 需求）：模式 + 前缀一起下发。</summary>
    public const string CmdMsgFilter = "msgfilter";

    public const string CmdReconnect = "reconnect";

    /// <summary>视角移动：一次性把视角转向 Direction 指定的方向（见 <c>MccLookDirection</c>）。</summary>
    public const string CmdLook = "look";

    /// <summary>界面填好的对话框取值 + 要点的动作（载荷是 <c>MccDialogSubmit</c> 的 JSON）。</summary>
    public const string CmdDialog = "dialog";

    /// <summary>取消服务器弹出的对话框。</summary>
    public const string CmdDialogCancel = "dlgcancel";

    /// <summary>容器窗口操作：载荷是 <c>MccWindowClick</c> 的 JSON（点格子 / 关窗口）。</summary>
    public const string CmdWindowClick = "wclick";

    /// <summary>
    /// 界面开 / 收"玩家背包"面板（<c>On</c> 字段为 true 表示打开）。
    /// 打开后子进程把玩家背包（窗口 0）的内容跟着推过来。
    /// </summary>
    public const string CmdWindowWatch = "wwatch";

    public const string CmdQuit = "quit";

    /// <summary>
    /// 功能“过程日志”开关（2026-10-20）：<c>Key</c> = 功能标识（attack/mouse/fishing/…），
    /// <c>On</c> = 是否发出。子进程据此改 Bot 的 <c>ProcessLogEnabled</c>，
    /// 日志在 Bot 里就不产生，父进程不做任何文本过滤。
    /// </summary>
    public const string CmdLogSwitch = "logswitch";

    public const string EvtReady = "ready";
    public const string EvtLog = "log";
    public const string EvtState = "state";
    public const string EvtJoined = "joined";
    public const string EvtError = "error";

    /// <summary>服务器弹出对话框（载荷是 <c>MccDialogInfo</c> 的 JSON，放在 Text 字段）。</summary>
    public const string EvtDialog = "dialog";

    /// <summary>服务器关闭对话框（Text 是对话框编号）。</summary>
    public const string EvtDialogEnd = "dlgend";

    /// <summary>服务器打开/刷新了容器窗口（Text 是 <c>MccWindowInfo</c> 的 JSON）。</summary>
    public const string EvtWindow = "window";

    /// <summary>服务器关闭了容器窗口（Text 是窗口编号）。</summary>
    public const string EvtWindowEnd = "winend";

    /// <summary>
    /// 服务器命令列表更新（declare_commands / tab-complete 合并结果）。
    /// Text 是根命令名数组的 JSON（不含前导 '/'），用于 MCCX 输入框的命令建议。
    /// </summary>
    public const string EvtCommands = "commands";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    /// <summary>共享的 JSON 选项（解析子进程事件载荷时用）。</summary>
    internal static JsonSerializerOptions SharedJsonOptions => JsonOptions;

    /// <summary>消息类型（见类注释）。</summary>
    [JsonPropertyName("t")]
    public string Type { get; set; } = string.Empty;

    /// <summary>日志文本 / 输入文本 / 状态名。</summary>
    [JsonPropertyName("v")]
    public string? Text { get; set; }

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("ver")]
    public string? Version { get; set; }

    [JsonPropertyName("on")]
    public bool On { get; set; }

    /// <summary>功能标识（目前只给 <see cref="CmdLogSwitch"/> 用）。</summary>
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("range")]
    public double Range { get; set; }

    [JsonPropertyName("cmin")]
    public int CooldownMinMs { get; set; }

    [JsonPropertyName("cmax")]
    public int CooldownMaxMs { get; set; }

    [JsonPropertyName("lmode")]
    public int LeftMode { get; set; }

    [JsonPropertyName("lhold")]
    public int LeftHoldMs { get; set; }

    [JsonPropertyName("lint")]
    public int LeftIntervalMs { get; set; }

    [JsonPropertyName("ljit")]
    public int LeftJitterPercent { get; set; }

    [JsonPropertyName("lon")]
    public bool LeftOn { get; set; }

    [JsonPropertyName("rmode")]
    public int RightMode { get; set; }

    [JsonPropertyName("rhold")]
    public int RightHoldMs { get; set; }

    [JsonPropertyName("rint")]
    public int RightIntervalMs { get; set; }

    [JsonPropertyName("rjit")]
    public int RightJitterPercent { get; set; }

    [JsonPropertyName("ron")]
    public bool RightOn { get; set; }

    /// <summary>服务器信息过滤模式（0 不过滤 / 1 全屏蔽 / 2 只屏蔽玩家消息 / 3 只显示指定前缀 / 4 只屏蔽指定前缀，见 <c>ServerFilterMode</c>）。</summary>
    [JsonPropertyName("sfmode")]
    public int ServerFilterMode { get; set; }

    /// <summary>"只显示指定前缀"模式的前缀列表（空 = 全放行）。与下面那份<b>相互独立</b>。</summary>
    [JsonPropertyName("sfshow")]
    public string ServerFilterShowPrefix { get; set; } = string.Empty;

    /// <summary>"只屏蔽指定前缀"模式的前缀列表（空 = 全显示）。</summary>
    [JsonPropertyName("sfblk")]
    public string ServerFilterBlockPrefix { get; set; } = string.Empty;

    /// <summary>准星探测距离（格，1-7）：左右键共用。缺省 5（老版本父进程不带该字段时的兜底）。</summary>
    [JsonPropertyName("reach")]
    public double Reach { get; set; } = 5.0;

    [JsonPropertyName("jitter")]
    public int JitterPercent { get; set; }

    [JsonPropertyName("attempts")]
    public int Attempts { get; set; }

    [JsonPropertyName("delay")]
    public int DelayMs { get; set; }

    /// <summary>自动钓鱼·收杆检测用浮漂附近的水花声音（MCC Enable_Sound_Detection，默认开）。</summary>
    [JsonPropertyName("fsnd")]
    public bool FishingSound { get; set; } = true;

    /// <summary>自动钓鱼·收杆检测用浮漂实体速度包（MCC Enable_Velocity_Detection，默认开）。</summary>
    [JsonPropertyName("fvel")]
    public bool FishingVelocity { get; set; } = true;

    /// <summary>自动钓鱼·抛竿超时（秒，MCC Fishing_Timeout，默认 300）。老版本父进程不带该字段时的兜底。</summary>
    [JsonPropertyName("ftmo")]
    public double FishingTimeout { get; set; } = 300.0;

    /// <summary>自动钓鱼·重抛间隔（秒，MCC Cast_Delay，默认 0.4）。老版本父进程不带该字段时的兜底。</summary>
    [JsonPropertyName("fdly")]
    public double FishingCastDelay { get; set; } = 0.4;

    /// <summary>砍怪的攻击生物过滤模式（0 不过滤 / 1 白名单 / 2 黑名单）。</summary>
    [JsonPropertyName("fmode")]
    public int FilterMode { get; set; }

    /// <summary>过滤名单（EntityType 名）。只在 fmode 非 0 时有意义。</summary>
    [JsonPropertyName("mobs")]
    public List<string>? Mobs { get; set; }

    /// <summary>视角移动的目标方向（<c>CmdLook</c> 专用，取值见 <c>MccLookDirection</c>）。</summary>
    [JsonPropertyName("dir")]
    public int Direction { get; set; }

    /// <summary>自动指令的整份指令表（<see cref="CmdAutoCommands"/> 专用）。</summary>
    [JsonPropertyName("acmd")]
    public List<AutoCommandDefinition>? AutoCommands { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>解析一行；空行/坏行返回 null（子进程日志里可能混进非协议文本，不能让程序崩）。</summary>
    public static RunnerMessage? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            return JsonSerializer.Deserialize<RunnerMessage>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
