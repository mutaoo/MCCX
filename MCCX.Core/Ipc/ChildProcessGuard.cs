using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MCCX.Core.Ipc;

/// <summary>
/// 子进程兜底清理（2026-10-23 用户报"点了断开 / 关掉 MCCX 后账号还挂在服务器上"）。
///
/// <para>
/// 把每个账号子进程放进一个带 <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> 的作业对象：
/// 主程序无论怎么结束（正常退出、崩溃、任务管理器强杀、先冻死再杀），
/// 作业句柄都会随进程结束而关闭，Windows 随之结束作业里的所有子进程 ——
/// 套接字由操作系统关闭，服务器立刻看到玩家离线。
/// </para>
///
/// <para>
/// 为什么不能只靠"命令管道 EOF 让子进程自己退出"：.NET 起子进程时把可继承句柄
/// 全部交给新进程，后启动的兄弟子进程会继承前面那些子进程的管道写端；
/// 主进程死了以后，那些管道仍然被兄弟进程持有，<b>EOF 永远不会到</b>。
/// 所以子进程必须由作业对象兜底（子进程里另有父进程 PID 看门狗做第二层保险）。
/// </para>
/// </summary>
internal static class ChildProcessGuard
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly object Gate = new();

    /// <summary>作业句柄：故意不关，进程退出时由系统关闭（那时才会连带结束子进程）。</summary>
    private static IntPtr _job;

    /// <summary>把刚启动的子进程放进"父进程一没就一起结束"的作业对象。失败只影响兜底，不影响正常使用。</summary>
    public static bool Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            IntPtr job = EnsureJob();
            if (job == IntPtr.Zero)
                return false;

            return AssignProcessToJobObject(job, process.Handle);
        }
        catch
        {
            // 拿不到作业对象（权限 / 已经被别的作业限制）就退回到原行为
            return false;
        }
    }

    private static IntPtr EnsureJob()
    {
        lock (Gate)
        {
            if (_job != IntPtr.Zero)
                return _job;

            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return IntPtr.Zero;

            JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = default;
            info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            _job = job;
            return _job;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
