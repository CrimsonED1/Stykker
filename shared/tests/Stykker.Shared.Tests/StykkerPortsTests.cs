using Stykker.Pairing;
using Stykker.Shared.Config;

namespace Stykker.Shared.Tests;

// Die festen Ports der Suite (AGENTS.md, Ports and files): ein Wert ändert sich nur mit Absicht, hier steht er fest.
public class StykkerPortsTests
{
    [Fact]
    public void The_fixed_ports_are_the_hub_llm_and_sys_block()
    {
        Assert.Equal(17400, StykkerPorts.Hub);
        Assert.Equal(17401, StykkerPorts.Llm);
        Assert.Equal(17402, StykkerPorts.Sys);
    }

    [Fact]
    public void The_default_hub_list_uses_the_fixed_ports()
    {
        var defaults = HubModules.Defaults.ToDictionary(m => m.Id, m => m.Port);

        Assert.Equal(StykkerPorts.Sys, defaults["sys"]);
        Assert.Equal(StykkerPorts.Llm, defaults["llm"]);
    }
}
