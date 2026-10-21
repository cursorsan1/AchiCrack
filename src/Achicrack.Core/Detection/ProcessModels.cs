namespace Achicrack.Core.Detection;

/// <summary>Lightweight description of a running process (no handles held).</summary>
public readonly record struct ProcessInfo(int ProcessId, string Name);

/// <summary>Identity of a process across polls (pid alone can be reused).</summary>
public readonly record struct ProcessKey(int ProcessId, string Name)
{
    public static ProcessKey From(ProcessInfo p) => new(p.ProcessId, p.Name);
}

/// <summary>OS process enumeration, abstracted for tests and for the Windows implementation.</summary>
public interface IProcessProvider
{
    /// <summary>Cheap enumeration of running processes.</summary>
    IReadOnlyList<ProcessInfo> GetProcesses();

    /// <summary>Full executable path, or null when it cannot be determined (access denied, exited...).</summary>
    string? TryGetExecutablePath(int processId);
}

public interface IForegroundProcessProvider
{
    /// <summary>Process id owning the foreground window, or null when unknown.</summary>
    int? GetForegroundProcessId();
}
