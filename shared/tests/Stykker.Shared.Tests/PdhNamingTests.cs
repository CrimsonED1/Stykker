using Stykker.Shared.Windows;

namespace Stykker.Shared.Tests;

public class PdhNamingTests
{
    [Theory]
    [InlineData("pid_1234_0x0000_0x00012345_0_engtype_3D", 1234)]
    [InlineData("pid_4_luid_0x0_0x1_engtype_Copy", 4)]
    [InlineData("_Total", 0)]
    [InlineData("pid_", 0)]
    [InlineData("pid_x_y", 0)]
    public void ThePidIsTakenFromTheInstanceName(string instance, int expected) => Assert.Equal(expected, Pdh.PidOf(instance));

    [Theory]
    [InlineData("pid_1_luid_engtype_3d", "3D")]
    [InlineData("pid_1_engtype_compute", "Compute")]
    [InlineData("pid_1_engtype_copy", "Copy")]
    [InlineData("pid_1_engtype_videodecode", "Video decode")]
    [InlineData("pid_1_engtype_videoencode", "Video encode")]
    [InlineData("pid_1_engtype_newthing", "Newthing")]
    [InlineData("pid_1_no_engine", "")]
    public void TheEngineIsTakenFromTheEndOfTheName(string instance, string expected) => Assert.Equal(expected, Pdh.EngineOf(instance));
}
