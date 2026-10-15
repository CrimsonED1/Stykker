using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N68 – Basisadresse und Pfad: Cloud-Anbieter stehen mit „/v1“ am Ende, der Pfad der Anfrage beginnt mit „/v1“
[TestClass]
public class N68_UpstreamJoinTests
{
    [TestMethod]
    public void A_base_ending_in_v1_does_not_get_a_second_v1()
    {
        Assert.AreEqual("https://integrate.api.nvidia.com/v1/chat/completions",
            NetAddr.JoinUpstream("https://integrate.api.nvidia.com/v1", "/v1/chat/completions"));
        Assert.AreEqual("https://openrouter.ai/api/v1/chat/completions",
            NetAddr.JoinUpstream("https://openrouter.ai/api/v1/", "/v1/chat/completions"));
        Assert.AreEqual("https://example.test/v1/models?x=1", NetAddr.JoinUpstream("https://example.test/v1", "/v1/models?x=1"));
    }

    [TestMethod]
    public void A_local_server_without_v1_keeps_the_path()
    {
        Assert.AreEqual("http://127.0.0.1:8080/v1/chat/completions", NetAddr.JoinUpstream("http://127.0.0.1:8080", "/v1/chat/completions"));
        Assert.AreEqual("http://127.0.0.1:8080/completion", NetAddr.JoinUpstream("http://127.0.0.1:8080/", "/completion"));
    }

    [TestMethod]
    public void A_path_without_v1_is_appended_as_it_is()
    {
        Assert.AreEqual("https://example.test/v1/chat/completions", NetAddr.JoinUpstream("https://example.test/v1", "/chat/completions"));
        Assert.AreEqual("https://example.test/v1/v1x", NetAddr.JoinUpstream("https://example.test/v1", "/v1x"));
    }
}
