using System.Runtime.InteropServices;
using System.Text;
using GameDeck.Core.Diagnostics;
using Microsoft.Win32;

namespace GameDeck.Core.Hardware;

// A null reading means "this PC or driver doesn't provide it": the dashboard shows "Unavailable". Nothing is estimated.

/// <param name="TemperatureC">Always null: Windows exposes no CPU temperature without a kernel driver, and GameDeck installs none.</param>
public sealed record CpuReading(string? Model, int LogicalCores, double? UsagePercent, double? TemperatureC, double? ClockMhz);

/// <param name="Source">"nvml" (NVIDIA driver), "windows" (performance counters: usage and memory only) or "none".</param>
public sealed record GpuReading(
    string? Model, double? UsagePercent, double? TemperatureC, double? VramUsedMb, double? VramTotalMb,
    double? ClockMhz, double? FanPercent, string Source);

public sealed record MemoryReading(double TotalMb, double UsedMb, double AvailableMb, double UsagePercent);

public sealed record DriveReading(string Name, string Label, double TotalGb, double FreeGb, double UsagePercent);

public sealed record HardwareSnapshot(DateTime TakenUtc, CpuReading Cpu, GpuReading Gpu, MemoryReading? Memory, IReadOnlyList<DriveReading> Drives);

/// <summary>
/// Reads CPU, GPU, memory and disk figures with documented user-mode APIs only: no driver, no admin
/// rights, nothing injected. One <see cref="Sample"/> call reads everything together (one PDH
/// collection, one NVML round), so callers share a single sampler instead of polling per sensor.
/// Each part fails on its own: a sensor that can't be read becomes null and the rest still work.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly string? _cpuModel;
    private readonly double? _cpuBaseMhz;
    private readonly Nvml? _nvml;
    private readonly string? _gpuModel;
    private readonly double? _gpuTotalMb;
    private IntPtr _query, _cpuPerformance, _gpuEngine, _gpuMemory;
    private ulong _previousIdle, _previousTotal;
    private bool _hasPrevious, _disposed;

    /// <param name="useNvml">False forces the Windows-counter path (as on a PC without an NVIDIA GPU).</param>
    public HardwareMonitor(bool useNvml = true)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            _cpuModel = (key?.GetValue("ProcessorNameString") as string)?.Trim();
            if (key?.GetValue("~MHz") is int mhz && mhz > 0) _cpuBaseMhz = mhz;
        }
        catch (Exception ex) { Log.Warn("Hardware: CPU description unavailable", ex); }

        if (useNvml) _nvml = Nvml.TryCreate();
        if (_nvml is null) (_gpuModel, _gpuTotalMb) = ReadDisplayAdapter();

        if (PdhOpenQuery(null, IntPtr.Zero, out _query) == 0)
        {
            PdhAddEnglishCounter(_query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _cpuPerformance);
            if (_nvml is null)
            {
                PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", IntPtr.Zero, out _gpuEngine);
                PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _gpuMemory);
            }
            PdhCollectQueryData(_query); // rate counters need a first sample to compare against
        }
        else _query = IntPtr.Zero;
        Log.Info($"Hardware: CPU '{_cpuModel ?? "?"}', GPU '{_nvml?.Name ?? _gpuModel ?? "?"}' via {(_nvml is not null ? "NVML" : "Windows counters")}");
    }

    public HardwareSnapshot Sample()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
            return new HardwareSnapshot(DateTime.UtcNow, Safe(ReadCpu, new CpuReading(_cpuModel, Environment.ProcessorCount, null, null, null)),
                Safe(ReadGpu, new GpuReading(_nvml?.Name ?? _gpuModel, null, null, null, _gpuTotalMb, null, null, "none")),
                Safe<MemoryReading?>(ReadMemory, null), Safe<IReadOnlyList<DriveReading>>(ReadDrives, []));
        }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception ex)
        {
            Log.Warn("Hardware: a reading failed", ex);
            return fallback;
        }
    }

    private CpuReading ReadCpu()
    {
        double? usage = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel + user; // kernel time includes idle time
            if (_hasPrevious && total > _previousTotal)
                usage = Math.Clamp(100.0 * (1 - (double)(idle - _previousIdle) / (total - _previousTotal)), 0, 100);
            (_previousIdle, _previousTotal, _hasPrevious) = (idle, total, true);
        }
        // The same figure Task Manager shows as "Speed": base frequency scaled by current performance.
        var clock = _cpuBaseMhz is { } b && ReadCounter(_cpuPerformance) is { } p ? b * p / 100 : (double?)null;
        return new CpuReading(_cpuModel, Environment.ProcessorCount, usage, null, clock);
    }

    private GpuReading ReadGpu()
    {
        if (_nvml is not null) return _nvml.Read();
        var engines = ReadCounterArray(_gpuEngine);
        var memory = ReadCounterArray(_gpuMemory);
        double? usage = engines is null ? null : Math.Clamp(engines.Sum(), 0, 100);
        double? used = memory is { Length: > 0 } ? memory.Max() / (1024 * 1024) : null;
        return new GpuReading(_gpuModel, usage, null, used, _gpuTotalMb, null, null, usage is null && used is null ? "none" : "windows");
    }

    private static MemoryReading? ReadMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m) || m.ullTotalPhys == 0) return null;
        const double mb = 1024 * 1024;
        var used = m.ullTotalPhys - m.ullAvailPhys;
        return new MemoryReading(m.ullTotalPhys / mb, used / mb, m.ullAvailPhys / mb, 100.0 * used / m.ullTotalPhys);
    }

    private static IReadOnlyList<DriveReading> ReadDrives()
    {
        const double gb = 1024.0 * 1024 * 1024;
        var list = new List<DriveReading>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0) continue;
                list.Add(new DriveReading(d.Name.TrimEnd('\\'), d.VolumeLabel, d.TotalSize / gb, d.TotalFreeSpace / gb, 100.0 * (d.TotalSize - d.TotalFreeSpace) / d.TotalSize));
            }
            catch { /* drive went away or is locked */ }
        }
        return list;
    }

    /// <summary>Name and dedicated memory of the display adapter with the most memory, from the driver's registry entry.</summary>
    private static (string? Model, double? TotalMb) ReadDisplayAdapter()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            (string? Model, double? TotalMb) best = (null, null);
            foreach (var name in cls?.GetSubKeyNames() ?? [])
            {
                if (name.Length != 4 || !name.All(char.IsDigit)) continue;
                try
                {
                    using var adapter = cls!.OpenSubKey(name);
                    if (adapter?.GetValue("DriverDesc") is not string model) continue;
                    double? mb = adapter.GetValue("HardwareInformation.qwMemorySize") is long bytes && bytes > 0 ? bytes / (1024.0 * 1024) : null;
                    if (best.Model is null || (mb ?? 0) > (best.TotalMb ?? 0)) best = (model, mb);
                }
                catch { /* protected subkey */ }
            }
            return best;
        }
        catch (Exception ex)
        {
            Log.Warn("Hardware: display adapter description unavailable", ex);
            return (null, null);
        }
    }

    private static double? ReadCounter(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        return PdhGetFormattedCounterValue(counter, PdhFmtDouble, out _, out var value) == 0 && value.CStatus is 0 or 1 ? value.DoubleValue : null;
    }

    /// <summary>All instances of a wildcard counter; null when the counter doesn't exist or has no data yet.</summary>
    private static double[]? ReadCounterArray(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        uint size = 0;
        if (PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out _, IntPtr.Zero) != PdhMoreData || size == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out var count, buffer) != 0) return null;
            var itemSize = IntPtr.Size + 16; // name pointer + PDH_FMT_COUNTERVALUE
            var values = new double[count];
            for (var i = 0; i < count; i++)
            {
                var status = Marshal.ReadInt32(buffer, i * itemSize + IntPtr.Size);
                var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer, i * itemSize + IntPtr.Size + 8));
                values[i] = status is 0 or 1 && double.IsFinite(value) ? value : 0;
            }
            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            _nvml?.Dispose();
        }
    }

    /// <summary>NVIDIA Management Library, shipped with every NVIDIA driver. Read-only queries on the first GPU.</summary>
    private sealed class Nvml : IDisposable
    {
        private readonly IntPtr _device;
        public string? Name { get; }

        private Nvml(IntPtr device, string? name) => (_device, Name) = (device, name);

        public static Nvml? TryCreate()
        {
            try
            {
                // Older drivers keep the library outside System32; loading it first lets the imports below find it.
                var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvml.dll");
                if (!File.Exists(Path.Combine(Environment.SystemDirectory, "nvml.dll")) && File.Exists(legacy)) NativeLibrary.TryLoad(legacy, out _);

                if (nvmlInit_v2() != 0) return null;
                if (nvmlDeviceGetHandleByIndex_v2(0, out var device) != 0)
                {
                    nvmlShutdown();
                    return null;
                }
                var name = new byte[96];
                var model = nvmlDeviceGetName(device, name, (uint)name.Length) == 0 ? Encoding.ASCII.GetString(name).TrimEnd('\0').Trim() : null;
                return new Nvml(device, string.IsNullOrEmpty(model) ? null : model);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return null; // no NVIDIA driver on this PC
            }
        }

        public GpuReading Read()
        {
            double? usage = nvmlDeviceGetUtilizationRates(_device, out var u) == 0 ? u.Gpu : null;
            double? temperature = nvmlDeviceGetTemperature(_device, 0, out var t) == 0 ? t : null;
            var hasMemory = nvmlDeviceGetMemoryInfo(_device, out var m) == 0;
            double? clock = nvmlDeviceGetClockInfo(_device, 0, out var c) == 0 ? c : null;
            double? fan = nvmlDeviceGetFanSpeed(_device, out var f) == 0 ? f : null;
            const double mb = 1024 * 1024;
            return new GpuReading(Name, usage, temperature, hasMemory ? m.Used / mb : null, hasMemory ? m.Total / mb : null, clock, fan, "nvml");
        }

        public void Dispose()
        {
            try { nvmlShutdown(); } catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Utilization { public uint Gpu, Memory; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Memory { public ulong Total, Free, Used; }

        [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] private static extern int nvmlShutdown();
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint celsius);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int clock, out uint mhz);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
    }

    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PdhValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? source, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhValue value);

    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
