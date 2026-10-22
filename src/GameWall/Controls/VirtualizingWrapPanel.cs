using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace GameWall.Controls;

/// <summary>
/// 支持虚拟化的自动换行面板。
///
/// WPF 自带的 WrapPanel 不虚拟化：库里有 200 个游戏就会实例化 200 张卡片，
/// 每张卡要建十几个控件、解码一张封面，实测 42 张就要一秒钟以上。
/// 这个面板只生成「视口里能看到的」那几行，其余等滚动到再生成，
/// 于是首屏开销只跟窗口大小有关，跟游戏数量脱钩。
///
/// 尺寸来源：卡片宽度由 XAML 绑到 Settings.CardWidth（用户可调），
/// 所以不能写死。第一次测量时先生成一张卡片量出真实尺寸，
/// 用它算每行几张；尺寸和上次不一样就再量一遍，最多三轮防抖动。
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    /// <summary>探尺寸时用的兜底宽高（量到真值后立即被覆盖）。</summary>
    private const double FallbackItemWidth = 224;
    private const double FallbackItemHeight = 300;

    /// <summary>
    /// 列表模式用：每行一张卡片、宽度撑满视口。
    /// 打开后不再按卡片自身宽度切列，行高仍然从卡片实测值校准。
    /// </summary>
    public static readonly DependencyProperty FullWidthProperty =
        DependencyProperty.Register(
            nameof(FullWidth),
            typeof(bool),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public bool FullWidth
    {
        get => (bool)GetValue(FullWidthProperty);
        set => SetValue(FullWidthProperty, value);
    }

    /// <summary>
    /// 卡片内容宽度。给了就按「卡片宽度 + 左右外边距」算格子宽度，
    /// 不再靠内容反推。模板里的宽度来自绑定（Settings.CardWidth），
    /// 虚拟化面板第一次测量时绑定还没解析完，反推会得到偏小的值，
    /// 所以这里让面板直接拿到真实宽度。
    /// </summary>
    public static readonly DependencyProperty CardWidthProperty =
        DependencyProperty.Register(
            nameof(CardWidth),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    /// <summary>卡片外边距（四周同值）。格子宽度 = CardWidth + CardMargin * 2。</summary>
    public static readonly DependencyProperty CardMarginProperty =
        DependencyProperty.Register(
            nameof(CardMargin),
            typeof(double),
            typeof(VirtualizingWrapPanel),
            new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double CardMargin
    {
        get => (double)GetValue(CardMarginProperty);
        set => SetValue(CardMarginProperty, value);
    }

    private double _itemWidth = FallbackItemWidth;
    private double _itemHeight = FallbackItemHeight;
    private int _measurePasses;

    /// <summary>本轮实测尺寸和缓存的不一致，需要带着新尺寸再量一次。</summary>
    private bool _needsAnotherPass;

    private Size _extent;
    private Size _viewport;
    private Point _offset;

    private int _itemsPerRow = 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        var itemCount = owner?.Items.Count ?? 0;

        if (itemCount == 0)
        {
            _extent = new Size(0, 0);
            _viewport = availableSize;
            UpdateScrollInfo();
            return new Size(0, 0);
        }

        var viewWidth = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? FallbackItemWidth * 4
            : availableSize.Width;

        var viewHeight = double.IsInfinity(availableSize.Height) || availableSize.Height <= 0
            ? FallbackItemHeight * 3
            : availableSize.Height;

        _viewport = new Size(viewWidth, viewHeight);

        if (FullWidth)
        {
            // 列表模式：一行一张，宽度就是视口宽度
            _itemWidth = viewWidth;
            _itemsPerRow = 1;
        }
        else
        {
            // 卡片宽度由 XAML 直接给（绑到 Settings.CardWidth），
            // 这样第一轮测量就能算出正确的列数，不用等第二轮校准。
            if (!double.IsNaN(CardWidth) && CardWidth > 1)
            {
                _itemWidth = CardWidth + (CardMargin * 2);
            }

            _itemsPerRow = Math.Max(1, (int)Math.Floor(viewWidth / _itemWidth));
        }

        var rows = (int)Math.Ceiling(itemCount / (double)_itemsPerRow);
        _extent = new Size(viewWidth, rows * _itemHeight);
        CoerceOffset();

        // 只为视口覆盖到的行生成容器，上下各多给一行缓冲，滚动时不至于露白
        var firstRow = Math.Max(0, (int)Math.Floor(_offset.Y / _itemHeight) - 1);
        var lastRow = Math.Min(rows - 1, (int)Math.Ceiling((_offset.Y + viewHeight) / _itemHeight) + 1);

        var firstIndex = firstRow * _itemsPerRow;
        var lastIndex = Math.Min(itemCount - 1, ((lastRow + 1) * _itemsPerRow) - 1);

        _needsAnotherPass = false;
        RealizeItems(owner!, firstIndex, lastIndex);
        CleanUpItems(firstIndex, lastIndex);
        UpdateScrollInfo();

        // 实测高度和假设的不一样：带着校准后的尺寸再量一轮，
        // 否则总高度、滚动范围都会算错。最多 3 轮，防止尺寸互相打架时死循环。
        if (_needsAnotherPass && _measurePasses < 3)
        {
            _measurePasses++;
            InvalidateMeasure();
        }
        else
        {
            _measurePasses = 0;
        }

        return _viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var viewWidth = finalSize.Width > 0 && !double.IsInfinity(finalSize.Width)
            ? finalSize.Width
            : _viewport.Width;

        var perRow = Math.Max(1, (int)Math.Floor(viewWidth / _itemWidth));

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            if (child is not UIElement element)
            {
                continue;
            }

            var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex < 0)
            {
                // 探针容器（还没绑定到数据项）就摆在原位，下一轮会被回收
                element.Arrange(new Rect(0, 0, _itemWidth, _itemHeight));
                continue;
            }

            var row = itemIndex / perRow;
            var col = itemIndex % perRow;
            element.Arrange(new Rect(col * _itemWidth, row * _itemHeight, _itemWidth, _itemHeight));
        }

        return finalSize;
    }

    /// <summary>生成 [firstIndex, lastIndex] 区间的容器，并维护 InternalChildren 顺序。</summary>
    private void RealizeItems(ItemsControl owner, int firstIndex, int lastIndex)
    {
        var generator = ItemContainerGenerator;
        var startPos = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

        var measuredAny = false;

        using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
            {
                var child = generator.GenerateNext(out var isNew) as UIElement;
                if (child is null)
                {
                    break;
                }

                if (isNew)
                {
                    if (childIndex >= InternalChildren.Count)
                    {
                        AddInternalChild(child);
                    }
                    else
                    {
                        InsertInternalChild(childIndex, child);
                    }

                    generator.PrepareItemContainer(child);
                }

                // 宽度给格子宽度（多余部分由卡片自己的 Margin 吃掉），高度给无限
                // 让它按内容自然长。注意高度不能也给无限：卡片封面用的是星号行，
                // 在无限高约束下星号行会塌成 0，量出来的高度只有一半。
                child.Measure(new Size(FullWidth ? _viewport.Width : _itemWidth, double.PositiveInfinity));

                // 用第一张真正绑定到数据的卡片校准尺寸。
                // 卡片宽度绑在窗口的 Settings.CardWidth 上（外加 Margin），
                // 所以只有进可视化树量出来的 DesiredSize 才是对的。
                if (!measuredAny && itemIndex == firstIndex && child is FrameworkElement fe)
                {
                    measuredAny = true;
                    var desired = fe.DesiredSize;

                    // 列表模式下宽度是横撑满的；给了 CardWidth 时宽度已经确定，
                    // 两种情况都不能拿实测宽度去改格宽，只校准行高。
                    var widthIsFixed = FullWidth || (!double.IsNaN(CardWidth) && CardWidth > 1);
                    if (!widthIsFixed && desired.Width > 1 && Math.Abs(desired.Width - _itemWidth) > 0.5)
                    {
                        _itemWidth = desired.Width;
                        _needsAnotherPass = true;
                    }

                    if (desired.Height > 1 && Math.Abs(desired.Height - _itemHeight) > 0.5)
                    {
                        _itemHeight = desired.Height;
                        _needsAnotherPass = true;
                    }
                }
            }
        }
    }

    /// <summary>回收滚动出视口的容器，否则等于没虚拟化。</summary>
    private void CleanUpItems(int firstIndex, int lastIndex)
    {
        var generator = ItemContainerGenerator;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            var itemIndex = generator.IndexFromGeneratorPosition(pos);

            // itemIndex < 0 是探针容器，保留（下一轮会复用）
            if (itemIndex >= 0 && (itemIndex < firstIndex || itemIndex > lastIndex))
            {
                generator.Remove(pos, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    private void CoerceOffset()
    {
        var maxY = Math.Max(0, _extent.Height - _viewport.Height);
        _offset.Y = Math.Max(0, Math.Min(_offset.Y, maxY));
        _offset.X = 0;
    }

    private void UpdateScrollInfo() => ScrollOwner?.InvalidateScrollInfo();

    /// <summary>卡片尺寸变了（拖设置面板的滑块）之后调用。</summary>
    public void ResetItemSize()
    {
        _measurePasses = 0;
        _needsAnotherPass = true;
        InvalidateMeasure();
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);

        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                _measurePasses = 0;
                _needsAnotherPass = true;
                _offset = new Point(0, 0);
                break;
        }

        InvalidateMeasure();
    }

    // ── IScrollInfo ─────────────────────────────────────────

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;

    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_offset.Y - 40);
    public void LineDown() => SetVerticalOffset(_offset.Y + 40);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - 120);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + 120);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        var max = Math.Max(0, _extent.Height - _viewport.Height);
        var target = Math.Max(0, Math.Min(offset, max));
        if (Math.Abs(_offset.Y - target) < 0.5)
        {
            return;
        }

        _offset.Y = target;
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is not UIElement element)
        {
            return rectangle;
        }

        var index = InternalChildren.IndexOf(element);
        if (index < 0)
        {
            return rectangle;
        }

        var itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(index, 0));
        if (itemIndex < 0)
        {
            return rectangle;
        }

        var row = itemIndex / Math.Max(1, _itemsPerRow);
        var top = row * _itemHeight;
        var bottom = top + _itemHeight;

        if (top < _offset.Y)
        {
            SetVerticalOffset(top);
        }
        else if (bottom > _offset.Y + _viewport.Height)
        {
            SetVerticalOffset(bottom - _viewport.Height);
        }

        return new Rect(0, top - _offset.Y, _viewport.Width, _itemHeight);
    }
}
