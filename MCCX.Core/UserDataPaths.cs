using System.Runtime.Versioning;

namespace MCCX.Core;

/// <summary>
/// 用户数据目录：账号库与配置/状态类文件统一放在程序目录下的 <c>UserData</c> 文件夹。
///
/// <para>动机（用户 2026-10-05 要求）：升级新版时只需把 exe 目录整个换掉，
/// 再把 <c>UserData</c> 这一个文件夹拷进去即可，不用在几十个文件里挑哪几个是数据。</para>
///
/// <para>位置规则沿用原有语义：exe 目录可写就用它（保持"文件夹拷走就能用"的便携性），
/// 不可写（装到 Program Files 等）就退到 %LOCALAPPDATA%\MCCX 下的同名子目录。
/// 具体基准目录由 <see cref="AccountStore.DefaultDirectory"/> 决定。</para>
///
/// <para>升级兼容：从旧版本（这些文件散落在 exe 目录里）首次启动时，
/// <see cref="EnsureReady"/> 会把它们<b>移动</b>进 UserData。已有同名目标文件则不覆盖，
/// 避免把新版的数据用旧文件盖掉。</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class UserDataPaths
{
    /// <summary>用户数据文件夹名（相对程序目录）。</summary>
    public const string FolderName = "UserData";

    /// <summary>
    /// 需要从旧位置（exe 目录根下）搬进 <see cref="Directory"/> 的文件名。
    /// 只搬这四个已知文件，程序目录里的其它文件（MCC 自带的 dll 等）一律不动。
    /// </summary>
    private static readonly string[] FlatFileNames =
    [
        "accounts.dat",
        "accounts.key",
        "ui-settings.json",
        "frequent-commands.json",
    ];

    private static readonly object Gate = new();

    private static bool _ready;

    /// <summary>用户数据目录的完整路径（不保证目录已创建，用前先 <see cref="EnsureReady"/>）。</summary>
    public static string Directory => Path.Combine(AccountStore.DefaultDirectory, FolderName);

    /// <summary>用户数据目录下某个文件的完整路径。</summary>
    public static string PathFor(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>本次启动是否把旧版本散落在程序目录根下的数据文件搬进了 <see cref="Directory"/>。</summary>
    public static bool MigratedFlatFiles { get; private set; }

    /// <summary>
    /// 确保目录存在并完成旧文件迁移。<b>幂等</b>：第一次真正做事，之后直接返回，
    /// 所以三个 Store 各自在构造/取路径前调它也不会有额外开销或冲突。
    /// </summary>
    public static void EnsureReady()
    {
        if (_ready)
            return;

        lock (Gate)
        {
            if (_ready)
                return;

            _ready = true; // 先置位：迁移失败也不该每次访问都重试

            try
            {
                string root = AccountStore.DefaultDirectory;
                string target = Directory;

                if (!string.Equals(root, target, StringComparison.OrdinalIgnoreCase))
                    System.IO.Directory.CreateDirectory(target);

                MigrateFlatFiles(root, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // 迁移失败不拦启动：读写会各自报真实错误（AccountStore.LastError 等）
                _ = ex;
            }
        }
    }

    /// <summary>把程序目录根下的旧数据文件移进 UserData；目标已存在的一律不动。</summary>
    private static void MigrateFlatFiles(string root, string target)
    {
        foreach (string name in FlatFileNames)
        {
            string source = Path.Combine(root, name);
            if (!File.Exists(source))
                continue;

            string destination = Path.Combine(target, name);
            if (File.Exists(destination))
                continue;

            try
            {
                File.Move(source, destination);
                MigratedFlatFiles = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 单个文件搬不动就留在原地：旧版本仍能用它，新版本读不到也不会丢数据
                _ = ex;
            }
        }
    }
}