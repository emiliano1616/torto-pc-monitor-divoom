using LibreHardwareMonitor.Hardware;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DivoomPCDataTool.Monitoring;

public class WindowsMonitoringStrategy : ISystemMonitoringStrategy
{
    private Computer? _computer;
    private UpdateVisitor? _updateVisitor;
    private readonly bool _debug;
    private string _selectedStorage = "";

    public WindowsMonitoringStrategy(bool debug = false)
    {
        _debug = debug;
    }

    public Task<bool> Initialize()
    {
        if (OperatingSystem.IsWindows())
        {
            bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);

            if (!isAdmin)
            {
                Console.WriteLine("Warning: Running without administrator privileges. Some sensors may not be accessible.");
                Console.WriteLine("Try running the application as administrator for full hardware monitoring support.");
            }
        }

        _updateVisitor = new UpdateVisitor();
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsControllerEnabled = true,
            IsMotherboardEnabled = true,
            IsNetworkEnabled = false,
            IsBatteryEnabled = false
        };

        _computer.Open();
        _computer.Accept(_updateVisitor);

        var storage = _computer.Hardware.Where(t => t.HardwareType == HardwareType.Storage).ToList();

        if (storage.Count > 1)
        {
            Console.WriteLine("We found multiple storage devices, please select the one you want to monitor");
            for (int index = 0; index < storage.Count; index++)
            {
                Console.WriteLine($"{index + 1}. {storage[index].Name}");
            }
            var input = Console.ReadLine();
            if (int.TryParse(input, out int selection) && selection > 0 && selection <= storage.Count)
            {
                _selectedStorage = storage[selection - 1].Name;
            }
        }

        return Task.FromResult(true);
    }

    public Task<SystemInfo> GetSystemInfo()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string cpuTemp = "--", cpuUse = "--", gpuTemp = "--",
               gpuUse = "--", memUse = "--", diskTemp = "--";

        try
        {
            sw.Restart();

            if (_computer != null)
            {
                _computer.Close();
                _computer = null;
            }

            _updateVisitor = new UpdateVisitor();
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = true,
                IsControllerEnabled = true,
                IsMotherboardEnabled = true,
                IsNetworkEnabled = false,
                IsBatteryEnabled = false
            };

            _computer.Open();
            _computer.Accept(_updateVisitor);
            if (_debug) Console.WriteLine($"Computer initialization took: {sw.ElapsedMilliseconds}ms");

            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();

                switch (hardware.HardwareType)
                {
                    case HardwareType.Cpu:
                        sw.Restart();
                        (cpuTemp, cpuUse) = GetCpuInfo(hardware);
                        if (_debug) Console.WriteLine($"CPU info check took: {sw.ElapsedMilliseconds}ms");
                        break;

                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                        sw.Restart();
                        (gpuTemp, gpuUse) = GetGpuInfo(hardware);
                        if (_debug) Console.WriteLine($"GPU info check took: {sw.ElapsedMilliseconds}ms");
                        break;

                    case HardwareType.Storage:
                        if (string.IsNullOrEmpty(_selectedStorage) == false && hardware.Name != _selectedStorage)
                        {
                            break;
                        }
                        sw.Restart();
                        diskTemp = GetDiskTemp(hardware);
                        if (_debug) Console.WriteLine($"Disk temperature check took: {sw.ElapsedMilliseconds}ms");
                        break;
                }
            }

            sw.Restart();
            memUse = GetMemoryUsage();
            if (_debug) Console.WriteLine($"Memory usage check took: {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            if (_debug)
            {
                Console.WriteLine($"Error accessing hardware: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }
        finally
        {
            if (_computer != null)
            {
                _computer.Close();
                _computer = null;
            }
        }

        return Task.FromResult(new SystemInfo(cpuTemp, cpuUse, gpuTemp, gpuUse, memUse, diskTemp));
    }

    public void Cleanup()
    {
        if (_computer != null)
        {
            _computer.Close();
            _computer = null;
        }
    }

    private (string temp, string usage) GetCpuInfo(IHardware hardware)
    {
        float totalCpuLoad = 0;
        int loadSensorCount = 0;
        float? packageTemp = null;

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
            {
                if (sensor.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase))
                {
                    packageTemp = sensor.Value;
                    break;
                }
                else if (sensor.Name.Equals("Core Average", StringComparison.OrdinalIgnoreCase) && !packageTemp.HasValue)
                {
                    packageTemp = sensor.Value;
                }
                else if (sensor.Name.Equals("Core Max", StringComparison.OrdinalIgnoreCase) && !packageTemp.HasValue)
                {
                    packageTemp = sensor.Value;
                }
            }
            else if (sensor.SensorType == SensorType.Load && sensor.Value.HasValue)
            {
                if (sensor.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase))
                {
                    totalCpuLoad = sensor.Value.Value;
                    loadSensorCount = 1;
                }
            }
        }

        string temp = packageTemp.HasValue ? $"{packageTemp:F0}C" : "--";
        string usage = loadSensorCount > 0 ? $"{totalCpuLoad:F0}%" : "--";

        return (temp, usage);
    }

    private (string temp, string usage) GetGpuInfo(IHardware hardware)
    {
        string temp = "--", usage = "--";

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
            {
                temp = $"{sensor.Value:F0}C";
            }
            else if (sensor.SensorType == SensorType.Load && sensor.Value.HasValue)
            {
                usage = $"{sensor.Value:F0}%";
            }
        }

        return (temp, usage);
    }

    private string GetDiskTemp(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
            {
                return $"{sensor.Value:F0}C";
            }
        }
        return "--";
    }

    private string GetMemoryUsage()
    {
        var memInfo = new MEMORYSTATUSEX();
        memInfo.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        GlobalMemoryStatusEx(ref memInfo);
        return $"{(memInfo.ullTotalPhys - memInfo.ullAvailPhys) * 100 / memInfo.ullTotalPhys:F0}%";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public int GetUpdateDelay()
    {
        return 500; // 500ms for Windows
    }
} 