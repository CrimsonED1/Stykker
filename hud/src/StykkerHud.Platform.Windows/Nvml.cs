using System.Runtime.InteropServices;
using System.Text;
using StykkerHud.Core;

namespace StykkerHud.Platform.Windows;

// Liest GPU-Werte direkt über NVIDIAs nvml.dll (kein nvidia-smi-Prozess pro Sekunde).
// übernommen aus StykkerLLM (Nvml.cs).
public sealed class Nvml : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu; public uint Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong Total; public ulong Free; public ulong Used; }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization u);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory m);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMaxClockInfo(IntPtr device, int type, out uint mhz);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")]
    private static extern int nvmlThrottleOld(IntPtr device, out ulong reasons);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCurrentClocksEventReasons")]
    private static extern int nvmlThrottleNew(IntPtr device, out ulong reasons);
    [DllImport("nvml.dll", CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
    [DllImport("nvml.dll", CharSet = CharSet.Ansi)] private static extern int nvmlSystemGetDriverVersion(StringBuilder version, uint length);

    private readonly IntPtr _device;
    private readonly string _name = "GPU";
    public bool Available { get; }
    public string? DriverVersion { get; }

    public Nvml()
    {
        try
        {
            if (nvmlInit_v2() != 0 || nvmlDeviceGetHandleByIndex_v2(0, out _device) != 0) return;
            var sb = new StringBuilder(96);
            if (nvmlDeviceGetName(_device, sb, (uint)sb.Capacity) == 0)
                _name = sb.ToString().Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "");
            var dv = new StringBuilder(80);
            if (nvmlSystemGetDriverVersion(dv, (uint)dv.Capacity) == 0 && dv.Length > 0) DriverVersion = dv.ToString();
            Available = true;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    public GpuSample? Read()
    {
        if (!Available) return null;
        nvmlDeviceGetUtilizationRates(_device, out var u);
        nvmlDeviceGetMemoryInfo(_device, out var m);
        nvmlDeviceGetPowerUsage(_device, out var p);
        nvmlDeviceGetEnforcedPowerLimit(_device, out var pl);
        nvmlDeviceGetTemperature(_device, 0, out var t);
        nvmlDeviceGetClockInfo(_device, 0, out var gfx);
        nvmlDeviceGetClockInfo(_device, 2, out var mem);
        nvmlDeviceGetMaxClockInfo(_device, 0, out var gfxMax);
        nvmlDeviceGetMaxClockInfo(_device, 2, out var memMax);
        ulong reasons = 0;
        try { if (nvmlThrottleNew(_device, out reasons) != 0) reasons = 0; }
        catch (EntryPointNotFoundException)
        {
            try { if (nvmlThrottleOld(_device, out reasons) != 0) reasons = 0; } catch (EntryPointNotFoundException) { }
        }
        const double gb = 1024.0 * 1024 * 1024;
        return new GpuSample(_name, u.Gpu, m.Used / gb, m.Total / gb, u.Memory, p / 1000.0, pl / 1000.0, t,
            gfx, gfxMax, mem, memMax, reasons);
    }

    public void Dispose()
    {
        if (Available) try { nvmlShutdown(); } catch { }
    }
}