using System.Windows;
using System.Windows.Input;

namespace GameWall.Views;

/// <summary>
/// 首次运行引导。只是说明页，点了按钮就关闭。
/// </summary>
public partial class FirstRunWindow : Window
{
    public FirstRunWindow()
    {
        InitializeComponent();
    }

    /// <summary>用户点了「开始添加游戏」，主窗口据此自动弹出选择文件对话框。</summary>
    public bool ShouldAddGame { get; private set; }

    /// <summary>窗口无边框，按住空白处可以拖动。</summary>
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点在按钮上的不接管，否则按钮点不下去
        if (e.OriginalSource is DependencyObject src && IsInteractive(src))
        {
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>判断这个元素（或它的祖先）是不是可交互控件。</summary>
    private static bool IsInteractive(DependencyObject source)
    {
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Button
                or System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.TextBox)
            {
                return true;
            }
        }

        return false;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        ShouldAddGame = true;
        Finish(true);
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Finish(false);
    }

    /// <summary>
    /// 关闭引导窗口。
    /// DialogResult 只有在窗口是 ShowDialog() 打开时才允许设置，
    /// 非模态（或自动化测试直接 Invoke 按钮）时设置会抛 InvalidOperationException，
    /// 所以这里先容错再兜底 Close()。
    /// </summary>
    private void Finish(bool addGame)
    {
        try
        {
            DialogResult = addGame;
        }
        catch (InvalidOperationException)
        {
            // 非模态打开：不需要返回值，直接关掉即可
        }

        Close();
    }
}
