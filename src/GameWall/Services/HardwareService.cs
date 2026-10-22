using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using GameWall.Models;

namespace GameWall.Services
{
    /// <summary>
    /// 硬件信息采集。
    /// 全部数据来自注册表与 Win32 API —— 不引入 System.Management / LibreHardwareMonitor
    /// 这类第三方依赖，也不起辅助进程，单文件发布不会因此变大。
    /// </summary>
    public static class HardwareService
    {
        // ── Win32 ────────────────────────────────────────────

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

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        // CharSet 必须是 Unicode：DISPLAY_DEVICE 里是 WCHAR 数组，
        // 少写这一句 ByValTStr 会按 ANSI 排布，结构体只有 424 字节（正确是 840），
        // 传给 EnumDisplayDevicesW 后 cb 不匹配，枚举直接停在第一个、分辨率也读不出来。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        // DEVMODE 有两种重载：DEVMODEA（156 字节）与 DEVMODEW（220 字节）。
        // 下面的 DllImport 写的是 CharSet.Unicode，实际调的是 EnumDisplaySettingsW，
        // 所以这里必须也是 Unicode 版本 —— 之前写 Ansi 时结构体大小对不上，
        // 函数返回 True 却把所有字段填成 0，分辨率和刷新率全读不到。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        private const int ENUM_CURRENT_SETTINGS = -1;
        private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x01;
        private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x04;

        // ── 对外入口 ─────────────────────────────────────────

        /// <summary>采集一次完整快照。任何一段失败都不影响其余部分。</summary>
        public static HardwareSnapshot Capture()
        {
            var snapshot = new HardwareSnapshot();
            try { snapshot.Cpu = ReadCpu(); } catch (Exception ex) { Logger.Error("读取 CPU 信息失败", ex); }
            try { snapshot.Gpus = ReadGpus(); } catch (Exception ex) { Logger.Error("读取显卡信息失败", ex); }
            try { snapshot.Memory = ReadMemory(); } catch (Exception ex) { Logger.Error("读取内存信息失败", ex); }
            try { snapshot.Disks = ReadDisks(); } catch (Exception ex) { Logger.Error("读取磁盘信息失败", ex); }
            try { snapshot.Displays = ReadDisplays(); } catch (Exception ex) { Logger.Error("读取显示器信息失败", ex); }
            try { snapshot.Machine = ReadMachine(); } catch (Exception ex) { Logger.Error("读取机型信息失败", ex); }
            try { snapshot.Networks = ReadNetworks(); } catch (Exception ex) { Logger.Error("读取网卡信息失败", ex); }
            return snapshot;
        }

        // ── CPU ──────────────────────────────────────────────

        private static CpuInfo ReadCpu()
        {
            var info = new CpuInfo
            {
                LogicalProcessors = Environment.ProcessorCount,
                PhysicalCores = ReadPhysicalCoreCount(),
            };

            using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
            {
                if (key != null)
                {
                    info.Name = (key.GetValue("ProcessorNameString") as string)?.Trim() ?? info.Name;
                    if (info.BaseClockMhz <= 0 && key.GetValue("~MHz") is int mhz) info.BaseClockMhz = mhz;
                    info.Architecture = DescribeArchitecture(key.GetValue("Identifier") as string);
                }
            }

            if (info.PhysicalCores <= 0) info.PhysicalCores = info.LogicalProcessors;
            return info;
        }

        private static string DescribeArchitecture(string? identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier)) return RuntimeInformation.OSArchitecture.ToString();
            if (identifier.Contains("ARM", StringComparison.OrdinalIgnoreCase)) return "ARM64";
            return RuntimeInformation.OSArchitecture == Architecture.X64 ? "x64" : RuntimeInformation.OSArchitecture.ToString();
        }

        /// <summary>
        /// 物理核心数。Environment.ProcessorCount 在开了超线程的机器上会翻倍，
        /// 这里按「每个物理处理器记录里 CoreCount 之和」算，读不到就退回逻辑数。
        /// </summary>
        private static int ReadPhysicalCoreCount()
        {
            // 单路机器通常只在 \0；多路则分散在 \0 \1 …，逐个累加
            var cores = 0;
            for (var i = 0; i < 64; i++)
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"HARDWARE\DESCRIPTION\System\CentralProcessor\{i}");
                if (key == null) break;
                if (key.GetValue("NumberOfCores") is int count && count > 0) cores += count;
            }

            return cores > 0 ? cores : Environment.ProcessorCount;
        }

        // ── 显卡 ─────────────────────────────────────────────

        private static List<GpuInfo> ReadGpus()
        {
            var result = new List<GpuInfo>();
            const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

            try
            {
                using var classKey = Registry.LocalMachine.OpenSubKey(displayClass);
                if (classKey == null) return result;

                foreach (var subName in classKey.GetSubKeyNames())
                {
                    // 只有 0000 / 0001 … 这样的实例子键才是真实适配器
                    if (subName.Length != 4 || !subName.All(char.IsDigit)) continue;

                    using var sub = classKey.OpenSubKey(subName);
                    if (sub == null) continue;

                    var name = (sub.GetValue("DriverDesc") as string)?.Trim();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var gpu = new GpuInfo
                    {
                        Name = name!,
                        DriverVersion = (sub.GetValue("DriverVersion") as string)?.Trim() ?? string.Empty,
                        DriverDate = FormatDriverDate(sub.GetValue("DriverDate") as string),
                        Vendor = (sub.GetValue("ProviderName") as string)?.Trim() ?? string.Empty,
                        IsVirtual = IsVirtualAdapter(name!),
                    };

                    gpu.MemoryBytes = ReadGpuMemory(sub);

                    // 远程桌面 / 串流 / 虚拟显示器驱动也会注册成显示适配器，
                    // 它们没有真实显存，列进「显卡」只会让人以为装了两块卡，直接不收。
                    if (gpu.IsVirtual && gpu.MemoryBytes <= 0) continue;

                    // 同一块卡在多个实例键里重复登记的情况，按名字去重
                    if (result.Any(g => string.Equals(g.Name, gpu.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    result.Add(gpu);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("枚举显卡失败", ex);
            }

            // 真实显卡排前面，虚拟适配器垫底
            return result.OrderBy(g => g.IsVirtual).ThenByDescending(g => g.MemoryBytes).ToList();
        }

        /// <summary>
        /// 显存在注册表里有三种存法，逐个试：
        /// 1. 同层的 HardwareInformation.qwMemorySize（REG_QWORD 或 8 字节 REG_BINARY）——新版 NVIDIA 驱动在这；
        /// 2. HardwareInformation 子键下的 qwMemorySize；
        /// 3. 同层 MemorySize（单位 MB，旧驱动）。
        /// </summary>
        private static long ReadGpuMemory(RegistryKey sub)
        {
            var direct = ReadQword(sub.GetValue("HardwareInformation.qwMemorySize"));
            if (direct > 0) return direct;

            using (var hardware = sub.OpenSubKey("HardwareInformation"))
            {
                var nested = ReadQword(hardware?.GetValue("qwMemorySize"));
                if (nested > 0) return nested;
            }

            if (sub.GetValue("MemorySize") is int mb && mb > 0) return (long)mb * 1024 * 1024;
            if (sub.GetValue("HardwareInformation.MemorySize") is byte[] mem && mem.Length >= 4)
            {
                var value = BitConverter.ToUInt32(mem, 0);
                // 这个值有时是负数补码（如 -1048576），算下来是天文数字，直接丢掉
                if (value > 0 && value < int.MaxValue) return value;
            }

            return 0;
        }

        private static long ReadQword(object? raw) => raw switch
        {
            long l => l > 0 ? l : 0,
            int i => i > 0 ? i : 0,
            byte[] bytes when bytes.Length == 8 => BitConverter.ToInt64(bytes, 0) is var v && v > 0 ? v : 0,
            _ => 0,
        };

        /// <summary>远程桌面、串流、虚拟显示器一类的适配器。名字里带这些词基本可以确定不是真显卡。</summary>
        private static bool IsVirtualAdapter(string name)
        {
            string[] marks =
            {
                "remote display", "remote desktop", "idddriver", "indirect display", "virtual display",
                "basic display", "basic render", "parsec", "oray", "sunlogin", "gameviewer",
                "todesk", "rustdesk", "anydesk", "teamviewer", "mirror driver", "displaylink",
            };
            return marks.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
        }

        private static string FormatDriverDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            // 注册表里通常是 2025-03-18 这样的字符串
            return DateTime.TryParse(raw, out var dt) ? dt.ToString("yyyy-MM-dd") : raw!;
        }

        // ── 内存 ─────────────────────────────────────────────

        private static MemoryInfo ReadMemory()
        {
            var info = new MemoryInfo();
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                info.TotalBytes = (long)status.ullTotalPhys;
                info.AvailableBytes = (long)status.ullAvailPhys;
                info.PageFileTotalBytes = (long)status.ullTotalPageFile;
                info.PageFileAvailableBytes = (long)status.ullAvailPageFile;
                info.LoadPercent = (int)status.dwMemoryLoad;
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                _ = key; // BIOS 里没有内存频率，留个扩展点

                // 内存条频率/条数在 SMBIOS 里，注册表看不到；
                // 这里读硬件资源映射里的物理内存区段数当「条数」的近似不可靠，干脆不猜。
                info.SpeedText = string.Empty;
            }
            catch
            {
                // 读不到就不显示，不影响主流程
            }

            return info;
        }

        // ── 磁盘 ─────────────────────────────────────────────

        private static List<DiskInfo> ReadDisks()
        {
            var list = new List<DiskInfo>();
            foreach (var drive in System.IO.DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    // 光驱、内存盘对游戏帮不上忙，跳过
                    if (drive.DriveType != System.IO.DriveType.Fixed && drive.DriveType != System.IO.DriveType.Removable)
                    {
                        continue;
                    }

                    list.Add(new DiskInfo
                    {
                        Drive = drive.Name.TrimEnd('\\'),
                        Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? string.Empty : drive.VolumeLabel,
                        FileSystem = drive.DriveFormat,
                        DriveType = DescribeDriveType(drive.DriveType),
                        TotalBytes = drive.TotalSize,
                        FreeBytes = drive.AvailableFreeSpace,
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error("读取磁盘信息失败：" + drive.Name, ex);
                }
            }

            return list.OrderBy(d => d.Drive).ToList();
        }

        private static string DescribeDriveType(System.IO.DriveType type) => type switch
        {
            System.IO.DriveType.Fixed => "本地磁盘",
            System.IO.DriveType.Removable => "可移动磁盘",
            System.IO.DriveType.Network => "网络驱动器",
            _ => type.ToString(),
        };

        // ── 显示器 ───────────────────────────────────────────

        private static List<DisplayInfo> ReadDisplays()
        {
            var list = new List<DisplayInfo>();
            for (uint i = 0; i < 8; i++)
            {
                var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevices(null, i, ref device, 0)) break;
                if ((device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;

                // 这一层的 DeviceString 是「适配器」（显卡）名，显示器名要再往下枚举一层才拿得到。
                var adapterName = device.DeviceString?.Trim() ?? string.Empty;

                var info = new DisplayInfo
                {
                    Adapter = adapterName,
                    IsPrimary = (device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                };

                var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                // EnumDisplayDevices 理论上总会填 DeviceName，但精简过的系统上见过为空，
                // 传 null 进去 EnumDisplaySettings 会直接抛 AccessViolation，这里挡一下。
                if (!string.IsNullOrEmpty(device.DeviceName) &&
                    EnumDisplaySettings(device.DeviceName, ENUM_CURRENT_SETTINGS, ref mode))
                {
                    info.Width = mode.dmPelsWidth;
                    info.Height = mode.dmPelsHeight;
                    info.RefreshRate = mode.dmDisplayFrequency;
                    info.BitsPerPixel = mode.dmBitsPerPel;
                }

                // 分辨率读不出来的条目（驱动没就绪、远程会话断开）整条丢掉：
                // 界面上留着「未知」既占地方又让人以为哪里坏了。
                if (!info.HasResolution) continue;

                info.Name = ReadMonitorName(device.DeviceName);
                if (string.IsNullOrWhiteSpace(info.Name))
                {
                    info.Name = info.IsPrimary ? "主显示器" : $"显示器 {list.Count + 1}";
                }

                list.Add(info);
            }

            return list.OrderByDescending(d => d.IsPrimary).ToList();
        }

        /// <summary>从「适配器 → 显示器」的第二层枚举里取显示器型号，比如「Generic PnP Monitor」。</summary>
        private static string ReadMonitorName(string? adapterDeviceName)
        {
            if (string.IsNullOrEmpty(adapterDeviceName)) return string.Empty;
            try
            {
                var monitor = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (EnumDisplayDevices(adapterDeviceName, 0, ref monitor, 0))
                {
                    return monitor.DeviceString?.Trim() ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("读取显示器型号失败", ex);
            }

            return string.Empty;
        }

        // ── 机型 / 系统 ──────────────────────────────────────

        private static MachineInfo ReadMachine()
        {
            var info = new MachineInfo { ComputerName = Environment.MachineName };

            using (var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
            {
                if (bios != null)
                {
                    info.Manufacturer = (bios.GetValue("SystemManufacturer") as string)?.Trim() ?? string.Empty;
                    info.Model = (bios.GetValue("SystemProductName") as string)?.Trim() ?? string.Empty;
                    info.Board = ((bios.GetValue("BaseBoardProduct") as string) ?? string.Empty).Trim();
                    info.BiosVersion = ((bios.GetValue("BIOSVersion") as string) ?? string.Empty).Trim();
                    var rawDate = (bios.GetValue("BIOSReleaseDate") as string)?.Trim();
                    if (!string.IsNullOrWhiteSpace(rawDate) && rawDate!.Length >= 8)
                    {
                        // 形如 03/18/2025
                        info.BiosDate = DateTime.TryParse(rawDate, out var dt) ? dt.ToString("yyyy-MM-dd") : rawDate;
                    }
                }
            }

            using (var os = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (os != null)
                {
                    info.OsName = (os.GetValue("ProductName") as string)?.Trim() ?? "Windows";
                    info.OsVersion = (os.GetValue("DisplayVersion") as string)?.Trim() ?? string.Empty;
                    var build = (os.GetValue("CurrentBuildNumber") as string)?.Trim() ?? string.Empty;
                    if (os.GetValue("UBR") is int ubr && ubr > 0) build = $"{build}.{ubr}";
                    info.OsBuild = build;

                    // 注册表里的 ProductName 到 Windows 11 依然是「Windows 10 …」，
                    // 微软为了兼容性一直没改。构建号 >= 22000 就说明其实是 11，这里纠正一下。
                    if (int.TryParse(build.Split('.')[0], out var buildNo) && buildNo >= 22000)
                    {
                        info.OsName = info.OsName.Replace("Windows 10", "Windows 11");
                    }
                }
            }

            return info;
        }

        // ── 网络 ─────────────────────────────────────────────

        private static List<NetworkInfo> ReadNetworks()
        {
            var list = new List<NetworkInfo>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    list.Add(new NetworkInfo
                    {
                        Name = nic.Name,
                        Description = nic.Description,
                        Type = DescribeNetworkType(nic.NetworkInterfaceType),
                        MacAddress = FormatMac(nic.GetPhysicalAddress()?.ToString()),
                        SpeedBps = SafeSpeed(nic),
                        IsUp = nic.OperationalStatus == OperationalStatus.Up,
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error("读取网卡失败", ex);
                }
            }

            return list.OrderByDescending(n => n.IsUp).ThenBy(n => n.Name).ToList();
        }

        private static long SafeSpeed(NetworkInterface nic)
        {
            try { return nic.Speed; } catch { return 0; }
        }

        private static string DescribeNetworkType(NetworkInterfaceType type) => type switch
        {
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "有线",
            NetworkInterfaceType.Wireless80211 => "无线",
            NetworkInterfaceType.Ppp => "拨号",
            _ => type.ToString(),
        };

        private static string FormatMac(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw!.Length != 12) return raw ?? string.Empty;
            var sb = new StringBuilder(17);
            for (var i = 0; i < 12; i += 2)
            {
                if (sb.Length > 0) sb.Append('-');
                sb.Append(raw[i]).Append(raw[i + 1]);
            }
            return sb.ToString();
        }

        // ── 建议推导 ─────────────────────────────────────────

        /// <summary>
        /// 按当前硬件给出「这台机器上值得做」的几条建议。
        /// 纯本地推导，不会自动改任何设置。
        /// </summary>
        public static List<HardwareAdvice> BuildAdvice(HardwareSnapshot snapshot, AppSettings settings)
        {
            var advices = new List<HardwareAdvice>();

            // 内存
            var totalGb = snapshot.Memory.TotalBytes / 1024d / 1024 / 1024;
            if (totalGb > 0 && totalGb < 16)
            {
                advices.Add(new HardwareAdvice
                {
                    Title = $"内存只有 {totalGb:0.#} GB",
                    Detail = "现在的大型游戏普遍建议 16 GB 起步。开启「关闭系统预留内存」与「加大系统缓存」能略微缓解，但根本办法还是加内存条。",
                    TweakId = "memory.reserved",
                    Positive = false,
                });
            }
            else if (totalGb >= 16)
            {
                advices.Add(new HardwareAdvice
                {
                    Title = $"内存 {totalGb:0.#} GB，足够应付 3A 大作",
                    Detail = "可以放心开启「游戏进程高优先级」与「MMCSS 游戏任务优化」，让前台游戏优先吃到 CPU 时间片。",
                    TweakId = "mmcss.games",
                    Positive = true,
                });
            }

            // 磁盘：系统盘剩余
            var systemDrive = snapshot.Disks.FirstOrDefault(d =>
                string.Equals(d.Drive, System.IO.Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase));
            if (systemDrive != null && systemDrive.TotalBytes > 0)
            {
                var freePercent = systemDrive.FreeBytes * 100.0 / systemDrive.TotalBytes;
                if (freePercent < 12)
                {
                    advices.Add(new HardwareAdvice
                    {
                        Title = $"系统盘只剩 {freePercent:0}% 空间",
                        Detail = "磁盘快满时页文件和着色器缓存都写不开，游戏会明显卡顿。建议先清一遍临时文件，或把游戏装到别的盘。",
                        Positive = false,
                    });
                }
                else
                {
                    advices.Add(new HardwareAdvice
                    {
                        Title = $"系统盘还有 {freePercent:0}% 空间，状态良好",
                        Detail = "保持了足够的余量，页文件与着色器缓存都有地方写。",
                        Positive = true,
                    });
                }
            }

            // 显卡
            var primaryGpu = snapshot.Gpus.FirstOrDefault();
            if (primaryGpu != null)
            {
                var isNvidia = primaryGpu.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
                var isAmd = primaryGpu.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                            || primaryGpu.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
                if (isNvidia)
                {
                    advices.Add(new HardwareAdvice
                    {
                        Title = "检测到 NVIDIA 显卡",
                        Detail = "可以开启「硬件加速 GPU 调度（HAGS）」，N 卡在支持的游戏里能降低输入延迟；如果驱动版本较老，建议先更新驱动。",
                        TweakId = "gpu.hags",
                        Positive = true,
                    });
                }
                else if (isAmd)
                {
                    advices.Add(new HardwareAdvice
                    {
                        Title = "检测到 AMD 显卡",
                        Detail = "建议开启「着色器缓存」，能明显减少首次加载与场景切换时的编译卡顿。",
                        TweakId = "gpu.shaderCache",
                        Positive = true,
                    });
                }

                if (snapshot.Gpus.Count > 1)
                {
                    advices.Add(new HardwareAdvice
                    {
                        Title = $"检测到 {snapshot.Gpus.Count} 块显卡",
                        Detail = "笔记本双显卡机型建议把「强制独显优先级」设为全局，避免游戏跑在核显上。",
                        TweakId = "gpu.dedicated",
                        Positive = true,
                    });
                }
            }

            // 显示器
            var primaryDisplay = snapshot.Displays.FirstOrDefault();
            if (primaryDisplay != null && primaryDisplay.RefreshRate > 0 && primaryDisplay.RefreshRate < 100)
            {
                advices.Add(new HardwareAdvice
                {
                    Title = $"主屏刷新率 {primaryDisplay.RefreshRate} Hz",
                    Detail = "如果是高刷屏，记得在「显示设置 → 高级显示」里把刷新率调到最高，这一步比任何注册表优化都有效。",
                    Positive = false,
                });
            }

            // CPU：大小核
            if (snapshot.Cpu.LogicalProcessors >= 16)
            {
                advices.Add(new HardwareAdvice
                {
                    Title = $"{snapshot.Cpu.TopologyText}，核心充足",
                    Detail = "可以试试「CPU 核心调度」把游戏绑到大核上，后台程序留给小核。",
                    Positive = true,
                });
            }

            // 电源计划
            if (!settings.HardwareToolboxSnoozed)
            {
                advices.Add(new HardwareAdvice
                {
                    Title = "电源计划建议用「高性能」或更高",
                    Detail = "平衡模式会在负载波动时来回降频，游戏里表现为帧数不稳。工具箱的「电源与性能」页可以一键切换。",
                    Positive = true,
                });
            }

            return advices;
        }
    }
}
