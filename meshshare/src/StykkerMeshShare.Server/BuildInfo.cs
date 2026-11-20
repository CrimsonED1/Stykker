using System.Reflection;

namespace StykkerMeshShare.Server;

// Wann dieser Server gebaut wurde (UTC, „2026-11-20T08:01:00Z“; siehe AssemblyMetadata im csproj). So geschrieben lässt
// es sich als Text vergleichen: der größere Wert ist der neuere Build.
public static class BuildInfo
{
    public static readonly string Time = typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BuildTime")?.Value ?? "";
}
