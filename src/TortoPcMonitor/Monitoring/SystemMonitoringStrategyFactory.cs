namespace DivoomPCDataTool.Monitoring;

public static class SystemMonitoringStrategyFactory
{
    public static ISystemMonitoringStrategy Create(bool debug = false)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsMonitoringStrategy(debug);
        }
        else if (OperatingSystem.IsMacOS())
        {
            return new MacOSMonitoringStrategy(debug);
        }
        
        throw new PlatformNotSupportedException("Current operating system is not supported.");
    }
} 