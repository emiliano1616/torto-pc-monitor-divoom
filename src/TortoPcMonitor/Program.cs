using System.Net;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using LibreHardwareMonitor.Hardware;
using System.Linq;
using System.Security.Principal;

namespace DivoomPCDataTool;

public class Program
{
    private static Computer? computer;
    private static UpdateVisitor? updateVisitor;
    private static int statusStartLine = 0; // Add this field at class level
    private static bool DEBUG = false; // Add this line at the class level

    private static bool sudoAccess = false;

    public static async Task Main(string[] args)
    {
        Console.WriteLine("Times Gate - PC Monitor Tool");

        await StartUp();
        // Start monitoring loop
        await StartMonitoring();
    }

    private static void PrintDeviceInfo(string index, DivoomDevice device)
    {
        Console.WriteLine($"{index}. {device.DeviceName} (IP: {device.DevicePrivateIP}, ID: {device.DeviceId})");
    }

    private static async Task StartUp()
    {
        if(OperatingSystem.IsWindows())
        {
            // Check if running with admin privileges
            bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);
            
            if (!isAdmin)
            {
                Console.WriteLine("Warning: Running without administrator privileges. Some sensors may not be accessible.");
                Console.WriteLine("Try running the application as administrator for full hardware monitoring support.");
            }
        }

        if(OperatingSystem.IsMacOS())
        {
            // Check if iStats is installed
            var iStatsVersion = await ExecuteCommand("gem", "list -i istats");
            
            if (!iStatsVersion.Contains("true"))
            {
                Console.WriteLine("iStats is not installed. Run this command: sudo gem install iStats");
                throw new Exception("iStats is not installed");
            }

            await ExecuteCommand("istats", "enable TCGC");

            var sudoCheck = await ExecuteCommand("sudo", "-n true");
            if (!string.IsNullOrEmpty(sudoCheck))
            {
                Console.WriteLine("Getting the GPU usage requires sudo access. Do you want to fetch the GPU usage? (y/n)");
                var input = Console.ReadLine();
                if (input == "y")
                {
                    sudoCheck = await ExecuteCommand("sudo", "true");
                    if (string.IsNullOrEmpty(sudoCheck))
                    {
                        sudoAccess = true;
                    } else {
                        Console.WriteLine("Failed to get sudo access. Not showing GPU usage.");
                    }
                }
            } else {
                sudoAccess = true;
            }
        }
    }

    private static async Task StartMonitoring()
    {
        var devices = await UpdateDeviceList();
        if (devices == null || devices.DeviceList == null || devices.DeviceList.Count == 0)
        {
            Console.WriteLine("No devices found.");
            return;
        }
        var selectedDevice = devices.DeviceList[0];

        if (devices.DeviceList.Count == 1)
        {
            PrintDeviceInfo("AutoSelecting device: ", devices.DeviceList[0]);
        }
        else if (devices.DeviceList.Count > 1)
        {
            Console.WriteLine("\nAvailable Devices:");
            for (int i = 0; i < devices.DeviceList.Count; i++)
            {
                PrintDeviceInfo((i + 1).ToString(), devices.DeviceList[i]);
            }
            Console.Write("\nSelect device number (or press Ctrl+C to exit): ");

            if (!int.TryParse(Console.ReadLine(), out int selection) || selection <= 0 || selection > devices.DeviceList.Count)
            {
                Console.WriteLine("Invalid device selection.");
                return;
            }

            selectedDevice = devices.DeviceList[selection - 1];
        }

        string url_info = $"http://app.divoom-gz.com/Channel/Get5LcdInfoV2?DeviceType=LCD&DeviceId={selectedDevice.DeviceId}";
        string lcdClockInfoStr = await HttpGet(url_info);
        if (string.IsNullOrEmpty(lcdClockInfoStr))
        {
            Console.WriteLine($"No clock found");
            return;
        }

        var lcdClockInfo = JsonConvert.DeserializeObject<DivoomLcdClockInfo>(lcdClockInfoStr);
        var selectedIndependenceIndex = -1;
        var selectedLcdIndex = -1;
        for (int i = 0; i < lcdClockInfo!.LcdIndependenceList.Count; i++)
        {
            selectedLcdIndex = lcdClockInfo.LcdIndependenceList[i].LcdList.FindIndex(l => l.LcdClockId == 625);
            if (selectedLcdIndex >= 0)
            {
                selectedIndependenceIndex = i;
                break;
            }
        }
        var selectedIndependence = selectedIndependenceIndex >= 0
            ? lcdClockInfo.LcdIndependenceList[selectedIndependenceIndex]
            : null;
        if (selectedIndependence == null)
        {
            Console.WriteLine($"No clock found with PC monitor selected.");
            return;
        }
        await SetIndependence(selectedDevice, selectedIndependence);

        while (true)
        {
            await SendHardwareInfo(selectedDevice, selectedLcdIndex);
            await Task.Delay(5000);
        }
    }

    private static async Task SetIndependence(DivoomDevice device, LcdIndependenceInfo independence)
    {
        var para_info = JsonConvert.SerializeObject(new Set5LcdChannelTypeRequest
        {
            Command = "Channel/Set5LcdChannelType",
            ChannelType = 1, //Independent dial
            LcdIndependence = independence.LcdIndependence,
        });

        await HttpPost($"http://{device.DevicePrivateIP}:80/post", para_info);
    }

    private static async Task<DivoomDeviceList?> UpdateDeviceList()
    {
        string url_info = "http://app.divoom-gz.com/Device/ReturnSameLANDevice";
        string device_list = await HttpGet(url_info);
        return JsonConvert.DeserializeObject<DivoomDeviceList>(device_list);
    }

    private static async Task<(string cpuTemp, string cpuUse, string gpuTemp,
        string gpuUse, string memUse, string diskTemp)> GetSystemInfo()
    {
        string cpuTemp = "--", cpuUse = "--", gpuTemp = "--",
               gpuUse = "--", memUse = "--", diskTemp = "--";

        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (computer != null)
                {
                    computer.Close();
                    computer = null;
                }

                updateVisitor = new UpdateVisitor();
                computer = new Computer
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

                computer.Open();
                computer.Accept(updateVisitor);

                foreach (var hardware in computer.Hardware)
                {
                    hardware.Update();

                    switch (hardware.HardwareType)
                    {
                        case HardwareType.Cpu:
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

                            if (packageTemp.HasValue)
                            {
                                cpuTemp = $"{packageTemp:F0}C";
                            }

                            if (loadSensorCount > 0)
                            {
                                cpuUse = $"{totalCpuLoad:F0}%";
                            }
                            break;

                        case HardwareType.GpuNvidia:
                        case HardwareType.GpuAmd:
                            foreach (var sensor in hardware.Sensors)
                            {
                                if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                                {
                                    gpuTemp = $"{sensor.Value:F0}C";
                                }
                                else if (sensor.SensorType == SensorType.Load && sensor.Value.HasValue)
                                {
                                    gpuUse = $"{sensor.Value:F0}%";
                                }
                            }
                            break;

                        case HardwareType.Storage:
                            foreach (var sensor in hardware.Sensors)
                            {
                                if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                                {
                                    diskTemp = $"{sensor.Value:F0}C";
                                    break;
                                }
                            }
                            break;
                    }
                }

                var memInfo = new MEMORYSTATUSEX();
                memInfo.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                GlobalMemoryStatusEx(ref memInfo);
                memUse = $"{(memInfo.ullTotalPhys - memInfo.ullAvailPhys) * 100 / memInfo.ullTotalPhys:F0}%";
            }
            catch (Exception ex)
            {
                if (DEBUG)
                {
                    Console.WriteLine($"Error accessing hardware: {ex.Message}");
                    Console.WriteLine($"Stack trace: {ex.StackTrace}");
                }
            }
            finally
            {
                if (computer != null)
                {
                    computer.Close();
                    computer = null;
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            // Get CPU temperature using iStats
            var cpuTempOutput = await ExecuteCommand("istats", "cpu temp --value-only");
            cpuTemp = $"{float.Parse(cpuTempOutput.Trim()):F0}C";
            if (DEBUG) Console.WriteLine($"CPU temperature check took: {sw.ElapsedMilliseconds}ms");

            // Get CPU usage using top
            sw.Restart();
            var cpuOutput = await ExecuteCommand("top", "-l 1 -n 0 -s 0");
            var cpuLines = cpuOutput.Split('\n');
            var cpuLine = cpuLines.FirstOrDefault(l => l.Contains("CPU usage:"));
            if (cpuLine != null)
            {
                var parts = cpuLine.Split(':')[1].Split(',');
                var userPct = float.Parse(parts[0].Replace("% user", "").Trim());
                var sysPct = float.Parse(parts[1].Replace("% sys", "").Trim());
                var totalCpu = Math.Min(userPct + sysPct, 100);
                cpuUse = $"{totalCpu:F0}%";
            }
            if (DEBUG) Console.WriteLine($"CPU usage check took: {sw.ElapsedMilliseconds}ms");

            // Fan speeds
            sw.Restart();
            var fanSpeedOutput = await ExecuteCommand("istats", "fan speed --value-only");
            if (DEBUG) Console.WriteLine($"Fan speed check took: {sw.ElapsedMilliseconds}ms");

            // Memory usage
            sw.Restart();
            var vmstat = await ExecuteCommand("vm_stat", "");
            var lines = vmstat.Split('\n');
            if (lines.Length > 1)
            {
                ulong freePages = 0, activePages = 0, inactivePages = 0, 
                      wiredPages = 0, compressedPages = 0;
                const ulong PAGE_SIZE = 4096; // Size of a page in bytes

                foreach (var line in lines)
                {
                    if (line.Contains("Pages free:"))
                        ulong.TryParse(line.Split(':')[1].Trim('.', ' '), out freePages);
                    else if (line.Contains("Pages active:"))
                        ulong.TryParse(line.Split(':')[1].Trim('.', ' '), out activePages);
                    else if (line.Contains("Pages inactive:"))
                        ulong.TryParse(line.Split(':')[1].Trim('.', ' '), out inactivePages);
                    else if (line.Contains("Pages wired down:"))
                        ulong.TryParse(line.Split(':')[1].Trim('.', ' '), out wiredPages);
                    else if (line.Contains("Pages occupied by compressor:"))
                        ulong.TryParse(line.Split(':')[1].Trim('.', ' '), out compressedPages);
                }


                // Calculate actual memory usage - include active, wired, compressed 
                var usedMemoryBytes = (activePages + wiredPages + compressedPages) * PAGE_SIZE;
                var totalPhysicalMemory = await ExecuteCommand("sysctl", "-n hw.memsize");
                var totalMemoryBytes = ulong.Parse(totalPhysicalMemory);
                var memoryUsagePercent = (usedMemoryBytes * 100) / totalMemoryBytes;
                
                memUse = $"{memoryUsagePercent}%";
            }
            if (DEBUG) Console.WriteLine($"Memory usage check took: {sw.ElapsedMilliseconds}ms");

            // GPU information
            sw.Restart();
            
            // GPU Temperature
            try
            {
                var gpuTempOutput = await ExecuteCommand("istats", "scan TCGC --value-only");
                if (!string.IsNullOrEmpty(gpuTempOutput))
                {
                    gpuTemp = $"{float.Parse(gpuTempOutput.Trim()):F0}C";
                }
            }
            catch (Exception ex)
            {
                gpuTemp = "N/A";
                if (DEBUG) Console.WriteLine($"Error getting macOS GPU temperature: {ex.Message}");
            }

            if (DEBUG) Console.WriteLine($"GPU temperature check took: {sw.ElapsedMilliseconds}ms");

            sw.Restart();

            // GPU Usage
            try
            {
                if (sudoAccess)
                {
                    var gpuUsageOutput = await ExecuteCommand("sudo", "powermetrics -n 1 -i 1000 --samplers gpu_power");
                    gpuUse = ParseGpuUsageFromPowermetrics(gpuUsageOutput);
                }
            }
            catch (Exception ex)
            {
                gpuUse = "N/A";
                if (DEBUG) Console.WriteLine($"Error getting macOS GPU usage: {ex.Message}");
            }
            
            if (DEBUG) Console.WriteLine($"GPU usage check took: {sw.ElapsedMilliseconds}ms");

            // Disk temperature
            sw.Restart();
            try
            {
                var diskTempOutput = await ExecuteCommand("istats", "scan TaLC --value-only");
                if (!string.IsNullOrEmpty(diskTempOutput))
                {
                    diskTemp = $"{float.Parse(diskTempOutput.Trim()):F0}C";
                }
            }
            catch
            {
                diskTemp = "N/A";
            }
            if (DEBUG) Console.WriteLine($"Disk temperature check took: {sw.ElapsedMilliseconds}ms");
        }

        return (cpuTemp, cpuUse, gpuTemp, gpuUse, memUse, diskTemp);
    }

    private static string ParseGpuUsageFromPowermetrics(string output)
    {
        try
        {
            var lines = output.Split('\n');
            var gpuBusyLine = lines.FirstOrDefault(l => l.Contains("GPU 0 GPU Busy"));
            if (gpuBusyLine != null)
            {
                var usage = gpuBusyLine.Split(':')[1].Trim().Replace("%", "");
                if (float.TryParse(usage, out float gpuValue))
                {
                    return $"{gpuValue:F0}%";
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing GPU usage: {ex.Message}");
        }
        return "N/A";
    }

    private static async Task<string> ExecuteCommand(string command, string arguments)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = command;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        process.Start();
        string result = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (!string.IsNullOrEmpty(error))
        {
            return error;
        }

        return result;
    }

    private static async Task SendHardwareInfo(DivoomDevice device, int selectedLcdIndex)
    {
        var (cpuTemp, cpuUse, gpuTemp, gpuUse, memUse, diskTemp) = await GetSystemInfo();

        var PostInfo = new DivoomDevicePostList
        {
            Command = "Device/UpdatePCParaInfo",
            ScreenList = new DivoomDevicePostItem[]
            {
                new DivoomDevicePostItem
                {
                    LcdId = selectedLcdIndex,
                    DispData = new[] { cpuUse, gpuUse, cpuTemp, gpuTemp, memUse, diskTemp }
                }
            }
        };

        // Send data to device
        string para_info = JsonConvert.SerializeObject(PostInfo);
        await HttpPost($"http://{device.DevicePrivateIP}:80/post", para_info);
        if(DEBUG) return;

        // If this is the first time, save the current cursor position
        if (statusStartLine == 0)
        {
            Console.WriteLine("\nSystem Status:");
            Console.WriteLine("┌──────────┬────────┬────────┐");
            Console.WriteLine("│ Component│   Use  │  Temp  │");
            Console.WriteLine("├──────────┼────────┼────────┤");
            Console.WriteLine("│ CPU      │        │        │");
            Console.WriteLine("│ GPU      │        │        │");
            Console.WriteLine("│ Memory   │        │   --   │");
            Console.WriteLine("│ Disk     │   --   │        │");
            Console.WriteLine("└──────────┴────────┴────────┘");
            statusStartLine = Console.CursorTop - 9; // Save the starting line
        }

        // Move cursor to the saved position and update values
        int currentLine = Console.CursorTop;
        Console.SetCursorPosition(0, statusStartLine + 4);
        Console.Write($"│ CPU      │{CenterText(cpuUse, 8)}│{CenterText(cpuTemp, 8)}│");
        Console.SetCursorPosition(0, statusStartLine + 5);
        Console.Write($"│ GPU      │{CenterText(gpuUse, 8)}│{CenterText(gpuTemp, 8)}│");
        Console.SetCursorPosition(0, statusStartLine + 6);
        Console.Write($"│ Memory   │{CenterText(memUse, 8)}│{CenterText("--", 8)}│");
        Console.SetCursorPosition(0, statusStartLine + 7);
        Console.Write($"│ Disk     │{CenterText("--", 8)}│{CenterText(diskTemp, 8)}│");
        Console.SetCursorPosition(0, statusStartLine + 8);
        Console.Write("└──────────┴────────┴────────┘");
        Console.SetCursorPosition(0, currentLine);
    }

    // Add this helper method to center text
    private static string CenterText(string text, int width)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text)) text = " ";
        return text.PadLeft((width - text.Length) / 2 + text.Length).PadRight(width);
    }

    private static async Task<string> HttpGet(string Url)
    {
        using var client = new HttpClient();
        return await client.GetStringAsync(Url);
    }

    private static async Task<string> HttpPost(string url, string sendData)
    {
        using var client = new HttpClient();
        var content = new StringContent(sendData, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content);
        var result = await response.Content.ReadAsStringAsync();
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
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
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}

// Keep the existing model classes
public class DivoomDeviceList
{
    public required List<DivoomDevice> DeviceList { get; set; }
}

public class DivoomDevice
{
    public string? DeviceName { get; set; }
    public string? DevicePrivateIP { get; set; }
    public int DeviceId { get; set; }
    public int Hardware { get; set; }
}

public class DivoomDevicePostItem
{
    public int LcdId { get; set; }
    public string[]? DispData { get; set; }
}

public class DivoomDevicePostList
{
    public string? Command { get; set; }
    public DivoomDevicePostItem[]? ScreenList { get; set; }
}

public class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer)
    {
        computer.Traverse(this);
    }

    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (IHardware subHardware in hardware.SubHardware)
            subHardware.Accept(this);
    }

    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}


// Add these classes after your existing model classes
public class DivoomLcdClockInfo
{
    public int ReturnCode { get; set; }
    public string ReturnMessage { get; set; } = string.Empty;
    public int ChannelType { get; set; }
    public int LcdIndependence { get; set; }
    public int ClockId { get; set; }
    public List<LcdIndependenceInfo> LcdIndependenceList { get; set; } = new();
    public int DeviceId { get; set; }
}

public class LcdIndependenceInfo
{
    public string IndependenceName { get; set; } = string.Empty;
    public int LcdIndependence { get; set; }
    public int? LcdIndependPos { get; set; }
    public List<LcdClockItem> LcdList { get; set; } = new();
}

public class LcdClockItem
{
    public int LcdClockId { get; set; }
    public int? LcdSelectIndex { get; set; }
    public string? ClockImagePixelId { get; set; }
}

public class Set5LcdChannelTypeRequest
{
    public int ChannelType { get; set; }
    public int LcdIndependence { get; set; }
    public required string Command { get; set; }
}