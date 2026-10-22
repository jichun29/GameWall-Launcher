using System.Collections.Generic;
using System.Linq;
using GameWall.Models;

namespace GameWall.Services
{
    /// <summary>
    /// 游戏优化方案目录。
    ///
    /// 值的来源：这些键值都是 Windows 官方文档里有记载、或被长期广泛使用的调整项，
    /// 参考了开源项目 NexBox 的 aq_registry 脚本集合（GPL-3.0，因此这里只借鉴「改哪个键、
    /// 改成什么值」这一事实性信息，全部代码为本项目自行实现）。
    ///
    /// 每条都带风险等级：
    ///   · 安全 —— 只影响观感或某些后台行为，随时能一键还原；
    ///   · 中等 —— 会关掉系统功能（搜索索引、系统还原点、休眠等），还原后恢复；
    ///   · 高风险 —— 改显卡驱动注册表 / 关安全组件，改完可能需要重启甚至重装驱动。
    /// 高风险项默认不勾选，界面上单独提示。
    /// </summary>
    public static class TweakCatalog
    {
        private static List<TweakDefinition>? _cached;

        /// <summary>全部分组顺序，界面按这个顺序渲染。</summary>
        public static readonly string[] Categories =
        {
            "游戏性能",
            "图形与显示",
            "输入延迟",
            "网络",
            "后台与系统",
            "隐私与广告",
        };

        public static IReadOnlyList<TweakDefinition> All => _cached ??= Build();

        public static TweakDefinition? Find(string id) =>
            All.FirstOrDefault(t => string.Equals(t.Id, id, System.StringComparison.OrdinalIgnoreCase));

        public static IEnumerable<TweakDefinition> InCategory(string category) =>
            All.Where(t => string.Equals(t.Category, category, System.StringComparison.OrdinalIgnoreCase));

        // ── 便于书写的短助手 ──────────────────────────────────

        private static TweakDefinition Tweak(
            string id, string name, string category, string summary, string effect,
            TweakRisk risk = TweakRisk.Low, bool admin = false, bool restart = false)
        {
            return new TweakDefinition
            {
                Id = id,
                Name = name,
                Category = category,
                Summary = summary,
                Effect = effect,
                Risk = risk,
                NeedsAdmin = admin,
                NeedsRestart = restart,
            };
        }

        private static RegOperation Hkcu(string subKey, string name, string value, RegValueKind kind = RegValueKind.Dword)
            => new() { Hive = RegHive.CurrentUser, SubKey = subKey, Name = name, Value = value, Kind = kind };

        private static RegOperation Hklm(string subKey, string name, string value, RegValueKind kind = RegValueKind.Dword)
            => new() { Hive = RegHive.LocalMachine, SubKey = subKey, Name = name, Value = value, Kind = kind };

        private static RegOperation HkUsers(string subKey, string name, string value, RegValueKind kind = RegValueKind.Dword)
            => new() { Hive = RegHive.Users, SubKey = subKey, Name = name, Value = value, Kind = kind };

        /// <summary>显卡驱动的实例子键（0000 / 0001 …），同一组值要写进每个适配器。</summary>
        private const string DisplayClassRoot =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        private const string MmSystemProfile =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

        private const string MmGamesTask = MmSystemProfile + @"\Tasks\Games";

        // ── 目录 ─────────────────────────────────────────────

        private static List<TweakDefinition> Build()
        {
            var list = new List<TweakDefinition>();

            // ═══ 游戏性能 ═══════════════════════════════════

            list.Add(Finish(Tweak(
                "game.mode", "Windows 游戏模式", "游戏性能",
                "打开系统自带的游戏模式开关。",
                "系统在检测到游戏运行时，会把 CPU / GPU 资源优先分给前台游戏，同时抑制后台更新与通知。"),
                Hkcu(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", "1")));

            list.Add(Finish(Tweak(
                "game.mmcss", "MMCSS 游戏任务优化", "游戏性能",
                "把多媒体调度器里 Games 任务的优先级拉满。",
                "游戏线程拿到更高的调度优先级与 GPU 优先级（8），音视频与输入路径更不容易被后台任务抢占。",
                admin: true, restart: true),
                Hklm(MmGamesTask, "Affinity", "0"),
                Hklm(MmGamesTask, "Background Only", "False", RegValueKind.String),
                Hklm(MmGamesTask, "Clock Rate", "10000"),
                Hklm(MmGamesTask, "GPU Priority", "8"),
                Hklm(MmGamesTask, "Priority", "6"),
                Hklm(MmGamesTask, "Scheduling Category", "High", RegValueKind.String),
                Hklm(MmGamesTask, "SFIO Priority", "High", RegValueKind.String),
                Hklm(MmGamesTask, "Latency Sensitive", "True", RegValueKind.String)));

            list.Add(Finish(Tweak(
                "game.mmcss.nolazy", "关闭 MMCSS 懒模式", "游戏性能",
                "让多媒体调度器常驻不休眠。",
                "避免游戏刚启动那几秒因为调度器还没醒来而出现掉帧或音频爆音。",
                admin: true, restart: true),
                Hklm(MmSystemProfile, "NoLazyMode", "1"),
                Hklm(MmSystemProfile, "AlwaysOn", "1")));

            list.Add(Finish(Tweak(
                "game.audio.priority", "音频任务高优先级", "游戏性能",
                "提升 Pro Audio 任务的调度等级。",
                "降低音频线程被抢占的概率，减少爆音与音画不同步。",
                admin: true, restart: true),
                Hklm(MmSystemProfile + @"\Tasks\Pro Audio", "Priority", "1"),
                Hklm(MmSystemProfile + @"\Tasks\Pro Audio", "Scheduling Category", "High", RegValueKind.String),
                Hklm(MmSystemProfile + @"\Tasks\Pro Audio", "SFIO Priority", "High", RegValueKind.String),
                Hklm(MmSystemProfile + @"\Tasks\Pro Audio", "GPU Priority", "8"),
                Hklm(MmSystemProfile + @"\Tasks\Pro Audio", "Clock Rate", "10000")));

            list.Add(Finish(Tweak(
                "game.responsiveness", "前台响应优先", "游戏性能",
                "把 SystemResponsiveness 从默认的 20 降到 10。",
                "留给后台任务的 CPU 时间从 20% 减到 10%，前台游戏能多拿到一成 CPU 时间。",
                admin: true, restart: true),
                Hklm(MmSystemProfile, "SystemResponsiveness", "10")));

            list.Add(Finish(Tweak(
                "game.priority.quantum", "前台时间片最长", "游戏性能",
                "Win32PrioritySeparation 设为 0x2A（短时间片 + 最高前台提升）。",
                "前台程序优先级提升最大、时间片更短，游戏响应更快；后台批量任务会更慢一点。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", "42")));

            list.Add(Finish(Tweak(
                "game.power.throttle", "关闭电源节流", "游戏性能",
                "禁用 PowerThrottling，并关掉休眠默认值。",
                "Windows 不会再把后台进程降频省电，游戏全程跑在满频；笔记本续航会变短。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", "1"),
                Hklm(@"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabledDefault", "0")));

            list.Add(Finish(Tweak(
                "game.sysmain", "关闭 SysMain（Superfetch）", "游戏性能",
                "停掉 SysMain 服务与预读。",
                "机械硬盘时代用来加速启动的服务，在 SSD 上收益很小，还会周期性占用磁盘；关掉后游戏加载更平稳。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Services\SysMain", "Start", "4"),
                Hklm(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnableSuperfetch", "0"),
                Hklm(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnablePrefetcher", "0")));

            list.Add(Finish(Tweak(
                "game.maintenance", "关闭自动维护", "游戏性能",
                "停掉 Windows 的定时维护计划。",
                "避免在你玩游戏时后台跑磁盘整理、系统诊断之类的任务造成偶发卡顿。",
                risk: TweakRisk.Medium, admin: true),
                Hklm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "MaintenanceDisabled", "1")));

            list.Add(Finish(Tweak(
                "game.largepages", "大页内存", "游戏性能",
                "声明 2 MB 大页的下限，让支持大页的程序能用上。",
                "部分模拟器与老游戏在开大页后读取更顺；对大多数现代游戏没有影响。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "LargePageMinimum", "2097152")));

            // ═══ 图形与显示 ═════════════════════════════════

            list.Add(Finish(Tweak(
                "gpu.hags", "硬件加速 GPU 调度（HAGS）", "图形与显示",
                "HwSchMode = 2，开启硬件 GPU 调度。",
                "由 GPU 自己管理显存与命令队列，支持的游戏里能降低 1~5 ms 输入延迟；个别老驱动可能有兼容问题。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"System\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", "2")));

            list.Add(Finish(Tweak(
                "gpu.dedicated", "强制独显优先", "图形与显示",
                "DirectX 全局设置里指定高性能 GPU。",
                "双显卡笔记本上避免游戏误跑到核显，省得手动一个个加白名单。",
                risk: TweakRisk.Low),
                Hkcu(@"SOFTWARE\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings",
                    "GpuPreference=2;", RegValueKind.String)));

            list.Add(Finish(Tweak(
                "gpu.flipmodel", "强制翻转模型（Flip Model）", "图形与显示",
                "让 DirectX 默认使用新的交换链模型。",
                "Flip Model 是 Win10 起更高效的呈现路径，输入延迟更低；极少数老游戏可能出现画面异常，还原即可。",
                risk: TweakRisk.Medium),
                Hkcu(@"SOFTWARE\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings",
                    "SwapEffectUpgradeEnable=1;", RegValueKind.String)));

            list.Add(Finish(Tweak(
                "gpu.autohdr", "自动 HDR", "图形与显示",
                "打开 DirectX 自动 HDR。",
                "支持 HDR 的显示器上，老游戏也能获得更亮的画面；SDR 显示器无影响。"),
                Hkcu(@"Software\Microsoft\DirectX\GraphicsSettings", "AutoHDREnable", "1")));

            list.Add(Finish(Tweak(
                "gpu.vrr", "可变刷新率优化（VRR）", "图形与显示",
                "让系统对支持 VRR 的显示器按可变刷新率渲染。",
                "配合 FreeSync / G-Sync 减少画面撕裂与卡顿感；需要显示器与线材都支持。"),
                Hkcu(@"Software\Microsoft\DirectX\GraphicsSettings", "VRROptimizeEnable", "1")));

            list.Add(Finish(Tweak(
                "gpu.shadercache", "着色器缓存", "图形与显示",
                "强制打开驱动层的着色器缓存（AMD UMD 键）。",
                "减少首次加载和场景切换时的编译卡顿，SSD 上收益最明显。A 卡驱动才有这个键，N 卡写了也无害。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(DisplayClassRoot + @"\0000\UMD", "ShaderCache", "50"),
                Hklm(DisplayClassRoot + @"\0001\UMD", "ShaderCache", "50")));

            list.Add(Finish(Tweak(
                "gpu.hdcp", "关闭 HDCP 握手", "图形与显示",
                "关掉显卡驱动的 HDCP 密钥校验。",
                "录制 / 直播时避免因为 HDCP 握手导致黑屏；不录屏的话不建议动。",
                risk: TweakRisk.High, admin: true, restart: true),
                Hklm(DisplayClassRoot + @"\0000", "RMHdcpKeyglobZero", "1"),
                Hklm(DisplayClassRoot + @"\0001", "RMHdcpKeyglobZero", "1")));

            list.Add(Finish(Tweak(
                "gpu.lowlatency", "驱动低延迟参数", "图形与显示",
                "把显卡驱动的若干延迟阈值调到最小值。",
                "理论上能缩短 GPU 的调度窗口，实际收益依驱动版本差别很大；改完建议先用几天再判断。",
                risk: TweakRisk.High, admin: true, restart: true),
                Hklm(DisplayClassRoot + @"\0000", "Node3DLowLatency", "1"),
                Hklm(DisplayClassRoot + @"\0000", "D3PCLatency", "1"),
                Hklm(DisplayClassRoot + @"\0000", "RmGspcMinFtuS", "1"),
                Hklm(DisplayClassRoot + @"\0000", "RmGspcMaxFtuS", "1")));

            list.Add(Finish(Tweak(
                "gpu.anticheat-mpo", "关闭多平面覆盖（MPO）", "图形与显示",
                "OverlayTestMode = 5，绕开 MPO 合成路径。",
                "能修掉一部分显卡驱动引起的闪屏 / 花屏；代价是窗口化游戏可能失去硬解叠加。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", "5")));

            // ═══ 输入延迟 ═══════════════════════════════════

            list.Add(Finish(Tweak(
                "input.mouse.accel", "关闭鼠标加速", "输入延迟",
                "MouseSpeed 与两个阈值全部归零。",
                "鼠标位移与准心 1:1 对应，FPS 玩家最在意的设置之一。"),
                Hkcu(@"Control Panel\Mouse", "MouseSpeed", "0", RegValueKind.String),
                Hkcu(@"Control Panel\Mouse", "MouseThreshold1", "0", RegValueKind.String),
                Hkcu(@"Control Panel\Mouse", "MouseThreshold2", "0", RegValueKind.String)));

            list.Add(Finish(Tweak(
                "input.keyboard.repeat", "键盘重复最快", "输入延迟",
                "KeyboardDelay = 0、KeyboardSpeed = 31。",
                "按住方向键时字符 / 移动更跟手，连点操作更顺。",
                restart: false),
                Hkcu(@"Control Panel\Keyboard", "KeyboardDelay", "0", RegValueKind.String),
                Hkcu(@"Control Panel\Keyboard", "KeyboardSpeed", "31", RegValueKind.String),
                HkUsers(@".DEFAULT\Control Panel\Keyboard", "KeyboardDelay", "0", RegValueKind.String),
                HkUsers(@".DEFAULT\Control Panel\Keyboard", "KeyboardSpeed", "31", RegValueKind.String)));

            list.Add(Finish(Tweak(
                "input.menu.delay", "菜单零延迟", "输入延迟",
                "MenuShowDelay 从 400 ms 改成 0。",
                "右键菜单、开始菜单立刻弹出，不再有那一小段等待。"),
                Hkcu(@"Control Panel\Desktop", "MenuShowDelay", "0", RegValueKind.String)));

            list.Add(Finish(Tweak(
                "input.hover.time", "悬停提示更快", "输入延迟",
                "MouseHoverTime 改成 10 ms。",
                "鼠标停在按钮上的提示几乎瞬间出现。"),
                Hkcu(@"Control Panel\Mouse", "MouseHoverTime", "10", RegValueKind.String)));

            list.Add(Finish(Tweak(
                "input.snap.default", "关闭自动对齐默认按钮", "输入延迟",
                "鼠标不会自己跳到对话框的默认按钮上。",
                "减少误点，尤其是玩窗口化游戏时鼠标飘出边界的情况。"),
                Hkcu(@"Control Panel\Mouse", "SnapToDefaultButton", "0", RegValueKind.String)));

            list.Add(Finish(Tweak(
                "input.thumb.delay", "文件缩略图即时预览", "输入延迟",
                "ExtendedUIHoverTime 设为 1 ms。",
                "资源管理器里悬停文件立刻就出预览，找游戏目录时更快。"),
                Hkcu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ExtendedUIHoverTime", "1")));

            // ═══ 网络 ═══════════════════════════════════════

            list.Add(Finish(Tweak(
                "net.throttling", "关闭网络节流", "网络",
                "NetworkThrottlingIndex = 0xFFFFFFFF，取消多媒体程序的网络限速。",
                "Windows 默认会给多媒体程序限速到约 10 包/毫秒，联机游戏在高峰期会因此丢包；关掉后更稳。",
                admin: true),
                Hklm(MmSystemProfile, "NetworkThrottlingIndex", "4294967295")));

            list.Add(Finish(Tweak(
                "net.ctcp", "启用 CTCP 拥塞控制", "网络",
                "把默认拥塞控制算法替换成 CTCP。",
                "在高延迟链路上吞吐更平滑，对下载和联机都有帮助；个别路由器可能不适应。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "CongestionProvider", "ctcp", RegValueKind.String)));

            list.Add(Finish(Tweak(
                "net.ttl", "统一 TTL 为 64", "网络",
                "DefaultTTL 设为 64。",
                "部分联机游戏对 TTL 不一致敏感，统一之后跨运营商连接更稳。",
                risk: TweakRisk.Low, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "DefaultTTL", "64")));

            list.Add(Finish(Tweak(
                "net.ecn", "关闭 ECN", "网络",
                "ECNCapability = 0。",
                "显式拥塞通知在老路由器上容易导致连接被重置，关掉可以少一些莫名的掉线。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "ECNCapability", "0")));

            list.Add(Finish(Tweak(
                "net.timedwait", "缩短 TIME_WAIT", "网络",
                "TcpTimedWaitDelay 从 240 s 缩到 30 s。",
                "端口回收更快，频繁重连的场景（换图、重进房间）不容易卡在「正在连接」。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TcpTimedWaitDelay", "30")));

            list.Add(Finish(Tweak(
                "net.userport", "扩大动态端口范围", "网络",
                "MaxUserPort 提到 65534。",
                "同时开的连接更多，避免联机 + 下载 + 语音时的端口耗尽。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "MaxUserPort", "65534")));

            list.Add(Finish(Tweak(
                "net.afd", "优化 AFD 收发缓冲", "网络",
                "DefaultReceiveWindow / DefaultSendWindow 设为 64 KB。",
                "降低高带宽下的拷贝开销，游戏内延迟抖动更小。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Services\AFD\Parameters", "DefaultReceiveWindow", "65535"),
                Hklm(@"SYSTEM\CurrentControlSet\Services\AFD\Parameters", "DefaultSendWindow", "65535")));

            list.Add(Finish(Tweak(
                "net.dns.cache", "DNS 缓存优化", "网络",
                "延长正向缓存、缩短负向缓存。",
                "少一点反复解析，进游戏、连服务器更快一点。",
                admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "MaxCacheTtl", "900"),
                Hklm(@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "MaxNegativeCacheTtl", "30"),
                Hklm(@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "NegativeSOACacheTime", "0")));

            list.Add(Finish(Tweak(
                "net.qos", "取消 QoS 预留带宽", "网络",
                "NonBestEffortLimit = 0，释放那 20% 的预留。",
                "Windows 默认给 QoS 预留 20% 带宽，关掉之后下载与联机都能用满。",
                admin: true),
                Hklm(@"SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit", "0")));

            // ═══ 后台与系统 ═════════════════════════════════

            list.Add(Finish(Tweak(
                "sys.gamedvr", "关闭 Game DVR 录制", "后台与系统",
                "关掉后台录制与 FSE 相关开关。",
                "后台录制会持续吃掉 3~10% 的 GPU，关掉后帧数更稳；需要录精彩瞬间的话别开这条。"),
                Hkcu(@"System\GameConfigStore", "GameDVR_Enabled", "0"),
                Hkcu(@"System\GameConfigStore", "GameDVR_FSEBehaviorMode", "2"),
                Hkcu(@"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", "0"),
                Hkcu(@"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", "0"),
                Hkcu(@"System\GameConfigStore", "GameDVR_EFSEFeatureFlags", "0"),
                Hkcu(@"System\GameConfigStore", "GameDVR_FSEBehavior", "2"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", "0"),
                Hklm(@"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", "0")));

            list.Add(Finish(Tweak(
                "sys.gamebar", "关闭 Xbox Game Bar 启动面板", "后台与系统",
                "不再在游戏启动时弹出 Xbox 工具栏。",
                "少一个浮层，也不会因为误触 Win+G 把游戏切到窗口化。"),
                Hkcu(@"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", "0"),
                Hkcu(@"Software\Microsoft\GameBar", "ShowStartupPanel", "0")));

            list.Add(Finish(Tweak(
                "sys.xbox.services", "停用 Xbox 相关服务", "后台与系统",
                "把 6 个 Xbox / GamingServices 服务设为禁用。",
                "不玩 Xbox 商店游戏的话这些服务纯占内存；需要注意，禁用后 Microsoft Store 里的游戏可能无法启动。",
                risk: TweakRisk.High, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Services\XboxGipSvc", "Start", "4"),
                Hklm(@"SYSTEM\CurrentControlSet\Services\XblAuthManager", "Start", "4"),
                Hklm(@"SYSTEM\CurrentControlSet\Services\XblGameSave", "Start", "4"),
                Hklm(@"SYSTEM\CurrentControlSet\Services\XboxNetApiSvc", "Start", "4"),
                Hklm(@"SYSTEM\CurrentControlSet\Services\GamingServices", "Start", "4"),
                Hklm(@"SYSTEM\CurrentControlSet\Services\GamingServicesNet", "Start", "4")));

            list.Add(Finish(Tweak(
                "sys.xbgm", "停用 Xbox 游戏监控服务", "后台与系统",
                "xbgm 服务设为禁用。",
                "这个服务只服务于 Xbox 主机的串流场景，PC 上关掉不影响任何游戏。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Services\xbgm", "Start", "4")));

            list.Add(Finish(Tweak(
                "sys.search", "停用 Windows 搜索索引", "后台与系统",
                "WSearch 服务设为禁用。",
                "索引服务在后台持续读盘，关掉后游戏加载时的磁盘争用明显减少；代价是开始菜单搜文件变慢。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Services\WSearch", "Start", "4")));

            list.Add(Finish(Tweak(
                "sys.lastaccess", "关闭文件最后访问时间", "后台与系统",
                "NtfsDisableLastAccessUpdate = 0x80000003。",
                "每次读文件都要更新一次时间戳，关掉后磁盘写入更少。",
                risk: TweakRisk.Low, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", "-2147483645")));

            list.Add(Finish(Tweak(
                "sys.mftzone", "扩大 MFT 预留区", "后台与系统",
                "NtfsMftZoneReservation = 2。",
                "给主文件表留更多连续空间，大量小文件（游戏本体）读写更顺；只对新分区生效。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsMftZoneReservation", "2")));

            list.Add(Finish(Tweak(
                "sys.dwm.anim", "精简窗口动画", "后台与系统",
                "关掉 Aero Peek 与缩略图常驻。",
                "窗口切换更干脆，低配机器上体感明显。"),
                Hkcu(@"SOFTWARE\Microsoft\Windows\DWM", "EnableAeroPeek", "0"),
                Hkcu(@"SOFTWARE\Microsoft\Windows\DWM", "AlwaysHibernateThumbnails", "0")));

            list.Add(Finish(Tweak(
                "sys.shake", "关闭窗口抖动最小化", "后台与系统",
                "拉窗口时不再把其它窗口最小化。",
                "避免玩窗口化游戏时手一滑把整排窗口收起来。"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "DisallowShaking", "1")));

            list.Add(Finish(Tweak(
                "sys.audio.duck", "关闭音量自动闪避", "后台与系统",
                "UserDuckingPreference = 3，通讯时不再自动压低其它声音。",
                "开着语音软件打游戏时，游戏音效不会被反复压低。"),
                Hkcu(@"SOFTWARE\Microsoft\Multimedia\Audio", "UserDuckingPreference", "3")));

            list.Add(Finish(Tweak(
                "sys.audio.spatial", "关闭空间音效", "后台与系统",
                "关掉系统级空间音频。",
                "少一层后处理，音频链路更短；用耳机虚拟环绕的玩家别开。"),
                Hkcu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio", "EnableSpatialAudio", "0")));

            list.Add(Finish(Tweak(
                "sys.audio.service", "音频服务即时启动", "后台与系统",
                "取消 Audiosrv 的延迟启动。",
                "开机后音频服务立刻可用，不会再出现「进游戏才发现没声音」。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"SYSTEM\CurrentControlSet\Services\Audiosrv", "DelayedAutoStart", "0")));

            list.Add(Finish(Tweak(
                "sys.background.apps", "禁止后台 UWP 常驻", "后台与系统",
                "关掉后台应用与搜索后台开关。",
                "商店应用不再在后台偷跑，内存与 CPU 占用更低。"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", "1"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", "0")));

            // ═══ 隐私与广告 ═════════════════════════════════

            list.Add(Finish(Tweak(
                "privacy.ads", "关闭系统推广与建议", "隐私与广告",
                "一次性关掉内容推送、锁屏推荐、开始菜单建议等 20 项。",
                "开始菜单与锁屏不再出现「推荐应用」，也少了一批后台联网请求。"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "ContentDeliveryAllowed", "0"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContentEnabled", "0"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "FeatureManagementEnabled", "0"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SoftLandingEnabled", "0"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "OemPreInstalledAppsEnabled", "0"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "PreInstalledAppsEnabled", "0"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", "0"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "RotatingLockScreenEnabled", "0"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "RotatingLockScreenOverlayEnabled", "0"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", "0"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", "0"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-353694Enabled", "0"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-353696Enabled", "0"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-353698Enabled", "0"),
                Hkcu(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", "0")));

            list.Add(Finish(Tweak(
                "privacy.activity", "关闭活动历史", "隐私与广告",
                "EnableActivityFeed = 0。",
                "系统不再记录你用过的应用与打开过的文件。",
                admin: true),
                Hklm(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", "0")));

            list.Add(Finish(Tweak(
                "privacy.copilot", "关闭 Copilot", "隐私与广告",
                "关掉 Windows Copilot 与 Copilot 键。",
                "少一个常驻进程，也避免误按 Copilot 键把游戏切出去。",
                admin: true),
                Hkcu(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", "1"),
                Hklm(@"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", "1"),
                Hkcu(@"Software\Microsoft\Windows\Shell\Copilot", "IsCopilotAvailable", "0")));

            list.Add(Finish(Tweak(
                "privacy.recall", "关闭 Recall 快照", "隐私与广告",
                "TurnOffSavingSnapshots = 1。",
                "系统不再定期截屏保存你的屏幕内容。",
                admin: true),
                Hklm(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "TurnOffSavingSnapshots", "1")));

            list.Add(Finish(Tweak(
                "privacy.clicktodo", "关闭 Click to Do", "隐私与广告",
                "DisableClickToDo = 1。",
                "关掉截图上的 AI 操作浮层。",
                admin: true),
                Hklm(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableClickToDo", "1")));

            list.Add(Finish(Tweak(
                "privacy.news", "关闭资讯与兴趣", "隐私与广告",
                "关掉任务栏的新闻推送。",
                "任务栏更干净，也少一批后台联网。",
                admin: true),
                Hklm(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", "0"),
                Hklm(@"SOFTWARE\Policies\Microsoft\Windows\Windows Feeds", "EnableFeeds", "0")));

            list.Add(Finish(Tweak(
                "privacy.nvidia", "关闭 NVIDIA 遥测", "隐私与广告",
                "关掉 N 卡控制面板的遥测与数据收集服务。",
                "少一个常驻容器进程，开机与游戏内的后台 IO 更少。",
                risk: TweakRisk.Medium, admin: true, restart: true),
                Hklm(@"SOFTWARE\NVIDIA Corporation\NvControlPanel2\Client", "OptInOrOutPreference", "0"),
                Hklm(@"SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID66610", "0"),
                Hklm(@"SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID64640", "0"),
                Hklm(@"SOFTWARE\NVIDIA Corporation\Global\FTS", "EnableRID44231", "0"),
                Hklm(@"SYSTEM\CurrentControlSet\Services\NvTelemetryContainer", "Start", "4")));

            return list;
        }

        /// <summary>把逗号分隔的操作列表挂到定义上（语法糖，让上面的目录读起来像配置表）。</summary>
        private static TweakDefinition Finish(TweakDefinition definition, params RegOperation[] operations)
        {
            definition.Operations.AddRange(operations);
            if (operations.Any(o => o.Hive != RegHive.CurrentUser)) definition.NeedsAdmin = true;
            return definition;
        }
    }
}
