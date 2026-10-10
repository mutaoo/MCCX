using System.Diagnostics;
using System.Net.Sockets;
using MinecraftClient.Protocol.Handlers;

namespace MCCX.Core;

/// <summary>
/// 服务器连接延迟探测（2026-10-08 需求：连接状态下方显示当前账号的延迟）。
///
/// 走一遍 Minecraft 状态握手（握手 → 状态请求 → ping/pong），按 ping 发出到 pong
/// 收回的往返计时，就是多人游戏列表里显示的那个延迟。
///
/// 为什么不直接用 MCC 的 <c>Protocol18Handler.DoPing</c>：它不把 PingMs 返回给调用方
/// （算完只塞进 ServerStatusInfo 交给日志展示），而且成功时会往控制台打协议行 +
/// 整段服务器信息，周期性调用正好把日志刷爆——撞上本轮"日志太频繁"的痛点。
/// 这里只复用 MCC 的 <c>DataTypes</c> 做封包，全程静默：测不到返回 -1（界面显示"--"）。
///
/// 注意：按账号里填的地址直连，不解析 SRV 记录；MCC 连接时可能经 SRV 指到别的
/// 端口，那种服务器会一直显示"--"（属极端情况，宁可不显示也不显示错的数）。
/// </summary>
public static class ServerLatency
{
    /// <summary>探测一次，返回往返毫秒；任何一步失败返回 -1。</summary>
    /// <param name="host">服务器域名或 IP。</param>
    /// <param name="port">端口。</param>
    /// <param name="timeoutMs">连接与收发的超时（毫秒），到点即放弃。</param>
    public static long Probe(string host, ushort port, int timeoutMs)
    {
        TcpClient? tcp = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            tcp = new TcpClient { ReceiveTimeout = timeoutMs, SendTimeout = timeoutMs };
            tcp.ConnectAsync(host, port)
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs))
                .GetAwaiter()
                .GetResult();

            NetworkStream stream = tcp.GetStream();
            // 协议号 1.8：状态握手的线格式自 1.7 起没变过，与握手包里报的 -1 无关
            DataTypes dataTypes = new(47);

            // 1) 握手：next state = 1 声明这条连接是来查状态的（协议号 -1 = 不指定，与 MCC 一致）
            byte[] portBytes = BitConverter.GetBytes(port);
            Array.Reverse(portBytes); // 网络序（大端）
            byte[] handshake = dataTypes.ConcatBytes(
                DataTypes.GetVarInt(0),
                DataTypes.GetVarInt(-1),
                dataTypes.GetString(host),
                portBytes,
                DataTypes.GetVarInt(1));
            Send(stream, dataTypes.ConcatBytes(DataTypes.GetVarInt(handshake.Length), handshake));

            // 2) 状态请求
            byte[] request = DataTypes.GetVarInt(0);
            Send(stream, dataTypes.ConcatBytes(DataTypes.GetVarInt(request.Length), request));

            // 3) 状态响应：包长 → 包 id(0x00) → JSON 字符串（只跳过内容，不解析）
            int packetLength = ReadVarInt(stream);
            if (packetLength <= 0 || ReadVarInt(stream) != 0x00)
                return -1;
            int jsonLength = ReadVarInt(stream);
            if (jsonLength <= 0)
                return -1;
            Skip(stream, jsonLength);

            // 部分代理服（BungeeCord 之类）读完状态响应就断开，收不到 Pong：
            // 那就退化成"发状态请求到收完响应"的耗时，仍能反映连不连得上、快不快。
            long statusMs = stopwatch.ElapsedMilliseconds;

            // 4) ping/pong：带时间戳发过去，服务器原样带回，掐这一段的往返
            try
            {
                long payload = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                byte[] ping = dataTypes.ConcatBytes(DataTypes.GetVarInt(0x01), DataTypes.GetLong(payload));
                Send(stream, dataTypes.ConcatBytes(DataTypes.GetVarInt(ping.Length), ping));

                long sentAt = Stopwatch.GetTimestamp();
                int pongLength = ReadVarInt(stream);
                if (pongLength <= 0 || ReadVarInt(stream) != 0x01)
                    return statusMs;

                return (long)Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds;
            }
            catch
            {
                return statusMs;
            }
        }
        catch
        {
            // 网络不通 / 超时 / 协议对不上：一律当"没测到"，不写日志（刷屏正是本轮要解决的问题）
            return -1;
        }
        finally
        {
            try { tcp?.Dispose(); } catch { /* 探测收尾，断不掉也无所谓 */ }
        }
    }

    private static void Send(NetworkStream stream, byte[] data) => stream.Write(data, 0, data.Length);

    private static int ReadVarInt(Stream stream)
    {
        int value = 0;
        int shift = 0;
        Span<byte> one = stackalloc byte[1];
        while (true)
        {
            if (stream.Read(one) <= 0)
                throw new EndOfStreamException();
            value |= (one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0)
                return value;
            shift += 7;
            if (shift > 35)
                throw new InvalidDataException("VarInt 过长");
        }
    }

    private static void Skip(Stream stream, int count)
    {
        Span<byte> buffer = stackalloc byte[4096];
        while (count > 0)
        {
            int read = stream.Read(buffer[..Math.Min(count, buffer.Length)]);
            if (read <= 0)
                throw new EndOfStreamException();
            count -= read;
        }
    }
}
