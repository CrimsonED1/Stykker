using System.Net;
using System.Net.Sockets;
using Stykker.Pairing;
using StykkerLlm.Cli;
using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// Die Vorgaben der Ports (Auftrag, Abschnitte 1 und 4): StykkerLLM auf 17401, die Suche nennt diesen Port, und der
// alte Port 8078 lässt nur die Wege der Model-Hosts durch (Übergang, Abschnitt 4.3).
[TestClass]
public class PortDefaultsTests
{
    [TestMethod]
    public void The_cli_and_the_client_default_to_17401()
    {
        var (args, error) = CliArgs.Parse(Array.Empty<string>());

        Assert.IsNull(error);
        Assert.AreEqual(17401, StykkerPorts.Llm);
        Assert.AreEqual(17401, args.Port);
        Assert.AreEqual("http://127.0.0.1:17401", ServerClient.DefaultUrl());
    }

    [TestMethod]
    public void The_search_answer_names_the_port_it_is_given()
    {
        // Der Server gibt seinen Webport weiter (Program.cs): ohne Angabe 17401, nie fest 8078
        StringAssert.Contains(NodeDiscovery.Reply(StykkerPorts.Llm, true), "\"port\":17401");
        Assert.IsFalse(NodeDiscovery.Reply(StykkerPorts.Llm, true).Contains("8078"));
    }

    [TestMethod]
    public void The_old_port_opens_only_on_the_default_port()
    {
        Assert.IsTrue(LegacyPort.Listens(17401));
        Assert.IsFalse(LegacyPort.Listens(8078), "8078 als --port bleibt wie bisher");
        Assert.IsFalse(LegacyPort.Listens(8090), "ein anderer Port ohne Übergang");
        Assert.IsFalse(LegacyPort.Listens(17500), "der Proxy hat keinen alten Port");
        Assert.AreEqual(8078, LegacyPort.Port);
    }

    [DataTestMethod]
    [DataRow("/hosts/connect", "GET")]
    [DataRow("/hosts/connect", "get")]
    [DataRow("/hosts/pair", "POST")]
    public void The_old_port_lets_only_the_model_host_paths_through(string path, string method) =>
        Assert.IsTrue(LegacyPort.Passes(path, method));

    [DataTestMethod]
    [DataRow("/", "GET")]
    [DataRow("/_blazor", "GET")]
    [DataRow("/api/state", "GET")]
    [DataRow("/pair", "POST")]
    [DataRow("/hosts/pair", "GET")]          // die Kopplung ist nur ein POST
    [DataRow("/hosts/connect/x", "GET")]      // nur genau dieser Weg
    [DataRow("/hosts", "GET")]
    public void The_old_port_answers_everything_else_with_410(string path, string method) =>
        Assert.IsFalse(LegacyPort.Passes(path, method));

    [TestMethod]
    public void The_410_text_names_the_new_port()
    {
        Assert.AreEqual("Stykker LLM moved to port 17401.", Strings.LegacyPortMoved(17401));
    }

    [TestMethod]
    public void A_port_that_is_in_use_is_not_free()
    {
        // Ein Listener auf allen Adressen belegt den Port: der Übergang darf dann nicht starten (Zeile im Log)
        var holder = new TcpListener(IPAddress.Any, 0);
        holder.Start();
        try
        {
            int port = ((IPEndPoint)holder.LocalEndpoint).Port;
            Assert.IsFalse(LegacyPort.IsFree(port));
        }
        finally
        {
            holder.Stop();
        }
    }
}
