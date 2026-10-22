using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using GameWall.Models;

namespace GameWall.Services
{
    /// <summary>
    /// 电源方案管理，直接走 powrprof.dll，不调 powercfg.exe
    /// —— 少一次进程启动，也不会因为路径里带空格出问题。
    ///
    /// 只做三件事：列出方案、切换方案、必要时把「卓越性能」复制出来。
    /// 不写任何自定义 .pow（那需要二进制模板，且切换效果与系统内置方案差别不大）。
    /// </summary>
    public static class PowerPlanService
    {
        private const uint AccessScheme = 16;

        // Windows 内置方案 GUID
        private static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
        private static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        private static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
        private static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerEnumerate(
            IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupGuid,
            uint accessFlags, uint index, byte[] buffer, ref uint bufferSize);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerReadFriendlyName(
            IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid, IntPtr powerSettingGuid,
            byte[] buffer, ref uint bufferSize);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerDuplicateScheme(
            IntPtr rootPowerKey, ref Guid sourceSchemeGuid, out IntPtr destinationSchemeGuid);

        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerWriteFriendlyName(
            IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid, IntPtr powerSettingGuid,
            byte[] buffer, uint bufferSize);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);

        // ── 查询 ─────────────────────────────────────────────

        /// <summary>当前生效的电源方案 GUID。</summary>
        public static Guid? GetActiveGuid()
        {
            try
            {
                if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
                try
                {
                    return Marshal.PtrToStructure<Guid>(ptr);
                }
                finally
                {
                    LocalFree(ptr);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("读取当前电源方案失败", ex);
                return null;
            }
        }

        /// <summary>列出全部电源方案，当前生效的排在最前。</summary>
        public static List<PowerPlanInfo> ListPlans()
        {
            var plans = new List<PowerPlanInfo>();
            var active = GetActiveGuid();

            try
            {
                for (uint index = 0; ; index++)
                {
                    var buffer = new byte[16];
                    var size = (uint)buffer.Length;
                    var code = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, buffer, ref size);
                    if (code == 259 /* ERROR_NO_MORE_ITEMS */) break;
                    if (code != 0) break;

                    var guid = new Guid(buffer);
                    var name = ReadFriendlyName(guid);
                    if (string.IsNullOrWhiteSpace(name)) name = guid.ToString();

                    plans.Add(new PowerPlanInfo
                    {
                        Guid = guid,
                        Name = name,
                        IsActive = active.HasValue && active.Value == guid,
                        IsBuiltIn = guid == Balanced || guid == HighPerformance || guid == PowerSaver || guid == Ultimate,
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error("枚举电源方案失败", ex);
            }

            plans.Sort((a, b) =>
            {
                if (a.IsActive != b.IsActive) return a.IsActive ? -1 : 1;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            });
            return plans;
        }

        private static string ReadFriendlyName(Guid scheme)
        {
            var buffer = new byte[1024];
            var size = (uint)buffer.Length;
            var guid = scheme;
            if (PowerReadFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
            {
                return string.Empty;
            }
            return Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, buffer.Length)).TrimEnd('\0');
        }

        // ── 切换 ─────────────────────────────────────────────

        /// <summary>切换到指定电源方案。需要管理员权限（写 HKLM 下的电源数据库）。</summary>
        public static TweakResult SetActive(Guid scheme)
        {
            try
            {
                var guid = scheme;
                var code = PowerSetActiveScheme(IntPtr.Zero, ref guid);
                if (code == 0)
                {
                    Logger.Info($"电源方案已切换：{scheme}");
                    return TweakResult.Ok("电源方案已切换，部分机器的风扇策略会在几秒后跟上。");
                }

                return TweakResult.Fail(code == 5
                    ? "切换电源方案需要管理员权限，请以管理员身份重新运行 GameWall。"
                    : $"切换失败，powrprof 返回错误码 {code}。");
            }
            catch (Exception ex)
            {
                Logger.Error("切换电源方案失败", ex);
                return TweakResult.Fail("切换电源方案时出现异常：" + ex.Message);
            }
        }

        /// <summary>切到「高性能」。</summary>
        public static TweakResult UseHighPerformance()
        {
            var plans = ListPlans();
            foreach (var plan in plans)
            {
                if (plan.Guid == HighPerformance) return SetActive(plan.Guid);
            }
            // 某些精简系统把高性能方案删了，就复制一份平衡方案改名为高性能
            return DuplicateAndActivate(Balanced, "高性能");
        }

        /// <summary>
        /// 启用「卓越性能」。Win10 1803 起内置但默认不显示，
        /// 这里先找，找不到就复制高性能方案再改名。
        /// </summary>
        public static TweakResult UseUltimatePerformance()
        {
            foreach (var plan in ListPlans())
            {
                if (plan.Guid == Ultimate || plan.Name.Contains("卓越", StringComparison.Ordinal))
                {
                    return SetActive(plan.Guid);
                }
            }

            var duplicated = DuplicateAndActivate(HighPerformance, "卓越性能");
            return duplicated;
        }

        private static TweakResult DuplicateAndActivate(Guid source, string newName)
        {
            try
            {
                var src = source;
                var code = PowerDuplicateScheme(IntPtr.Zero, ref src, out var destPtr);
                if (code != 0 || destPtr == IntPtr.Zero)
                {
                    return TweakResult.Fail($"复制电源方案失败，错误码 {code}。");
                }

                Guid dest;
                try
                {
                    dest = Marshal.PtrToStructure<Guid>(destPtr);
                }
                finally
                {
                    LocalFree(destPtr);
                }

                var nameBytes = Encoding.Unicode.GetBytes(newName + "\0");
                var guid = dest;
                PowerWriteFriendlyName(IntPtr.Zero, ref guid, IntPtr.Zero, IntPtr.Zero, nameBytes, (uint)nameBytes.Length);

                return SetActive(dest);
            }
            catch (Exception ex)
            {
                Logger.Error("创建电源方案失败", ex);
                return TweakResult.Fail("创建电源方案时出现异常：" + ex.Message);
            }
        }
    }
}
