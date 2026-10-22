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
