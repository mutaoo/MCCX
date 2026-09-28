using MinecraftClient;

namespace MCCX.Core;

/// <summary>
/// GUI 版 <see cref="IConsoleBackend"/>：把 MCC 的控制台输出转发为 C# 事件供 UI 订阅，
/// 输入则由 UI 主动调用 <see cref="SubmitInput"/> 推入 MCC 的输入管道，
/// 因此不再需要任何控制台读取线程（<see cref="BeginReadThread"/> 是空操作）。
/// </summary>
public sealed class MCCUiBackend : IConsoleBackend
{
    /// <summary>
    /// MCC 输出的原始文本（可能带 § 颜色码，也可能包含换行）。
    /// 事件可能在 MCC 的任意线程上触发，订阅方负责自行切换到 UI 线程。
    /// </summary>
    public event Action<string>? OutputReceived;

    /// <summary>MCC 请求清屏（/clear 等）。</summary>
    public event Action? ScreenCleared;

    public event EventHandler<string>? MessageReceived;

    /// <summary>
    /// 输入框内容变化（用于 MCC 的 TAB 补全）。GUI 暂未接入补全，先保留事件成员，
    /// 后续接入时直接在 <see cref="SubmitInput"/> 前后抛出即可。
    /// </summary>
#pragma warning disable CS0067 // The event is never used
    public event EventHandler<ConsoleInputBuffer>? OnInputChange;
#pragma warning restore CS0067

    /// <summary>兼容接口：GUI 下由界面决定是否回显输入，MCC 不需要读取该标志。</summary>
    public bool DisplayUserInput { get; set; } = true;

    public void Init()
    {
    }

    public void WriteLine(string text) => Emit(text);

    public void WriteLineFormatted(string text) => Emit(text);

    /// <summary>没有控制台读取线程：输入全部来自 UI。</summary>
    public void BeginReadThread()
    {
    }

    public void StopReadThread()
    {
    }

    /// <summary>同步读取一整行的场景由界面异步化处理，这里返回空字符串避免阻塞 MCC 线程。</summary>
    public string RequestImmediateInput() => string.Empty;

    /// <summary>离线账号不需要密码，返回 null 会让 MCC 按离线模式继续。</summary>
    public string? ReadPassword() => null;

    public void ClearInputBuffer()
    {
    }

    public void ClearScreen() => ScreenCleared?.Invoke();

    public void SetInputVisible(bool visible)
    {
    }

    public void SetBackreadBufferLimit(int limit)
    {
    }

    public void Shutdown()
    {
    }

    /// <summary>
    /// 把一行 UI 输入送进 MCC 控制台输入管道（经 ConsoleInputRouter 路由到 McClient）。
    /// </summary>
    public void SubmitInput(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        MessageReceived?.Invoke(this, text);
    }

    private void Emit(string text)
    {
        Action<string>? handler = OutputReceived;
        if (handler is not null)
            handler(text ?? string.Empty);
    }
}
