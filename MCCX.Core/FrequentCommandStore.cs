using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCCX.Core;

/// <summary>一条“常用命令”。</summary>
public sealed class FrequentCommand
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>累计使用次数（自动记录时累加）。</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; } = 1;

    [JsonPropertyName("usedAt")]
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// 这条命令能不能被**自动**记录。
    ///
    /// 只收 MCC 内部命令（/ 开头），聊天内容不算命令；
    /// 含口令的命令一律不自动入库——它们的参数就是密码本身，
    /// 落成明文文件等于把密码写在磁盘上。
    /// </summary>
    public static bool IsAutoRecordable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '/')
            return false;

        // "//xxx" 是把 "/xxx" 原样发到聊天，不是内部命令
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            return false;

        int end = 1;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]))
            end++;

        string name = trimmed[1..end].ToLowerInvariant();
        if (SecretCommandNames.Contains(name, StringComparer.Ordinal))
            return false;

        // /dialog input、/dialog set 的取值就是口令本身
        if (name == "dialog")
        {
            int second = end;
            while (second < trimmed.Length && char.IsWhiteSpace(trimmed[second]))
                second++;

            int secondEnd = second;
            while (secondEnd < trimmed.Length && !char.IsWhiteSpace(trimmed[secondEnd]))
                secondEnd++;

            string sub = trimmed[second..secondEnd].ToLowerInvariant();
            return sub is not ("input" or "set");
        }

        return true;
    }

    /// <summary>参数就是口令的命令名（不含前导斜杠，全小写）：这些绝不自动入库。</summary>
    private static readonly string[] SecretCommandNames =
    [
        "login", "register", "password", "passwd", "pwd", "auth", "authenticate",
        "changepassword", "changepass", "setpassword",
    ];
}

/// <summary>
/// 常用命令的明文 JSON 存取（命令本身不算敏感数据；含口令的命令不会被自动记录，见
/// <see cref="FrequentCommand.IsAutoRecordable"/>）。
///
/// 目录规则与账号库一致：优先 exe 同目录（整文件夹拷走就能用），
/// 不可写时退到 %LOCALAPPDATA%\MCCX。
/// </summary>
public sealed class FrequentCommandStore
{
    /// <summary>最多保留条数：够翻又不至于让下拉列表变成一堵墙。</summary>
    public const int MaxEntries = 30;

    private const string FileName = "frequent-commands.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public FrequentCommandStore(string? directory = null)
    {
        _path = Path.Combine(directory ?? DefaultDirectory, FileName);
    }

    /// <summary>上次读/写失败的原因；成功时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>读取已保存的常用命令；文件不存在返回空列表，文件坏了按空列表启动（不崩）。</summary>
    public List<FrequentCommand> Load()
    {
        LastError = null;

        try
        {
            if (!File.Exists(_path))
                return [];

            string json = File.ReadAllText(_path);
            List<FrequentCommand>? list = JsonSerializer.Deserialize<List<FrequentCommand>>(json, JsonOptions);
            if (list is null)
                return [];

            // 只留有内容的、按内容去重，避免手改文件后出现空行/重复项
            HashSet<string> seen = new(StringComparer.Ordinal);
            List<FrequentCommand> result = [];
            foreach (FrequentCommand item in list)
            {
                string text = (item.Text ?? string.Empty).Trim();
                if (text.Length == 0 || !seen.Add(text))
                    continue;

                item.Text = text;
                result.Add(item);
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            LastError = ex.Message;
            return [];
        }
    }

    /// <summary>整体写回。失败只记 <see cref="LastError"/>，不抛（常用命令丢了也不该影响挂机）。</summary>
    public bool Save(IReadOnlyList<FrequentCommand> entries)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(entries, JsonOptions);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _path, overwrite: true);
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>exe 目录能写就用它，写不了退到 %LOCALAPPDATA%\MCCX。</summary>
    private static string DefaultDirectory
    {
        get
        {
            string exeDirectory = AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (IsWritable(exeDirectory))
                return exeDirectory;

            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCCX");

            try
            {
                return IsWritable(fallback) ? fallback : exeDirectory;
            }
            catch
            {
                return exeDirectory;
            }
        }
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, ".mccx-cmd-writetest");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
