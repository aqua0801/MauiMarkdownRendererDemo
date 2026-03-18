using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using CSharpMath.SkiaSharp;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Maui.Controls.Shapes;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Typography.OpenFont;

namespace MauiMarkdownRendererWithLaTeX
{
    /// <summary>
    /// Provides a layout control for rendering Markdown content with support for code blocks, tables, LaTeX math, and
    /// advanced formatting in .NET MAUI applications. Supports incremental updates for streaming scenarios and exposes
    /// properties for customizing appearance and font settings.
    /// </summary>
    /// <remarks>The control supports real-time streaming updates by coalescing rapid changes and throttling
    /// re-renders to avoid UI flooding. It offers bindable properties for customizing text color, code block background,
    /// quote bar color, and font sizes for both regular and LaTeX content. LaTeX rendering is supported via an integrated
    /// math painter and can be styled independently. The renderer is thread-safe for streaming append operations. For best
    /// results, set the relevant properties before assigning Markdown text. The control is designed for use in .NET MAUI
    /// cross-platform applications.</remarks>
    public class MarkdownRenderer : VerticalStackLayout
    {
        public enum RenderType
        {
            IncrementalRender , FullRender , Unknown
        }

        /// <summary>
        /// Typed image that remembers the LaTeX source so it can be re-rendered
        /// when colours or font sizes change.
        /// </summary>
        private sealed class LatexImage : Image
        {
            public string Latex { get; set; } = string.Empty;
        }

        private sealed class ColoredCodeBlock : Border;
        private sealed class HighlightedLabel : Label
        {
            public string Code { get; set; } = string.Empty;
            public string Language { get; set; } = string.Empty;
        }

        private readonly record struct RenderedBlock(IView View, string Text);


        private static readonly string MonospaceFont =
#if WINDOWS
            "Cascadia Code, Consolas, Courier New"
#elif ANDROID
            "monospace"
#elif IOS || MACCATALYST
            "Menlo"
#else
            "Courier New"
#endif
            ;

        private static readonly string SansSerifFont =
#if WINDOWS
            "Segoe UI"
#elif ANDROID
            "Roboto"
#elif IOS || MACCATALYST
            "San Francisco"
#else
            "Segoe UI"
#endif
            ;

        //Bindables----------------------------------------------------------------------

        public static readonly BindableProperty TextColorProperty =
            BindableProperty.Create(nameof(TextColor), typeof(Color), typeof(MarkdownRenderer),
                defaultValue: null);

        public static readonly BindableProperty LatexTextColorProperty =
            BindableProperty.Create(nameof(LatexTextColor), typeof(Color), typeof(MarkdownRenderer),
                defaultValue: null,
                propertyChanged: OnLatexTextColorChanged);

        public static readonly BindableProperty BaseFontSizeProperty =
            BindableProperty.Create(nameof(BaseFontSize), typeof(double), typeof(MarkdownRenderer),
                defaultValue: 14.0,
                propertyChanged: OnBaseFontSizeChanged);

        public static readonly BindableProperty LatexFontSizeProperty =
            BindableProperty.Create(nameof(LatexFontSize), typeof(double), typeof(MarkdownRenderer),
                defaultValue: 14.0,
                propertyChanged: OnLatexFontSizeChanged);

        public static readonly BindableProperty CodeBackgroundColorProperty =
            BindableProperty.Create(nameof(CodeBackgroundColor), typeof(Color), typeof(MarkdownRenderer),
                defaultValue: Color.FromArgb("#F6F8FA"),
                propertyChanged: OnCodeBlockBackgroundColorChanged);

        public static readonly BindableProperty QuoteBarColorProperty =
            BindableProperty.Create(nameof(QuoteBarColor), typeof(Color), typeof(MarkdownRenderer),
                defaultValue: Color.FromArgb("#D0D7DE"));

        public static readonly BindableProperty WarmUpProperty =
            BindableProperty.Create(nameof(WarmUp), typeof(bool), typeof(MarkdownRenderer),
                defaultValue: false,
                propertyChanged: OnWarmUpChanged);

        //Bindable callbacks-------------------------------------------------------------

        private CancellationTokenSource? _fontSizeRenderCts;

        private static void OnBaseFontSizeChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is not MarkdownRenderer r) return;
            if (oldValue is double ofs && newValue is double nfs && ofs == nfs) return;
            if (newValue is not double fs) return;

            r._painter.FontSize = (float)fs;

            if (string.IsNullOrEmpty(r._text)) return;

            r._fontSizeRenderCts?.Cancel();
            r._fontSizeRenderCts = new CancellationTokenSource();
            var token = r._fontSizeRenderCts.Token;

            _ = Task.Delay(150, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                MainThread.BeginInvokeOnMainThread(() => _ = r.RenderMarkdownAsync(r._text));
            }, TaskScheduler.Default);
        }

        private static void OnLatexFontSizeChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is MarkdownRenderer r && newValue is double fs)
            {
                r._painter.FontSize = (float)fs;
                _ = r.UpdateLatexImagesAsync(r.LatexTextColor ?? Colors.Black);
            }
        }

        private static void OnCodeBlockBackgroundColorChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is MarkdownRenderer r && newValue is Color nc)
            {
                if (oldValue is Color oc && nc == oc)
                    return;
                _ = r.UpdateCodeBackgroundColorAsync();
            }
        }

        private static void OnLatexTextColorChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is MarkdownRenderer r && newValue is Color c)
                _ = r.UpdateLatexImagesAsync(c);
        }

        private static void OnWarmUpChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is MarkdownRenderer r && newValue is true)
            {
                r.HandlerChanged += r.OnHandlerChangedForWarmUp;
            }
        }

        public Color? TextColor
        {
            get => (Color?)GetValue(TextColorProperty);
            set => SetValue(TextColorProperty, value);
        }

        public Color? LatexTextColor
        {
            get => (Color?)GetValue(LatexTextColorProperty);
            set => SetValue(LatexTextColorProperty, value);
        }

        public double BaseFontSize
        {
            get => (double)GetValue(BaseFontSizeProperty);
            set => SetValue(BaseFontSizeProperty, value);
        }

        public double LatexFontSize
        {
            get => (double)GetValue(LatexFontSizeProperty);
            set => SetValue(LatexFontSizeProperty, value);
        }

        public Color CodeBackgroundColor
        {
            get => (Color)GetValue(CodeBackgroundColorProperty);
            set => SetValue(CodeBackgroundColorProperty, value);
        }

        public Color QuoteBarColor
        {
            get => (Color)GetValue(QuoteBarColorProperty);
            set => SetValue(QuoteBarColorProperty, value);
        }
        public bool WarmUp
        {
            get => (bool)GetValue(WarmUpProperty);
            set => SetValue(WarmUpProperty, value);
        }

        public event EventHandler<RenderType>? RenderFinished;
        public string FontFamily { get; set; } = SansSerifFont;

        /// <summary>Minimum time between incremental re-renders during streaming.</summary>
        public TimeSpan AppendThrottle { get; set; } = TimeSpan.FromMilliseconds(50);

        private readonly MarkdownPipeline _pipeline;
        private readonly CodeColorizer _colorizer;
        private readonly MathPainter _painter = new();

        // Incremental render state
        private string _text = string.Empty;
        private List<Block> _lastBlocks = new();
        private readonly List<IView> _blockViews = new();
        private readonly Dictionary<IView, string> _blockTextMap = new();

        // Streaming throttle
        private readonly SemaphoreSlim _renderGate = new(1, 1);
        private bool _pendingRender;

        public MarkdownRenderer()
        {
            _pipeline = new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .UseMathematics()
                .UseTaskLists()
                .Build();

            _colorizer = new CodeColorizer();
        }

        public string Text
        {
            get => _text;
            set
            {
                _pendingRender = false;
                _fontSizeRenderCts?.Cancel();

                _text = value;
                _ = RenderMarkdownAsync(_text);
            }
        }

        /// <summary>
        /// Appends a typeface to the LaTeX painter's fallback chain.
        /// Call this for each font you want to support in math rendering.
        /// Fonts are tried in the order they are added.
        ///
        /// Recommended fonts for broad coverage:
        ///   - latinmodern-math.otf  (standard LaTeX math symbols, Greek)
        ///   - NotoSansMath-Regular.ttf  (extended Unicode math blocks)
        ///   - kaiu.ttf  (CJK characters in math expressions)
        /// </summary>
        public async Task AddMathFontAsync(string appPackageFileName)
        {
            try
            {
                using var stream = await FileSystem.OpenAppPackageFileAsync(appPackageFileName);
                var face = new OpenFontReader().Read(stream);
                _painter.LocalTypefaces.Append(face);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MarkdownRenderer] Font '{appPackageFileName}' failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Appends a typeface from a stream directly.
        /// Use this when loading from embedded assembly resources or other sources.
        /// </summary>
        public void AddMathFont(Stream fontStream)
        {
            try
            {
                var face = new OpenFontReader().Read(fontStream);
                _painter.LocalTypefaces.Append(face);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MarkdownRenderer] Font stream load failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Pre-warms the renderer by performing a silent render of minimal markdown.
        /// Call once at app startup (e.g. during splash/loading screen) to avoid
        /// cold-start latency on the first real render.
        ///
        /// Hits all major cold paths:
        /// - Markdig parse pipeline
        /// - SkiaSharp surface initialisation
        /// - LaTeX rasterisation (MathPainter + font)
        /// - Code highlighting tokenizer (per language via Tokenize)
        /// - MAUI view/handler initialisation per control type
        /// - Regex compilation for all TokenRules
        /// </summary>
        public async Task WarmUpAsync()
        {
            const string warmupMarkdown = 
                """
                # x
                $x$
                ```csharp
                        var x = 1;
                ```
                """;

            var prev = _text;
            try
            {
                await RenderMarkdownAsync(warmupMarkdown);
            }
            finally
            {
                Clear();
                _text = prev;
                Debug.WriteLine("[Debug] Wramuped !");
            }
        }

        private async void OnHandlerChangedForWarmUp(object? sender, EventArgs e)
        {
            if (Handler is null) return;
            HandlerChanged -= OnHandlerChangedForWarmUp;
            await WarmUpAsync();
        }

        /// <summary>
        /// Appends a streaming chunk and schedules a throttled incremental re-render.
        /// Thread-safe; coalesces rapid updates so the UI is never flooded.
        /// </summary>
        public void Append(string chunk)
        {
            _text += chunk;
            ScheduleRender();
        }

        private void ScheduleRender()
        {
            if (_pendingRender) return;
            _pendingRender = true;
            _ = ThrottledRenderLoopAsync();
        }

        private async Task ThrottledRenderLoopAsync()
        {
            await Task.Delay(AppendThrottle);

            if (!_pendingRender)
                return;

            _pendingRender = false;

            if (!await _renderGate.WaitAsync(0))
            {
                _pendingRender = true;
                return;
            }

            try
            {
                await RenderIncrementalAsync(_text);
            }
            finally
            {
                _renderGate.Release();
                if (_pendingRender)
                    _ = ThrottledRenderLoopAsync();
                RenderFinished?.Invoke(this, RenderType.IncrementalRender);
            }
        }

        public new void Clear()
        {
            _pendingRender = false;
            _fontSizeRenderCts?.Cancel();
            _fontSizeRenderCts = null;

            _lastBlocks.Clear();
            _blockViews.Clear();
            _blockTextMap.Clear();
            _text = string.Empty;

            Children.Clear();

            base.Clear();
        }

        /// <summary>
        /// Asynchronously parses the specified Markdown text and updates the view hierarchy to display the rendered
        /// content.
        /// </summary>
        /// <remarks>Existing child views and block views are cleared before rendering the new content. If
        /// the Markdown is invalid or an error occurs during rendering, the view hierarchy will be left empty and the
        /// error will be logged for debugging purposes.</remarks>
        /// <param name="markdown">The Markdown-formatted string to be rendered. Cannot be null.</param>
        /// <returns>A task that represents the asynchronous rendering operation.</returns>
        private async Task RenderMarkdownAsync(string markdown)
        {
            _lastBlocks.Clear();
            Children.Clear();
            _blockViews.Clear();
            _blockTextMap.Clear();

            try
            {
                var blocks = await BuildBlockViewsAsync(markdown);

                for (int i = 0; i < blocks.Count; i++)
                {
                    var view = blocks[i].View;
                    if (view is View v && i == 0)
                        v.Margin = new Thickness(v.Margin.Left, 0, v.Margin.Right, v.Margin.Bottom);

                    RegisterBlock(view, blocks[i].Text);
                    Children.Add(view);
                }

                if (!string.IsNullOrWhiteSpace(markdown))
                    Children.Add(BuildCopyAllButton());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MarkdownRenderer] Render error: {ex}");
            }
            finally
            {
                RenderFinished?.Invoke(this,RenderType.FullRender);
            }
        }

        private View BuildCopyAllButton()
        {
            var btn = new Button
            {
                Text = "⎘ Copy",
                FontSize = 11,
                Padding = new Thickness(8, 4),
                CornerRadius = 6,
                HorizontalOptions = LayoutOptions.End,
                Margin = new Thickness(0, 4, 0, 0),

                BorderWidth = 0,
                BorderColor = Colors.Transparent,
                BackgroundColor = Colors.Transparent,
            };

            VisualStateManager.SetVisualStateGroups(btn, new VisualStateGroupList
            {
                new VisualStateGroup
                {
                    States =
                    {
                        new VisualState { Name = "Normal" },
                        new VisualState
                        {
                            Name = "PointerOver",
                            Setters =
                            {
                                new Setter
                                {
                                    Property = Button.BackgroundColorProperty,
                                    Value    = Color.FromArgb("#1A808080")
                                }
                            }
                        }
                    }
                }
            });

            btn.Clicked += async (_, _) =>
            {
                await Clipboard.Default.SetTextAsync(_text);

                var toast = Toast.Make("Content copied !", ToastDuration.Short, 14);
                await toast.Show();
            };

            return btn;
        }

        private void RegisterBlock(IView view, string text)
        {
            _blockViews.Add(view);
            _blockTextMap[view] = text;

            if (view is View v)
            {
                var tap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
                tap.Tapped += async (s, e) =>
                {
                    if (_blockTextMap.TryGetValue(view, out var t))
                    {
                        _ = Clipboard.Default.SetTextAsync(t);

                        var toast = Toast.Make("Content copied !", ToastDuration.Short, 14);
                        await toast.Show();
                    }
                };
                v.GestureRecognizers.Add(tap);
            }
        }


        private async Task RenderIncrementalAsync(string text)
        {
            var document = Markdown.Parse(text, _pipeline);
            var newBlocks = document.ToList();

            int prefix = 0;
            while (prefix < _lastBlocks.Count &&
                   prefix < newBlocks.Count &&
                   BlocksEqual(_lastBlocks[prefix], newBlocks[prefix]))
                prefix++;

            for (int i = _blockViews.Count - 1; i >= prefix; i--)
            {
                Children.Remove(_blockViews[i]);
                _blockViews.RemoveAt(i);
            }

            for (int i = prefix; i < newBlocks.Count; i++)
            {
                if (newBlocks[i] is LinkReferenceDefinitionGroup) continue;

                var rendered = await RenderBlockAsync(newBlocks[i]);
                if (rendered is null) continue;

                var view = rendered.Value.View;
                if (i == 0 && view is View v)
                    v.Margin = new Thickness(v.Margin.Left, 0, v.Margin.Right, v.Margin.Bottom);

                RegisterBlock(view, rendered.Value.Text);
                Children.Add(view);
            }

            _lastBlocks = newBlocks;
        }

        private static bool BlocksEqual(Block a, Block b) =>
            a.GetType() == b.GetType() &&
            a.Span.Start == b.Span.Start &&
            a.Span.End == b.Span.End;


        private async Task<List<RenderedBlock>> BuildBlockViewsAsync(string markdown)
        {
            var document = Markdown.Parse(markdown, _pipeline);
            var results = new List<RenderedBlock>();

            foreach (var block in document)
            {
                if (block is LinkReferenceDefinitionGroup) continue;
                var rendered = await RenderBlockAsync(block);
                if (rendered.HasValue)
                    results.Add(rendered.Value);
            }

            return results;
        }


        private async Task<RenderedBlock?> RenderBlockAsync(Block block, int indentLevel = 0)
        {
            switch (block)
            {
                case ParagraphBlock paragraph:
                    {
                        var rb = await RenderParagraphAsync(paragraph);
                        if (rb.View is View v) v.Margin = new Thickness(0, 4, 0, 4);
                        return rb;
                    }

                case HeadingBlock heading:
                    {
                        double multiplier = GetHeadingMultiplier(heading.Level);
                        var formatted = RenderInlinesToFormattedString(
                            heading.Inline, fontSize: BaseFontSize * multiplier);

                        foreach (var span in formatted.Spans)
                            span.FontAttributes |= FontAttributes.Bold;

                        var lbl = new Label
                        {
                            FormattedText = formatted,
                            FontAttributes = FontAttributes.Bold,
                            FontFamily = FontFamily,
                            Margin = new Thickness(0, heading.Level <= 2 ? 18 : 12, 0, 4),
                            LineBreakMode = LineBreakMode.WordWrap
                        };

                        ApplyTextColorBinding(lbl);
                        ApplyFontSizeTracking(lbl, multiplier);

                        if (heading.Level <= 2)
                        {
                            var stack = new VerticalStackLayout { Spacing = 4 };
                            stack.Children.Add(lbl);
                            stack.Children.Add(new BoxView
                            {
                                HeightRequest = heading.Level == 1 ? 2 : 1,
                                BackgroundColor = Color.FromArgb("#D0D7DE"),
                                HorizontalOptions = LayoutOptions.Fill
                            });
                            stack.Margin = lbl.Margin;
                            lbl.Margin = Thickness.Zero;
                            return new RenderedBlock(stack, formatted.ToString());
                        }

                        return new RenderedBlock(lbl, formatted.ToString());
                    }

                case MathBlock math:
                    {
                        var latex = math.Lines.ToString();
                        var view = await CreateLatexViewAsync(latex);
                        var wrapper = new ScrollView
                        {
                            Orientation = ScrollOrientation.Horizontal,
                            Content = view,
                            Margin = new Thickness(0, 10, 0, 10)
                        };
                        return new RenderedBlock(wrapper, latex);
                    }

                case CodeBlock code:
                    return await RenderCodeBlockAsync(code);

                case ThematicBreakBlock:
                    {
                        var hr = new BoxView
                        {
                            HeightRequest = 1,
                            BackgroundColor = Color.FromArgb("#D0D7DE"),
                            HorizontalOptions = LayoutOptions.Fill,
                            Margin = new Thickness(0, 12)
                        };
                        return new RenderedBlock(hr, "---");
                    }

                case ListBlock list:
                    {
                        var view = await RenderListAsync(list, indentLevel);
                        view.Margin = new Thickness(0, 4);
                        return new RenderedBlock(view, string.Empty);
                    }

                case QuoteBlock quote:
                    {
                        var view = await RenderQuoteAsync(quote);
                        return new RenderedBlock(view, string.Empty);
                    }

                case Table table:
                    return await RenderTableAsync(table);

                default:
                    {
                        var text = block.ToString() ?? string.Empty;
                        var lbl = new Label
                        {
                            Text = text,
                            FontFamily = FontFamily,
                            LineBreakMode = LineBreakMode.WordWrap
                        };
                        ApplyTextColorBinding(lbl);
                        ApplyFontSizeTracking(lbl);
                        return new RenderedBlock(lbl, text);
                    }
            }
        }

        private async Task<RenderedBlock> RenderParagraphAsync(ParagraphBlock paragraph)
        {
            bool needsViews = paragraph.Inline?.Descendants()
                .Any(i => i is MathInline || (i is LinkInline l && l.IsImage)) ?? false;

            if (!needsViews)
            {
                var formatted = RenderInlinesToFormattedString(
                    paragraph.Inline, fontSize: BaseFontSize);

                var lbl = new Label
                {
                    FormattedText = formatted,
                    FontFamily = FontFamily,
                    LineHeight = 1.4,
                    LineBreakMode = LineBreakMode.WordWrap
                };
                ApplyTextColorBinding(lbl);
                ApplyFontSizeTracking(lbl);
                return new RenderedBlock(lbl, formatted.ToString());
            }

            var container = new FlexLayout
            {
                Direction = Microsoft.Maui.Layouts.FlexDirection.Row,
                Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
                AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center,
                HorizontalOptions = LayoutOptions.Fill
            };

            var sb = new StringBuilder();
            foreach (var cv in await RenderInlinesToViewsAsync(paragraph.Inline))
            {
                container.Children.Add(cv.View);
                sb.Append(cv.Text);
            }

            return new RenderedBlock(container, sb.ToString());
        }


        private async Task<RenderedBlock> RenderCodeBlockAsync(CodeBlock code)
        {
            var codeText = code.Lines.ToString();
            string? lang = (code is FencedCodeBlock fenced)
                ? fenced.Info?.Trim().ToLowerInvariant()
                : null;

            if (lang == "markdown")
            {
                var inner = new VerticalStackLayout();
                foreach (var rb in await BuildBlockViewsAsync(codeText))
                    inner.Children.Add(rb.View);
                return new RenderedBlock(WrapInCodeBorder(inner), codeText);
            }

            var highlightedLabel = new HighlightedLabel
            {
                FormattedText = _colorizer.Highlight(codeText, lang),
                FontSize = BaseFontSize,
                LineBreakMode = LineBreakMode.NoWrap,
                Code = codeText,
                Language = lang
            };

            var scroll = new ScrollView
            {
                Orientation = ScrollOrientation.Horizontal,
                Content = highlightedLabel
            };

            return new RenderedBlock(WrapInCodeBorder(scroll), codeText);
        }

        private ColoredCodeBlock WrapInCodeBorder(View content)
        {
            return new()
            {
                Content = content,
                Padding = new Thickness(14, 10),
                BackgroundColor = CodeBackgroundColor,
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Margin = new Thickness(0, 8, 0, 8)
            };
        }

        private async Task<RenderedBlock> RenderTableAsync(Table table)
        {
            var outerBorder = new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                StrokeThickness = 1,
                Stroke = new SolidColorBrush(Color.FromArgb("#D0D7DE")),
                Margin = new Thickness(0, 8, 0, 8),
                Padding = Thickness.Zero
            };

            var grid = new Grid { ColumnSpacing = 0, RowSpacing = 0 };

            int colCount = table.ColumnDefinitions.Count;
            for (int c = 0; c < colCount; c++)
                grid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int r = 0;
            foreach (TableRow row in table)
            {
                bool isHeader = row.IsHeader;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                int c = 0;
                foreach (TableCell cell in row)
                {
                    if (cell.Count < 1) { c++; continue; }

                    var cellContent = new VerticalStackLayout
                    {
                        Padding = new Thickness(10, 6),
                        Spacing = 2,
                        HorizontalOptions = LayoutOptions.Fill,
                        BackgroundColor = isHeader
                            ? Color.FromArgb("#F6F8FA")
                            : Colors.Transparent
                    };

                    foreach (var cellBlock in cell)
                    {
                        var rendered = await RenderBlockAsync(cellBlock);
                        if (rendered?.View is View cellBlockView)
                        {
                            cellBlockView.Margin = Thickness.Zero;
                            if (isHeader) BoldAllLabels(cellBlockView);
                            cellContent.Children.Add(cellBlockView);
                        }
                    }

                    var cellWrapper = new Grid();
                    cellWrapper.Children.Add(cellContent);

                    if (r < table.Count - 1)
                        cellWrapper.Children.Add(new BoxView
                        {
                            HeightRequest = 1,
                            BackgroundColor = Color.FromArgb("#D0D7DE"),
                            VerticalOptions = LayoutOptions.End
                        });

                    if (c < colCount - 1)
                        cellWrapper.Children.Add(new BoxView
                        {
                            WidthRequest = 1,
                            BackgroundColor = Color.FromArgb("#D0D7DE"),
                            HorizontalOptions = LayoutOptions.End
                        });

                    grid.Add(cellWrapper, c, r);
                    c++;
                }
                r++;
            }

            outerBorder.Content = grid;
            return new RenderedBlock(outerBorder, string.Empty);
        }


        private async Task<View> RenderQuoteAsync(QuoteBlock quote)
        {
            var content = new VerticalStackLayout { Spacing = 2 };
            foreach (var block in quote)
            {
                var rendered = await RenderBlockAsync(block);
                if (rendered?.View != null)
                    content.Children.Add(rendered.Value.View);
            }

            var bar = new BoxView { BackgroundColor = QuoteBarColor };
            var body = new VerticalStackLayout
            {
                Padding = new Thickness(12, 0, 0, 0),
                Children = { content }
            };

            var grid = new Grid
            {
                Margin = new Thickness(0, 8),
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = 4 },
                    new ColumnDefinition { Width = GridLength.Star }
                }
            };
            grid.Add(bar, 0, 0);
            grid.Add(body, 1, 0);
            return grid;
        }

        private async Task<View> RenderListAsync(ListBlock list, int indentLevel = 0)
        {
            var container = new VerticalStackLayout { Spacing = 2 };
            int index = list.IsOrdered && int.TryParse(list.OrderedStart, out int start) ? start : 1;

            foreach (var item in list.OfType<ListItemBlock>())
            {
                var row = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = GridLength.Auto },
                        new ColumnDefinition { Width = GridLength.Star }
                    },
                    ColumnSpacing = 6,
                    Padding = new Thickness(indentLevel * 16, 2, 0, 2)
                };

                bool isTask = false;
                if (item.Count > 0 &&
                    item[0] is ParagraphBlock pb &&
                    pb.Inline?.FirstChild is TaskList task)
                {
                    isTask = true;
                    var cb = new CheckBox
                    {
                        IsChecked = task.Checked,
                        IsEnabled = false,
                        VerticalOptions = LayoutOptions.Start
                    };
                    Grid.SetColumn(cb, 0);
                    row.Children.Add(cb);
                }

                if (!isTask)
                {
                    string marker = list.IsOrdered ? $"{index}." : "•";
                    var bullet = new Label
                    {
                        Text = marker,
                        FontFamily = FontFamily,
                        VerticalTextAlignment = TextAlignment.Start,
                        MinimumWidthRequest = list.IsOrdered ? 28 : 16
                    };
                    ApplyTextColorBinding(bullet);
                    ApplyFontSizeTracking(bullet);
                    Grid.SetColumn(bullet, 0);
                    row.Children.Add(bullet);
                }

                var itemContent = new VerticalStackLayout { Spacing = 0 };
                foreach (var subBlock in item)
                {
                    int nextIndent = subBlock is ListBlock ? indentLevel + 1 : indentLevel;
                    var rendered = await RenderBlockAsync(subBlock, nextIndent);
                    if (rendered?.View != null)
                        itemContent.Children.Add(rendered.Value.View);
                }

                Grid.SetColumn(itemContent, 1);
                row.Children.Add(itemContent);
                container.Children.Add(row);

                if (list.IsOrdered) index++;
            }

            return container;
        }

        /// <summary>
        /// Converts a container of inline elements into a formatted string suitable for display, applying inherited
        /// font attributes, text decorations, color, and font size as specified.
        /// </summary>
        /// <remarks>This method recursively processes nested inline elements, preserving formatting such
        /// as emphasis, code, and links. Tap gesture recognizers are added to link spans to enable navigation when
        /// tapped.</remarks>
        /// <param name="container">The container of inline elements to render. If null, an empty formatted string is returned.</param>
        /// <param name="inheritedAttributes">The font attributes to apply to the rendered text, such as bold or italic. These attributes are inherited by
        /// child elements unless overridden.</param>
        /// <param name="inheritedDecorations">The text decorations to apply to the rendered text, such as underline or strikethrough. These decorations
        /// are inherited by child elements unless overridden.</param>
        /// <param name="inheritedColor">The text color to apply to the rendered text. If null, the default color is used.</param>
        /// <param name="fontSize">The font size to apply to the rendered text. If null, the base font size is used.</param>
        /// <returns>A formatted string representing the rendered inline elements, with appropriate font attributes, decorations,
        /// color, and font size applied. Returns an empty formatted string if the container is null or contains no
        /// elements.</returns>
        private FormattedString RenderInlinesToFormattedString(
            ContainerInline? container,
            FontAttributes inheritedAttributes = FontAttributes.None,
            TextDecorations inheritedDecorations = TextDecorations.None,
            Color? inheritedColor = null,
            double? fontSize = null)
        {
            var formatted = new FormattedString();
            if (container == null) return formatted;

            double size = fontSize ?? BaseFontSize;

            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        {
                            formatted.Spans.Add(new Span
                            {
                                Text = literal.Content.ToString(),
                                FontSize = size,
                                FontAttributes = inheritedAttributes,
                                TextDecorations = inheritedDecorations,
                                TextColor = inheritedColor
                            });
                            break;
                        }

                    case LineBreakInline lb:
                        formatted.Spans.Add(new Span
                        {
                            Text = lb.IsHard ? "\n" : " ",
                            FontSize = size
                        });
                        break;

                    case EmphasisInline emphasis:
                        {
                            var attrs = inheritedAttributes;
                            var decs = inheritedDecorations;

                            if (emphasis.DelimiterChar is '*' or '_')
                            {
                                if (emphasis.DelimiterCount == 1) attrs |= FontAttributes.Italic;
                                else if (emphasis.DelimiterCount >= 2) attrs |= FontAttributes.Bold;
                            }
                            else if (emphasis.DelimiterChar == '~' && emphasis.DelimiterCount == 2)
                                decs |= TextDecorations.Strikethrough;

                            var inner = RenderInlinesToFormattedString(
                                emphasis, attrs, decs, inheritedColor, size);
                            foreach (var s in inner.Spans) formatted.Spans.Add(s);
                            break;
                        }

                    case CodeInline code:
                        formatted.Spans.Add(new Span
                        {
                            Text = $"\u202F{code.Content}\u202F",
                            FontSize = size,
                            FontFamily = MonospaceFont,
                            BackgroundColor = CodeBackgroundColor
                        });
                        break;

                    case LinkInline link when !link.IsImage:
                        {
                            var inner = RenderInlinesToFormattedString(
                                link,
                                inheritedAttributes,
                                inheritedDecorations | TextDecorations.Underline,
                                Color.FromArgb("#1A73E8"),
                                size);

                            foreach (var span in inner.Spans)
                            {
                                if (!string.IsNullOrEmpty(link.Url))
                                {
                                    var gesture = new TapGestureRecognizer();
                                    var url = link.Url;
                                    gesture.Tapped += async (_, _) =>
                                        await Launcher.Default.OpenAsync(url);
                                    span.GestureRecognizers.Add(gesture);
                                }
                                formatted.Spans.Add(span);
                            }
                            break;
                        }

                    case AutolinkInline autolink:
                        {
                            var span = new Span
                            {
                                Text = autolink.Url,
                                FontSize = size,
                                TextColor = Color.FromArgb("#1A73E8"),
                                TextDecorations = TextDecorations.Underline
                            };
                            var gesture = new TapGestureRecognizer();
                            var url = autolink.Url;
                            gesture.Tapped += async (_, _) =>
                                await Launcher.Default.OpenAsync(url);
                            span.GestureRecognizers.Add(gesture);
                            formatted.Spans.Add(span);
                            break;
                        }

                    case ContainerInline ci:
                        {
                            var inner = RenderInlinesToFormattedString(
                                ci, inheritedAttributes, inheritedDecorations, inheritedColor, size);
                            foreach (var s in inner.Spans) formatted.Spans.Add(s);
                            break;
                        }
                }
            }

            return formatted;
        }

        private async Task<List<RenderedBlock>> RenderInlinesToViewsAsync(
            ContainerInline? container)
        {
            var views = new List<RenderedBlock>();
            if (container == null) return views;

            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LineBreakInline lb:
                        {
                            var lbl = new Label
                            {
                                Text = lb.IsHard ? "\n" : " ",
                                FontFamily = FontFamily
                            };
                            ApplyTextColorBinding(lbl);
                            views.Add(new RenderedBlock(lbl, lb.IsHard ? "\n" : " "));
                            break;
                        }

                    case LiteralInline lit:
                        {
                            var text = lit.Content.ToString();

                            if (views.Count > 0 &&
                                views[^1].View is Label prev &&
                                prev.GestureRecognizers.Count == 0 &&
                                prev.FontFamily == FontFamily)
                            {
                                prev.Text += text;
                                views[^1] = new RenderedBlock(prev, views[^1].Text + text);
                            }
                            else
                            {
                                var lbl = new Label
                                {
                                    Text = text,
                                    FontFamily = FontFamily,
                                    LineBreakMode = LineBreakMode.WordWrap
                                };
                                ApplyTextColorBinding(lbl);
                                ApplyFontSizeTracking(lbl);
                                views.Add(new RenderedBlock(lbl, text));
                            }
                            break;
                        }

                    case EmphasisInline emp:
                        {
                            var inner = await RenderInlinesToViewsAsync(emp);
                            foreach (var cv in inner)
                            {
                                if (cv.View is Label lbl)
                                {
                                    if (emp.DelimiterChar is '*' or '_')
                                        lbl.FontAttributes |= emp.DelimiterCount >= 2
                                            ? FontAttributes.Bold
                                            : FontAttributes.Italic;
                                    else if (emp.DelimiterChar == '~' && emp.DelimiterCount == 2)
                                        lbl.TextDecorations |= TextDecorations.Strikethrough;
                                }
                                views.Add(cv);
                            }
                            break;
                        }

                    case CodeInline code:
                        {
                            var lbl = new Label
                            {
                                Text = code.Content,
                                FontFamily = MonospaceFont,
                                LineBreakMode = LineBreakMode.WordWrap
                            };
                            ApplyFontSizeTracking(lbl);
                            views.Add(new RenderedBlock(
                                new Border
                                {
                                    Content = lbl,
                                    Padding = new Thickness(5, 2),
                                    BackgroundColor = CodeBackgroundColor,
                                    StrokeShape = new RoundRectangle { CornerRadius = 4 }
                                },
                                code.Content));
                            break;
                        }

                    case LinkInline img when img.IsImage:
                        {
                            var image = new Image
                            {
                                Source = ImageSource.FromUri(new Uri(img.Url ?? string.Empty)),
                                Aspect = Aspect.AspectFit,
                                WidthRequest = 300,
                                HeightRequest = 200
                            };
                            views.Add(new RenderedBlock(image, img.Url ?? string.Empty));
                            break;
                        }

                    case LinkInline link:
                        {
                            var inner = await RenderInlinesToViewsAsync(link);
                            foreach (var cv in inner)
                            {
                                if (cv.View is Label lbl)
                                {
                                    lbl.TextColor = Color.FromArgb("#1A73E8");
                                    lbl.TextDecorations = TextDecorations.Underline;
                                    var gesture = new TapGestureRecognizer();
                                    var url = link.Url;
                                    gesture.Tapped += async (_, _) =>
                                    {
                                        if (!string.IsNullOrEmpty(url))
                                            await Launcher.Default.OpenAsync(url);
                                    };
                                    lbl.GestureRecognizers.Add(gesture);
                                }
                                views.Add(cv);
                            }
                            break;
                        }

                    case AutolinkInline autolink:
                        {
                            var lbl = new Label
                            {
                                Text = autolink.Url,
                                FontFamily = FontFamily,
                                TextColor = Color.FromArgb("#1A73E8"),
                                TextDecorations = TextDecorations.Underline
                            };
                            var gesture = new TapGestureRecognizer();
                            var url = autolink.Url;
                            gesture.Tapped += async (_, _) =>
                                await Launcher.Default.OpenAsync(url);
                            lbl.GestureRecognizers.Add(gesture);
                            views.Add(new RenderedBlock(lbl, autolink.Url));
                            break;
                        }

                    case MathInline math:
                        {
                            var latex = math.Content.ToString();
                            var view = await CreateLatexViewAsync(latex);
                            views.Add(new RenderedBlock(view, latex));
                            break;
                        }

                    case ContainerInline ci:
                        views.AddRange(await RenderInlinesToViewsAsync(ci));
                        break;
                }
            }

            return views;
        }


        private async Task<LatexImage> CreateLatexViewAsync(string latex)
        {
            var (source, w, h) = RenderLatexToSource(latex);
            return new LatexImage
            {
                Source = source,
                WidthRequest = w,
                HeightRequest = h,
                Latex = latex,
                Aspect = Aspect.AspectFit
            };
        }

        private (ImageSource source, float width, float height) RenderLatexToSource(string latex)
        {
            _painter.LaTeX = PreprocessLatex(latex);
            var size = _painter.Measure();
            float w = MathF.Ceiling(size.Width) + 6;
            float h = MathF.Ceiling(size.Height) + 6;

            var info = new SKImageInfo((int)w, (int)h, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            _painter.Draw(canvas);

            using var image = surface.Snapshot();
            using var skData = image.Encode(SKEncodedImageFormat.Png, 100);
            var bytes = skData.ToArray(); // captured in closure — no disposal issue

            return (ImageSource.FromStream(() => new MemoryStream(bytes)), w, h);
        }


        /// <summary>
        /// Preprocesses a LaTeX string by normalizing certain commands and removing or rewriting specific environments
        /// and formatting elements.
        /// </summary>
        /// <remarks>This method replaces or removes various LaTeX commands and environments to produce a
        /// more uniform and simplified LaTeX string. It is useful for preparing LaTeX input for further parsing or
        /// rendering.</remarks>
        /// <param name="latex">The LaTeX input string to preprocess. Cannot be null or whitespace.</param>
        /// <returns>A processed LaTeX string with standardized commands and cleaned formatting. Returns the original string if
        /// it is null or whitespace.</returns>
        private static string PreprocessLatex(string latex)
        {
            if (string.IsNullOrWhiteSpace(latex)) return latex;

            latex = ReplaceCommandName(latex, "cfrac", "frac");
            latex = ReplaceCommandName(latex, "dfrac", "frac");
            latex = ReplaceCommandName(latex, "tfrac", "frac");
            latex = ReplaceCommandName(latex, "boldsymbol", "mathbf");
            latex = ReplaceCommandName(latex, "bm", "mathbf");
            latex = ReplaceCommandName(latex, "operatorname*", "text");
            latex = ReplaceCommandName(latex, "operatorname", "text");

            latex = RewriteExtensibleArrow(latex, "xrightarrow", "rightarrow");
            latex = RewriteExtensibleArrow(latex, "xleftarrow", "leftarrow");
            latex = RewriteExtensibleArrow(latex, "xmapsto", "mapsto");

            latex = RewriteArrayEnvironment(latex);
            latex = latex.Replace(@"\hline", "");

            latex = Regex.Replace(latex, @"\\begin\{align\*?\}", @"\begin{aligned}");
            latex = Regex.Replace(latex, @"\\end\{align\*?\}", @"\end{aligned}");
            latex = Regex.Replace(latex, @"\\begin\{equation\*?\}", "");
            latex = Regex.Replace(latex, @"\\end\{equation\*?\}", "");

            latex = StripCommandWithArg(latex, "DeclareMathOperator");
            latex = StripCommandWithArg(latex, "label");
            latex = StripCommandWithArg(latex, "tag");

            latex = latex.Replace(@"\nonumber", "");
            latex = latex.Replace(@"\allowbreak", "");
            latex = latex.Replace(@"\!", "");

            return latex.Trim();
        }

        private static string ReplaceCommandName(string latex, string oldCmd, string newCmd) =>
            Regex.Replace(
                latex,
                @"\\" + Regex.Escape(oldCmd) + @"(?=[^a-zA-Z]|$)",
                @"\" + newCmd);

        private static string RewriteExtensibleArrow(string latex, string cmd, string baseArrow)
        {
            var sb = new StringBuilder();
            int i = 0;
            string trigger = @"\" + cmd;

            while (i < latex.Length)
            {
                int idx = latex.IndexOf(trigger, i, StringComparison.Ordinal);
                if (idx < 0) { sb.Append(latex, i, latex.Length - i); break; }

                int afterCmd = idx + trigger.Length;
                if (afterCmd < latex.Length &&
                    char.IsLetter(latex[afterCmd]) &&
                    latex[afterCmd] != '{' && latex[afterCmd] != '[')
                {
                    sb.Append(latex, i, afterCmd - i);
                    i = afterCmd;
                    continue;
                }

                sb.Append(latex, i, idx - i);
                int pos = afterCmd;

                if (pos < latex.Length && latex[pos] == '[')
                {
                    int depth = 1; pos++;
                    while (pos < latex.Length && depth > 0)
                    {
                        if (latex[pos] == '[') depth++;
                        else if (latex[pos] == ']') depth--;
                        pos++;
                    }
                }

                string sup = "";
                if (pos < latex.Length && latex[pos] == '{')
                {
                    int start = pos + 1, depth = 1; pos++;
                    while (pos < latex.Length && depth > 0)
                    {
                        if (latex[pos] == '{') depth++;
                        else if (latex[pos] == '}') depth--;
                        pos++;
                    }
                    sup = latex.Substring(start, pos - start - 1);
                }

                sb.Append(string.IsNullOrEmpty(sup)
                    ? @"\" + baseArrow
                    : $@"\overset{{{sup}}}{{\{baseArrow}}}");

                i = pos;
            }

            return sb.ToString();
        }

        private static string RewriteArrayEnvironment(string latex)
        {
            latex = Regex.Replace(latex, @"\\begin\{array\*?\}\s*\{[^}]*\}", @"\begin{matrix}");
            latex = Regex.Replace(latex, @"\\end\{array\*?\}", @"\end{matrix}");
            return latex;
        }

        private static string StripCommandWithArg(string latex, string cmd)
        {
            var sb = new StringBuilder();
            int i = 0;
            string trigger = @"\" + cmd + "{";

            while (i < latex.Length)
            {
                int idx = latex.IndexOf(trigger, i, StringComparison.Ordinal);
                if (idx < 0) { sb.Append(latex, i, latex.Length - i); break; }

                sb.Append(latex, i, idx - i);
                int pos = idx + trigger.Length, depth = 1;
                while (pos < latex.Length && depth > 0)
                {
                    if (latex[pos] == '{') depth++;
                    else if (latex[pos] == '}') depth--;
                    pos++;
                }
                i = pos;
            }

            return sb.Length == 0 ? latex : sb.ToString();
        }


        //Bindables/Updates--------------------------------------------------------------

        private async Task UpdateLatexImagesAsync(Color newColor)
        {
            _painter.TextColor = newColor.ToSKColor();

            foreach (var blockView in _blockViews)
            {
                if (blockView is not View view) continue;

                foreach (var img in view.GetVisualTreeDescendants().OfType<LatexImage>())
                {
                    var (source, w, h) = RenderLatexToSource(img.Latex);
                    img.Source = source;
                    img.WidthRequest = w;
                    img.HeightRequest = h;
                }

                await Task.Yield();
            }
        }

        private async Task UpdateCodeBackgroundColorAsync()
        {

            foreach (var blockView in _blockViews)
            {
                if (blockView is not View view) continue;

                foreach (var block in view.GetVisualTreeDescendants().OfType<ColoredCodeBlock>())
                {
                    block.BackgroundColor = CodeBackgroundColor;
                    if (block.Content != null)
                    {
                        foreach (var lbl in block.Content.GetVisualTreeDescendants().OfType<HighlightedLabel>())
                        {
                            lbl.FormattedText = _colorizer.Highlight(lbl.Code, lbl.Language);
                        }
                    }
                }

                await Task.Yield();
            }
        }


        private void ApplyTextColorBinding(Label label) =>
            label.SetBinding(Label.TextColorProperty,
                new Binding(nameof(TextColor), source: this));

        private void ApplyFontSizeTracking(Label label, double multiplier = 1.0)
        {
            double size = BaseFontSize * multiplier;
            label.FontSize = size;
        }


        private static void BoldAllLabels(View root)
        {
            if (root is Label lbl)
            {
                lbl.FontAttributes |= FontAttributes.Bold;
                if (lbl.FormattedText != null)
                    foreach (var span in lbl.FormattedText.Spans)
                        span.FontAttributes |= FontAttributes.Bold;
                return;
            }

            IEnumerable<IView>? children = root switch
            {
                Layout layout => layout.Children,
                ContentView cv => cv.Content is View v1 ? new[] { v1 } : null,
                Border b => b.Content is View v2 ? new[] { v2 } : null,
                ScrollView sv => sv.Content is View v3 ? new[] { v3 } : null,
                _ => null
            };

            if (children == null) return;
            foreach (var child in children.OfType<View>())
                BoldAllLabels(child);
        }


        private static double GetHeadingMultiplier(int level) => level switch
        {
            1 => 2.0,
            2 => 1.6,
            3 => 1.3,
            4 => 1.1,
            _ => 1.0
        };
    }
}