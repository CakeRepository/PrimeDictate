using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using MediaColor = System.Windows.Media.Color;

namespace PrimeDictate;

internal partial class TranscriptionOverlayWindow : Window
{
    private const double FullOverlayWidth = 500;
    private const double FullOverlayHeight = 400;
    private const double CompactOverlaySize = 108;
    private const double FullMicSize = 80;
    private const double CompactMicSize = 62;
    private const double FullGlowSize = 250;
    private const double CompactGlowSize = 150;
    private const int MaxDisplayedTranscriptChars = 900;
    private const int WaveformBarCount = 90;
    private const int ParticleCount = 150;
    private static readonly SolidColorBrush AccentBrush = CreateFrozenBrush(0, 122, 204);
    private static readonly SolidColorBrush ReadyBrush = CreateFrozenBrush(32, 164, 112);
    private static readonly SolidColorBrush RecordingBrush = CreateFrozenBrush(220, 53, 69);
    
    private readonly Random random = new Random();
    private readonly System.Windows.Shapes.Rectangle[] leftBars = new System.Windows.Shapes.Rectangle[WaveformBarCount];
    private readonly System.Windows.Shapes.Rectangle[] rightBars = new System.Windows.Shapes.Rectangle[WaveformBarCount];
    private readonly double[] barTargets = new double[WaveformBarCount];
    private readonly double[] barCurrents = new double[WaveformBarCount];
    
    private readonly System.Windows.Shapes.Ellipse[] pShapes = new System.Windows.Shapes.Ellipse[ParticleCount];
    private readonly double[] pX = new double[ParticleCount];
    private readonly double[] pY = new double[ParticleCount];
    private readonly double[] pVX = new double[ParticleCount];
    private readonly double[] pVY = new double[ParticleCount];
    private readonly double[] pLife = new double[ParticleCount];

    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer visualizerTimer;
    private DateTime startTime;
    private double currentSmoothedRms;
    private double targetRms;
    private OverlayMode overlayMode = OverlayMode.CompactMicrophone;
    private SolidColorBrush currentStateBrush = ReadyBrush;

    public TranscriptionOverlayWindow()
    {
        InitializeComponent();
        this.SourceInitialized += this.OnSourceInitialized;
        
        this.timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        this.timer.Tick += this.OnTimerTick;

        this.visualizerTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        this.visualizerTimer.Tick += this.OnVisualizerTick;

        this.InitializeWaveform();
        this.SetOverlayMode(OverlayMode.CompactMicrophone);
    }

    private void InitializeWaveform()
    {
        var gradientRight = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 0)
        };
        gradientRight.GradientStops.Add(new GradientStop(MediaColor.FromRgb(0, 122, 204), 0.0));
        gradientRight.GradientStops.Add(new GradientStop(MediaColor.FromRgb(163, 62, 255), 1.0));
        gradientRight.Freeze();
        
        var gradientLeft = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(1, 0),
            EndPoint = new System.Windows.Point(0, 0)
        };
        gradientLeft.GradientStops.Add(new GradientStop(MediaColor.FromRgb(0, 122, 204), 0.0));
        gradientLeft.GradientStops.Add(new GradientStop(MediaColor.FromRgb(163, 62, 255), 1.0));
        gradientLeft.Freeze();

        for (int i = 0; i < WaveformBarCount; i++)
        {
            // Drop opacity to 0.3 at edges instead of letting it fully disappear
            double opacity = 1.0 - (((double)i / WaveformBarCount) * 0.7);
            
            var leftBar = new System.Windows.Shapes.Rectangle
            {
                Width = 2,
                Height = 2,
                RadiusX = 1,
                RadiusY = 1,
                Fill = gradientLeft,
                Margin = new Thickness(0, 0, 2, 0),
                Opacity = opacity,
                VerticalAlignment = VerticalAlignment.Center
            };
            this.leftBars[i] = leftBar;
            this.LeftWaveform.Children.Add(leftBar);

            var rightBar = new System.Windows.Shapes.Rectangle
            {
                Width = 2,
                Height = 2,
                RadiusX = 1,
                RadiusY = 1,
                Fill = gradientRight,
                Margin = new Thickness(0, 0, 2, 0),
                Opacity = opacity,
                VerticalAlignment = VerticalAlignment.Center
            };
            this.rightBars[i] = rightBar;
            this.RightWaveform.Children.Add(rightBar);
        }

        var particleBrush = new SolidColorBrush(MediaColor.FromRgb(100, 200, 255));
        particleBrush.Freeze();
        for (int i = 0; i < ParticleCount; i++)
        {
            this.ResetParticle(i);
            
            this.pShapes[i] = new System.Windows.Shapes.Ellipse
            {
                Width = 2 + (this.random.NextDouble() * 2),
                Height = 2 + (this.random.NextDouble() * 2),
                Fill = particleBrush,
                Opacity = 0,
                IsHitTestVisible = false
            };
            // Start off-screen initially
            System.Windows.Controls.Canvas.SetLeft(this.pShapes[i], -100);
            System.Windows.Controls.Canvas.SetTop(this.pShapes[i], -100);
            this.ParticleCanvas.Children.Add(this.pShapes[i]);
        }
    }

    private void ResetParticle(int i)
    {
        double frameWidth = this.ActualWidth > 0 ? this.ActualWidth : 500;
        double halfW = frameWidth / 2.0;
        double centerY = this.ParticleCanvas.ActualHeight > 0 ? this.ParticleCanvas.ActualHeight / 2.0 : 60.0;

        // Spread fully across the horizontal line width uniformly
        this.pX[i] = (this.random.NextDouble() * frameWidth);
        this.pY[i] = centerY + ((this.random.NextDouble() - 0.5) * 4.0); // tightly on the line

        // Exact straight up/down
        this.pVX[i] = 0.0; 
        
        // Shoot UP or DOWN
        bool goesUp = this.random.NextDouble() > 0.5;
        this.pVY[i] = (goesUp ? -1.0 : 1.0) * (0.8 + this.random.NextDouble() * 3.0);
        this.pLife[i] = 0.5 + (this.random.NextDouble() * 0.5);
    }

    public void SetSticky(bool isSticky)
    {
        this.PinButton.IsChecked = isSticky;
    }

    public void SetOverlayMode(OverlayMode overlayMode)
    {
        this.overlayMode = overlayMode;
        bool isCompact = overlayMode == OverlayMode.CompactMicrophone;

        this.HeaderPanel.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        this.TranscriptCard.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        this.FooterPanel.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        this.CompactStatusBadge.Visibility = isCompact ? Visibility.Visible : Visibility.Collapsed;
        this.LeftWaveform.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        this.RightWaveform.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;
        this.ParticleCanvas.Visibility = isCompact ? Visibility.Collapsed : Visibility.Visible;

        this.HeaderRow.Height = isCompact ? new GridLength(0) : GridLength.Auto;
        this.SpacerRow.Height = isCompact ? new GridLength(0) : new GridLength(12);
        this.FooterRow.Height = isCompact ? new GridLength(0) : GridLength.Auto;
        this.TranscriptRow.Height = isCompact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        this.VisualizationRow.Height = isCompact ? new GridLength(1, GridUnitType.Star) : new GridLength(120);

        this.ContentPanel.Margin = isCompact ? new Thickness(10) : new Thickness(16, 8, 16, 16);
        this.VisualizationPanel.Margin = isCompact ? new Thickness(0) : new Thickness(-16, 0, -16, 0);
        this.OverlayBorder.CornerRadius = isCompact ? new CornerRadius(28) : new CornerRadius(12);
        this.ResizeMode = isCompact ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;

        if (isCompact)
        {
            this.Width = CompactOverlaySize;
            this.Height = CompactOverlaySize;
            this.MinWidth = CompactOverlaySize;
            this.MaxWidth = CompactOverlaySize;
            this.MinHeight = CompactOverlaySize;
            this.MaxHeight = CompactOverlaySize;
        }
        else
        {
            this.Width = FullOverlayWidth;
            this.Height = FullOverlayHeight;
            this.MinWidth = 360;
            this.MaxWidth = double.PositiveInfinity;
            this.MinHeight = 280;
            this.MaxHeight = double.PositiveInfinity;
        }

        this.ConfigureMicVisuals(isCompact);
        this.ApplyStateBrush();

        if (isCompact)
        {
            this.PositionCompactInLowerRight();
        }
    }

    public void SetReadyState(string backendLabel)
    {
        var statusText = $"Ready [{backendLabel}]";
        this.HeaderText.Text = statusText;
        this.currentStateBrush = ReadyBrush;
        this.ApplyStateBrush();
        this.TranscriptText.Text = "Waiting for hotkey...";
        this.ToolTip = statusText;
        this.TimerText.Text = "00:00";
        this.timer.Stop();
        this.visualizerTimer.Stop();
        this.UpdateAudioLevel(0);
        this.OnVisualizerTick(null, EventArgs.Empty);
    }

    public void UpdateAudioLevel(double rms)
    {
        this.targetRms = rms;
    }

    private void OnVisualizerTick(object? sender, EventArgs e)
    {
        // Decay target so it naturally zeroes out
        this.targetRms *= 0.8;
        
        // Smooth out the animation
        this.currentSmoothedRms = (this.currentSmoothedRms * 0.7) + (this.targetRms * 0.3);
        
        // Base scales when silent
        double minScale = 1.0;
        double targetScale1 = minScale + (this.currentSmoothedRms * 4.0);
        double targetScale2 = minScale + (this.currentSmoothedRms * 8.0);
        
        double targetOpacity1 = this.currentSmoothedRms > 0.01 ? 0.6 : 0.0;
        double targetOpacity2 = this.currentSmoothedRms > 0.05 ? 0.4 : 0.0;

        this.Ring1Scale.ScaleX = targetScale1;
        this.Ring1Scale.ScaleY = targetScale1;
        this.Ring1.Opacity = targetOpacity1;

        this.Ring2Scale.ScaleX = targetScale2;
        this.Ring2Scale.ScaleY = targetScale2;
        this.Ring2.Opacity = targetOpacity2;

        this.UpdateWaveformBounds();
    }

    private void UpdateWaveformBounds()
    {
        double dbIntensity = this.currentSmoothedRms * 400.0;
        
        // Shift history directly to create an undeniable outward physical scroll
        // No sine waves or ripples that could cause optical illusions
        for (int i = WaveformBarCount - 1; i > 0; i--)
        {
            this.barTargets[i] = this.barTargets[i - 1];
        }
        
        // The newest sample is placed exactly at the microphone (center)
        this.barTargets[0] = 3.0 + (dbIntensity * (1.0 + (this.random.NextDouble() * 0.4)));

        for (int i = 0; i < WaveformBarCount; i++)
        {
            double normalized = (double)i / WaveformBarCount;
            // Smooth fade to the edges
            double fadeDist = Math.Pow(1.0 - normalized, 1.2);
            double maxDistHeight = 120.0 * fadeDist;

            // Extremely fast responsive rise, so the scrolling is very literal
            double moveSpeed = this.barTargets[i] > this.barCurrents[i] ? 0.8 : 0.4;
            this.barCurrents[i] += (this.barTargets[i] - this.barCurrents[i]) * moveSpeed;
            
            double finalHeight = this.barCurrents[i] * fadeDist;
            if (finalHeight > maxDistHeight) finalHeight = maxDistHeight;
            if (finalHeight < 3.0) finalHeight = 3.0;

            this.leftBars[i].Height = finalHeight;
            this.rightBars[i].Height = finalHeight;
        }

        double particleIntensityBoost = dbIntensity * 0.05;
        for (int i = 0; i < ParticleCount; i++)
        {
            // Pure vertical drift
            this.pY[i] += this.pVY[i] + (this.pVY[i] * particleIntensityBoost);
            
            this.pLife[i] -= 0.01 + (particleIntensityBoost * 0.005);
            if (this.pLife[i] <= 0 || this.pY[i] < -20 || this.pY[i] > this.ParticleCanvas.ActualHeight + 20)
            {
                this.ResetParticle(i);
            }

            System.Windows.Controls.Canvas.SetLeft(this.pShapes[i], this.pX[i]);
            System.Windows.Controls.Canvas.SetTop(this.pShapes[i], this.pY[i]);
            
            double brightness = this.pLife[i] * (0.3 + (dbIntensity * 0.02));
            if (brightness > 1.0) brightness = 1.0;
            this.pShapes[i].Opacity = brightness;
        }
    }

    public void UpdateTranscript(string transcript, bool isProcessing, string backendLabel)
    {
        var statusText = isProcessing
            ? $"Processing [{backendLabel}]"
            : $"Listening [{backendLabel}]";
        this.HeaderText.Text = statusText;
        this.currentStateBrush = isProcessing ? ReadyBrush : RecordingBrush;
        this.ApplyStateBrush();
        var displayTranscript = transcript.Trim();
        if (displayTranscript.Length > MaxDisplayedTranscriptChars)
        {
            displayTranscript = "..." + displayTranscript[^MaxDisplayedTranscriptChars..];
        }

        this.TranscriptText.Text = string.IsNullOrWhiteSpace(displayTranscript)
            ? $"Listening with {backendLabel}..."
            : displayTranscript;
        this.ToolTip = statusText;
             
        if (!isProcessing && !this.timer.IsEnabled)
        {
            this.startTime = DateTime.Now;
            this.timer.Start();
            this.visualizerTimer.Start();
        }
        else if (isProcessing && this.timer.IsEnabled)
        {
            this.timer.Stop();
            this.visualizerTimer.Stop();
            UpdateAudioLevel(0); // Zero out visualizer
            this.OnVisualizerTick(null, EventArgs.Empty);
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (this.overlayMode == OverlayMode.CompactMicrophone)
        {
            this.PositionCompactInLowerRight();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        this.timer.Stop();
        this.timer.Tick -= this.OnTimerTick;
        this.visualizerTimer.Stop();
        this.visualizerTimer.Tick -= this.OnVisualizerTick;
        this.SourceInitialized -= this.OnSourceInitialized;
        base.OnClosed(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle);
        // Excluded: NativeMethods.WsExTransparent -> to allow hit testing (mouse clicks/drag)
        var newStyle = new IntPtr(style.ToInt64() |
            NativeMethods.WsExNoActivate |
            NativeMethods.WsExToolWindow);
        _ = NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, newStyle);

        if (this.overlayMode == OverlayMode.CompactMicrophone)
        {
            this.PositionCompactInLowerRight();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.Now - this.startTime;
        this.TimerText.Text = $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
    }

    private void OnBorderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        this.DragMove();
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        bool isPinned = this.PinButton.IsChecked == true;
        if (System.Windows.Application.Current is App app)
        {
            app.SaveStickyState(isPinned);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            app.ShowSettings();
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        this.Hide();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(this.TranscriptText.Text);
        }
        catch
        {
            // Ignore clipboard errors
        }
    }

    private void ConfigureMicVisuals(bool isCompact)
    {
        double micSize = isCompact ? CompactMicSize : FullMicSize;
        double glowSize = isCompact ? CompactGlowSize : FullGlowSize;
        double glyphFontSize = isCompact ? 28 : 34;

        this.GlowEllipse.Width = glowSize;
        this.GlowEllipse.Height = glowSize;
        this.Ring1.Width = micSize;
        this.Ring1.Height = micSize;
        this.Ring2.Width = micSize;
        this.Ring2.Height = micSize;
        this.Ring1Scale.CenterX = micSize / 2.0;
        this.Ring1Scale.CenterY = micSize / 2.0;
        this.Ring2Scale.CenterX = micSize / 2.0;
        this.Ring2Scale.CenterY = micSize / 2.0;
        this.MicCircle.Width = micSize;
        this.MicCircle.Height = micSize;
        this.MicGlyph.FontSize = glyphFontSize;
        this.MicGlyph.Margin = isCompact ? new Thickness(0, 0, 0, 2) : new Thickness(0, 0, 0, 4);
    }

    private void ApplyStateBrush()
    {
        this.StateDot.Fill = this.currentStateBrush;
        this.CompactStateDot.Fill = this.currentStateBrush;

        var micBrush = this.overlayMode == OverlayMode.CompactMicrophone
            ? this.currentStateBrush
            : AccentBrush;
        this.MicCircle.Stroke = micBrush;
        this.MicGlyph.Foreground = micBrush;
        this.Ring1.Stroke = micBrush;
        this.Ring2.Stroke = micBrush;
    }

    private void PositionCompactInLowerRight()
    {
        if (this.overlayMode != OverlayMode.CompactMicrophone)
        {
            return;
        }

        const double margin = 24;
        var workArea = SystemParameters.WorkArea;
        this.Left = workArea.Right - this.Width - margin;
        this.Top = workArea.Bottom - this.Height - margin;
    }

    private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(MediaColor.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
