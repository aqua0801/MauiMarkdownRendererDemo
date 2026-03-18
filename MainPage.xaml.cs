
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using System.Diagnostics;

namespace MauiMarkdownRendererWithLaTeX
{
    public partial class MainPage : ContentPage
    {
        private readonly Stopwatch sw = new ();

        public MainPage()
        {
            InitializeComponent();
        }

        public async void OnSubmitButtonClicked(object sender,EventArgs e)
        {
            var raw = mdEditor.Text;
            mdr.AppendThrottle = TimeSpan.Zero;

            if (String.IsNullOrWhiteSpace(raw))
                return;
            sw.Restart();

            void OnRenderFinished(object? sender , MarkdownRenderer.RenderType type)
            {
                if(type==MarkdownRenderer.RenderType.FullRender)
                {
                    sw.Stop();
                    var duration = MathF.Round(sw.ElapsedMilliseconds, 2);
                    var toast = Toast.Make($"Total time usage : {duration} ms", ToastDuration.Short, 14);
                    toast.Show();
                    mdr.RenderFinished -= OnRenderFinished;
                }
            }

            mdr.Clear();
            mdr.RenderFinished += OnRenderFinished;

            var inputs = new List<string>();

            if (incrementalSwitch.IsToggled && int.TryParse(chunkSizeEntry.Text,out var size))
            {
                inputs.AddRange(
                    raw.Chunk(size)
                    .Select(x => new String(x))
                    );
            }
            else
                inputs.Add(raw);


            foreach (var chunk in inputs) 
            {
                mdr.Append(chunk);
                await Task.Yield();
            }

            mdr.Text = raw;

        }

        public void OnBaseFontSizeChanged(object sender, EventArgs e)
        {
            if (sender is not Slider slider)
                return;

            mdr.BaseFontSize = slider.Value;
        }

        public void OnLatexFontSizeChanged(object sender, EventArgs e)
        {
            if (sender is not Slider slider)
                return;

            mdr.LatexFontSize = slider.Value;
        }

        public void OnColorUnfocused(object sender, EventArgs e)
        {
            if (sender is not Entry entry)
                return;

            if(Color.TryParse(entry.Text,out var color))
            {
                if(entry==textColorEntry)
                    mdr.TextColor = color;
                else if(entry==latexColorEntry)
                    mdr.LatexTextColor = color;
            }
        }

        public void OnThrottleUnfocused(object sender, EventArgs e)
        {
            if (sender is not Entry entry)
                return;

            if(int.TryParse(entry.Text , out var ms))
            {
                mdr.AppendThrottle = TimeSpan.FromMilliseconds(ms);
            }

        }

    }
}
