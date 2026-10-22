using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using GameWall.Models;

namespace GameWall.Services
{
    /// <summary>
    /// 硬件报告导出（TXT / JSON）。
    /// 只写用户选定的文件，不上传任何东西。
    /// </summary>
    public static class HardwareReportService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string BuildText(HardwareSnapshot snapshot, IReadOnlyList<PowerPlanInfo> plans)
        {
            var sb = new StringBuilder();
            void Line(string text = "") => sb.AppendLine(text);
            void Section(string title)
            {
                Line();
                Line(new string('─', 46));
                Line(title);
                Line(new string('─', 46));
            }

            Line($"GameWall 硬件报告    {snapshot.CapturedAt:yyyy-MM-dd HH:mm:ss}");
            Line($"GameWall 版本        {AppInfo.VersionLabel}");
            Line($"单文件发布           {(AppInfo.IsSingleFileBuild ? "是" : "否（框架依赖版）")}");

            Section("处理器");
            if (!string.IsNullOrWhiteSpace(snapshot.Cpu.Name)) Line($"型号      {snapshot.Cpu.Name}");
            Line($"核心      {snapshot.Cpu.TopologyText}");
            if (!string.IsNullOrWhiteSpace(snapshot.Cpu.Architecture)) Line($"架构      {snapshot.Cpu.Architecture}");
            if (snapshot.Cpu.BaseClockMhz > 0) Line($"基准频率  {snapshot.Cpu.BaseClockMhz} MHz");

            Section("显卡");
            if (snapshot.Gpus.Count == 0) Line("未能读取到显卡信息");
            foreach (var gpu in snapshot.Gpus)
            {
                Line($"型号      {gpu.Name}{(gpu.IsVirtual ? "（虚拟 / 远程显示适配器）" : string.Empty)}");
                if (!string.IsNullOrEmpty(gpu.Vendor)) Line($"厂商      {gpu.Vendor}");
                if (gpu.HasMemory) Line($"显存      {gpu.MemoryText}");
                if (!string.IsNullOrEmpty(gpu.DriverVersion)) Line($"驱动版本  {gpu.DriverVersion}");
                if (!string.IsNullOrEmpty(gpu.DriverDate)) Line($"驱动日期  {gpu.DriverDate}");
                Line();
            }

            Section("内存");
            Line($"容量      {snapshot.Memory.TotalText}");
            if (snapshot.Memory.TotalBytes > 0)
            {
                Line($"已用      {snapshot.Memory.UsedText}（{snapshot.Memory.LoadPercent}%）");
                Line($"可用      {snapshot.Memory.AvailableText}");
            }

            if (snapshot.Memory.PageFileTotalBytes > 0)
            {
                Line($"页面文件  {GpuInfo.FormatSize(snapshot.Memory.PageFileTotalBytes - snapshot.Memory.PageFileAvailableBytes)}" +
                     $" / {GpuInfo.FormatSize(snapshot.Memory.PageFileTotalBytes)}");
            }

            Section("磁盘");
            if (snapshot.Disks.Count == 0) Line("未能读取到磁盘信息");
            foreach (var disk in snapshot.Disks)
            {
                var label = string.IsNullOrWhiteSpace(disk.Label) ? string.Empty : $"（{disk.Label}）";
                Line($"{disk.Drive}{label}  {disk.DriveType} / {disk.FileSystem}");
                Line($"   {disk.UsageText}  已用 {disk.UsedPercent:0}%");
            }

            Section("显示器");
            if (snapshot.Displays.Count == 0) Line("未能读取到显示器信息");
            foreach (var display in snapshot.Displays)
            {
                Line($"{display.Name}{(display.IsPrimary ? "（主屏）" : string.Empty)}");
                var line = display.ResolutionText;
                if (display.BitsPerPixel > 0) line += $"  {display.BitsPerPixel} bit";
                if (!string.IsNullOrWhiteSpace(line)) Line("   " + line.Trim());
            }

            Section("主板与系统");
            if (!string.IsNullOrWhiteSpace(snapshot.Machine.MachineText)) Line($"机型      {snapshot.Machine.MachineText}");
            if (!string.IsNullOrEmpty(snapshot.Machine.Board)) Line($"主板      {snapshot.Machine.Board}");
            if (!string.IsNullOrEmpty(snapshot.Machine.BiosVersion))
            {
                Line($"BIOS      {snapshot.Machine.BiosVersion}{(string.IsNullOrEmpty(snapshot.Machine.BiosDate) ? string.Empty : " / " + snapshot.Machine.BiosDate)}");
            }

            Line($"系统      {snapshot.Machine.OsName} {snapshot.Machine.OsVersion}（{snapshot.Machine.OsBuild}）".Replace("（）", string.Empty));
            if (!string.IsNullOrWhiteSpace(snapshot.Machine.ComputerName)) Line($"计算机名  {snapshot.Machine.ComputerName}");

            Section("网络");
            if (snapshot.Networks.Count == 0) Line("未能读取到网卡信息");
            foreach (var nic in snapshot.Networks)
            {
                Line($"{nic.Name}  [{nic.Type}]  {(nic.IsUp ? "已连接" : "未连接")}");
                if (!string.IsNullOrWhiteSpace(nic.Description)) Line($"   {nic.Description}");
                var extra = new List<string>();
                if (!string.IsNullOrEmpty(nic.MacAddress)) extra.Add("MAC " + nic.MacAddress);
                if (!string.IsNullOrEmpty(nic.SpeedText)) extra.Add("链路 " + nic.SpeedText);
                if (extra.Count > 0) Line("   " + string.Join("   ", extra));
            }

            Section("电源方案");
            if (plans.Count == 0) Line("未能读取到电源方案");
            foreach (var plan in plans)
            {
                Line($"{(plan.IsActive ? "→ " : "  ")}{plan.Name}{(plan.IsBuiltIn ? "（系统内置）" : string.Empty)}");
            }

            Line();
            Line("本报告完全在本机生成，游戏墙不会上传任何数据。");
            return sb.ToString();
        }

        public static string BuildJson(HardwareSnapshot snapshot, IReadOnlyList<PowerPlanInfo> plans)
        {
            var payload = new
            {
                generatedAt = snapshot.CapturedAt,
                app = new { name = AppInfo.ProductName, version = AppInfo.Version, singleFile = AppInfo.IsSingleFileBuild },
                cpu = snapshot.Cpu,
                gpus = snapshot.Gpus,
                memory = snapshot.Memory,
                disks = snapshot.Disks,
                displays = snapshot.Displays,
                machine = snapshot.Machine,
                networks = snapshot.Networks,
                powerPlans = plans.Select(p => new { guid = p.Guid, name = p.Name, active = p.IsActive, builtIn = p.IsBuiltIn }),
            };
            return JsonSerializer.Serialize(payload, JsonOptions);
        }

        public static string DefaultFileName(string extension) =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"GameWall-硬件报告-{DateTime.Now:yyyyMMdd-HHmmss}.{extension.TrimStart('.')}");

        public static TweakResult Save(string path, string content)
        {
            try
            {
                File.WriteAllText(path, content, new UTF8Encoding(true));
                return TweakResult.Ok("已导出：" + path);
            }
            catch (Exception ex)
            {
                Logger.Error("导出硬件报告失败", ex);
                return TweakResult.Fail("导出失败：" + ex.Message);
            }
        }
    }
}
