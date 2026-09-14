using System.Security.Principal;

namespace GabCode.Windows.Projects;

internal sealed class WindowsInstancePresence : IDisposable
{
    private readonly Mutex mutex;
    private readonly bool ownsMutex;

    private WindowsInstancePresence(Mutex mutex, bool ownsMutex)
    {
        this.mutex = mutex;
        this.ownsMutex = ownsMutex;
    }

    internal bool IsFirstInstance => ownsMutex;

    internal static WindowsInstancePresence Acquire(string? isolationScope = null)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("gabCode could not determine the current Windows user identity.");
        var suffix = isolationScope is null ? string.Empty : $".{isolationScope}";
        var mutex = new Mutex(initiallyOwned: true, $"Local\\gabCode.instance.{sid}{suffix}", out var createdNew);
        return new WindowsInstancePresence(mutex, createdNew);
    }

    public void Dispose()
    {
        if (ownsMutex) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
