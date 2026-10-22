using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameWall.Models;

namespace GameWall.Services;

/// <summary>
/// 读出 / 写回 library.json 的仓储层。
/// </summary>
public sealed class LibraryService
{
    private List<GameEntry> _games = new();

    public IReadOnlyList<GameEntry> Games => _games;

    /// <summary>加载游戏库；文件不存在时返回空列表（首次运行）。</summary>
    public void Load()
    {
        AppPaths.EnsureCreated();
        var data = JsonStore.Load<List<GameEntry>>(AppPaths.LibraryFile) ?? new List<GameEntry>();
        _games = data.Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Name)).ToList();

        // 保证 SortIndex 连续，老数据没有这个字段时按添加时间补全
        var missing = _games.Where(g => g.SortIndex <= 0).OrderBy(g => g.AddedAt).ToList();
        var next = _games.Count == 0 ? 0 : _games.Max(g => g.SortIndex);
        foreach (var g in missing)
        {
            g.SortIndex = ++next;
        }

        Normalize();

        // 首次运行（还没有 library.json）时写一个空数组，
        // 让数据目录结构一眼可见，也方便用户手工编辑/备份。
        if (!File.Exists(AppPaths.LibraryFile))
        {
            Save();
        }
    }

    public void Save()
    {
        Normalize();
        JsonStore.Save(AppPaths.LibraryFile, _games);
    }

    private void Normalize()
    {
        var ordered = _games.OrderBy(g => g.SortIndex).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].SortIndex = i + 1;
        }

        _games = ordered;
    }

    /// <summary>是否已经存在同一可执行文件（避免重复添加）。</summary>
    public GameEntry? FindByExecutable(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        return _games.FirstOrDefault(g =>
            string.Equals(g.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase));
    }

    public GameEntry Add(GameEntry entry)
    {
        entry.SortIndex = _games.Count == 0 ? 1 : _games.Max(g => g.SortIndex) + 1;
        _games.Add(entry);
        Save();
        return entry;
    }

    public bool Remove(GameEntry entry)
    {
        var removed = _games.Remove(entry);
        if (removed)
        {
            // 顺手删掉本项目生成的封面副本，避免 covers 目录越攒越多
            TryDeleteCover(entry);
            Save();
        }

        return removed;
    }

    private static void TryDeleteCover(GameEntry entry)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(entry.CoverPath)
                && entry.CoverPath.StartsWith(AppPaths.Covers, StringComparison.OrdinalIgnoreCase)
                && File.Exists(entry.CoverPath))
            {
                File.Delete(entry.CoverPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("删除封面副本失败", ex);
        }
    }

    /// <summary>拖拽排序：把 source 移动到 target 前面（或末尾）。</summary>
    public void Move(GameEntry source, GameEntry? target, bool after)
    {
        var ordered = _games.OrderBy(g => g.SortIndex).ToList();
        if (!ordered.Contains(source))
        {
            return;
        }

        ordered.Remove(source);
        if (target is null)
        {
            ordered.Add(source);
        }
        else
        {
            var idx = ordered.IndexOf(target);
            if (idx < 0)
            {
                ordered.Add(source);
            }
            else
            {
                ordered.Insert(after ? idx + 1 : idx, source);
            }
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].SortIndex = i + 1;
        }

        _games = ordered;
    }

    /// <summary>所有出现过的标签（用于筛选下拉）。</summary>
    public List<string> AllTags() => _games
        .SelectMany(g => g.Tags)
        .Where(t => !string.IsNullOrWhiteSpace(t))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(t => t, StringComparer.CurrentCulture)
        .ToList();
}
