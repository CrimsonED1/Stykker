using Stykker.Shared.Sampling;

namespace StykkerHud.Core;

// Die Messschleife der Anzeige (siehe ViewerLoop): sie liest nur, solange jemand zusieht.
public sealed class HudService : ViewerLoop<HudSnapshot>
{
    public HudService(MetricsSampler sampler) : base(sampler) { }
}
