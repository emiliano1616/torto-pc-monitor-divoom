namespace DivoomPCDataTool.Monitoring;

public interface ISystemMonitoringStrategy
{
    Task<bool> Initialize();
    Task<SystemInfo> GetSystemInfo();
    void Cleanup();
    int GetUpdateDelay();
}

public record SystemInfo(
    string CpuTemperature,
    string CpuUsage,
    string GpuTemperature,
    string GpuUsage,
    string MemoryUsage,
    string DiskTemperature
); 