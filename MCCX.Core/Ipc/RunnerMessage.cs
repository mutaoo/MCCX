using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCCX.Core.Ipc;

/// <summary>
/// 多开父子进程之间的一行式协议（一行一个 JSON）。
///
/// 方向与类型：
/// 父 → 子（命令）：<c>connect</c> / <c>disconnect</c> / <c>input</c> / <c>attack</c> /
/// <c>mouse</c> / <c>fishing</c> / <c>reconnect</c> / <c>quit</c>
/// 子 → 父（事件）：<c>ready</c> / <c>log</c> / <c>state</c> / <c>joined</c> / <c>error</c>
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
    public const string CmdReconnect = "reconnect";
    public const string CmdQuit = "quit";

    public const string EvtReady = "ready";
    public const string EvtLog = "log";
    public const string EvtState = "state";
    public const string EvtJoined = "joined";
    public const string EvtError = "error";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

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

    [JsonPropertyName("range")]
    public double Range { get; set; }

    [JsonPropertyName("cmin")]
    public int CooldownMinMs { get; set; }

    [JsonPropertyName("cmax")]
    public int CooldownMaxMs { get; set; }

    [JsonPropertyName("mode")]
    public int Mode { get; set; }

    [JsonPropertyName("side")]
    public int Side { get; set; }

    [JsonPropertyName("hold")]
    public int HoldMs { get; set; }

    [JsonPropertyName("interval")]
    public int IntervalMs { get; set; }

    [JsonPropertyName("jitter")]
    public int JitterPercent { get; set; }

    [JsonPropertyName("attempts")]
    public int Attempts { get; set; }

    [JsonPropertyName("delay")]
    public int DelayMs { get; set; }

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
