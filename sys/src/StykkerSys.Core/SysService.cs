using Stykker.Shared.Sampling;

namespace StykkerSys.Core;

// Die Messschleife der Prozessliste (siehe ViewerLoop): sie liest nur, solange jemand die Liste ansieht.
public sealed class SysService : ViewerLoop<SysSnapshot>
{
    public SysService(ProcessSampler sampler) : base(sampler) { }
}
