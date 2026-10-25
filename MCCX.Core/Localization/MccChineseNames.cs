using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MCCX.Core.Localization;

/// <summary>
/// 中文名表（物品 / 附魔的名字），界面上就不再是 "Diamond Sword" 而是 "钻石剑"。
///
/// <para>来源：官方客户端资源镜像 mcmeta（GitHub，分支名 <c>&lt;版本&gt;-assets</c>）里的
/// <c>assets/minecraft/lang/zh_cn.json</c>，按当前连接的版本下一次，缓存到
/// <c>UserData\lang\zh_cn-&lt;版本&gt;.json</c>，以后离线也能用。
/// 直连 raw.githubusercontent.com 在国内时通时不通，所以先走 GitHub 加速口（见 <see cref="LanguageHosts"/>）。</para>
///
/// <para>为什么不用 MCC 自己的语言设置：MCC 只带了 1.19.3 的 en_us.json，
/// 它下载语言文件用的也是 1.19.3 的资源索引，新版物品（重锤、树脂砖之类）在它里面根本没有名字。
/// 这里直接查当前版本的语言表，顺带把 MCC 叫不出名字的新物品也补上。</para>
///
/// <para>查不到就返回 null，调用方回退到 MCC 的英文名：没网、没缓存、表里没这一条，
/// 都只是"名字还是英文"，不影响界面。</para>
/// </summary>
public static class MccChineseNames
{
    /// <summary>官方资源镜像上的相对路径：<c>{0}</c> 是版本（分支名是 <c>&lt;版本&gt;-assets</c>）。</summary>
    private const string LanguageFilePath =
        "misode/mcmeta/{0}-assets/assets/minecraft/lang/zh_cn.json";

    /// <summary>
    /// 语言文件的几个镜像（<c>{0}</c> = 上面的相对路径）。
    /// 直连 raw.githubusercontent.com 在国内时通时不通，先用两个 GitHub 加速口。
    /// </summary>
    private static readonly string[] LanguageHosts =
    [
        "https://ghfast.top/https://raw.githubusercontent.com/{0}",
        "https://gh-proxy.com/https://raw.githubusercontent.com/{0}",
        "https://raw.githubusercontent.com/{0}",
    ];

    /// <summary>jsDelivr 的路径格式和上面不一样，单独拼一份（它的缓存对语言文件是有效的）。</summary>
    private const string JsDelivrUrl =
        "https://cdn.jsdelivr.net/gh/misode/mcmeta@{0}-assets/assets/minecraft/lang/zh_cn.json";

    /// <summary>拿不到当前连接的版本时依次试这些（新的在前）。</summary>
    private static readonly string[] FallbackVersions = ["26.1", "1.21.11", "1.21.4"];

    /// <summary>条数明显不对（半截响应、错误页）就不认这份表。</summary>
    private const int MinimumEntries = 1000;

    private static readonly object Gate = new();

    /// <summary>等这次加载结果的回调（界面正开着面板时用：好了重发一次，英文名换成中文）。</summary>
    private static readonly List<Action> Pending = [];

    private static readonly HttpClient Http = CreateClient();

    private static volatile Dictionary<string, string>? _names;

    private static bool _loading;

    /// <summary>表已经就绪（这时候 <see cref="Lookup"/> 才有东西可查）。</summary>
    public static bool IsReady => _names is not null;

    /// <summary>表里的条目数（日志用）。</summary>
    public static int Count => _names?.Count ?? 0;

    /// <summary>
    /// 在后台把中文名表准备好（幂等，同一个进程只下一次）。
    ///
    /// <para><paramref name="onLoaded"/> 只在"这次真的加载成功"时回调一次；已经就绪的话<b>不</b>回调
    /// （否则调用方在回调里重发面板、重发时又注册一次，就会转圈）。</para>
    /// </summary>
    public static void EnsureLoaded(string? version, Action? onLoaded = null)
    {
        bool start = false;

        lock (Gate)
        {
            if (_names is not null)
                return;

            if (onLoaded is not null)
                Pending.Add(onLoaded);

            if (!_loading)
            {
                _loading = true;
                start = true;
            }
        }

        if (start)
            _ = Task.Run(() => LoadAsync(version));
    }

    /// <summary>按完整的语言键查中文名（如 <c>item.minecraft.diamond_sword</c>）。</summary>
    public static string? Lookup(string key)
    {
        Dictionary<string, string>? names = _names;
        return names is not null && names.TryGetValue(key, out string? value) ? value : null;
    }

    /// <summary>物品（含方块的物品形态）注册名 → 中文名。</summary>
    public static string? ItemName(string registryName)
    {
        if (string.IsNullOrEmpty(registryName))
            return null;

        // 和 MCC 的 Item.GetTypeString 同一个顺序：物品优先，其次是方块
        return Lookup("item.minecraft." + registryName) ?? Lookup("block.minecraft." + registryName);
    }

    /// <summary>附魔注册名（snake_case，如 sharpness）→ 中文名。</summary>
    public static string? EnchantmentName(string registryName)
    {
        return string.IsNullOrEmpty(registryName)
            ? null
            : Lookup("enchantment.minecraft." + registryName);
    }

    private static async Task LoadAsync(string? version)
    {
        try
        {
            foreach (string candidate in Versions(version))
            {
                Dictionary<string, string>? table = TryGetCached(candidate)
                    ?? await TryDownloadAsync(candidate).ConfigureAwait(false);

                if (table is not null)
                {
                    Publish(table);
                    return;
                }
            }
        }
        catch
        {
            // 中文名是锦上添花：出错就继续用英文，不打扰任何人
        }
        finally
        {
            lock (Gate)
            {
                _loading = false;
                if (_names is null)
                    Pending.Clear(); // 没成功：等下次开面板再试，这次的回调作废
            }
        }
    }

    /// <summary>加载好了：记下来，然后叫醒等着刷界面的回调。</summary>
    private static void Publish(Dictionary<string, string> table)
    {
        Action[] callbacks;
        lock (Gate)
        {
            _names = table;
            callbacks = [.. Pending];
            Pending.Clear();
        }

        foreach (Action callback in callbacks)
        {
            try
            {
                callback();
            }
            catch
            {
                // 回调只是"重发一次面板"：失败就当这次没刷
            }
        }
    }

    private static Dictionary<string, string>? TryGetCached(string version)
    {
        try
        {
            string? path = CachePath(version);
            return path is not null && File.Exists(path)
                ? Parse(File.ReadAllText(path, Encoding.UTF8))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<Dictionary<string, string>?> TryDownloadAsync(string version)
    {
        foreach (string url in LanguageUrls(version))
        {
            try
            {
                using HttpResponseMessage response = await Http.GetAsync(url).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Dictionary<string, string>? table = Parse(json);
                if (table is null)
                    continue; // 半截响应 / 错误页：换下一个镜像

                try
                {
                    // 缓存是可选的：写不进去（只读目录之类）这次照样用，只是下次还得再下
                    string? path = CachePath(version);
                    if (path is not null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                        string temp = path + "." + Environment.ProcessId + ".tmp";
                        await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false)).ConfigureAwait(false);
                        File.Move(temp, path, overwrite: true);
                    }
                }
                catch
                {
                    // 忽略
                }

                return table;
            }
            catch
            {
                // 这个镜像连不上：换下一个
            }
        }

        return null;
    }

    /// <summary>这个版本可以试的几个下载地址（按好用程度排）。</summary>
    private static IEnumerable<string> LanguageUrls(string version)
    {
        string path = string.Format(LanguageFilePath, version);

        foreach (string host in LanguageHosts)
            yield return string.Format(host, path);

        yield return string.Format(JsDelivrUrl, version);
    }

    private static Dictionary<string, string>? Parse(string json)
    {
        try
        {
            Dictionary<string, string>? table = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return table is { Count: >= MinimumEntries } ? table : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>先试当前连接的版本，再试兜底版本（去掉重复）。</summary>
    private static IEnumerable<string> Versions(string? version)
    {
        if (!string.IsNullOrWhiteSpace(version) && version.Contains('.'))
        {
            string trimmed = version.Trim();
            if (!FallbackVersions.Contains(trimmed, StringComparer.Ordinal))
                yield return trimmed;
        }

        foreach (string fallback in FallbackVersions)
            yield return fallback;
    }

    private static string? CachePath(string version)
    {
        if (!OperatingSystem.IsWindows())
            return null; // 用户数据目录只有 Windows 上才有（MCCX 是 Windows 应用）

        // 版本号来自协议表或者上面的兜底表，这里再挡一次路径穿越：缓存文件名只留安全字符
        StringBuilder safe = new(version.Length);
        foreach (char c in version)
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        }

        return Path.Combine(UserDataPaths.Directory, "lang", "zh_cn-" + safe + ".json");
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MCCX/1.0 (Minecraft Console Client X)");
        return client;
    }
}
