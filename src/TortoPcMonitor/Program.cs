using System.Runtime.InteropServices;
using Newtonsoft.Json;
using LibreHardwareMonitor.Hardware;
using DivoomPCDataTool.Monitoring;

namespace DivoomPCDataTool;

public class Program
{
    private static int statusStartLine = 0; // Add this field at class level
    private static bool DEBUG = false; // Add this line at the class level

    private static ISystemMonitoringStrategy? _monitoringStrategy;

    public static async Task Main(string[] args)
    {
        // Check for debug flag
        DEBUG = args.Contains("-D");

        Console.WriteLine("Times Gate - PC Monitor Tool");
        await StartUp();
        await StartMonitoring();
    }

    private static void PrintDeviceInfo(string index, DivoomDevice device)
    {
        Console.WriteLine($"{index}. {device.DeviceName} (IP: {device.DevicePrivateIP}, ID: {device.DeviceId})");
    }

    private static async Task StartUp()
    {
        _monitoringStrategy = SystemMonitoringStrategyFactory.Create(DEBUG);
        await _monitoringStrategy.Initialize();
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
            Console.Write("\nSelect device number: ");

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
            await Task.Delay(_monitoringStrategy!.GetUpdateDelay());
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

    private static async Task SendHardwareInfo(DivoomDevice device, int selectedLcdIndex)
    {
        var systemInfo = await _monitoringStrategy!.GetSystemInfo();

        var PostInfo = new DivoomDevicePostList
        {
            Command = "Device/UpdatePCParaInfo",
            ScreenList = new DivoomDevicePostItem[]
            {
                new DivoomDevicePostItem
                {
                    LcdId = selectedLcdIndex,
                    DispData = new[] { systemInfo.CpuUsage, systemInfo.GpuUsage, systemInfo.CpuTemperature, systemInfo.GpuTemperature, systemInfo.MemoryUsage, systemInfo.DiskTemperature }
                }
            }
        };

        // Send data to device
        string para_info = JsonConvert.SerializeObject(PostInfo);
        await HttpPost($"http://{device.DevicePrivateIP}:80/post", para_info);
        if (DEBUG) return;

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
        Console.Write($"│ CPU      │{CenterText(systemInfo.CpuUsage, 8)}│{CenterText(systemInfo.CpuTemperature, 8)}│");
        Console.SetCursorPosition(0, statusStartLine + 5);
        Console.Write($"│ GPU      │{CenterText(systemInfo.GpuUsage, 8)}│{CenterText(systemInfo.GpuTemperature, 8)}│");
        Console.SetCursorPosition(0, statusStartLine + 6);
        Console.Write($"│ Memory   │{CenterText(systemInfo.MemoryUsage, 8)}│{CenterText("--", 8)}│");
        Console.SetCursorPosition(0, statusStartLine + 7);
        Console.Write($"│ Disk     │{CenterText("--", 8)}│{CenterText(systemInfo.DiskTemperature, 8)}│");
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