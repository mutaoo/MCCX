using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MCCX.Core;

/// <summary>
/// 账号加密持久化（对应需求 3.1、开发步骤 2）：
/// 账号列表以 JSON 组织，用随机生成的 AES-256-GCM 密钥加密后落盘；
/// 该密钥本身用 Windows DPAPI（当前用户）保护，因此只有本机本账号能解密。
///
/// 文件布局（优先 exe 同目录，保证"绿色便携"；该目录不可写时退到
/// %LOCALAPPDATA%\MCCX；旧版本用 %APPDATA%\MCCX\，即改名前的 MccX）：
///   accounts.key  DPAPI 保护后的 32 字节 AES 密钥
///   accounts.dat  [1B 版本][12B nonce][16B tag][密文]
///
/// 原子写入：先写 .tmp 再整体替换，避免断电产生半截文件。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class AccountStore : IDisposable
{
    private const byte FormatVersion = 1;
    private const int KeySizeBytes = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string DataFileName = "accounts.dat";
    private const string KeyFileName = "accounts.key";

    // DPAPI 熵值带版本号：改名前叫 MccX，熵值是 "MccX.AccountStore.v1"。
    // 现在程序名是 MCCX，新写入用新熵值；读取时新旧都试，否则老账号解不开。
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MCCX.AccountStore.v1");
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("MccX.AccountStore.v1");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _dataPath;
    private readonly string _keyPath;
    private readonly List<AccountProfile> _accounts = [];

    private byte[]? _key;
    private bool _disposed;

    public AccountStore(string? directory = null)
    {
        bool isDefaultDirectory = directory is null;
        _directory = directory ?? DefaultDirectory;
        _dataPath = Path.Combine(_directory, DataFileName);
        _keyPath = Path.Combine(_directory, KeyFileName);

        EnsureDirectoryExists();

        // 只有默认位置才做迁移：测试用的临时目录不应被历史数据污染
        if (isDefaultDirectory)
            MigrateLegacyDataIfAbsent();
    }

    /// <summary>默认存储目录：exe 所在目录；不可写时退到 %LOCALAPPDATA%\MCCX。</summary>
    public static string DefaultDirectory { get; } = ResolveDefaultDirectory();

    /// <summary>%LOCALAPPDATA%\MCCX（exe 目录不可写时的备用位置；也是旧版迁移的来源之一）。</summary>
    public static string FallbackDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCCX");

    /// <summary>true 表示 exe 目录不可写，账号库实际落在 <see cref="FallbackDirectory"/>。</summary>
    public static bool UsingFallbackDirectory =>
        !string.Equals(DefaultDirectory, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    /// <summary>本次启动是否把旧的 %APPDATA%\MCCX 账号文件搬到了程序目录。</summary>
    public bool MigratedLegacyData { get; private set; }

    /// <summary>
    /// 本实例实际使用的账号库目录（默认位置时即 <see cref="DefaultDirectory"/>）。
    /// 名字刻意不叫 Directory：那会遮蔽 <see cref="System.IO.Directory"/>，
    /// 让本类里所有 Directory.CreateDirectory/Exists 都编不过。
    /// </summary>
    public string StorageDirectory => _directory;

    /// <summary>
    /// exe 目录能写就用它（保持"整个文件夹拷走就能用"的便携语义）；
    /// 只读位置（装到 Program Files、解压到受限目录）不能写就退到
    /// %LOCALAPPDATA%\MCCX，避免"加了账号、一重启就没了"。
    /// </summary>
    private static string ResolveDefaultDirectory()
    {
        string exeDirectory = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (IsWritable(exeDirectory))
            return exeDirectory;

        try
        {
            Directory.CreateDirectory(FallbackDirectory);
            if (IsWritable(FallbackDirectory))
                return FallbackDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 两个位置都不行：仍返回 exe 目录，让 Load/Persist 把真实错误写进 LastError
            _ = ex;
        }

        return exeDirectory;
    }

    /// <summary>写一个探针文件验证目录可写（比只看目录是否存在可靠：只读目录也存在）。</summary>
    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, ".mccx-writetest");
            // 用显式 new byte[0] 而不是集合表达式 []：[] 在这里推不出目标类型（CS9174）。
            File.WriteAllBytes(probe, new byte[0]);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _ = ex;
            return false;
        }
    }

    private void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(_directory))
                Directory.CreateDirectory(_directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 建目录失败先不报错：Load/Persist 会把真正的失败原因写进 LastError
            _ = ex;
        }
    }

    /// <summary>
    /// 程序目录还没有账号数据时，把旧版本存放在 %APPDATA% 下（目录名 MccX 或 MCCX）的文件复制过来（DPAPI 同一用户可直接解密）。
    /// 已有数据则直接使用，不覆盖。
    /// </summary>
    private void MigrateLegacyDataIfAbsent()
    {
        if (File.Exists(_dataPath))
            return;

        try
        {
            // 旧版目录名是 MccX，改名后是 MCCX，两个位置都要找
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string legacyDirectory = string.Empty;
            foreach (string name in new[] { "MCCX", "MccX" })
            {
                string candidate = Path.Combine(appData, name);
                if (Directory.Exists(candidate))
                {
                    legacyDirectory = candidate;
                    break;
                }
            }

            if (legacyDirectory.Length == 0)
                return;

            string legacyDataPath = Path.Combine(legacyDirectory, DataFileName);
            if (!File.Exists(legacyDataPath))
                return;

            File.Copy(legacyDataPath, _dataPath, overwrite: false);

            string legacyKeyPath = Path.Combine(legacyDirectory, KeyFileName);
            if (File.Exists(legacyKeyPath) && !File.Exists(_keyPath))
                File.Copy(legacyKeyPath, _keyPath, overwrite: false);

            MigratedLegacyData = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 迁移失败不影响启动：当作没有历史账号
            _ = ex;
        }
    }

    /// <summary>上次加载/保存失败的原因（文件损坏、DPAPI 失败等），成功时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>读取并解密账号列表（结果同时作为内部缓存）。</summary>
    public IReadOnlyList<AccountProfile> Load()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            LastError = null;
            _accounts.Clear();

            if (!File.Exists(_dataPath))
                return [.. _accounts];

            try
            {
                byte[] payload = File.ReadAllBytes(_dataPath);
                byte[] json = Decrypt(payload);
                List<AccountProfile>? list = JsonSerializer.Deserialize<List<AccountProfile>>(json, JsonOptions);
                if (list is not null)
                {
                    foreach (AccountProfile profile in list)
                    {
                        if (!string.IsNullOrWhiteSpace(profile.Username))
                            _accounts.Add(profile);
                    }
                }

                Array.Clear(json);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
            {
                // 损坏或密钥不匹配：清空而非崩溃，由调用方决定是否提示用户
                LastError = ex.Message;
                _accounts.Clear();
            }

            SortLocked();
            return [.. _accounts];
        }
    }

    /// <summary>
    /// 新增或更新一个账号。按 Id 匹配；若 Id 不同但「用户名+服务器+端口」相同，视为同一账号并合并，
    /// 返回合并后的最终记录（调用方用它的 Id 刷新 UI）。
    /// </summary>
    public AccountProfile Upsert(AccountProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(profile.DisplayName))
                profile.DisplayName = profile.Username;

            AccountProfile? existing = _accounts.FirstOrDefault(a =>
                a.Id == profile.Id ||
                (string.Equals(a.Username, profile.Username, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(a.ServerHost, profile.ServerHost, StringComparison.OrdinalIgnoreCase)
                 && a.Port == profile.Port));

            if (existing is null)
            {
                _accounts.Add(profile);
            }
            else if (!ReferenceEquals(existing, profile))
            {
                // 字段拷贝集中在 AccountProfile.CopyFrom（一处列全，避免新增字段漏合并）；
                // LastYaw/LastPitch 是进服恢复功能的历史遗留字段（功能已移除），这里保留库里的旧值。
                float? lastYaw = existing.LastYaw;
                float? lastPitch = existing.LastPitch;

                existing.CopyFrom(profile);

                existing.LastYaw = lastYaw;
                existing.LastPitch = lastPitch;

                profile = existing;
            }

            SortLocked();
            PersistLocked();
            return profile;
        }
    }

    /// <summary>删除账号，返回是否命中。</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            int removed = _accounts.RemoveAll(a => a.Id == id);
            if (removed == 0)
                return false;

            PersistLocked();
            return true;
        }
    }

    /// <summary>
    /// 只更新一个账号的攻击生物过滤设置并落盘。
    /// 不碰 LastUsedAt：改个勾选不该把账号顶到“最近使用”、打乱左侧列表顺序。
    /// </summary>
    /// <returns>是否命中该账号。</returns>
    public bool UpdateAttackFilter(string id, int mode, IReadOnlyList<string> mobs)
    {
        ArgumentNullException.ThrowIfNull(mobs);

        lock (_gate)
        {
            ThrowIfDisposed();

            AccountProfile? account = _accounts.FirstOrDefault(a => a.Id == id);
            if (account is null)
                return false;

            account.AttackFilterMode = mode;
            account.AttackFilterMobs = [.. mobs];
            PersistLocked();
            return true;
        }
    }

    /// <summary>
    /// 只更新一个账号的"上次视角"并落盘。不碰 LastUsedAt（不改变左侧列表顺序）。
    /// 调用方是界面层：连接前它会把子进程 Bot 写下的最新视角并回账号库，
    /// 这样"视角跟随账号"与其他参数保持一致。
    /// </summary>
    /// <returns>是否命中该账号。</returns>
    public bool UpdateLastView(string id, float? yaw, float? pitch)
    {
        if (yaw is null || pitch is null)
            return false;

        lock (_gate)
        {
            ThrowIfDisposed();

            AccountProfile? account = _accounts.FirstOrDefault(a => a.Id == id);
            if (account is null)
                return false;

            account.LastYaw = yaw;
            account.LastPitch = pitch;
            PersistLocked();
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_key is not null)
            {
                CryptographicOperations.ZeroMemory(_key);
                _key = null;
            }
        }

        GC.SuppressFinalize(this);
    }

    #region 加解密与文件

    private void SortLocked()
    {
        _accounts.Sort(static (a, b) => b.LastUsedAt.CompareTo(a.LastUsedAt));
    }

    private void PersistLocked()
    {
        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(_accounts, JsonOptions);
            byte[] payload = Encrypt(json);
            Array.Clear(json);

            Directory.CreateDirectory(_directory);
            string tempPath = _dataPath + ".tmp";
            File.WriteAllBytes(tempPath, payload);
            File.Move(tempPath, _dataPath, overwrite: true);
            LastError = null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
        }
    }

    private byte[] Encrypt(byte[] plain)
    {
        byte[] key = GetOrCreateKey();
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] tag = new byte[TagSize];
        byte[] cipher = new byte[plain.Length];

        using (AesGcm aes = new(key, TagSize))
            aes.Encrypt(nonce, plain, cipher, tag);

        byte[] payload = new byte[1 + NonceSize + TagSize + cipher.Length];
        payload[0] = FormatVersion;
        Buffer.BlockCopy(nonce, 0, payload, 1, NonceSize);
        Buffer.BlockCopy(tag, 0, payload, 1 + NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, payload, 1 + NonceSize + TagSize, cipher.Length);
        return payload;
    }

    private byte[] Decrypt(byte[] payload)
    {
        if (payload.Length < 1 + NonceSize + TagSize || payload[0] != FormatVersion)
            throw new CryptographicException("账号数据文件格式不正确。");

        byte[] key = GetOrCreateKey();
        byte[] nonce = payload.AsSpan(1, NonceSize).ToArray();
        byte[] tag = payload.AsSpan(1 + NonceSize, TagSize).ToArray();
        byte[] cipher = payload.AsSpan(1 + NonceSize + TagSize).ToArray();
        byte[] plain = new byte[cipher.Length];

        using (AesGcm aes = new(key, TagSize))
            aes.Decrypt(nonce, cipher, tag, plain);

        return plain;
    }

    private byte[] GetOrCreateKey()
    {
        if (_key is not null)
            return _key;

        if (File.Exists(_keyPath))
        {
            byte[] protectedKey = File.ReadAllBytes(_keyPath);
            try
            {
                _key = ProtectedData.Unprotect(protectedKey, Entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // 老版本（MccX 时代）用旧熵值保护的密钥：解开后按新熵值重新保护并回写
                byte[] legacyKey = ProtectedData.Unprotect(protectedKey, LegacyEntropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_keyPath, ProtectedData.Protect(legacyKey, Entropy, DataProtectionScope.CurrentUser));
                _key = legacyKey;
            }
            finally
            {
                Array.Clear(protectedKey);
            }

            return _key;
        }

        byte[] key = RandomNumberGenerator.GetBytes(KeySizeBytes);
        Directory.CreateDirectory(_directory);
        byte[] protectedKey2 = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_keyPath, protectedKey2);
        _key = key;
        return _key;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    #endregion
}
