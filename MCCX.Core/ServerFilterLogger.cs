using MinecraftClient.Logger;

namespace MCCX.Core;

/// <summary>
/// 服务器信息过滤模式（2026-10-04 用户需求：全屏蔽 / 只屏蔽玩家信息 / 只显示或只屏蔽特定前缀，
/// 前缀支持列表多项匹配，2026-10-04 二批）。
/// 只作用于"服务器发来的聊天/系统消息"；连接提示、客户端自身日志、Bot 日志一概不碰。
/// </summary>
public enum ServerFilterMode
{
    /// <summary>不过滤（默认）：服务器消息全部显示。</summary>
    None = 0,

    /// <summary>全屏蔽：服务器聊天/系统消息全部不进日志。</summary>
    BlockAll = 1,

    /// <summary>只屏蔽玩家信息：形如 <c>&lt;玩家名&gt; 消息</c> 的公屏消息不显示，系统消息照常。</summary>
    BlockPlayerChat = 2,

    /// <summary>只显示指定前缀：去颜色码后命中前缀列表中任一项的才显示，其余屏蔽；列表为空等于不过滤。</summary>
    PrefixWhitelist = 3,

    /// <summary>只屏蔽指定前缀：命中前缀列表中任一项的不显示，其余照常；列表为空等于不过滤。</summary>
    BlockPrefix = 4,
}

/// <summary>
/// 包一层 MCC 的 <see cref="ILogger"/> 实现服务器信息过滤（2026-10-04 需求）。
/// <para>
/// 只拦 <c>Chat</c> 频道——它是 <c>McClient.OnTextReceived</c> 打印服务器消息的唯一出口
/// （<c>Log.Chat(color + messageText)</c>），即"服务器发来的聊天/系统消息"。
/// Info/Warn/Error/Debug（连接过程、客户端提示、Bot 日志）全部原样转发，自动化日志绝不误伤。
/// </para>
/// <para>
/// 拦截发生在显示之前：Bot 事件（<c>GetText</c> 等）拿到的仍是完整消息，过滤只影响
/// "看不看得见"，不影响任何自动化行为。挂接方式是覆盖 <c>McClient.Log</c> 公开字段，
/// 不需要改 MCC 源码。
/// </para>
/// </summary>
public sealed class ServerFilterLogger : ILogger
{
    private readonly ILogger _inner;
    private volatile ServerFilterMode _mode;

    /// <summary>
    /// "只显示指定前缀"用的前缀列表（2026-10-05 用户要求：与"只屏蔽"的那份<b>各存各的</b>，
    /// 以前两种方式共用一个输入框，切方式就得出另一份前缀、要反复重填）。
    /// </summary>
    private volatile string[] _showPrefixes = [];

    /// <summary>"只屏蔽指定前缀"用的前缀列表（与 <see cref="_showPrefixes"/> 相互独立）。</summary>
    private volatile string[] _blockPrefixes = [];

    public ServerFilterLogger(ILogger inner, ServerFilterMode mode, string showPrefix, string blockPrefix)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _mode = mode;
        _showPrefixes = ParsePrefixes(showPrefix);
        _blockPrefixes = ParsePrefixes(blockPrefix);
    }

    /// <summary>改过滤参数：连接中 / 已连接都能调，对后续消息即时生效。</summary>
    public void SetFilter(ServerFilterMode mode, string showPrefix, string blockPrefix)
    {
        _mode = mode;
        _showPrefixes = ParsePrefixes(showPrefix);
        _blockPrefixes = ParsePrefixes(blockPrefix);
    }

    /// <summary>
    /// 前缀列表解析：逗号（中英文）、顿号、竖线分隔，逐项去首尾空白、丢空项。
    /// 单个前缀原样工作（老配置与单前缀用法完全不受影响）。
    /// </summary>
    private static string[] ParsePrefixes(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return [];

        string[] parts = prefix.Split(
            [',', '，', '、', '|'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? [] : parts;
    }

    public void Chat(string msg)
    {
        if (ShouldShow(msg))
            _inner.Chat(msg);
    }

    // 与 LoggerBase 同款格式化语义：参数版先格式化、再落到本类的 Chat(string)，
    // 保证无论 MCC 用哪个重载发消息，过滤都不会被绕过。
    public void Chat(string msg, params object[] args) => Chat(string.Format(msg, args));

    public void Chat(object msg) => Chat(msg?.ToString() ?? string.Empty);

    private bool ShouldShow(string msg)
    {
        ServerFilterMode mode = _mode;
        if (mode == ServerFilterMode.None)
            return true;
        if (mode == ServerFilterMode.BlockAll)
            return false;

        string text = StripColorCodes(msg);

        // MCC 的消息标记（系统消息 §7▌§r、签名消息 §2▌§r 之类）去掉颜色码后还剩一个行首 ▌，
        // 会挡住 "<名字>" 判定和前缀匹配；只剥行首这一个（配置开不开标记都稳）。
        if (text.Length > 0 && text[0] == '▌')
            text = text[1..];

        if (mode == ServerFilterMode.BlockPlayerChat)
            return text.Length == 0 || text[0] != '<';

        if (mode == ServerFilterMode.PrefixWhitelist)
        {
            // 只显示指定前缀：列表为空 = 全放行；命中列表里任一前缀才显示
            string[] show = _showPrefixes;
            if (show.Length == 0)
                return true;

            foreach (string p in show)
            {
                if (text.StartsWith(p, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        if (mode == ServerFilterMode.BlockPrefix)
        {
            // 只屏蔽指定前缀：列表为空 = 全放行；命中列表里任一前缀的才挡
            string[] block = _blockPrefixes;
            if (block.Length == 0)
                return true;

            foreach (string p in block)
            {
                if (text.StartsWith(p, StringComparison.Ordinal))
                    return false;
            }
        }

        return true;
    }

    /// <summary>去 § 颜色码：玩家消息格式与前缀都在纯文本上判定（服务器常给消息加装饰色码）。</summary>
    private static string StripColorCodes(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('§') < 0)
            return text ?? string.Empty;

        char[] buffer = new char[text.Length];
        int n = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '§' && i + 1 < text.Length)
            {
                i++; // 格式码连同后面的字符一起丢掉
                continue;
            }

            buffer[n++] = text[i];
        }

        return new string(buffer, 0, n);
    }

    // ---- 其余成员原样转发（params/object 重载直接交给内层：语义等价且不涉及过滤）----

    public bool DebugEnabled { get => _inner.DebugEnabled; set => _inner.DebugEnabled = value; }
    public bool WarnEnabled { get => _inner.WarnEnabled; set => _inner.WarnEnabled = value; }
    public bool InfoEnabled { get => _inner.InfoEnabled; set => _inner.InfoEnabled = value; }
    public bool ErrorEnabled { get => _inner.ErrorEnabled; set => _inner.ErrorEnabled = value; }
    public bool ChatEnabled { get => _inner.ChatEnabled; set => _inner.ChatEnabled = value; }

    public void Info(string msg) => _inner.Info(msg);
    public void Info(string msg, params object[] args) => _inner.Info(msg, args);
    public void Info(object msg) => _inner.Info(msg);

    public void Debug(string msg) => _inner.Debug(msg);
    public void Debug(string msg, params object[] args) => _inner.Debug(msg, args);
    public void Debug(object msg) => _inner.Debug(msg);

    public void PacketDebug(string msg) => _inner.PacketDebug(msg);
    public void PacketDebug(string msg, params object[] args) => _inner.PacketDebug(msg, args);
    public void PacketDebug(object msg) => _inner.PacketDebug(msg);

    public void Warn(string msg) => _inner.Warn(msg);
    public void Warn(string msg, params object[] args) => _inner.Warn(msg, args);
    public void Warn(object msg) => _inner.Warn(msg);

    public void Error(string msg) => _inner.Error(msg);
    public void Error(string msg, params object[] args) => _inner.Error(msg, args);
    public void Error(object msg) => _inner.Error(msg);
}
