namespace DivoomPCDataTool.Monitoring;

public class MacOSMonitoringStrategy : ISystemMonitoringStrategy
{
    private readonly bool _debug;
    private bool _sudoAccess = false;

    public MacOSMonitoringStrategy(bool debug = false)
    {
        _debug = debug;
    }

    public async Task<bool> Initialize()
    {
        var iStatsVersion = await ExecuteCommand("gem", "list -i istats");

        if (!iStatsVersion.Contains("true"))
        {
            Console.WriteLine("iStats is not installed. Run this command: sudo gem install iStats");
            return false;
        }

        await ExecuteCommand("istats", "enable TCGC");

        var sudoCheck = await ExecuteCommand("sudo", "-n true");
        if (!string.IsNullOrEmpty(sudoCheck))
        {
            Console.WriteLine("Getting the GPU usage requires sudo access. Do you want to fetch the GPU usage? (y/n)");
            var input = Console.ReadLine();
            if (input?.ToLower() == "y")
            {
                sudoCheck = await ExecuteCommand("sudo", "true");
                if (string.IsNullOrEmpty(sudoCheck))
                {
                    _sudoAccess = true;
                }
                else
                {
                    Console.WriteLine("Failed to get sudo access. Not showing GPU usage.");
                }
            }
        }
        else
        {
            _sudoAccess = true;
        }

        return true;
    }

    public async Task<SystemInfo> GetSystemInfo()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Get CPU temperature and usage
        var cpuTemp = await GetCpuTemperature();
        if (_debug) Console.WriteLine($"CPU temperature check took: {sw.ElapsedMilliseconds}ms");

        sw.Restart();
        var cpuUse = await GetCpuUsage();
        if (_debug) Console.WriteLine($"CPU usage check took: {sw.ElapsedMilliseconds}ms");

        // Get Memory usage
        sw.Restart();
        var memUse = await GetMemoryUsage();
        if (_debug) Console.WriteLine($"Memory usage check took: {sw.ElapsedMilliseconds}ms");

        // Get GPU information
        sw.Restart();
        var gpuTemp = await GetGpuTemperature();
        if (_debug) Console.WriteLine($"GPU temperature check took: {sw.ElapsedMilliseconds}ms");

        sw.Restart();
        var gpuUse = await GetGpuUsage();
        if (_debug) Console.WriteLine($"GPU usage check took: {sw.ElapsedMilliseconds}ms");

        // Get Disk temperature
        sw.Restart();
        var diskTemp = await GetDiskTemperature();
        if (_debug) Console.WriteLine($"Disk temperature check took: {sw.ElapsedMilliseconds}ms");

        return new SystemInfo(cpuTemp, cpuUse, gpuTemp, gpuUse, memUse, diskTemp);
    }

    public void Cleanup()
    {
        // No cleanup needed for macOS
    }

    private async Task<string> GetCpuTemperature()
    {
        var cpuTempOutput = await ExecuteCommand("istats", "cpu temp --value-only");
        return $"{float.Parse(cpuTempOutput.Trim()):F0}C";
    }

    private async Task<string> GetCpuUsage()
    {
        var cpuOutput = await ExecuteCommand("top", "-l 1 -n 0 -s 0");
        var cpuLines = cpuOutput.Split('\n');
        var cpuLine = cpuLines.FirstOrDefault(l => l.Contains("CPU usage:"));
        if (cpuLine != null)
        {
            var parts = cpuLine.Split(':')[1].Split(',');
            var userPct = float.Parse(parts[0].Replace("% user", "").Trim());
            var sysPct = float.Parse(parts[1].Replace("% sys", "").Trim());
            var totalCpu = Math.Min(userPct + sysPct, 100);
            return $"{totalCpu:F0}%";
        }
        return "--";
    }

    private async Task<string> GetMemoryUsage()
    {
        var vmstat = await ExecuteCommand("vm_stat", "");
        var lines = vmstat.Split('\n');
        if (lines.Length > 1)
        {
            ulong freePages = 0, activePages = 0, inactivePages = 0,
                  wiredPages = 0, compressedPages = 0;
            const ulong PAGE_SIZE = 4096;

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

            var usedMemoryBytes = (activePages + wiredPages + compressedPages) * PAGE_SIZE;
            var totalPhysicalMemory = await ExecuteCommand("sysctl", "-n hw.memsize");
            var totalMemoryBytes = ulong.Parse(totalPhysicalMemory);
            var memoryUsagePercent = (usedMemoryBytes * 100) / totalMemoryBytes;

            return $"{memoryUsagePercent}%";
        }
        return "--";
    }

    private async Task<string> GetGpuTemperature()
    {
        try
        {
            var gpuTempOutput = await ExecuteCommand("istats", "scan TCGC --value-only");
            if (!string.IsNullOrEmpty(gpuTempOutput))
            {
                return $"{float.Parse(gpuTempOutput.Trim()):F0}C";
            }
        }
        catch (Exception ex)
        {
            if (_debug) Console.WriteLine($"Error getting macOS GPU temperature: {ex.Message}");
        }
        return "--";
    }

    private async Task<string> GetGpuUsage()
    {
        try
        {
            if (_sudoAccess)
            {
                var gpuUsageOutput = await ExecuteCommand("sudo", "powermetrics -n 1 -i 1000 --samplers gpu_power");
                return ParseGpuUsageFromPowermetrics(gpuUsageOutput);
            }
        }
        catch (Exception ex)
        {
            if (_debug) Console.WriteLine($"Error getting macOS GPU usage: {ex.Message}");
        }
        return "--";
    }

    private async Task<string> GetDiskTemperature()
    {
        try
        {
            var diskTempOutput = await ExecuteCommand("istats", "scan TaLC --value-only");
            if (!string.IsNullOrEmpty(diskTempOutput))
            {
                return $"{float.Parse(diskTempOutput.Trim()):F0}C";
            }
        }
        catch { }
        return "--";
    }

    private string ParseGpuUsageFromPowermetrics(string output)
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
            if (_debug) Console.WriteLine($"Error parsing GPU usage: {ex.Message}");
        }
        return "--";
    }

    private async Task<string> ExecuteCommand(string command, string arguments)
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

    public int GetUpdateDelay()
    {
        return 5000; // 5 seconds for macOS
    }
} 