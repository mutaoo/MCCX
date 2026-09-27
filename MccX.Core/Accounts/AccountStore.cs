using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MccX.Core;

/// <summary>
/// 账号加密持久化（对应需求 3.1、开发步骤 2）：
/// 账号列表以 JSON 组织，用随机生成的 AES-256-GCM 密钥加密后落盘；
/// 该密钥本身用 Windows DPAPI（当前用户）保护，因此只有本机本账号能解密。
///
/// 文件布局（默认 %APPDATA%\MccX\）：
///   accounts.key  DPAPI 保护后的 32 字节 AES 密钥
///   accounts.dat  [1B 版本][12B nonce][16B tag][密文]
///
/// 原子写入：先写 .tmp 再整体替换，避免断电产生半截文件。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AccountStore : IDisposable
{
    private const byte FormatVersion = 1;
    private const int KeySizeBytes = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string DataFileName = "accounts.dat";
    private const string KeyFileName = "accounts.key";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MccX.AccountStore.v1");

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

    /// <summary>默认存储目录：程序（exe）所在目录，与程序同路径；不存在则创建。</summary>
    public static string DefaultDirectory =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>本次启动是否把旧的 %APPDATA%\MccX 账号文件搬到了程序目录。</summary>
    public bool MigratedLegacyData { get; private set; }

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
    /// 程序目录还没有账号数据时，把旧版本存放在 %APPDATA%\MccX 的文件复制过来（DPAPI 同一用户可直接解密）。
    /// 已有数据则直接使用，不覆盖。
    /// </summary>
    private void MigrateLegacyDataIfAbsent()
    {
        if (File.Exists(_dataPath))
            return;

        try
        {
            string legacyDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MccX");
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
                existing.Username = profile.Username;
                existing.ServerHost = profile.ServerHost;
                existing.Port = profile.Port;
                existing.MinecraftVersion = profile.MinecraftVersion;
                existing.DisplayName = profile.DisplayName;
                existing.Credential = profile.Credential;
                existing.LastUsedAt = profile.LastUsedAt;
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
            _key = ProtectedData.Unprotect(protectedKey, Entropy, DataProtectionScope.CurrentUser);
            Array.Clear(protectedKey);
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
