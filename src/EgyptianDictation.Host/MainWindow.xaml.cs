using System.Windows;
using EgyptianDictation.Contracts;

namespace EgyptianDictation.Host;

public partial class MainWindow : Window
{
    private readonly MicrophoneDictationController _controller = new();
    private readonly HostPipeBridge _pipeBridge;

    public MainWindow()
    {
        InitializeComponent();
        AttributionText.Text = ProductAttribution.FullNotice;
        _pipeBridge = new HostPipeBridge(_controller);
        _controller.StatusChanged += status => Dispatcher.Invoke(() => StatusText.Text = status);
        _controller.TextCommitted += (_, text, metrics, newParagraph) => Dispatcher.Invoke(() =>
        {
            if (newParagraph) TranscriptBox.AppendText(Environment.NewLine);
            if (TranscriptBox.Text.Length > 0 && !char.IsWhiteSpace(TranscriptBox.Text[^1]))
                TranscriptBox.AppendText(" ");
            TranscriptBox.AppendText(text);
            TranscriptBox.ScrollToEnd();
            MetricsText.Text = $"RTF: {metrics.RealTimeFactor:F2} | صوت: {metrics.AudioSeconds:F1} ث | معالجة: {metrics.InferenceSeconds:F1} ث";
            DiagnosticsText.Text = $"المحرك: Cohere Transcribe Arabic 07-2026 | Q5_K_M | {metrics.Backend} | " +
                $"النموذج: محمل | خيوط CPU: {metrics.WorkerThreads} | تحميل: {metrics.ModelLoadSeconds:F1} ث | " +
                $"ذاكرة العامل: {metrics.WorkingSetBytes / 1024d / 1024d:F0} MB | المسار: {metrics.ModelPath}";
        });
        Closed += async (_, _) =>
        {
            await _pipeBridge.DisposeAsync();
            await _controller.DisposeAsync();
        };
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartButton.IsEnabled = false;
            await _controller.StartAsync();
            StopButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "خطأ";
            MessageBox.Show(this, ex.Message, "تعذر بدء الإملاء", MessageBoxButton.OK, MessageBoxImage.Error);
            StartButton.IsEnabled = true;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _controller.Stop();
        StopButton.IsEnabled = false;
        StartButton.IsEnabled = true;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TranscriptBox.Text))
            Clipboard.SetText(TranscriptBox.Text);
    }
}
