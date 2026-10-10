using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCCX.Core;

/// <summary>
/// 界面级全局设置的落盘（<c>ui-settings.json</c>，在用户数据目录 <c>UserData</c> 里，
/// 与账号库同目录）。
///
/// 与账号无关、也不该进账号库（那是按账号分条的加密数据），所以单独一个明文小文件；
/// 里面没有敏感信息（暗色模式开关、账号是否按服务器分组）。
/// </summary>
public static class UiSettingsStore
{
    private sealed class Data
    {
        [JsonPropertyName("darkMode")]
        public bool DarkMode { get; set; }

        /// <summary>左侧账号列表是否按服务器分组（2026-10-05 用户要求，默认关）。</summary>
        [JsonPropertyName("groupAccountsByServer")]
        public bool GroupAccountsByServer { get; set; }

        /// <summary>
        /// 调试模式（2026-10-09 用户要求：左上角「设置」子菜单里的开关）。
        /// 打开后 MCC 的 DebugMessages + PacketDebugMessages 一起开，
        /// 网络循环退出原因、包级收发等诊断信息才会流到日志区（默认关，避免刷屏）。
        /// </summary>
        [JsonPropertyName("debugMode")]
        public bool DebugMode { get; set; }
    }

    private const string FileName = "ui-settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly object Gate = new();

    /// <summary>
    /// 设置文件路径：在用户数据目录（程序目录下的 UserData）里，与账号库同目录，
    /// 升级时和账号库一起被那个文件夹带走。
    /// </summary>
    public static string FilePath
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("MCCX 仅在 Windows 上运行。");

            UserDataPaths.EnsureReady();
            return UserDataPaths.PathFor(FileName);
        }
    }

    /// <summary>读设置；没有文件 / 文件坏了一律返回默认（保持既有外观）。</summary>
    private static Data Read()
    {
        if (!OperatingSystem.IsWindows())
            return new Data();

        try
        {
            lock (Gate)
            {
                if (!File.Exists(FilePath))
                    return new Data();

                return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath), JsonOptions) ?? new Data();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // 读不到就用默认设置，不能因为一个设置文件让界面起不来
            return new Data();
        }
    }

    /// <summary>写设置（先 .tmp 再替换）。失败只吞掉，不影响界面。</summary>
    private static void Write(Action<Data> update)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(UserDataPaths.Directory);

                // 读-改-写：两项设置共用一个文件，别把对方那项抹掉
                Data data = Read();
                update(data);

                string path = FilePath;
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
                File.Move(temp, path, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // 设置没记住只影响下次启动的默认外观，不值得打断用户
        }
    }

    /// <summary>读暗色模式开关；没有文件 / 文件坏了一律按浅色（保持既有外观）。</summary>
    public static bool ReadDarkMode() => Read().DarkMode;

    /// <summary>写暗色模式开关。</summary>
    public static void WriteDarkMode(bool darkMode) => Write(d => d.DarkMode = darkMode);

    /// <summary>读"账号按服务器分组"开关；默认关（保持单列最近使用倒序）。</summary>
    public static bool ReadGroupAccountsByServer() => Read().GroupAccountsByServer;

    /// <summary>写"账号按服务器分组"开关。</summary>
    public static void WriteGroupAccountsByServer(bool enabled) => Write(d => d.GroupAccountsByServer = enabled);

    /// <summary>读"调试模式"开关；没有文件 / 文件坏了一律按关（不刷屏）。</summary>
    public static bool ReadDebugMode() => Read().DebugMode;

    /// <summary>写"调试模式"开关。</summary>
    public static void WriteDebugMode(bool enabled) => Write(d => d.DebugMode = enabled);
}