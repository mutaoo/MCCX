using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace MccX.Core.Networking;

/// <summary>
/// 服务器地址解析，只做纯内存/网络查询，不读写任何配置文件。
/// <list type="number">
/// <item>支持在“服务器”一栏直接写 host:port（IPv6 写成 [地址]:端口），自动拆出端口；</item>
/// <item>没写端口时先查 DNS SRV 记录 _minecraft._tcp（与原版客户端行为一致），</item>
/// <item>查不到或不适用时回退到默认端口 25565。</item>
/// </list>
/// </summary>
public static class ServerAddress
{
    /// <summary>Minecraft 未声明端口时使用的默认端口。</summary>
    public const ushort DefaultPort = 25565;

    /// <summary>SRV 记录类型编号。</summary>
    private const int SrvRecordType = 33;

    /// <summary>单个 DNS 服务器的查询超时（毫秒），超时就换下一个。</summary>
    private const int PerServerTimeoutMs = 1500;

    /// <summary>整次 SRV 查询的总预算（毫秒），超时直接用默认端口，不能让用户等太久。</summary>
    private const int TotalTimeoutMs = 3000;

    /// <summary>最多尝试的 DNS 服务器数量。</summary>
    private const int MaxDnsServers = 4;

    /// <summary>
    /// 拆分用户输入：
    /// <c>example.com:25566</c> → ("example.com", "25566")；
    /// <c>[::1]:25566</c> → ("::1", "25566")；
    /// 只有地址没有端口时第二个返回值为 null。
    /// </summary>
    public static (string Host, string? Port) Split(string input)
    {
        string text = input.Trim();
        if (text.Length == 0)
            return (text, null);

        // 方括号形式：IPv6 地址，可能带端口
        if (text[0] == '[')
        {
            int close = text.IndexOf(']');
            if (close <= 0)
                return (text, null);

            string bracketHost = text[1..close];
            string rest = text[(close + 1)..].Trim();
            if (rest.StartsWith(':') && IsUsablePort(rest[1..], out string bracketPort))
                return (bracketHost, bracketPort);

            return (bracketHost, null);
        }

        int separator = text.LastIndexOf(':');
        if (separator <= 0)
            return (text, null);

        // 多个冒号 = 裸 IPv6 地址（如 ::1），整段都当主机，不拆
        if (text.IndexOf(':') != separator)
            return (text, null);

        string portText = text[(separator + 1)..];
        return IsUsablePort(portText, out string? port)
            ? (text[..separator], port)
            : (text, null);
    }

    /// <summary>是否已经是 IP 字面量（是的话查 SRV 没有意义）。</summary>
    public static bool IsIpLiteral(string host)
        => IPAddress.TryParse(host.Trim(), out _);

    /// <summary>
    /// 查询 <c>_minecraft._tcp.&lt;domain&gt;</c> 的 SRV 记录拿到真实端口。
    /// 任何失败（无网络、DNS 不支持、超时、非域名）都返回 null，由调用方回退默认端口。
    /// </summary>
    public static Task<ushort?> TrySrvPortAsync(string host, CancellationToken cancellationToken = default)
    {
        string domain = host.Trim().TrimEnd('.');
        // IP、localhost、局域网短名（不含点）都查不到有意义的 SRV，直接跳过
        if (domain.Length == 0 || !domain.Contains('.') || IsIpLiteral(domain))
            return Task.FromResult<ushort?>(null);

        // DNS 枚举/收发都放后台线程，别卡 UI
        return Task.Run(() => QuerySrvPortAsync(domain, cancellationToken), CancellationToken.None);
    }

    #region SRV 查询

    private static async Task<ushort?> QuerySrvPortAsync(string domain, CancellationToken externalToken)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        total.CancelAfter(TotalTimeoutMs);

        try
        {
            IReadOnlyList<IPAddress> servers = GetDnsServers();
            if (servers.Count == 0)
                return null;

            ushort id = (ushort)Random.Shared.Next(0, 65536);
            byte[] query = BuildSrvQuery("_minecraft._tcp." + domain, id);
            if (query.Length == 0)
                return null;

            foreach (IPAddress server in servers)
            {
                byte[] response;
                try
                {
                    response = await QueryServerAsync(server, query, total.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break; // 总预算用完，回退默认端口
                }
                catch
                {
                    continue; // 这台 DNS 不通/答得不对，换下一台
                }

                ushort? port = ParseSrvPort(response, id);
                if (port is > 0)
                    return port;
            }
        }
        catch (OperationCanceledException)
        {
            // 超时不算错误
        }
        catch
        {
            // 查不到就用默认端口，任何异常都不能影响连接流程
        }

        return null;
    }

    /// <summary>取本机各网卡配置的 DNS 服务器（只读系统配置，不写任何东西）。</summary>
    private static IReadOnlyList<IPAddress> GetDnsServers()
    {
        var servers = new List<IPAddress>(MaxDnsServers);

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (IPAddress dns in nic.GetIPProperties().DnsAddresses)
                {
                    // Windows 会给没配 DNS 的虚拟网卡（VMware/WSL 等）填一组 fec0::ffff:* 占位地址，
                    // 发过去必然失败，而且它们通常排在真网卡前面——不滤掉会把真正的 DNS 挤出尝试名单。
                    if (dns.IsIPv6SiteLocal)
                        continue;

                    if (!servers.Contains(dns))
                        servers.Add(dns);

                    if (servers.Count >= MaxDnsServers)
                        return servers;
                }
            }
        }
        catch
        {
            // 拿不到网卡信息就当作没有 DNS
        }

        return servers;
    }

    private static byte[] BuildSrvQuery(string name, ushort id)
    {
        var buffer = new List<byte>(name.Length + 20);

        void WriteUInt16(int value)
        {
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }

        WriteUInt16(id);
        WriteUInt16(0x0100); // 标准查询 + 期望递归
        WriteUInt16(1);      // QDCOUNT
        WriteUInt16(0);      // ANCOUNT
        WriteUInt16(0);      // NSCOUNT
        WriteUInt16(0);      // ARCOUNT

        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length > 63) // 单段标签超长，格式不合法，放弃
                return [];

            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }

        buffer.Add(0);        // 名字结束
        WriteUInt16(SrvRecordType);
        WriteUInt16(1);       // IN

        return [.. buffer];
    }

    /// <summary>先 UDP；若服务器置了截断标志（TC）则改走 TCP 重查。</summary>
    private static async Task<byte[]> QueryServerAsync(IPAddress server, byte[] query, CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(server.AddressFamily);

        using var perServer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        perServer.CancelAfter(PerServerTimeoutMs);

        await udp.SendAsync(query, query.Length, new IPEndPoint(server, 53)).ConfigureAwait(false);
        UdpReceiveResult result = await udp.ReceiveAsync(perServer.Token).ConfigureAwait(false);

        byte[] response = result.Buffer;
        bool truncated = response.Length >= 3 && (response[2] & 0x02) != 0;
        return truncated ? await QueryTcpAsync(server, query, cancellationToken).ConfigureAwait(false) : response;
    }

    private static async Task<byte[]> QueryTcpAsync(IPAddress server, byte[] query, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient(server.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PerServerTimeoutMs);

        try
        {
            await tcp.ConnectAsync(server, 53, timeout.Token).ConfigureAwait(false);

            NetworkStream stream = tcp.GetStream();
            byte[] prefix = [(byte)(query.Length >> 8), (byte)query.Length];
            await stream.WriteAsync(prefix, timeout.Token).ConfigureAwait(false);
            await stream.WriteAsync(query, timeout.Token).ConfigureAwait(false);

            byte[] head = new byte[2];
            await stream.ReadExactlyAsync(head, timeout.Token).ConfigureAwait(false);
            int length = (head[0] << 8) | head[1];
            if (length is <= 0 or > 65535)
                return [];

            byte[] body = new byte[length];
            await stream.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
            return body;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>从 DNS 应答里挑出 SRV 记录的端口字段。</summary>
    private static ushort? ParseSrvPort(byte[] data, ushort expectedId)
    {
        if (data.Length < 12)
            return null;
        if (data[0] != (byte)(expectedId >> 8) || data[1] != (byte)expectedId)
            return null;
        if ((data[2] & 0x80) == 0) // 不是响应
            return null;

        int questionCount = (data[4] << 8) | data[5];
        int answerCount = (data[6] << 8) | data[7];
        int position = 12;

        for (int i = 0; i < questionCount; i++)
        {
            position = SkipName(data, position);
            if (position < 0 || position + 4 > data.Length)
                return null;
            position += 4;
        }

        for (int i = 0; i < answerCount; i++)
        {
            position = SkipName(data, position);
            if (position < 0 || position + 10 > data.Length)
                return null;

            int type = (data[position] << 8) | data[position + 1];
            int recordClass = (data[position + 2] << 8) | data[position + 3];
            int dataLength = (data[position + 8] << 8) | data[position + 9];
            position += 10;

            if (position + dataLength > data.Length)
                return null;

            // SRV 的 rdata：priority(2) weight(2) port(2) target(...)
            if (type == SrvRecordType && recordClass == 1 && dataLength >= 6)
            {
                int port = (data[position + 4] << 8) | data[position + 5];
                if (port is > 0 and <= 65535)
                    return (ushort)port;
            }

            position += dataLength;
        }

        return null;
    }

    /// <summary>跳过域名字段，支持压缩指针；失败返回 -1。</summary>
    private static int SkipName(byte[] data, int position)
    {
        while (position < data.Length)
        {
            int length = data[position];
            if (length == 0)
                return position + 1;

            if ((length & 0xC0) == 0xC0) // 压缩指针固定占两字节
                return position + 2 <= data.Length ? position + 2 : -1;

            position += 1 + length;
        }

        return -1;
    }

    /// <summary>是不是可用端口（1-65535 的纯数字，不接受带符号/小数点的写法）。</summary>
    private static bool IsUsablePort(string text, out string port)
    {
        string trimmed = text.Trim();
        if (ushort.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out ushort value) && value > 0)
        {
            port = trimmed;
            return true;
        }

        port = string.Empty;
        return false;
    }

    #endregion
}
