using System.Diagnostics;
using System.Text;
using Achicrack.Core.Detection;

namespace Achicrack.App.Infrastructure;

/// <summary>
/// Process enumeration via <see cref="Process.GetProcesses()"/> (no handles opened) and executable path
/// lookup via QueryFullProcessImageName with PROCESS_QUERY_LIMITED_INFORMATION — read-only, no injection,
/// works for most processes without elevation.
/// </summary>
internal sealed class WindowsProcessProvider : IProcessProvider, IForegroundProcessProvider
{
    public IReadOnlyList<ProcessInfo> GetProcesses()
    {
        var processes = Process.GetProcesses();
        var result = new List<ProcessInfo>(processes.Length);
        foreach (var p in processes)
        {
            try
            {
                result.Add(new ProcessInfo(p.Id, p.ProcessName));
            }
            catch (InvalidOperationException)
            {
                // exited during enumeration
            }
            finally
            {
                p.Dispose();
            }
        }

        return result;
    }

    public string? TryGetExecutablePath(int processId)
    {
        if (processId <= 4)
        {
            return null; // Idle / System
        }

        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var capacity = 1024;
            var sb = new StringBuilder(capacity);
            return NativeMethods.QueryFullProcessImageName(handle, 0, sb, ref capacity) ? sb.ToString() : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public int? GetForegroundProcessId()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == 0 ? null : (int)pid;
    }
}
