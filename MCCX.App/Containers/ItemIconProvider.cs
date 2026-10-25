using System.Collections.Concurrent;
using System.Net.Http;
using MCCX.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MCCX_App.Containers;

/// <summary>
/// 物品图标：按物品注册名（snake_case）取一张图，供容器窗口的格子显示。
///
/// <para>来源与顺序（全部惰性、按物品缓存；同一个物品的图标整份程序只下一次）：</para>
/// <list type="number">
///   <item><description>磁盘缓存：<c>UserData\item-icons-v3\&lt;物品名&gt;.png</c>，命中即用，离线也能显示；
///   （v3 是"一律原版素材"这一版；v1/v2 是旧版留下的，v2 里混着第三方渲染图，留着不用管、也不去删）</description></item>
///   <item><description><b>原版贴图</b>：物品是那张 16px 像素画（官方资源镜像 mcmeta 的
///   <c>textures/item/&lt;物品名&gt;.png</c>）；方块类物品先问 <c>textures/block/&lt;物品名&gt;.png</c>
///   （火把、梯子这类只有方块贴图，而蛋糕反过来只有物品贴图，所以两条都问，只是顺序按名字归类来）。
///   下载后用最近邻放大 4 倍（16px → 64px），免得交给界面被插值拉糊；
///   少数名字对不上的走 <see cref="TextureAliases"/>（时钟 / 指南针是动画物品，弩取待发那一张）。</description></item>
///   <item><description><b>最后一档</b>：原版没有平铺贴图的少数物品（盾牌、旗帜、床这种靠模型画出来的东西）
///   用 minecraftitemids.com 的成品渲染图，至少格子里有个图标。这张图<b>不写进磁盘缓存</b>，
///   只放本次运行的临时目录：下次启动会重新走一遍原版贴图那条路，网络抖一下不会把画风永久改掉。</description></item>
/// </list>
///
/// <para>2026-10-16 用户要求"画风统一"：方块和物品都走原版像素画（方块就是平铺贴图），
/// 不再像以前那样方块用第三方渲染图，两种画风摆在一起一眼就看得出不一样。</para>
///
/// <para>镜像：raw.githubusercontent.com 在国内时通时不通，所以同一张图会同时问几个前端镜像
/// （谁先回来用谁，见 <see cref="TextureHosts"/>）；都不行才退到渲染图。</para>
///
/// <para>取不到就当没有图标（格子只显示名字与数量的提示），不弹错、不阻塞界面：
/// <see cref="TryGetFile"/> 立即返回缓存里的结果，未命中时后台下载，好了再由调用方刷一次。</para>
/// </summary>
public static class ItemIconProvider
{
    /// <summary>最后的兜底图标（靠模型画出来、没有平铺贴图的物品）。128px 够高 DPI 缩着用。</summary>
    private const string RenderedIconUrl = "https://minecraftitemids.com/item/128/{0}.png";

    /// <summary>
    /// 原版贴图名和物品注册名对不上的几个：
    /// 时钟 / 指南针这些是动画物品，贴图叫 <c>&lt;名字&gt;_00.png</c>（第 0 帧）；弩平时显示的是待发那一张。
    /// </summary>
    private static readonly Dictionary<string, string> TextureAliases = new(StringComparer.Ordinal)
    {
        ["clock"] = "clock_00",
        ["compass"] = "compass_00",
        ["recovery_compass"] = "recovery_compass_00",
        ["crossbow"] = "crossbow_standby",
    };

    /// <summary>mcmeta 上的相对路径：<c>{0}</c> 版本、<c>{1}</c> item/block、<c>{2}</c> 物品名。</summary>
    private const string RawTexturePath = "misode/mcmeta/{0}-assets/assets/minecraft/textures/{1}/{2}.png";

    /// <summary>
    /// 原版贴图的几个镜像（<c>{0}</c> = 上面的相对路径）。
    /// 前两个是国内能直接用的 GitHub 加速口（比直连稳得多），最后一个是直连兜底。
    /// </summary>
    private static readonly string[] TextureHosts =
    [
        "https://ghfast.top/https://raw.githubusercontent.com/{0}",
        "https://gh-proxy.com/https://raw.githubusercontent.com/{0}",
        "https://raw.githubusercontent.com/{0}",
    ];

    /// <summary>原版贴图是 16px 像素画，放大倍数（32px 以上的一律不放大）。</summary>
    private const int TextureUpscale = 4;

    /// <summary>没拿到版本号时按这一版的贴图取（原版贴图跨版本基本一致，取新版的兼容面最大）。</summary>
    private const string DefaultTextureVersion = "26.1";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>物品名 → 图标文件路径（null = 试过了，没有）。</summary>
    private static readonly ConcurrentDictionary<string, string?> Resolved = new(StringComparer.Ordinal);

    /// <summary>正在下载的物品名 → 下载任务（同一个物品的多个格子共用一次下载）。</summary>
    private static readonly ConcurrentDictionary<string, Task<string?>> InFlight = new(StringComparer.Ordinal);

    /// <summary>本次运行里"只拿到兜底渲染图"的物品：再被画到就补一次原版贴图（见 <see cref="RetryFlat"/>）。</summary>
    private static readonly ConcurrentDictionary<string, byte> Degraded = new(StringComparer.Ordinal);

    /// <summary>补正已经做过的物品（一个物品每份程序只补一次）。</summary>
    private static readonly ConcurrentDictionary<string, byte> RetriedFlat = new(StringComparer.Ordinal);

    private static string? _iconsDirectory;

    private static string? _sessionDirectory;

    /// <summary>
    /// 某个物品的图标<b>换新</b>了（兜底渲染图在后台补成了原版贴图）。参数是物品注册名。
    ///
    /// <para>订阅方（容器面板）据此把面板上同一个物品的格子重画一遍：不然只有触发补正的那一格
    /// 变了样，旁边同一种物品还留着旧图，画风照样是混的。</para>
    /// </summary>
    public static event Action<string>? IconAvailable;

    /// <summary>
    /// 立即取缓存里的图标文件路径；没有就返回 null 并顺手在后台下一张
    /// （下载完成后 <paramref name="onReady"/> 会在后台线程回调，调用方负责切回 UI 线程）。
    /// </summary>
    /// <param name="itemId">物品注册名。</param>
    /// <param name="minecraftVersion">哪一版的贴图（null = 用默认版本）。</param>
    /// <param name="block">是不是"方块的物品形态"（决定先问 <c>block/</c> 还是 <c>item/</c> 那张贴图）。</param>
    /// <param name="onReady">下载好了的回调（参数是缓存文件路径）。</param>
    public static string? TryGetFile(string itemId, string? minecraftVersion, bool block, Action<string>? onReady)
    {
        string key = Normalize(itemId);
        if (key.Length == 0)
            return null;

        if (Resolved.TryGetValue(key, out string? cached))
        {
            if (cached is null)
                return null; // 之前试过，没有

            RetryFlat(key, minecraftVersion, block); // 这次只拿到兜底渲染图的话，顺手再问问原版贴图
            return cached;
        }

        string? local = Path.Combine(IconsDirectory, key + ".png");
        if (File.Exists(local))
        {
            Resolved[key] = local;
            return local;
        }

        Task<string?> task = InFlight.GetOrAdd(key, _ => DownloadAsync(key, minecraftVersion, block));
        if (task.IsCompleted)
        {
            string? result = task.Result;
            Resolved[key] = result;
            InFlight.TryRemove(key, out _);
            return result;
        }

        if (onReady is not null)
        {
            _ = task.ContinueWith(
                t =>
                {
                    string? file = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                    Resolved[key] = file;
                    InFlight.TryRemove(key, out _);
                    if (file is not null)
                    {
                        try
                        {
                            onReady(file);
                        }
                        catch
                        {
                            // 界面上这个格子可能早就没了：忽略
                        }
                    }
                },
                TaskScheduler.Default);
        }

        return null;
    }

    /// <summary>
    /// 图标缓存目录（首次访问时创建）。只存原版贴图那一档（见类注释）；v1/v2 是旧版目录，
    /// 留着不管也不删（想清理整个文件夹删掉即可，用到的会自己重下）。
    /// </summary>
    private static string IconsDirectory
    {
        get
        {
            if (_iconsDirectory is not null)
                return _iconsDirectory;

            string directory = Path.Combine(UserDataPaths.Directory, "item-icons-v3");
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch
            {
                // 建不出来就用临时目录：图标是纯缓存，丢了没有影响
                directory = Path.Combine(Path.GetTempPath(), "MCCX-item-icons-v3");
                try
                {
                    Directory.CreateDirectory(directory);
                }
                catch
                {
                    // 连临时目录都不行：下面的写盘会失败，按"没有图标"处理
                }
            }

            _iconsDirectory = directory;
            return directory;
        }
    }

    /// <summary>
    /// 兜底渲染图的落脚点：本次运行的临时目录。刻意不写进磁盘缓存，
    /// 这样下次启动会重新走一遍原版贴图那条路：网络抖一下换个画风的事不会一直背着。
    /// </summary>
    private static string SessionFile(string key)
    {
        string directory = _sessionDirectory ??= CreateSessionDirectory();
        return Path.Combine(directory, key + ".png");
    }

    private static string CreateSessionDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MCCX-item-icons-v3-session");
        try
        {
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch
        {
            // 临时目录都建不出来就退回常驻缓存目录：宁可留下兜底图，也别整个没图标
            return IconsDirectory;
        }
    }

    /// <summary>
    /// 一个候选来源：同一张图的几个镜像地址、是不是原版贴图（决定要不要最近邻放大）、
    /// 以及要不要落磁盘缓存（兜底渲染图不落，见 <see cref="SessionFile"/>）。
    /// </summary>
    private readonly record struct IconSource(IReadOnlyList<string> Urls, bool RawTexture, bool CacheIt = true);

    /// <summary>
    /// 原版贴图请求等多久：有就是有、没有就是 404，正常几百毫秒就有结果；
    /// 镜像偶尔会挂住（连接不回应），而"这个物品没有这张贴图"是很常见的事，别一直等。
    /// </summary>
    private static readonly TimeSpan TextureTimeout = TimeSpan.FromSeconds(3);

    /// <summary>渲染图请求等多久：拿一张真图回来值得多等一下。</summary>
    private static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(10);

    private static Task<string?> DownloadAsync(string key, string? minecraftVersion, bool block)
        => DownloadCoreAsync(key, minecraftVersion, block, flatOnly: false);

    /// <summary>只走原版贴图那一档（补正用：兜底渲染图已经在手上，不必再问一遍）。</summary>
    private static Task<string?> DownloadFlatAsync(string key, string? minecraftVersion, bool block)
        => DownloadCoreAsync(key, minecraftVersion, block, flatOnly: true);

    private static async Task<string?> DownloadCoreAsync(string key, string? minecraftVersion, bool block, bool flatOnly)
    {
        string target = Path.Combine(IconsDirectory, key + ".png");

        foreach (IconSource source in BuildSources(key, minecraftVersion, block))
        {
            if (flatOnly && !source.CacheIt)
                continue;

            byte[]? bytes = await FetchFastestAsync(source.Urls, source.RawTexture ? TextureTimeout : RenderTimeout)
                .ConfigureAwait(false);
            if (bytes is not { Length: > 0 })
                continue;

            try
            {
                // 原版贴图先最近邻放大：像素画让界面自己插值会糊成一片
                if (source.RawTexture)
                    bytes = await UpscaleNearestAsync(bytes).ConfigureAwait(false) ?? bytes;

                // 原版贴图落磁盘缓存；兜底渲染图只放临时目录（下次启动重新走原版那条路）
                string path = source.CacheIt ? target : SessionFile(key);

                // 先写临时文件再改名：半截文件不会留在缓存里被当成有效图片
                string temp = path + "." + Environment.ProcessId + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);

                if (!source.CacheIt)
                    Degraded[key] = 0;

                return path;
            }
            catch
            {
                // 写盘失败：换下一个来源试试
            }
        }

        return null;
    }

    /// <summary>
    /// 上次只拿到兜底渲染图的物品：这次顺手再问一遍原版贴图（一个物品每份程序只补一次）。
    ///
    /// <para>起因（2026-10-16 用户反馈）：原版贴图那一步偶尔撞上网络抖动，兜底渲染图就留在了缓存里，
    /// 那个物品从此一直是另一种画风。现在兜底图不落磁盘缓存，并且这里补一次：补到了就换成原版贴图，
    /// 再喊一声 <see cref="IconAvailable"/> 让面板把同一种物品的格子重画。</para>
    /// </summary>
    private static void RetryFlat(string key, string? minecraftVersion, bool block)
    {
        if (!Degraded.ContainsKey(key) || !RetriedFlat.TryAdd(key, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                string? path = await DownloadFlatAsync(key, minecraftVersion, block).ConfigureAwait(false);
                if (path is null)
                    return;

                Degraded.TryRemove(key, out _);
                Resolved[key] = path;
                IconAvailable?.Invoke(key);
            }
            catch
            {
                // 补不上就算了：这一份先按兜底图显示
            }
        });
    }

    /// <summary>
    /// 按顺序给出候选来源（前面的先问，拿不到就换下一个）。
    ///
    /// <para>2026-10-16 用户要求"画风统一"：一律原版贴图（物品是 16px 像素画，方块就是平铺贴图，
    /// 同一套像素画风）。方块 / 物品的差别只决定先问哪一边（省掉一次必然的 404），不影响最终画风；
    /// 第三方渲染图留给"原版没有平铺贴图"的少数物品当最后一档。</para>
    /// </summary>
    private static IEnumerable<IconSource> BuildSources(string key, string? minecraftVersion, bool block)
    {
        // 贴图在哪一版都一样（物品不会被删，新物品只会出现在新版本里），所以只问一个版本：
        // 多问两个版本就是白白多等两轮 404。
        string version = TextureVersion(minecraftVersion);

        IconSource sprite = TextureSource(version, "item", key);
        IconSource blockSheet = TextureSource(version, "block", key);

        // 先问对的那一边：物品贴图在 item/ 下，方块贴图在 block/ 下
        yield return block ? blockSheet : sprite;

        // 名字对不上的少数几个（时钟 / 指南针的动画帧、弩的待发状态）
        if (TextureAliases.TryGetValue(key, out string? alias))
            yield return TextureSource(version, "item", alias);

        // 另一边也问一遍：蛋糕只有 item/ 那张，火把只有 block/ 那张，光看名字归类猜不准
        yield return block ? sprite : blockSheet;

        // 最后一档：原版没有平铺贴图的（盾牌 / 旗帜 / 床），拿张渲染图至少别空着；这张不落缓存
        yield return new IconSource([string.Format(RenderedIconUrl, key)], RawTexture: false, CacheIt: false);
    }

    /// <summary>一个版本的一份原版贴图：几个镜像都算出来，取的时候一起问。</summary>
    private static IconSource TextureSource(string version, string kind, string key)
    {
        string path = string.Format(RawTexturePath, version, kind, key);
        return new IconSource([.. TextureHosts.Select(host => string.Format(host, path))], RawTexture: true);
    }

    /// <summary>
    /// 取一个来源的图片：镜像一起问，谁先回来用谁。
    ///
    /// <para>前端镜像经常抽风（连接被重置、挂住不回），挨个等超时太慢了；只有一个地址时（渲染图）
    /// 就自己试两次，网络抖一下还有机会。</para>
    /// </summary>
    private static async Task<byte[]?> FetchFastestAsync(IReadOnlyList<string> urls, TimeSpan timeout)
    {
        if (urls.Count == 1)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(300).ConfigureAwait(false);

                byte[]? bytes = await TryFetchAsync(urls[0], timeout).ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                    return bytes;
            }

            return null;
        }

        List<Task<byte[]?>> pending = [.. urls.Select(url => TryFetchAsync(url, timeout))];
        while (pending.Count > 0)
        {
            Task<byte[]?> finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);

            if (finished.Status == TaskStatus.RanToCompletion && finished.Result is { Length: > 0 } bytes)
                return bytes;
        }

        return null;
    }

    /// <summary>取一个地址的图片；任何失败（404 / 连不上 / 超时）都算"这个来源没有"。</summary>
    private static async Task<byte[]?> TryFetchAsync(string url, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 最近邻放大（16px → 64px）：原版像素画的方块感留得住，
    /// 失败（不是图片 / 解码不了）就返回 null，按原样存。
    /// </summary>
    private static async Task<byte[]?> UpscaleNearestAsync(byte[] png)
    {
        try
        {
            using InMemoryRandomAccessStream input = new();
            DataWriter writer = new(input);
            writer.WriteBytes(png);
            await writer.StoreAsync();
            writer.DetachStream();
            writer.Dispose();
            input.Seek(0);

            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);

            // 已经是大图的（128px 渲染图之类）不要动
            uint width = decoder.PixelWidth;
            uint height = decoder.PixelHeight;
            if (width is 0 or > 64 || height is 0 or > 64)
                return null;

            BitmapTransform transform = new()
            {
                ScaledWidth = width * TextureUpscale,
                ScaledHeight = height * TextureUpscale,
                InterpolationMode = BitmapInterpolationMode.NearestNeighbor,
            };

            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            using InMemoryRandomAccessStream output = new();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();

            uint size = (uint)output.Size;
            using DataReader reader = new(output.GetInputStreamAt(0));
            await reader.LoadAsync(size);

            byte[] result = new byte[size];
            reader.ReadBytes(result);
            return result;
        }
        catch
        {
            // 解码失败：按原图存（界面还能显示，只是会有点糊）
            return null;
        }
    }

    /// <summary>这次用哪个版本的贴图：调用方给了具体版本就用它，否则用默认版本。</summary>
    private static string TextureVersion(string? minecraftVersion)
        => !string.IsNullOrWhiteSpace(minecraftVersion) && minecraftVersion.Contains('.')
            ? minecraftVersion.Trim()
            : DefaultTextureVersion;

    private static string Normalize(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return string.Empty;

        // 物品名是注册名：只留小写字母、数字和下划线，顺便挡掉路径穿越
        string key = itemId.Trim().ToLowerInvariant();
        foreach (char c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return string.Empty;
        }

        return key;
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MCCX/1.0 (Minecraft Console Client X)");
        return client;
    }
}
