using Stykker.Shared.Windows;
using StykkerLlm.Core;

namespace StykkerLlm.Platform.Windows;

// Der GPU-Speicher eines Servers (Stykker.Shared) in der Form, die StykkerLLM erwartet
public sealed class GpuMemoryAdapter : IGpuMemoryQuery
{
    private readonly GpuPidMemoryQuery _query;

    public GpuMemoryAdapter(int pid)
    {
        Pid = pid;
        _query = new GpuPidMemoryQuery(pid);
    }

    public int Pid { get; }

    public (double DedicatedGb, double SharedGb)? Read() => _query.Read();

    public void Dispose() => _query.Dispose();
}
