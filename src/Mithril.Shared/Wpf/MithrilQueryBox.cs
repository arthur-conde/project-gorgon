using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Mithril.Shared.Wpf.Query;

namespace Mithril.Shared.Wpf;

/// <summary>
/// Single-line editor for a <see cref="MithrilDataGrid"/> query with syntax
/// highlighting (via overlay TextBlock) and autocomplete (via popup list).
/// Bind <see cref="QueryText"/> two-way and set <see cref="Grid"/> to the
/// target <see cref="MithrilDataGrid"/>.
/// </summary>
[TemplatePart(Name = "PART_Editor", Type = typeof(TextBox))]
[TemplatePart(Name = "PART_Overlay", Type = typeof(TextBlock))]
[TemplatePart(Name = "PART_Popup", Type = typeof(Popup))]
[TemplatePart(Name = "PART_List", Type = typeof(ListBox))]
public class MithrilQueryBox : Control
{
    static MithrilQueryBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(MithrilQueryBox), new FrameworkPropertyMetadata(typeof(MithrilQueryBox)));
    }

    public static readonly DependencyProperty QueryTextProperty = DependencyProperty.Register(
        nameof(QueryText), typeof(string), typeof(MithrilQueryBox),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnQueryTextChanged));

    public static readonly DependencyProperty GridProperty = DependencyProperty.Register(
        nameof(Grid), typeof(MithrilDataGrid), typeof(MithrilQueryBox),
        new FrameworkPropertyMetadata(null, OnGridChanged));

    public static readonly DependencyProperty WatermarkProperty = DependencyProperty.Register(
        nameof(Watermark), typeof(string), typeof(MithrilQueryBox),
        new FrameworkPropertyMetadata("Query…"));

    public string QueryText
    {
        get => (string)GetValue(QueryTextProperty);
        set => SetValue(QueryTextProperty, value);
    }

    public MithrilDataGrid? Grid
    {
        get => (MithrilDataGrid?)GetValue(GridProperty);
        set => SetValue(GridProperty, value);
    }

    public string Watermark
    {
        get => (string)GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    private TextBox? _editor;
    private TextBlock? _overlay;
    private Popup? _popup;
    private ListBox? _list;
    private bool _suppressTextSync;
    private bool _popupDismissedByUser;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_editor is not null)
        {
            _editor.TextChanged -= OnEditorTextChanged;
            _editor.PreviewKeyDown -= OnEditorPreviewKeyDown;
            _editor.SelectionChanged -= OnEditorSelectionChanged;
            _editor.LostFocus -= OnEditorLostFocus;
            DataObject.RemovePastingHandler(_editor, OnPaste);
        }

        _editor = GetTemplateChild("PART_Editor") as TextBox;
        _overlay = GetTemplateChild("PART_Overlay") as TextBlock;
        _popup = GetTemplateChild("PART_Popup") as Popup;
        _list = GetTemplateChild("PART_List") as ListBox;

        if (_editor is not null)
        {
            _editor.Text = QueryText ?? string.Empty;
            _editor.TextChanged += OnEditorTextChanged;
            _editor.PreviewKeyDown += OnEditorPreviewKeyDown;
            _editor.SelectionChanged += OnEditorSelectionChanged;
            _editor.LostFocus += OnEditorLostFocus;
            DataObject.AddPastingHandler(_editor, OnPaste);
        }
        if (_popup is not null && _editor is not null)
        {
            _popup.PlacementTarget = _editor;
        }
        if (_list is not null)
        {
            _list.PreviewMouseLeftButtonUp += OnListClicked;
        }
        UpdateHighlighting();
    }

    private static void OnQueryTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MithrilQueryBox box) return;
        if (box._editor is null) return;
        if (box._suppressTextSync) return;
        var newText = (e.NewValue as string) ?? string.Empty;
        if (!string.Equals(box._editor.Text, newText, StringComparison.Ordinal))
        {
            box._editor.Text = newText;
        }
        box.UpdateHighlighting();
    }

    private static void OnGridChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MithrilQueryBox box) return;
        if (e.OldValue is MithrilDataGrid oldGrid)
        {
            oldGrid.SchemaChanged -= box.OnGridSchemaChanged;
        }
        if (e.NewValue is MithrilDataGrid newGrid)
        {
            newGrid.SchemaChanged += box.OnGridSchemaChanged;
        }
        box.UpdateHighlighting();
    }

    private void OnGridSchemaChanged(object? sender, EventArgs e) => UpdateHighlighting();

    private void OnEditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_editor is null) return;
        _suppressTextSync = true;
        try
        {
            QueryText = _editor.Text;
        }
        finally
        {
            _suppressTextSync = false;
        }
        UpdateHighlighting();
        _popupDismissedByUser = false;
        RefreshCompletion();
    }

    private void OnEditorSelectionChanged(object? sender, RoutedEventArgs e)
    {
        RefreshCompletion();
    }

    private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        // Dismiss if focus left us entirely (popup's ListBox keeps focus inside the visual tree).
        if (_popup is null) return;
        if (_list is not null && _list.IsKeyboardFocusWithin) return;
        ClosePopup();
    }

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        // Coerce paste to plain text so rich clipboard contents don't pollute formatting.
        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            var text = (string)e.DataObject.GetData(DataFormats.UnicodeText);
            var obj = new DataObject(DataFormats.UnicodeText, text);
            e.DataObject = obj;
            e.FormatToApply = DataFormats.UnicodeText;
        }
    }

    private void OnEditorPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_popup?.IsOpen == true && _list is not null)
        {
            switch (e.Key)
            {
                case Key.Down:
                    MoveListSelection(+1);
                    e.Handled = true;
                    return;
                case Key.Up:
                    MoveListSelection(-1);
                    e.Handled = true;
                    return;
                case Key.Tab:
                case Key.Enter:
                    if (_list.SelectedItem is CompletionItem sel)
                    {
                        AcceptCompletion(sel);
                        e.Handled = true;
                        return;
                    }
                    break;
                case Key.Escape:
                    _popupDismissedByUser = true;
                    ClosePopup();
                    e.Handled = true;
                    return;
            }
        }
        if (e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _popupDismissedByUser = false;
            RefreshCompletion();
            e.Handled = true;
        }
    }

    private void MoveListSelection(int delta)
    {
        if (_list is null || _list.Items.Count == 0) return;
        int idx = _list.SelectedIndex + delta;
        if (idx < 0) idx = _list.Items.Count - 1;
        if (idx >= _list.Items.Count) idx = 0;
        _list.SelectedIndex = idx;
        _list.ScrollIntoView(_list.SelectedItem);
    }

    private void OnListClicked(object sender, MouseButtonEventArgs e)
    {
        if (_list?.SelectedItem is CompletionItem sel)
        {
            AcceptCompletion(sel);
            e.Handled = true;
        }
    }

    private void RefreshCompletion()
    {
        if (_editor is null || _popup is null || _list is null || Grid is null || _popupDismissedByUser)
        {
            if (_popup is not null) ClosePopup();
            return;
        }
        var text = _editor.Text ?? string.Empty;
        int caret = _editor.CaretIndex;
        var schema = Grid.GetColumnSchema();
        IReadOnlyList<string> Sampler(string col) => Grid.GetDistinctValues(col);
        IReadOnlyList<CompletionItem> items = QueryCompletionProvider.Suggest(text, caret, schema, Sampler);

        // Bare-text mode: only offer column names, so the popup doesn't push
        // operators/values while the user is typing a plain search term. A known
        // column name anywhere in the input counts as grammar-intent — that
        // way `CropType LIK` still surfaces `LIKE`.
        var knownColumns = new HashSet<string>(schema.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        if (!QueryParser.LooksLikeGrammar(text, knownColumns))
        {
            items = items.Where(i => i.Kind == CompletionKind.Column).ToList();
        }

        if (items.Count == 0)
        {
            ClosePopup();
            return;
        }
        _list.ItemsSource = items;
        _list.SelectedIndex = 0;
        PositionPopupAtCaret();
        _popup.IsOpen = true;
    }

    private void PositionPopupAtCaret()
    {
        if (_editor is null || _popup is null)
        {
            return;
        }
        var caret = _editor.GetRectFromCharacterIndex(_editor.CaretIndex);
        if (double.IsNaN(caret.X) || double.IsInfinity(caret.X))
        {
            return;
        }
        _popup.HorizontalOffset = caret.X;
        _popup.VerticalOffset = caret.Bottom;
    }

    private void ClosePopup()
    {
        if (_popup is not null)
        {
            _popup.IsOpen = false;
        }
        if (_list is not null)
        {
            _list.ItemsSource = null;
        }
    }

    private void AcceptCompletion(CompletionItem item)
    {
        if (_editor is null) return;
        var text = _editor.Text ?? string.Empty;
        int start = Math.Clamp(item.ReplaceStart, 0, text.Length);
        int end = Math.Clamp(item.ReplaceEnd, start, text.Length);
        var newText = text[..start] + item.InsertText + text[end..];
        // Operators/keywords benefit from a trailing space; values don't force one.
        var caretAfter = start + item.InsertText.Length;
        if (item.Kind is CompletionKind.Column or CompletionKind.Keyword or CompletionKind.Operator)
        {
            newText = newText[..caretAfter] + " " + newText[caretAfter..];
            caretAfter++;
        }
        _editor.Text = newText;
        _editor.CaretIndex = caretAfter;
        _popupDismissedByUser = false;
        RefreshCompletion();
    }

    // ─────────────── highlighting ───────────────

    private void UpdateHighlighting()
    {
        if (_overlay is null) return;
        var text = _editor?.Text ?? QueryText ?? string.Empty;
        var knownColumns = Grid is null
            ? (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(Grid.GetColumnSchema().Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var spans = QueryHighlighter.Highlight(text, knownColumns);

        _overlay.Inlines.Clear();
        int cursor = 0;
        foreach (var span in spans)
        {
            if (span.Start > cursor)
            {
                _overlay.Inlines.Add(new Run(text[cursor..span.Start]) { Foreground = BrushFor(null) });
            }
            int end = Math.Min(span.Start + span.Length, text.Length);
            if (end > span.Start)
            {
                _overlay.Inlines.Add(new Run(text[span.Start..end]) { Foreground = BrushFor(span.Kind) });
            }
            cursor = Math.Max(cursor, end);
        }
        if (cursor < text.Length)
        {
            _overlay.Inlines.Add(new Run(text[cursor..]) { Foreground = BrushFor(null) });
        }
    }

    private Brush BrushFor(HighlightKind? kind)
    {
        if (kind is null)
        {
            return (Brush)(TryFindResource("TextPrimaryBrush") ?? Brushes.White);
        }
        return kind.Value switch
        {
            HighlightKind.Column        => (Brush)(TryFindResource("QueryColumnBrush")    ?? Brushes.Goldenrod),
            HighlightKind.UnknownColumn => (Brush)(TryFindResource("QueryErrorBrush")      ?? Brushes.IndianRed),
            HighlightKind.Keyword       => (Brush)(TryFindResource("QueryKeywordBrush")    ?? Brushes.LightSkyBlue),
            HighlightKind.Operator      => (Brush)(TryFindResource("QueryOperatorBrush")   ?? Brushes.LightGray),
            HighlightKind.String        => (Brush)(TryFindResource("QueryStringBrush")     ?? Brushes.LightGreen),
            HighlightKind.Number        => (Brush)(TryFindResource("QueryNumberBrush")     ?? Brushes.Khaki),
            HighlightKind.Duration      => (Brush)(TryFindResource("QueryDurationBrush")   ?? Brushes.Khaki),
            HighlightKind.Punct         => (Brush)(TryFindResource("QueryPunctBrush")      ?? Brushes.Silver),
            HighlightKind.Error         => (Brush)(TryFindResource("QueryErrorBrush")       ?? Brushes.IndianRed),
            _ => (Brush)(TryFindResource("TextPrimaryBrush") ?? Brushes.White),
        };
    }
}
