using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Block = System.Windows.Documents.Block;
using Inline = System.Windows.Documents.Inline;
using MarkdownTable = Markdig.Extensions.Tables.Table;
using Table = System.Windows.Documents.Table;

namespace GitManager.App.Controls;

// Render the parsed Markdown tree directly: message text never becomes executable HTML or XAML.
public sealed class MarkdownView : RichTextBox
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseTaskLists()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough).DisableHtml().Build();
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");
    private static readonly Brush CodeBackground = Brush("#F0F3F6");
    private static readonly Brush BorderColor = Brush("#D0D7DE");
    private readonly DispatcherTimer _renderTimer;
    private bool _dirty;
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(nameof(Markdown), typeof(string),
        typeof(MarkdownView), new FrameworkPropertyMetadata("", (owner, _) => ((MarkdownView)owner).QueueRender()));
    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }

    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        IsUndoEnabled = false;
        IsTabStop = false;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        HorizontalScrollBarVisibility = VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _renderTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            // Keep copying/selection stable while a response is still streaming.
            if (IsKeyboardFocusWithin && !Selection.IsEmpty) return;
            RenderNow();
        };
        SelectionChanged += (_, _) => { if (_dirty && Selection.IsEmpty) QueueRender(); };
        LostKeyboardFocus += (_, _) => { if (_dirty) QueueRender(); };
        Loaded += (_, _) => RenderNow();
        Unloaded += (_, _) => _renderTimer.Stop();
    }

    private void QueueRender()
    {
        _dirty = true;
        if (IsLoaded && !_renderTimer.IsEnabled) _renderTimer.Start();
    }
    internal void RenderNow()
    {
        _renderTimer.Stop();
        var document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = FontFamily, FontSize = FontSize,
            Foreground = Foreground, TextAlignment = TextAlignment.Left, LineHeight = FontSize * 1.55 };
        foreach (var block in Markdig.Markdown.Parse(Markdown ?? "", Pipeline)) AddBlock(document.Blocks, block);
        Document = document;
        _dirty = false;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs args)
    {
        for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll)
            {
                args.Handled = true;
                scroll.RaiseEvent(new MouseWheelEventArgs(args.MouseDevice, args.Timestamp, args.Delta) { RoutedEvent = Mouse.MouseWheelEvent });
                return;
            }
        base.OnPreviewMouseWheel(args);
    }

    private static void AddBlock(BlockCollection target, Markdig.Syntax.Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = Paragraph(heading.Inline);
                title.FontSize = heading.Level switch { 1 => 19, 2 => 16, 3 => 14, _ => 12 };
                title.FontWeight = FontWeights.SemiBold;
                title.Margin = new Thickness(0, 8, 0, 6);
                target.Add(title);
                break;
            case ParagraphBlock paragraph: target.Add(Paragraph(paragraph.Inline)); break;
            case CodeBlock code:
                target.Add(new Paragraph(new Run(code.Lines.ToString())) { FontFamily = CodeFont, FontSize = 11,
                    Background = CodeBackground, Padding = new Thickness(8), BorderBrush = BorderColor, BorderThickness = new Thickness(1), Margin = new Thickness(0, 5, 0, 9) });
                break;
            case QuoteBlock quote:
                var section = new Section { BorderBrush = BorderColor, BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(9, 1, 0, 1), Foreground = Brush("#57606A"), Margin = new Thickness(0, 5, 0, 8) };
                foreach (var child in quote) AddBlock(section.Blocks, child);
                target.Add(section);
                break;
            case ListBlock list:
                var output = new System.Windows.Documents.List { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Padding = new Thickness(19, 0, 0, 0), Margin = new Thickness(0, 3, 0, 8) };
                if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start)) output.StartIndex = Math.Max(1, start);
                foreach (var child in list.OfType<ListItemBlock>())
                {
                    var item = new ListItem { Margin = new Thickness(0, 0, 0, 3) };
                    foreach (var content in child) AddBlock(item.Blocks, content);
                    output.ListItems.Add(item);
                }
                target.Add(output);
                break;
            case MarkdownTable table: target.Add(RenderTable(table)); break;
            case ThematicBreakBlock:
                target.Add(new Paragraph { BorderBrush = BorderColor, BorderThickness = new Thickness(0, 0, 0, 1),
                    FontSize = 1, LineHeight = 1, Margin = new Thickness(0, 8, 0, 8) });
                break;
            case ContainerBlock container:
                foreach (var child in container) AddBlock(target, child);
                break;
            case LeafBlock leaf: target.Add(new Paragraph(new Run(leaf.Lines.ToString())) { Margin = new Thickness(0, 0, 0, 7) }); break;
        }
    }

    private static Paragraph Paragraph(ContainerInline? content)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 7) };
        if (content is not null) AddInlines(paragraph.Inlines, content);
        return paragraph;
    }
    private static void AddInlines(InlineCollection target, ContainerInline source)
    {
        foreach (var inline in source)
            switch (inline)
            {
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case CodeInline code: target.Add(new Run(code.Content) { FontFamily = CodeFont, Background = CodeBackground }); break;
                case LineBreakInline: target.Add(new LineBreak()); break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else
                    {
                        if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold;
                        if (emphasis.DelimiterCount % 2 != 0) span.FontStyle = FontStyles.Italic;
                    }
                    AddInlines(span.Inlines, emphasis); target.Add(span);
                    break;
                case LinkInline link:
                    if (link.IsImage)
                    {
                        var imageLabel = new Span(new Run("[图片] ")) { Foreground = Brush("#57606A") };
                        AddInlines(imageLabel.Inlines, link); target.Add(imageLabel);
                    }
                    else if (TryWebUri(link.Url, out var uri))
                    {
                        var hyperlink = Hyperlink(uri!); AddInlines(hyperlink.Inlines, link); target.Add(hyperlink);
                    }
                    else AddInlines(target, link);
                    break;
                case AutolinkInline auto:
                    if (TryWebUri(auto.Url, out var address)) target.Add(Hyperlink(address!, auto.Url));
                    else target.Add(new Run(auto.Url));
                    break;
                case TaskList task: target.Add(new Run(task.Checked ? "☑ " : "☐ ")); break;
                case HtmlInline html: target.Add(new Run(html.Tag)); break;
                case ContainerInline container: AddInlines(target, container); break;
            }
    }
    private static Table RenderTable(MarkdownTable source)
    {
        var table = new Table { CellSpacing = 0, BorderBrush = BorderColor, BorderThickness = new Thickness(1), Margin = new Thickness(0, 5, 0, 9) };
        var columns = Math.Max(1, source.ColumnDefinitions.Count);
        for (var index = 0; index < columns; index++) table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        foreach (var row in source.OfType<Markdig.Extensions.Tables.TableRow>())
        {
            var output = new TableRow(); group.Rows.Add(output);
            foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                var outputCell = new TableCell { Padding = new Thickness(6, 4, 6, 4), BorderBrush = BorderColor,
                    BorderThickness = new Thickness(0, 0, 1, 1), ColumnSpan = Math.Max(1, cell.ColumnSpan) };
                if (row.IsHeader) { outputCell.FontWeight = FontWeights.SemiBold; outputCell.Background = CodeBackground; }
                foreach (var block in cell) AddBlock(outputCell.Blocks, block);
                output.Cells.Add(outputCell);
            }
        }
        return table;
    }
    internal static bool TryWebUri(string? value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri)
        && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0;
    private static Hyperlink Hyperlink(Uri uri, string? label = null)
    {
        var link = new Hyperlink { NavigateUri = uri, Foreground = Brush("#0969DA"), ToolTip = uri.AbsoluteUri };
        if (label is not null) link.Inlines.Add(new Run(label));
        link.RequestNavigate += (sender, args) =>
        {
            args.Handled = true;
            if (!TryWebUri(args.Uri.AbsoluteUri, out _)) return;
            try { Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
        };
        return link;
    }
    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush;
    }
}
