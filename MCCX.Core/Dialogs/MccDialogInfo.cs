using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCCX.Core.Dialogs;

/// <summary>
/// 服务器下发的对话框（MCC 的 Dialog 系统，1.21.6+ 服务端对话框）在界面层的投影：
/// 标题、正文、输入项、动作按钮。纯数据、不含 MCC 类型，可以直接过 IPC 管道传输。
///
/// 为什么要单独建模：密码这类输入在控制台里没法直接打字（MCC 把它渲染成输入框，
/// 而 MCCX 的输入框是聊天/命令通道），所以由界面弹一个输入框收集取值，
/// 再由 MCCX 组装回写，密码不进日志、不进聊天历史。
/// </summary>
public sealed class MccDialogInfo
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    /// <summary>对话框编号：服务器关闭同一条对话框时用它对上号。</summary>
    [JsonPropertyName("rev")]
    public int Revision { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>正文（多段已换行拼好，只含纯文本）。</summary>
    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("inputs")]
    public List<MccDialogField> Inputs { get; set; } = [];

    [JsonPropertyName("actions")]
    public List<MccDialogAction> Actions { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>解析一段 JSON；坏数据返回 null（不能让界面因为一条坏消息崩掉）。</summary>
    public static MccDialogInfo? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<MccDialogInfo>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>对话框里的一个输入项。</summary>
public sealed class MccDialogField
{
    /// <summary>输入键（回写时用它，不能改）。</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>类型：text / boolean / option / number / unknown。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = MccDialogKind.Text;

    [JsonPropertyName("value")]
    public string InitialValue { get; set; } = string.Empty;

    [JsonPropertyName("max")]
    public int MaxLength { get; set; } = 32;

    [JsonPropertyName("multiline")]
    public bool Multiline { get; set; }

    /// <summary>option 类型的候选值。</summary>
    [JsonPropertyName("options")]
    public List<string>? Options { get; set; }

    [JsonPropertyName("start")]
    public float Start { get; set; }

    [JsonPropertyName("end")]
    public float End { get; set; }

    /// <summary>
    /// 界面上按密码遮蔽显示。MCC 的对话框本身没有"密码"概念（就是普通文本），
    /// 这里按键名/标签猜测，猜不准也只是显示方式不同，不影响回写。
    /// </summary>
    [JsonPropertyName("secret")]
    public bool IsSecret { get; set; }
}

/// <summary>对话框底部的一个动作按钮。</summary>
public sealed class MccDialogAction
{
    /// <summary>动作编号（MCC 的 click 从 1 开始）。</summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>是否是"取消"类动作（界面把它放到关闭按钮上）。</summary>
    [JsonPropertyName("cancel")]
    public bool IsCancel { get; set; }
}

/// <summary>输入项类型常量（与 <see cref="MccDialogField.Kind"/> 对应）。</summary>
public static class MccDialogKind
{
    public const string Text = "text";
    public const string Boolean = "boolean";
    public const string Option = "option";
    public const string Number = "number";
    public const string Unknown = "unknown";
}

/// <summary>界面提交对话框时下发的载荷：填好的取值 + 要点的动作。</summary>
public sealed class MccDialogSubmit
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    [JsonPropertyName("a")]
    public int ActionIndex { get; set; }

    [JsonPropertyName("v")]
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static MccDialogSubmit? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<MccDialogSubmit>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
