using StykkerLlm.Core;

namespace StykkerLlm.Tests;

// N77 – Ein Python-Server aus einem venv wird als Basis-Python erkannt; der Start nimmt das Python des venv im Arbeitsordner
[TestClass]
public class N77_VenvPythonTests
{
    [TestMethod]
    public void The_venv_python_in_the_working_folder_is_used()
    {
        var root = Path.Combine(Path.GetTempPath(), "stykker-n77-" + Guid.NewGuid().ToString("N"));
        try
        {
            var basePy = Path.Combine(root, "Python", "python.exe");
            var work = Path.Combine(root, "app");
            var venvPy = Path.Combine(work, ".venv", "Scripts", "python.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(basePy)!);
            Directory.CreateDirectory(Path.GetDirectoryName(venvPy)!);
            File.WriteAllText(basePy, "");
            File.WriteAllText(venvPy, "");
            File.WriteAllText(Path.Combine(work, ".venv", "pyvenv.cfg"), "");

            Assert.AreEqual(venvPy, ServerLauncher.VenvPython(basePy, work));
            Assert.AreEqual(venvPy, ServerLauncher.VenvPython(venvPy, work), "schon ein venv: bleibt");
            Assert.AreEqual(basePy, ServerLauncher.VenvPython(basePy, Path.Combine(root, "other")), "kein venv im Arbeitsordner");
            var exe = Path.Combine(root, "llama-server.exe");
            Assert.AreEqual(exe, ServerLauncher.VenvPython(exe, work), "nur Python");

            var plan = ServerLauncher.BuildPlan(new LaunchSpec("s", basePy, new[] { "server.py", "--port", "8082" }, work, null, null, null), Path.Combine(root, "logs"));
            Assert.AreEqual(venvPy, plan.Program);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
