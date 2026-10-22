using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using GameWall.Models;

namespace GameWall.Services
{
    /// <summary>
    /// 优化项的应用与还原。
    ///
    /// 设计原则：
    /// 1. **改之前先记**：每个 (根, 子键, 值名) 的旧值都写进
    ///    %AppData%\GameWall\tweaks\applied.json，还原时逐条写回；
    ///    旧值本来不存在就记成「不存在」，还原时把它删掉而不是写个 0。
    /// 2. **一条失败不影响其它条**：批量应用时逐条收集失败原因，最后一起汇报。
    /// 3. **不动系统还原点**：不碰 SystemRestore，也不调任何外部 exe。
    /// </summary>
    public sealed class TweakService
    {
        private static readonly JsonSerializerOptions BackupJson = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        private readonly string _backupFile;
        private readonly List<AppliedTweakRecord> _records;

        public TweakService()
        {
            var dir = Path.Combine(AppPaths.Root, "tweaks");
            Directory.CreateDirectory(dir);
            _backupFile = Path.Combine(dir, "applied.json");
            _records = LoadRecords();
        }

        /// <summary>当前进程是否以管理员身份运行。</summary>
        public static bool IsElevated()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                Logger.Error("检测管理员权限失败", ex);
                return false;
            }
        }

        /// <summary>已应用的优化项 Id（按备份记录算，比我方 settings 里的更可靠）。</summary>
        public IReadOnlyList<string> AppliedIds =>
            _records.Select(r => r.TweakId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public bool IsApplied(string tweakId) =>
            _records.Any(r => string.Equals(r.TweakId, tweakId, StringComparison.OrdinalIgnoreCase));

        // ── 应用 ─────────────────────────────────────────────

        /// <summary>应用一条优化项。已应用过则直接返回成功。</summary>
        public TweakResult Apply(TweakDefinition tweak)
        {
            if (tweak.Operations.Count == 0)
            {
                return TweakResult.Fail("这条优化没有定义任何注册表操作。");
            }

            if (IsApplied(tweak.Id))
            {
                return TweakResult.Ok($"{tweak.Name}：之前已经应用过了。");
            }

            if (tweak.NeedsAdmin && !IsElevated())
            {
                return TweakResult.Fail($"{tweak.Name} 需要管理员权限。请右键 GameWall →「以管理员身份运行」后重试。");
            }

            var record = new AppliedTweakRecord
            {
                TweakId = tweak.Id,
                Name = tweak.Name,
                AppliedAt = DateTime.Now,
            };

            var failures = new List<string>();
            foreach (var operation in tweak.Operations)
            {
                try
                {
                    var snapshot = ReadCurrent(operation);
                    if (!WriteValue(operation))
                    {
                        failures.Add(operation.LocationText);
                        continue;
                    }

                    // 写成功之后再记账，避免「记了却没写成」导致还原时误删
                    record.Entries.Add(snapshot);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
                {
                    failures.Add($"{operation.LocationText}（权限不足）");
                }
                catch (Exception ex)
                {
                    failures.Add($"{operation.LocationText}（{ex.Message}）");
                    Logger.Error($"应用优化项 {tweak.Id} 失败：{operation.LocationText}", ex);
                }
            }

            if (record.Entries.Count > 0)
            {
                _records.Add(record);
                SaveRecords();
            }

            if (failures.Count == 0)
            {
                var hint = tweak.NeedsRestart ? " 重启后生效。" : string.Empty;
                return TweakResult.Ok($"{tweak.Name}：已应用。{hint}");
            }

            var result = new TweakResult
            {
                Success = record.Entries.Count > 0,
                Message = record.Entries.Count > 0
                    ? $"{tweak.Name}：部分应用成功，{failures.Count} 项失败。"
                    : $"{tweak.Name}：应用失败。",
            };
            result.Failures.AddRange(failures);
            return result;
        }

        /// <summary>批量应用，跳过高风险项除非显式允许。</summary>
        public TweakResult ApplyMany(IEnumerable<TweakDefinition> tweaks, bool includeHighRisk)
        {
            var list = tweaks.ToList();
            if (list.Count == 0) return TweakResult.Fail("没有勾选任何优化项。");

            var blocked = list.Where(t => t.Risk == TweakRisk.High && !includeHighRisk).ToList();
            if (blocked.Count > 0)
            {
                return TweakResult.Fail(
                    $"以下项目属于高风险，已跳过：{string.Join("、", blocked.Select(t => t.Name))}。" +
                    "要应用它们请在界面上单独确认。");
            }

            var failures = new List<string>();
            var applied = 0;
            var restartNeeded = false;

            foreach (var tweak in list)
            {
                var result = Apply(tweak);
                if (result.Success) applied++;
                failures.AddRange(result.Failures);
                if (result.Success && tweak.NeedsRestart) restartNeeded = true;
                if (!result.Success && result.Failures.Count == 0) failures.Add(result.Message);
            }

            var message = $"已应用 {applied} / {list.Count} 项优化。";
            if (restartNeeded) message += " 其中有项目需要重启电脑后才生效。";
            if (failures.Count > 0) message += $" {failures.Count} 项未成功。";

            var final = new TweakResult { Success = applied > 0, Message = message };
            final.Failures.AddRange(failures.Take(12));
            return final;
        }

        // ── 还原 ─────────────────────────────────────────────

        /// <summary>还原一条优化项：把备份里的旧值逐条写回去。</summary>
        public TweakResult Revert(string tweakId)
        {
            var records = _records
                .Where(r => string.Equals(r.TweakId, tweakId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (records.Count == 0)
            {
                return TweakResult.Fail("这条优化没有找到可还原的记录。");
            }

            var name = records[0].Name;
            var failures = new List<string>();
            var restored = 0;

            foreach (var entry in records.SelectMany(r => r.Entries))
            {
                try
                {
                    if (RestoreEntry(entry)) restored++;
                    else failures.Add(entry.LocationText);
                }
                catch (Exception ex)
                {
                    failures.Add($"{entry.LocationText}（{ex.Message}）");
                    Logger.Error($"还原优化项 {tweakId} 失败：{entry.LocationText}", ex);
                }
            }

            if (failures.Count == 0)
            {
                _records.RemoveAll(r => string.Equals(r.TweakId, tweakId, StringComparison.OrdinalIgnoreCase));
                SaveRecords();
                return TweakResult.Ok($"{name}：已还原（{restored} 项）。");
            }

            var result = new TweakResult
            {
                Success = failures.Count == 0,
                Message = $"{name}：还原了 {restored} 项，{failures.Count} 项失败。",
            };
            result.Failures.AddRange(failures);
            return result;
        }

        /// <summary>还原全部已应用的优化项。</summary>
        public TweakResult RevertAll()
        {
            var ids = AppliedIds.ToList();
            if (ids.Count == 0) return TweakResult.Ok("当前没有应用过任何优化项。");

            var failures = new List<string>();
            var ok = 0;
            foreach (var id in ids)
            {
                var result = Revert(id);
                if (result.Success) ok++;
                else failures.AddRange(result.Failures);
            }

            var message = $"已还原 {ok} / {ids.Count} 组优化。";
            if (failures.Count > 0) message += $" {failures.Count} 项未能还原，详情见日志。";

            var final = new TweakResult { Success = ok > 0, Message = message };
            final.Failures.AddRange(failures.Take(12));
            return final;
        }

        // ── 状态查询 ─────────────────────────────────────────

        /// <summary>
        /// 探测一条优化项当前是否「已经是目标状态」。
        /// 用于在界面上显示「已开启」而不必依赖备份记录（用户可能在别处改过）。
        /// </summary>
        public static bool IsCurrentlyOn(TweakDefinition tweak)
        {
            if (tweak.Operations.Count == 0) return false;
            try
            {
                foreach (var operation in tweak.Operations)
                {
                    var current = ReadCurrentValue(operation);
                    var target = operation.ToRegistryValue();
                    if (!ValuesEqual(current, target)) return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"探测优化项 {tweak.Id} 状态失败", ex);
                return false;
            }
        }

        private static bool ValuesEqual(object? current, object target)
        {
            if (current is null) return false;
            if (current is byte[] cb && target is byte[] tb) return cb.SequenceEqual(tb);
            return string.Equals(
                Convert.ToString(current, System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToString(target, System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.OrdinalIgnoreCase);
        }

        // ── 注册表读写 ───────────────────────────────────────

        private static RegistryKey OpenBase(RegHive hive, bool writable)
        {
            return hive switch
            {
                RegHive.CurrentUser => Registry.CurrentUser.OpenSubKey(string.Empty, writable)!,
                RegHive.LocalMachine => Registry.LocalMachine.OpenSubKey(string.Empty, writable)!,
                _ => Registry.Users.OpenSubKey(string.Empty, writable)!,
            };
        }

        private static RegistryValueKind ToRegistryKind(RegValueKind kind) => kind switch
        {
            RegValueKind.Dword => RegistryValueKind.DWord,
            RegValueKind.Qword => RegistryValueKind.QWord,
            RegValueKind.Binary => RegistryValueKind.Binary,
            _ => RegistryValueKind.String,
        };

        /// <summary>读取某个值；返回 null 表示该值不存在。</summary>
        private static object? ReadCurrentValue(RegOperation operation)
        {
            using var baseKey = OpenBase(operation.Hive, false);
            using var key = baseKey.OpenSubKey(operation.SubKey, false);
            return key?.GetValue(operation.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        private static AppliedValue ReadCurrent(RegOperation operation)
        {
            var current = ReadCurrentValue(operation);
            var entry = new AppliedValue
            {
                Hive = operation.Hive,
                SubKey = operation.SubKey,
                Name = operation.Name,
                Existed = current != null,
            };

            switch (current)
            {
                case int dword:
                    entry.Kind = RegValueKind.Dword;
                    entry.Value = dword.ToString();
                    break;
                case long qword:
                    entry.Kind = RegValueKind.Qword;
                    entry.Value = qword.ToString();
                    break;
                case byte[] bytes:
                    entry.Kind = RegValueKind.Binary;
                    entry.Value = string.Join(",", bytes.Select(b => b.ToString("X2")));
                    break;
                case string text:
                    entry.Kind = RegValueKind.String;
                    entry.Value = text;
                    break;
                case null:
                    entry.Kind = operation.Kind;
                    entry.Value = string.Empty;
                    break;
                default:
                    entry.Kind = RegValueKind.String;
                    entry.Value = Convert.ToString(current, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    break;
            }

            return entry;
        }

        private static bool WriteValue(RegOperation operation)
        {
            using var baseKey = OpenBase(operation.Hive, true);
            using var key = baseKey.CreateSubKey(operation.SubKey, writable: true);
            if (key == null) return false;
            key.SetValue(operation.Name, operation.ToRegistryValue(), ToRegistryKind(operation.Kind));
            return true;
        }

        private static bool RestoreEntry(AppliedValue entry)
        {
            using var baseKey = OpenBase(entry.Hive, true);
            using var key = baseKey.OpenSubKey(entry.SubKey, writable: true) ?? baseKey.CreateSubKey(entry.SubKey, writable: true);
            if (key == null) return false;

            if (!entry.Existed)
            {
                // 原来没有这个值 → 删掉它，而不是写 0（写 0 会改变系统默认行为）
                key.DeleteValue(entry.Name, throwOnMissingValue: false);
                return true;
            }

            var operation = new RegOperation
            {
                Hive = entry.Hive,
                SubKey = entry.SubKey,
                Name = entry.Name,
                Kind = entry.Kind,
                Value = entry.Value,
            };
            key.SetValue(operation.Name, operation.ToRegistryValue(), ToRegistryKind(operation.Kind));
            return true;
        }

        // ── 备份文件 ─────────────────────────────────────────

        private List<AppliedTweakRecord> LoadRecords()
        {
            try
            {
                if (!File.Exists(_backupFile)) return new List<AppliedTweakRecord>();
                var json = File.ReadAllText(_backupFile);
                return JsonSerializer.Deserialize<List<AppliedTweakRecord>>(json, BackupJson)
                       ?? new List<AppliedTweakRecord>();
            }
            catch (Exception ex)
            {
                Logger.Error("读取优化备份失败", ex);
                return new List<AppliedTweakRecord>();
            }
        }

        private void SaveRecords()
        {
            try
            {
                var json = JsonSerializer.Serialize(_records, BackupJson);
                var tmp = _backupFile + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _backupFile, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Error("写入优化备份失败", ex);
            }
        }
    }

    /// <summary>一次「应用」的记录，用于还原。</summary>
    public sealed class AppliedTweakRecord
    {
        public string TweakId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime AppliedAt { get; set; }
        public List<AppliedValue> Entries { get; set; } = new();
    }

    /// <summary>单个注册表值的旧状态。</summary>
    public sealed class AppliedValue
    {
        public RegHive Hive { get; set; }
        public string SubKey { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public RegValueKind Kind { get; set; }

        /// <summary>旧值是否本来就存在。false 表示还原时要删掉这个值。</summary>
        public bool Existed { get; set; }

        /// <summary>旧值的字符串形式。</summary>
        public string Value { get; set; } = string.Empty;

        public string LocationText =>
            $"{(Hive == RegHive.CurrentUser ? "HKCU" : Hive == RegHive.LocalMachine ? "HKLM" : "HKU")}\\{SubKey}\\{Name}";
    }
}
