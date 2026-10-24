using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GitManager.Core;

namespace GitManager.App.Controls;

// Keep an index of wrapped row heights, and draw only rows intersecting the viewport.
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
    private const double TextInset = 23;
    private const double TextRightPadding = 10;
    private readonly Typeface _typeface = new("Cascadia Mono, Consolas");
    private readonly Dictionary<(int Row, int Pane), FormattedText> _visibleText = [];
    private double[] _rowOffsets = [0];
    private bool _layoutDirty = true;
    private double _asciiAdvance;
    private double _firstBaseline;
    private Size _viewport;
    private Size _extent;
    private double _verticalOffset;
    private int _selectionStart = -1;
    private int _selectionEnd = -1;
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
    internal int CachedTextCount => _visibleText.Count;

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
        view._selectionStart = view._selectionEnd = -1;
        view._layoutDirty = true;
        view._rowOffsets = [0];
        view._visibleText.Clear();
        view.UpdateScrollInfo();
    }
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 700 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 400 : availableSize.Height);

    protected override Size ArrangeOverride(Size finalSize)
    {
        var widthChanged = Math.Abs(finalSize.Width - _viewport.Width) > .01;
        var anchor = RowAtOffset(_verticalOffset);
        var fraction = anchor < 0 ? 0 : (_verticalOffset - _rowOffsets[anchor]) / (_rowOffsets[anchor + 1] - _rowOffsets[anchor]);
        _viewport = finalSize;
        if (widthChanged) _layoutDirty = true;
        EnsureRowLayout();
        if (widthChanged && anchor >= 0 && anchor < Count)
            _verticalOffset = _rowOffsets[anchor] + fraction * (_rowOffsets[anchor + 1] - _rowOffsets[anchor]);
        UpdateScrollInfo();
        return finalSize;
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _layoutDirty = true;
        _asciiAdvance = 0;
        InvalidateMeasure();
    }
    private double CodeWidth => Math.Max(1, (IsSplit ? _viewport.Width / 2 - Gutter : _viewport.Width - Gutter * 2) - TextInset - TextRightPadding);
    private double HeaderWidth => Math.Max(1, _viewport.Width - 28);

    private void EnsureRowLayout()
    {
        if (!_layoutDirty || _viewport.Width <= 0) return;
        _layoutDirty = false;
        _visibleText.Clear();
        _rowOffsets = new double[Count + 1];
        if (Document is null) return;
        if (_asciiAdvance == 0)
        {
            var probe = FormatWrapped("M", CodeWidth, false, false);
            _asciiAdvance = Math.Max(probe.WidthIncludingTrailingWhitespace, Format("W", TextColor).WidthIncludingTrailingWhitespace);
            _firstBaseline = probe.Baseline;
        }
        var heights = new Dictionary<(string Text, bool Header), double>();
        double Height(DiffLine? line, bool header = false)
        {
            if (line is null || line.Text.Length == 0) return RowHeight;
            var key = (line.Text, header);
            if (heights.TryGetValue(key, out var cached)) return cached;
            var width = header ? HeaderWidth : CodeWidth;
            // ASCII code that fits the fixed-pitch font needs no paragraph layout.
            var fits = !header && !line.Text.Contains('\t') && line.Text.Length * _asciiAdvance <= width - 1
                && line.Text.All(character => character is >= ' ' and <= '~');
            var height = fits ? RowHeight : Math.Max(RowHeight,
                Math.Ceiling((FormatWrapped(line.Text, width, header, false).Height - .01) / RowHeight) * RowHeight);
            heights[key] = height;
            return height;
        }
        for (var index = 0; index < Count; index++)
        {
            double height;
            if (IsSplit)
            {
                var pair = Document.Pairs[index];
                height = pair.IsHeader ? Height(pair.Left, true) : Math.Max(Height(pair.Left), Height(pair.Right));
            }
            else
            {
                var line = Document.Lines[index];
                height = Height(line, line.Kind is DiffKind.Hunk or DiffKind.Information);
            }
            _rowOffsets[index + 1] = _rowOffsets[index] + height;
        }
    }
    private void UpdateScrollInfo()
    {
        EnsureRowLayout();
        _extent = new Size(_viewport.Width, _rowOffsets.Length == Count + 1 ? _rowOffsets[^1] : Count * RowHeight);
        _verticalOffset = Math.Clamp(_verticalOffset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.White, null, new Rect(RenderSize));
        if (Document is null || Count == 0)
        {
            var message = Document is null ? "选择文件后，差异将显示在这里" : "此文件没有文本差异（可能仅修改了文件名、权限或二进制内容）";
            var text = FormatWrapped(message, Math.Max(1, _viewport.Width - 48), true, false);
            dc.DrawText(text, new Point(24, 30));
            return;
        }
        EnsureRowLayout();
        if (_rowOffsets.Length != Count + 1) return;
        var first = RowAtOffset(_verticalOffset);
        var last = Math.Min(Count, RowAtOffset(_verticalOffset + _viewport.Height) + 1);
        foreach (var key in _visibleText.Keys.Where(key => key.Row < first - 1 || key.Row > last).ToArray()) _visibleText.Remove(key);
        for (var index = first; index < last; index++)
        {
            var y = _rowOffsets[index] - _verticalOffset;
            var height = _rowOffsets[index + 1] - _rowOffsets[index];
            if (IsSplit)
            {
                var pair = Document.Pairs[index];
                if (pair.IsHeader) DrawHeader(dc, pair.Left!, index, y, height);
                else
                {
                    var half = _viewport.Width / 2;
                    DrawPane(dc, pair.Left, index, 0, half, y, height, true);
                    DrawPane(dc, pair.Right, index, half, _viewport.Width - half, y, height, false);
                }
            }
            else DrawUnified(dc, Document.Lines[index], index, y, height);
            if (_selectionStart >= 0 && index >= Math.Min(_selectionStart, _selectionEnd) && index <= Math.Max(_selectionStart, _selectionEnd))
                dc.DrawRectangle(SelectionColor, null, new Rect(0, y, _viewport.Width, height));
        }
        if (IsSplit) dc.DrawLine(new Pen(BorderColor, 1), new Point(_viewport.Width / 2, 0), new Point(_viewport.Width / 2, _viewport.Height));
    }

    private FormattedText VisibleText(int index, int pane, string text, bool header = false)
    {
        if (!_visibleText.TryGetValue((index, pane), out var formatted))
        {
            formatted = FormatWrapped(text, header ? HeaderWidth : CodeWidth, header, !header);
            _visibleText[(index, pane)] = formatted;
        }
        return formatted;
    }
    private void DrawHeader(DrawingContext dc, DiffLine line, int index, double y, double height)
    {
        dc.DrawRectangle(line.Kind == DiffKind.Hunk ? HunkColor : EmptyColor, null, new Rect(0, y, _viewport.Width, height));
        dc.PushClip(new RectangleGeometry(new Rect(12, y, Math.Max(0, _viewport.Width - 24), height)));
        dc.DrawText(VisibleText(index, 2, line.Text, true), new Point(14, y));
        dc.Pop();
    }
    private void DrawPane(DrawingContext dc, DiffLine? line, int index, double x, double width, double y, double height, bool left)
    {
        var rect = new Rect(x, y, Math.Max(0, width), height);
        dc.PushClip(new RectangleGeometry(rect));
        var background = line is null ? EmptyColor : line.Kind == DiffKind.Addition ? Added : line.Kind == DiffKind.Deletion ? Removed : Brushes.White;
        dc.DrawRectangle(background, null, rect);
        var gutterBackground = line?.Kind == DiffKind.Addition ? AddedGutter : line?.Kind == DiffKind.Deletion ? RemovedGutter : EmptyColor;
        dc.DrawRectangle(gutterBackground, null, new Rect(x, y, Math.Min(Gutter, width), height));
        if (line is null) { dc.Pop(); return; }
        var code = VisibleText(index, left ? 0 : 1, line.Text);
        var number = left ? line.OldNumber : line.NewNumber;
        if (number.HasValue)
        {
            var formatted = Format(number.Value.ToString(CultureInfo.InvariantCulture), Muted, 11);
            dc.DrawText(formatted, new Point(x + Gutter - formatted.Width - 8, y + _firstBaseline - formatted.Baseline));
        }
        var mark = line.Kind == DiffKind.Addition ? "+" : line.Kind == DiffKind.Deletion ? "−" : " ";
        var marker = Format(mark, Muted);
        dc.PushClip(new RectangleGeometry(new Rect(x + Gutter, y, Math.Max(0, width - Gutter), height)));
        dc.DrawText(marker, new Point(x + Gutter + 7, y + _firstBaseline - marker.Baseline));
        dc.DrawText(code, new Point(x + Gutter + TextInset, y));
        dc.Pop();
        dc.Pop();
    }
    private void DrawUnified(DrawingContext dc, DiffLine line, int index, double y, double height)
    {
        if (line.Kind is DiffKind.Hunk or DiffKind.Information) { DrawHeader(dc, line, index, y, height); return; }
        dc.PushClip(new RectangleGeometry(new Rect(0, y, _viewport.Width, height)));
        var background = line.Kind == DiffKind.Addition ? Added : line.Kind == DiffKind.Deletion ? Removed : Brushes.White;
        dc.DrawRectangle(background, null, new Rect(0, y, _viewport.Width, height));
        var gutter = line.Kind == DiffKind.Addition ? AddedGutter : line.Kind == DiffKind.Deletion ? RemovedGutter : EmptyColor;
        dc.DrawRectangle(gutter, null, new Rect(0, y, Math.Min(Gutter * 2, _viewport.Width), height));
        var code = VisibleText(index, 3, line.Text);
        void DrawNumber(int number, double x)
        {
            var formatted = Format(number.ToString(CultureInfo.InvariantCulture), Muted, 11);
            dc.DrawText(formatted, new Point(x, y + _firstBaseline - formatted.Baseline));
        }
        if (line.OldNumber.HasValue) DrawNumber(line.OldNumber.Value, 10);
        if (line.NewNumber.HasValue) DrawNumber(line.NewNumber.Value, Gutter + 10);
        var mark = line.Kind == DiffKind.Addition ? "+" : line.Kind == DiffKind.Deletion ? "−" : " ";
        var marker = Format(mark, Muted);
        dc.PushClip(new RectangleGeometry(new Rect(Gutter * 2, y, Math.Max(0, _viewport.Width - Gutter * 2), height)));
        dc.DrawText(marker, new Point(Gutter * 2 + 7, y + _firstBaseline - marker.Baseline));
        dc.DrawText(code, new Point(Gutter * 2 + TextInset, y));
        dc.Pop();
        dc.Pop();
    }
    private FormattedText FormatWrapped(string text, double width, bool header, bool syntax)
    {
        var formatted = Format(text, header ? Muted : TextColor, header ? 11.5 : 12, syntax);
        formatted.MaxTextWidth = width;
        formatted.Trimming = TextTrimming.None; // Also forces emergency wrapping of unbroken identifiers/URLs.
        formatted.LineHeight = RowHeight;
        return formatted;
    }
    private FormattedText Format(string text, Brush foreground, double size = 12, bool syntax = false)
    {
        text = text.Replace("\t", "    ");
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

    internal int RowAtOffset(double offset)
    {
        if (_rowOffsets.Length <= 1) return -1;
        var found = Array.BinarySearch(_rowOffsets, Math.Max(0, offset));
        return Math.Clamp(found >= 0 ? found : ~found - 1, 0, _rowOffsets.Length - 2);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (Count == 0) return;
        _selectionStart = _selectionEnd = RowAtOffset(e.GetPosition(this).Y + _verticalOffset);
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured || Count == 0) return;
        _selectionEnd = RowAtOffset(e.GetPosition(this).Y + _verticalOffset);
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
    internal string SelectionText(int first, int last)
    {
        if (Document is null || Count == 0) return "";
        var start = Math.Clamp(Math.Min(first, last), 0, Count - 1);
        var count = Math.Clamp(Math.Max(first, last), 0, Count - 1) - start + 1;
        var lines = IsSplit ? Document.Pairs.Skip(start).Take(count).Select(pair => (pair.Right ?? pair.Left)?.Text ?? "")
            : Document.Lines.Skip(start).Take(count).Select(line => line.Text);
        return string.Join(Environment.NewLine, lines);
    }
    private void CopySelection()
    {
        if (_selectionStart >= 0 && Document is not null) Clipboard.SetText(SelectionText(_selectionStart, _selectionEnd));
    }
    public bool CanHorizontallyScroll { get => false; set { } }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _verticalOffset;
    public ScrollViewer? ScrollOwner { get; set; }
    public void SetHorizontalOffset(double offset) { }
    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) return;
        _verticalOffset = offset;
        UpdateScrollInfo();
    }
    public void LineDown() => SetVerticalOffset(VerticalOffset + RowHeight);
    public void LineUp() => SetVerticalOffset(VerticalOffset - RowHeight);
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + RowHeight * 3);
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - RowHeight * 3);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageDown() => SetVerticalOffset(VerticalOffset + Math.Max(RowHeight, ViewportHeight - RowHeight));
    public void PageUp() => SetVerticalOffset(VerticalOffset - Math.Max(RowHeight, ViewportHeight - RowHeight));
    public void PageLeft() { }
    public void PageRight() { }
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
