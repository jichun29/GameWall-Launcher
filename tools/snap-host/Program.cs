using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SnapHost
{
    /// <summary>
    /// 离线截图宿主。
    /// 当前会话的桌面合成不正常（PrintWindow 只能拿到黑屏、CopyFromScreen 拿到壁纸），
    /// 改用 RenderTargetBitmap 直接渲染窗口视觉树，拿到的是真实 WPF 像素。
    /// 用法：SnapHost.exe &lt;输出目录&gt; &lt;场景&gt; ...
    /// 场景格式：name:theme:view[:nav]  theme=Dark|Light  view=Card|List
    ///          name:settings / name:tidy / name:toolbox:Dark:navkey / name:about / name:firstrun
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var app = new GameWall.App();
            app.InitializeComponent();

            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAILED: " + ex);
                return 2;
            }
        }

        private static int Run(string[] args)
        {
            var outDir = args.Length > 0 ? args[0] : ".";
            Directory.CreateDirectory(outDir);

            for (var i = 1; i < args.Length; i++)
            {
                var parts = args[i].Split(':');
                var path = Path.Combine(outDir, parts[0] + ".png");
                try
                {
                    Capture(parts, path);
                    Console.WriteLine("saved " + path + " " + new FileInfo(path).Length);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("scene FAILED " + parts[0] + ": " + ex);
                }
            }

            return 0;
        }

        private static void Capture(string[] parts, string path)
        {
            var kind = parts.Length > 1 ? parts[1] : "main";

            if (kind == "settings" || kind == "tidy" || kind == "toolbox" || kind == "about" || kind == "log")
            {
                CaptureDialog(parts, path);
                return;
            }

            if (kind == "firstrun")
            {
                SwapTheme(parts.Length > 2 ? parts[2] : "Dark");
                var fr = new GameWall.Views.FirstRunWindow();
                Prepare(fr, 660, 560);
                Pump(fr.Dispatcher, 5);
                Save(fr, path);
                fr.Close();
                Pump(fr.Dispatcher, 1);
                return;
            }

            // 默认：主窗口。parts = name:main:theme:view
            var theme = parts.Length > 2 ? parts[2] : "Dark";
            var view = parts.Length > 3 ? parts[3] : "Card";

            var win = new GameWall.Views.MainWindow();
            Prepare(win, 1280, 820);
            Pump(win.Dispatcher, 4);

            var vm = win.ViewModel;
            vm.Settings.Theme = theme;
            SwapTheme(theme);
            vm.ViewMode = view == "List"
                ? GameWall.Models.WallViewMode.List
                : GameWall.Models.WallViewMode.Card;
            vm.ReloadCards();
            vm.NotifySettingsChanged();

            // name:main:Dark:Card:select —— 选中态截图用。
            // 用来看「选中描边和封面的圆角对不对得上」，纯目视截图里很难判断。
            if (parts.Length > 4 && parts[4] == "select" && vm.Cards.Count > 0)
            {
                vm.SelectedCard = vm.Cards[0];
            }

            Pump(win.Dispatcher, 6);

            Console.WriteLine("  view=" + vm.ViewMode + " cards=" + vm.Cards.Count + " theme=" + vm.Settings.Theme);
            Save(win, path);
            win.Close();
            Pump(win.Dispatcher, 1);
        }

        private static void CaptureDialog(string[] parts, string path)
        {
            var kind = parts[1];
            var theme = parts.Length > 2 ? parts[2] : "Dark";
            var nav = parts.Length > 3 ? parts[3] : "";

            var owner = new GameWall.Views.MainWindow();
            Prepare(owner, 1280, 820);
            Pump(owner.Dispatcher, 3);
            owner.ViewModel.Settings.Theme = theme;
            SwapTheme(theme);

            Window dialog;
            int width;
            int height;

            switch (kind)
            {
                case "settings":
                    var settingsWin = new GameWall.Views.Dialogs.SettingsWindow(owner.ViewModel.Settings);
                    width = 760; height = 800; dialog = settingsWin;
                    break;
                case "tidy":
                    var tidy = new GameWall.Views.Dialogs.TidyDesktopWindow(owner.ViewModel.LibraryGames);
                    width = 800; height = 720; dialog = tidy;
                    break;
                case "toolbox":
                    var toolbox = new GameWall.Views.Dialogs.GameToolboxWindow();
                    width = 1120; height = 780; dialog = toolbox;
                    if (!string.IsNullOrEmpty(nav))
                    {
                        toolbox.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
                        Thread.Sleep(200);
                        SelectNav(toolbox, nav);
                        Pump(toolbox.Dispatcher, 5);
                    }
                    break;
                case "log":
                    // 日志窗口读的是 Logger 的内存缓冲，宿主进程里是空的，
                    // 先灌几条有代表性的记录，截图才看得出排版和等级配色。
                    SeedLog();
                    var logWin = new GameWall.Views.Dialogs.LogWindow();
                    width = 980; height = 700; dialog = logWin;
                    break;
                default:
                    var about = new GameWall.Views.Dialogs.AboutWindow();
                    width = 620; height = 660; dialog = about;
                    break;
            }

            dialog.Owner = null;
            Prepare(dialog, width, height);
            Pump(dialog.Dispatcher, 6);
            if (kind == "toolbox")
            {
                var vm2 = dialog.GetType().GetField("_vm",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(dialog);
                Console.WriteLine("  page=" + vm2?.GetType().GetProperty("CurrentTitle")?.GetValue(vm2)
                    + " items=" + (vm2?.GetType().GetProperty("FilteredTweaks")?.GetValue(vm2) as System.Collections.ICollection)?.Count);
            }
            Save(dialog, path);
            dialog.Close();
            Pump(dialog.Dispatcher, 1);
            owner.Close();
            Pump(owner.Dispatcher, 1);
        }

        /// <summary>
        /// 用反射把工具箱切到指定分类。
        /// 走反射是为了不在产品代码里为截图开一个只在这条路径上用得到的口子。
        /// </summary>
        /// <summary>
        /// 换主题。
        /// 这里不能直接用 ThemeManager.Apply：它写的是 pack://application:,,,/Themes/X.xaml，
        /// 这种 URI 会在「入口程序集」里找资源 —— 截图宿主是 SnapHost.exe，
        /// 而主题字典编译在 GameWall.dll 里，必须显式带上程序集名才能找到。
        /// </summary>
        private static void SwapTheme(string theme)
        {
            var name = theme == "Light" ? "Light" : "Dark";
            var uri = new Uri("pack://application:,,,/GameWall;component/Themes/" + name + ".xaml", UriKind.Absolute);
            var dict = new ResourceDictionary { Source = uri };
            var app = Application.Current;
            for (var i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
            {
                var src = app.Resources.MergedDictionaries[i].Source?.OriginalString ?? string.Empty;
                if (src.Contains("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)
                    || src.Contains("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase))
                {
                    app.Resources.MergedDictionaries.RemoveAt(i);
                }
            }
            app.Resources.MergedDictionaries.Insert(0, dict);
        }

        private static void SelectNav(Window window, string key)
        {
            var vmField = window.GetType().GetField("_vm",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var vm = vmField?.GetValue(window);
            if (vm == null)
            {
                return;
            }

            var navItems = vm.GetType().GetProperty("NavItems")?.GetValue(vm) as System.Collections.IEnumerable;
            if (navItems == null)
            {
                return;
            }

            foreach (var item in navItems)
            {
                var itemKey = item.GetType().GetProperty("Key")?.GetValue(item) as string;
                if (string.Equals(itemKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    vm.GetType().GetProperty("SelectedNav")?.SetValue(vm, item);
                    return;
                }
            }
        }

        /// <summary>
        /// 给日志窗口截图灌一批样例记录。
        /// 只影响截图宿主进程的内存缓冲，不写文件、不影响真实日志。
        /// </summary>
        private static void SeedLog()
        {
            GameWall.Services.Logger.Info("主题已应用：Dark → Dark");
            GameWall.Services.Logger.Info("Steam 官方中文名库已载入 59 条（d:\\program files (x86)\\steam\\appcache\\appinfo.vdf）");
            GameWall.Services.Logger.Info("开始扫描：5 个根目录，最小体积 40 KB");
            GameWall.Services.Logger.Info("封面匹配（游戏目录自带图片）：赛博朋克2077 → cover.png");
            GameWall.Services.Logger.Info("扫描完成：新增 3 个游戏，跳过 14 个已存在的");
            GameWall.Services.Logger.Warn("Steam 缓存里没有 646910 的封面，改用占位图");
            GameWall.Services.Logger.Info("启动游戏：赛博朋克2077（Cyberpunk2077.exe）");
            GameWall.Services.Logger.Info("检查更新成功（GitHub 直连）：远端 v1.0.17 / 本地 v1.0.16 / hasUpdate=True");
            GameWall.Services.Logger.Error("提取图标失败：访问被拒绝", new UnauthorizedAccessException("对路径“E:\\WeGameApps\\英雄联盟（含经典模式）\\Game”的访问被拒绝。"));
            GameWall.Services.Logger.Info("已收纳 2 个桌面快捷方式 → desktop-backup\\20261023-171245");
        }

        private static void Prepare(Window win, int width, int height)
        {
            win.WindowStartupLocation = WindowStartupLocation.Manual;
            win.Left = -20000;
            win.Top = -20000;
            win.Width = width;
            win.Height = height;
            win.ShowInTaskbar = false;
            win.Show();
        }

        private static void Pump(Dispatcher dispatcher, int rounds)
        {
            for (var i = 0; i < rounds; i++)
            {
                dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
                dispatcher.Invoke(DispatcherPriority.Render, new Action(() => { }));
                Thread.Sleep(140);
            }
        }

        private static void Save(Window win, string path)
        {
            var width = (int)Math.Round(win.ActualWidth);
            var height = (int)Math.Round(win.ActualHeight);
            if (width <= 0 || height <= 0)
            {
                width = 1280;
                height = 820;
            }

            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(win);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
    }
}
