using System;
using System.Collections.Generic;

namespace GameWall.Models
{
    /// <summary>
    /// 一次硬件快照。全部字段都来自本机的注册表 / Win32 API，
    /// 不发任何网络请求，也不需要第三方硬件库。
    /// </summary>
    public sealed class HardwareSnapshot
    {
        public CpuInfo Cpu { get; set; } = new();
        public List<GpuInfo> Gpus { get; set; } = new();
        public MemoryInfo Memory { get; set; } = new();
        public List<DiskInfo> Disks { get; set; } = new();
        public List<DisplayInfo> Displays { get; set; } = new();
        public MachineInfo Machine { get; set; } = new();
        public List<NetworkInfo> Networks { get; set; } = new();
        public DateTime CapturedAt { get; set; } = DateTime.Now;
    }

    public sealed class CpuInfo
    {
        public string Name { get; set; } = "未知处理器";
        public int PhysicalCores { get; set; }
        public int LogicalProcessors { get; set; }
        public int BaseClockMhz { get; set; }
        public string Architecture { get; set; } = string.Empty;

        /// <summary>核心拓扑摘要，例如「8 核 16 线程」。</summary>
        public string TopologyText =>
            PhysicalCores > 0
                ? $"{PhysicalCores} 核 {LogicalProcessors} 线程"
                : $"{LogicalProcessors} 线程";
    }

    public sealed class GpuInfo
    {
        public string Name { get; set; } = "未知显卡";
        public string DriverVersion { get; set; } = string.Empty;
        public string DriverDate { get; set; } = string.Empty;
        public long MemoryBytes { get; set; }
        public string Vendor { get; set; } = string.Empty;

        public string MemoryText => MemoryBytes > 0
            ? FormatSize(MemoryBytes)
            : "未知";

        internal static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            var index = 0;
            while (value >= 1024 && index < units.Length - 1)
            {
                value /= 1024;
                index++;
            }
            return index <= 1
                ? $"{value:0} {units[index]}"
                : $"{value:0.#} {units[index]}";
        }
    }

    public sealed class MemoryInfo
    {
        public long TotalBytes { get; set; }
        public long AvailableBytes { get; set; }
        public long PageFileTotalBytes { get; set; }
        public long PageFileAvailableBytes { get; set; }
        public int LoadPercent { get; set; }
        public string SpeedText { get; set; } = string.Empty;
        public int ModuleCount { get; set; }

        public long UsedBytes => Math.Max(0, TotalBytes - AvailableBytes);

        public string TotalText => GpuInfo.FormatSize(TotalBytes);
        public string UsedText => GpuInfo.FormatSize(UsedBytes);
        public string AvailableText => GpuInfo.FormatSize(AvailableBytes);
    }

    public sealed class DiskInfo
    {
        public string Drive { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string FileSystem { get; set; } = string.Empty;
        public string DriveType { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }

        public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

        public double UsedPercent => TotalBytes > 0 ? UsedBytes * 100.0 / TotalBytes : 0;

        public string UsageText =>
            $"{GpuInfo.FormatSize(UsedBytes)} / {GpuInfo.FormatSize(TotalBytes)}（剩余 {GpuInfo.FormatSize(FreeBytes)}）";
    }

    public sealed class DisplayInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Adapter { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public int RefreshRate { get; set; }
        public int BitsPerPixel { get; set; }
        public bool IsPrimary { get; set; }

        public string ResolutionText =>
            Width > 0 ? $"{Width} × {Height} @ {RefreshRate} Hz" : "未知";
    }

    public sealed class MachineInfo
    {
        public string Manufacturer { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Board { get; set; } = string.Empty;
        public string BiosVersion { get; set; } = string.Empty;
        public string BiosDate { get; set; } = string.Empty;
        public string OsName { get; set; } = string.Empty;
        public string OsVersion { get; set; } = string.Empty;
        public string OsBuild { get; set; } = string.Empty;
        public string ComputerName { get; set; } = string.Empty;

        public string MachineText =>
            string.IsNullOrWhiteSpace(Model) ? "未知机型" : $"{Manufacturer} {Model}".Trim();
    }

    public sealed class NetworkInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string MacAddress { get; set; } = string.Empty;
        public long SpeedBps { get; set; }
        public bool IsUp { get; set; }

        public string SpeedText => SpeedBps > 0
            ? $"{SpeedBps / 1_000_000d:0.#} Mbps"
            : "未知";
    }

    /// <summary>
    /// 一条优化建议：由当前硬件快照推导出的「这台机器上值得开/值得关」的项目。
    /// 只是建议，不做任何自动修改。
    /// </summary>
    public sealed class HardwareAdvice
    {
        public string Title { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;

        /// <summary>关联的优化项 Id，用户点「去处理」时能直接定位。</summary>
        public string? TweakId { get; set; }

        /// <summary>true 表示这是「值得开启」的建议，false 表示「注意/不建议」。</summary>
        public bool Positive { get; set; } = true;
    }
}
