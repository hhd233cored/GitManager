using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GitManager.Core;

namespace GitManager.App.Controls;

// Draw only visible rows: even a very large patch has a small, constant visual tree.
public sealed class DiffView : FrameworkElement, IScrollInfo
{
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(nameof(Document), typeof(DiffDocument), typeof(DiffView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, ContentChanged));
    public static readonly DependencyProperty IsSplitProperty = DependencyProperty.Register(nameof(IsSplit), typeof(bool), typeof(DiffView),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, ContentChanged));
    public DiffDocument? Document { get => (DiffDocument?)GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }
    public bool IsSplit { get => (bool)GetValue(IsSplitProperty); set => SetValue(IsSplitProperty, value); }
    private const double RowHeight = 23;
    private const double Gutter = 48;
    private readonly Typeface _typeface = new("Cascadia Mono, Consolas");
    private Size _viewport;
    private Size _extent;
    private double _verticalOffset;
    private double _horizontalOffset;
    private int _selectionStart = -1;
    private int _selectionEnd = -1;
    private int _longestLine;
    private static readonly Brush TextColor = Brush("#24292F");
    private static readonly Brush Muted = Brush("#87939F");
    private static readonly Brush BorderColor = Brush("#E2E7EC");
    private static readonly Brush Added = Brush("#E6FFEC");
    private static readonly Brush Removed = Brush("#FFEBE9");
    private static readonly Brush AddedGutter = Brush("#CCFFD8");
    private static readonly Brush RemovedGutter = Brush("#FFD7D5");
    private static readonly Brush HunkColor = Brush("#EEF5FC");
    private static readonly Brush EmptyColor = Brush("#F6F8FA");
    private static readonly Brush SelectionColor = Brush("#333D8BEB");
    private static readonly Brush Purple = Brush("#8250DF");
    private static readonly Brush StringColor = Brush("#0A3069");
    private static readonly Regex Syntax = new("(\"[^\"]*\"|'[^']*')|\\b(public|private|protected|internal|class|sealed|static|void|return|new|var|using|namespace|async|await|if|else|true|false|null|const|let|function|import|from|export|interface|readonly|string|int|bool)\\b|//.*$", RegexOptions.Compiled);
    private int Count => Document is null ? 0 : IsSplit ? Document.Pairs.Count : Document.Lines.Count;

    public DiffView()
    {
        Focusable = true;
        Cursor = Cursors.IBeam;
        ClipToBounds = true;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelection()));
        var menu = new ContextMenu();
        var selected = new MenuItem { Header = "复制所选行", InputGestureText = "Ctrl+C" };
        selected.Click += (_, _) => CopySelection();
        var patch = new MenuItem { Header = "复制完整 Diff" };
        patch.Click += (_, _) => { if (Document is not null) Clipboard.SetText(Document.Patch); };
        menu.Items.Add(selected);
        menu.Items.Add(patch);
        ContextMenu = menu;
    }

    private static Brush Brush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
    private static void ContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (DiffView)d;
        view._verticalOffset = 0;
        view._horizontalOffset = 0;
        view._selectionStart = view._selectionEnd = -1;
        view._longestLine = view.Document?.Lines.MaxBy(line => line.Text.Length)?.Text.Length ?? 0;
        view.UpdateScrollInfo();
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(double.IsInfinity(availableSize.Width) ? 700 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 400 : availableSize.Height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport = finalSize;
        UpdateScrollInfo();
        return finalSize;
    }
    private void UpdateScrollInfo()
    {
        var textWidth = Math.Min(_longestLine, 5000) * 8.1 + 35;
        _extent = new Size(Math.Max(_viewport.Width, IsSplit ? (textWidth + Gutter) * 2 : textWidth + Gutter * 2), Count * RowHeight);
        _verticalOffset = Math.Clamp(_verticalOffset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        _horizontalOffset = Math.Clamp(_horizontalOffset, 0, Math.Max(0, _extent.Width - _viewport.Width));
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (Document is null || Count == 0)
        {
            var message = Document is null ? "选择文件后，差异将显示在这里" : "此文件没有文本差异（可能仅修改了文件名、权限或二进制内容）";
            var text = Format(message, Muted, 12, false);
            dc.DrawText(text, new Point(24, 30));
            return;
        }
        var first = Math.Max(0, (int)(_verticalOffset / RowHeight));
        var last = Math.Min(Count, first + (int)Math.Ceiling(_viewport.Height / RowHeight) + 1);
        for (var index = first; index < last; index++)
        {
            var y = index * RowHeight - _verticalOffset;
            if (IsSplit)
            {
                var pair = Document.Pairs[index];
                if (pair.IsHeader) DrawHeader(dc, pair.Left!, y);
                else
                {
                    var half = _viewport.Width / 2;
                    DrawPane(dc, pair.Left, 0, half, y, true);
                    DrawPane(dc, pair.Right, half, _viewport.Width - half, y, false);
                }
            }
            else DrawUnified(dc, Document.Lines[index], y);
            if (_selectionStart >= 0 && index >= Math.Min(_selectionStart, _selectionEnd) && index <= Math.Max(_selectionStart, _selectionEnd))
                dc.DrawRectangle(SelectionColor, null, new Rect(0, y, _viewport.Width, RowHeight));
        }
        if (IsSplit) dc.DrawLine(new Pen(BorderColor, 1), new Point(_viewport.Width / 2, 0), new Point(_viewport.Width / 2, _viewport.Height));
    }

    private void DrawHeader(DrawingContext dc, DiffLine line, double y)
    {
        dc.DrawRectangle(line.Kind == DiffKind.Hunk ? HunkColor : EmptyColor, null, new Rect(0, y, _viewport.Width, RowHeight));
        dc.PushClip(new RectangleGeometry(new Rect(12, y, Math.Max(0, _viewport.Width - 24), RowHeight)));
        dc.DrawText(Format(line.Text, Muted, 11.5), new Point(14, y + 3));
        dc.Pop();
    }

    private void DrawPane(DrawingContext dc, DiffLine? line, double x, double width, double y, bool left)
    {
        var rect = new Rect(x, y, Math.Max(0, width), RowHeight);
        var background = line is null ? EmptyColor : line.Kind == DiffKind.Addition ? Added : line.Kind == DiffKind.Deletion ? Removed : Brushes.White;
        dc.DrawRectangle(background, null, rect);
        var gutterBackground = line?.Kind == DiffKind.Addition ? AddedGutter : line?.Kind == DiffKind.Deletion ? RemovedGutter : EmptyColor;
        dc.DrawRectangle(gutterBackground, null, new Rect(x, y, Math.Min(Gutter, width), RowHeight));
        if (line is null) return;
        var number = left ? line.OldNumber : line.NewNumber;
        if (number.HasValue)
        {
            var formatted = Format(number.Value.ToString(CultureInfo.InvariantCulture), Muted, 11);
            dc.DrawText(formatted, new Point(x + Gutter - formatted.Width - 8, y + 3));
        }
        var mark = line.Kind == DiffKind.Addition ? "+" : line.Kind == DiffKind.Deletion ? "−" : " ";
        dc.PushClip(new RectangleGeometry(new Rect(x + Gutter, y, Math.Max(0, width - Gutter), RowHeight)));
        dc.DrawText(Format(mark, Muted), new Point(x + Gutter + 7 - _horizontalOffset / 2, y + 3));
        dc.DrawText(Format(line.Text, TextColor, syntax: true), new Point(x + Gutter + 23 - _horizontalOffset / 2, y + 3));
        dc.Pop();
    }

    private void DrawUnified(DrawingContext dc, DiffLine line, double y)
    {
        if (line.Kind is DiffKind.Hunk or DiffKind.Information) { DrawHeader(dc, line, y); return; }
        var background = line.Kind == DiffKind.Addition ? Added : line.Kind == DiffKind.Deletion ? Removed : Brushes.White;
        dc.DrawRectangle(background, null, new Rect(0, y, _viewport.Width, RowHeight));
        var gutter = line.Kind == DiffKind.Addition ? AddedGutter : line.Kind == DiffKind.Deletion ? RemovedGutter : EmptyColor;
        dc.DrawRectangle(gutter, null, new Rect(0, y, Gutter * 2, RowHeight));
        if (line.OldNumber.HasValue) dc.DrawText(Format(line.OldNumber.Value.ToString(CultureInfo.InvariantCulture), Muted, 11), new Point(10, y + 3));
        if (line.NewNumber.HasValue) dc.DrawText(Format(line.NewNumber.Value.ToString(CultureInfo.InvariantCulture), Muted, 11), new Point(Gutter + 10, y + 3));
        var mark = line.Kind == DiffKind.Addition ? "+" : line.Kind == DiffKind.Deletion ? "−" : " ";
        dc.PushClip(new RectangleGeometry(new Rect(Gutter * 2, y, Math.Max(0, _viewport.Width - Gutter * 2), RowHeight)));
        dc.DrawText(Format(mark, Muted), new Point(Gutter * 2 + 7 - _horizontalOffset, y + 3));
        dc.DrawText(Format(line.Text, TextColor, syntax: true), new Point(Gutter * 2 + 23 - _horizontalOffset, y + 3));
        dc.Pop();
    }

    private FormattedText Format(string text, Brush foreground, double size = 12, bool syntax = false)
    {
        text = text.Replace("\t", "    ");
        if (text.Length > 5000) text = text[..5000] + " …";
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            _typeface, size, foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (syntax && text.Length < 2000)
        {
            foreach (Match match in Syntax.Matches(text))
            {
                var brush = match.Value.StartsWith("//", StringComparison.Ordinal) ? Muted : match.Value[0] is '\'' or '"' ? StringColor : Purple;
                formatted.SetForegroundBrush(brush, match.Index, match.Length);
            }
        }
        return formatted;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (Count == 0) return;
        _selectionStart = _selectionEnd = Math.Clamp((int)((e.GetPosition(this).Y + _verticalOffset) / RowHeight), 0, Count - 1);
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || Count == 0) return;
        _selectionEnd = Math.Clamp((int)((e.GetPosition(this).Y + _verticalOffset) / RowHeight), 0, Count - 1);
        InvalidateVisual();
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); ReleaseMouseCapture(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: LineDown(); break;
            case Key.Up: LineUp(); break;
            case Key.PageDown: PageDown(); break;
            case Key.PageUp: PageUp(); break;
            case Key.Home: SetVerticalOffset(0); break;
            case Key.End: SetVerticalOffset(ExtentHeight); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }
    private void CopySelection()
    {
        if (Document is null || _selectionStart < 0) return;
        var first = Math.Min(_selectionStart, _selectionEnd);
        var count = Math.Abs(_selectionStart - _selectionEnd) + 1;
        var lines = IsSplit ? Document.Pairs.Skip(first).Take(count).Select(pair => (pair.Right ?? pair.Left)?.Text ?? "")
            : Document.Lines.Skip(first).Take(count).Select(line => line.Text);
        Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _horizontalOffset;
    public double VerticalOffset => _verticalOffset;
    public ScrollViewer? ScrollOwner { get; set; }
    public void SetHorizontalOffset(double offset) { _horizontalOffset = Math.Clamp(offset, 0, Math.Max(0, ExtentWidth - ViewportWidth)); UpdateScrollInfo(); }
    public void SetVerticalOffset(double offset) { _verticalOffset = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight)); UpdateScrollInfo(); }
    public void LineDown() => SetVerticalOffset(VerticalOffset + RowHeight);
    public void LineUp() => SetVerticalOffset(VerticalOffset - RowHeight);
    public void LineLeft() => SetHorizontalOffset(HorizontalOffset - 32);
    public void LineRight() => SetHorizontalOffset(HorizontalOffset + 32);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + RowHeight * 3);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - RowHeight * 3);
    public void MouseWheelLeft() => LineLeft();
    public void MouseWheelRight() => LineRight();
    public void PageDown() => SetVerticalOffset(VerticalOffset + Math.Max(RowHeight, ViewportHeight - RowHeight));
    public void PageUp() => SetVerticalOffset(VerticalOffset - Math.Max(RowHeight, ViewportHeight - RowHeight));
    public void PageLeft() => SetHorizontalOffset(HorizontalOffset - ViewportWidth);
    public void PageRight() => SetHorizontalOffset(HorizontalOffset + ViewportWidth);
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
