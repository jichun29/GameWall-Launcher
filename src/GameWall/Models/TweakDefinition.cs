using System;
using System.Collections.Generic;

namespace GameWall.Models
{
    /// <summary>一条注册表操作的宿主根。</summary>
    public enum RegHive
    {
        CurrentUser = 0,
        LocalMachine = 1,
        Users = 2,
    }

    /// <summary>值类型，只支持优化项实际会用到的那几种。</summary>
    public enum RegValueKind
    {
        Dword = 0,
        String = 1,
        Qword = 2,
        Binary = 3,
    }

    /// <summary>一条注册表写入操作。</summary>
    public sealed class RegOperation
    {
        public RegHive Hive { get; set; }
        public string SubKey { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public RegValueKind Kind { get; set; } = RegValueKind.Dword;

        /// <summary>字符串形式的目标值：Dword/Qword 用十进制，String 用原文，Binary 用逗号分隔的十六进制。</summary>
        public string Value { get; set; } = string.Empty;

        public object ToRegistryValue()
        {
            return Kind switch
            {
                RegValueKind.Dword => unchecked((int)long.Parse(Value)),
                RegValueKind.Qword => long.Parse(Value),
                RegValueKind.Binary => ParseHex(Value),
                _ => Value,
            };
        }

        private static byte[] ParseHex(string text)
        {
            var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var bytes = new byte[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                bytes[i] = Convert.ToByte(parts[i], 16);
            }
            return bytes;
        }

        /// <summary>给界面看的「路径\值名」。</summary>
        public string LocationText =>
            $"{(Hive == RegHive.CurrentUser ? "HKCU" : Hive == RegHive.LocalMachine ? "HKLM" : "HKU")}\\{SubKey}\\{Name}";
    }

    /// <summary>风险等级。界面按这个上色，用户在勾选前能一眼看出哪些是「改坏了要重装」的。</summary>
    public enum TweakRisk
    {
        Low = 0,
        Medium = 1,
        High = 2,
    }

    /// <summary>一条优化方案。</summary>
    public sealed class TweakDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        /// <summary>一句话说明这条优化做了什么。</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>分组：游戏性能 / 图形与显示 / 输入延迟 / 网络 / 后台与系统。</summary>
        public string Category { get; set; } = "游戏性能";

        public TweakRisk Risk { get; set; } = TweakRisk.Low;

        /// <summary>是否需要管理员权限才能写入。</summary>
        public bool NeedsAdmin { get; set; }

        /// <summary>生效是否需要重启（或注销 / 重启游戏）。</summary>
        public bool NeedsRestart { get; set; }

        /// <summary>预期效果，写具体一点，别写「提升性能」这种空话。</summary>
        public string Effect { get; set; } = string.Empty;

        public List<RegOperation> Operations { get; set; } = new();

        public string RiskText => Risk switch
        {
            TweakRisk.High => "高风险",
            TweakRisk.Medium => "中等",
            _ => "安全",
        };
    }

    /// <summary>电源方案。</summary>
    public sealed class PowerPlanInfo
    {
        public Guid Guid { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsActive { get; set; }

        /// <summary>是不是 Windows 内置的那三套（平衡 / 节能 / 高性能）。</summary>
        public bool IsBuiltIn { get; set; }

        public string DisplayText => IsActive ? $"{Name}（当前）" : Name;

        /// <summary>
        /// 一句话说明这套方案是干什么的，界面上当副标题用。
        /// 按名字认，是因为系统里的方案名可能被用户改过，认不出来就退成通用说明。
        /// </summary>
        public string Description
        {
            get
            {
                if (Name.Contains("卓越", StringComparison.Ordinal)) return "隐藏的内置方案，延迟最低、功耗最高";
                if (Name.Contains("高性能", StringComparison.Ordinal)) return "不主动降频，玩游戏最稳";
                if (Name.Contains("平衡", StringComparison.Ordinal)) return "系统默认，性能与功耗折中";
                if (Name.Contains("节能", StringComparison.Ordinal)) return "尽量省电，性能会打折扣";
                return IsBuiltIn ? "Windows 内置方案" : "自定义方案";
            }
        }
    }

    /// <summary>应用/还原一条优化项的结果。</summary>
    public sealed class TweakResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public List<string> Failures { get; set; } = new();

        public static TweakResult Ok(string message) => new() { Success = true, Message = message };

        public static TweakResult Fail(string message) => new() { Success = false, Message = message };
    }
}
