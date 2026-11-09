using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Remindly.Core;

namespace Remindly.App.Services;

/// <summary>
/// Android's Move-to-Done flow on the PC (approved M1–M5): a countdown chip with UNDO for the phone's per-tab delay,
/// then a green tick over the card, the card shrinking away and the gap closing; unticking a Done card slides it out
/// and it comes back in Active with a short glow. Nothing is saved until the countdown ends, exactly like the phone.
/// With Windows "Animation effects" off, cards only fade.
/// </summary>
internal static class DoneFlow
{
    private sealed class Pending
    {
        public long EndsAt;
        public int Ms;
        public DispatcherTimer Timer = null!;
    }

    private static readonly Dictionary<string, Pending> _pending = new();
    private static readonly HashSet<string> _moving = new();
    private static readonly Dictionary<string, WeakReference<FrameworkElement>> _cards = new();
    private static readonly Dictionary<string, long> _glowUntil = new();
    private static readonly List<(string Key, WeakReference<TextBlock> Label)> _labels = new();
    private static DispatcherTimer? _tick;
    private static (bool ToDone, long At)? _pulse;

    // Android timings (Engine.kt Fx, Components.kt FxCard). The smoke test shortens them.
    internal static int CelebrateMs = 380, CollapseMs = 280, SlideMs = 340, GlowMs = 1100;

    /// <summary>A countdown started or was cancelled: the visible page should redraw its cards.</summary>
    public static event Action? Changed;

    public static string Key(Item item) => "item:" + item.Id;
    public static string Key(CallReminder call) => "call:" + call.Id;

    /// <summary>Counting down (or animating away): its checkbox shows ticked and nothing is saved yet.</summary>
    public static bool IsBusy(string key) => _pending.ContainsKey(key) || _moving.Contains(key);
    public static bool IsPending(string key) => _pending.ContainsKey(key);

    private static long Now => Environment.TickCount64;
    private static bool Reduced => !SystemParameters.ClientAreaAnimation;

    public static int SecondsLeft(string key) =>
        _pending.TryGetValue(key, out var p) ? (int)Math.Max(0, Math.Ceiling((p.EndsAt - Now) / 1000.0)) : 0;

    /// <summary>Tick: count down for <paramref name="delaySec"/> (0 = straight to the green tick), then run commit.</summary>
    public static void Complete(string key, int delaySec, Action commit)
    {
        if (IsBusy(key)) return;
        delaySec = Math.Clamp(delaySec, 0, 300);
        if (delaySec == 0) { Celebrate(key, commit); return; }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delaySec) };
        var pending = new Pending { EndsAt = Now + delaySec * 1000L, Ms = delaySec * 1000, Timer = timer };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_pending.Remove(key)) return;
            Celebrate(key, commit);
        };
        _pending[key] = pending;
        timer.Start();
        EnsureTick();
        Changed?.Invoke();
    }

    /// <summary>UNDO or unticking during the countdown: the item stays active and nothing was saved.</summary>
    public static void Cancel(string key)
    {
        if (!_pending.Remove(key, out var p)) return;
        p.Timer.Stop();
        Changed?.Invoke();
    }

    /// <summary>Stops every countdown without saving (design switch, tests).</summary>
    public static void CancelAll()
    {
        foreach (var p in _pending.Values) p.Timer.Stop();
        bool any = _pending.Count > 0;
        _pending.Clear();
        if (any) Changed?.Invoke();
    }

    /// <summary>Untick in Done: the card slides out, then commit runs and the item glows when it is next shown.</summary>
    public static void Revive(string key, Action commit)
    {
        if (IsBusy(key)) return;
        _moving.Add(key);
        void Finish()
        {
            _moving.Remove(key);
            _pulse = (false, Now);
            MarkGlow(key);
            commit();
            Changed?.Invoke();   // redraw even if the item changed meanwhile, so no card stays collapsed
        }
        var card = Find(key);
        if (card == null) { Finish(); return; }
        if (Reduced) { Fade(card, 150, () => Finish()); return; }
        var slide = new TranslateTransform();
        card.RenderTransform = slide;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(260, Ms(SlideMs)) { EasingFunction = ease });
        var fade = new DoubleAnimation(0.15, Ms(SlideMs)) { EasingFunction = ease };
        fade.Completed += (_, _) => Collapse(card, Finish);
        card.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>An Undo that brings an item back glows it like a revive.</summary>
    public static void MarkGlow(string key) => _glowUntil[key] = Now + GlowMs + 400;

    /// <summary>Consumed by the Active / Done switch once, to pop the count that just changed.</summary>
    public static bool TakePulse(out bool toDone)
    {
        toDone = false;
        if (_pulse is not { } p) return false;
        _pulse = null;
        if (Now - p.At > 2000) return false;
        toDone = p.ToDone;
        return true;
    }

    /// <summary>Registers the card that represents <paramref name="key"/> so its animation can play on it.</summary>
    public static T Track<T>(T card, string key) where T : FrameworkElement
    {
        _cards[key] = new WeakReference<FrameworkElement>(card);
        if (_glowUntil.TryGetValue(key, out var until))
        {
            if (until > Now) card.Loaded += (_, _) => Glow(card);
            else _glowUntil.Remove(key);
        }
        return card;
    }

    /// <summary>"Moving to Done in 3s · UNDO" with a draining bar, or null when the item is not counting down.</summary>
    public static FrameworkElement? Strip(string key)
    {
        if (!_pending.TryGetValue(key, out var p)) return null;
        var ink = Ui.Res("SuccessBrush");
        var label = Ui.Text(StripText(key), 12.5, FontWeights.SemiBold, ink, wrap: false);
        label.VerticalAlignment = VerticalAlignment.Center;
        _labels.Add((key, new WeakReference<TextBlock>(label)));
        var undo = WorkspaceUi.Action("done-undo-" + key.Replace(':', '-'), "Undo, keep it active",
            Ui.Text("UNDO", 12.5, FontWeights.ExtraBold, ink, wrap: false), () => Cancel(key));
        undo.Padding = new Thickness(8, 2, 8, 2);
        var clock = Ui.Glyph(Glyphs.Clock, 13, ink);
        clock.Margin = new Thickness(0, 0, 6, 0);
        var bar = new Border
        {
            Height = 3, Background = ink, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch,
            RenderTransformOrigin = new Point(0, 0.5),
        };
        double left = Math.Clamp((p.EndsAt - Now) / (double)p.Ms, 0, 1);
        var drain = new ScaleTransform(left, 1);
        bar.RenderTransform = drain;
        var row = Ui.Columns("Auto,*,Auto", clock, label, undo);
        row.Margin = new Thickness(10, 3, 4, 6);
        var grid = new Grid();
        grid.Children.Add(row);
        grid.Children.Add(bar);
        var box = new Border
        {
            Background = Ui.Res("SuccessSoftBrush"), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 6, 0, 0),
            ClipToBounds = true, Child = grid,
        };
        WorkspaceUi.Identify(box, "done-countdown-" + key.Replace(':', '-'), "Moving to Done");
        if (!Reduced)
            box.Loaded += (_, _) => drain.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(left, 0, TimeSpan.FromMilliseconds(Math.Max(0, p.EndsAt - Now))));
        return box;
    }

    private static string StripText(string key) => $"Moving to Done in {SecondsLeft(key)}s";

    private static void EnsureTick()
    {
        if (_tick != null) return;
        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _tick.Tick += (_, _) =>
        {
            _labels.RemoveAll(entry => !entry.Label.TryGetTarget(out var label) || !_pending.ContainsKey(entry.Key) || !label.IsLoaded && label.Parent == null);
            foreach (var (key, weak) in _labels)
                if (weak.TryGetTarget(out var label)) label.Text = StripText(key);
            if (_pending.Count == 0) { _tick!.Stop(); _tick = null; _labels.Clear(); }
        };
        _tick.Start();
    }

    private static FrameworkElement? Find(string key)
    {
        if (!_cards.TryGetValue(key, out var weak) || !weak.TryGetTarget(out var card)) return null;
        return card.IsLoaded && card.IsVisible && PresentationSource.FromVisual(card) != null ? card : null;
    }

    private static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(Math.Max(1, ms)));

    private static void Celebrate(string key, Action commit)
    {
        _moving.Add(key);
        TickAdorner? adorner = null;
        AdornerLayer? layer = null;
        void Finish()
        {
            if (adorner != null) layer?.Remove(adorner);
            _moving.Remove(key);
            _pulse = (true, Now);
            commit();
            Changed?.Invoke();
        }
        var card = Find(key);
        if (card == null) { Finish(); return; }
        if (Reduced) { Fade(card, 150, Finish); return; }
        layer = AdornerLayer.GetAdornerLayer(card);
        if (layer != null)
        {
            adorner = new TickAdorner(card);
            layer.Add(adorner);
            adorner.Play(CelebrateMs);
        }
        card.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        card.RenderTransform = scale;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, Ms(CelebrateMs)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, Ms(CelebrateMs)) { EasingFunction = ease });
        var fade = new DoubleAnimation(0.12, Ms(CelebrateMs)) { EasingFunction = ease };
        fade.Completed += (_, _) => Collapse(card, Finish);
        card.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Closes the gap the card leaves, so the cards below slide up instead of jumping.</summary>
    private static void Collapse(FrameworkElement card, Action then)
    {
        double height = card.ActualHeight;
        if (height <= 0) { then(); return; }
        var shrink = new DoubleAnimation(height, 0, Ms(CollapseMs)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        shrink.Completed += (_, _) => then();
        card.BeginAnimation(FrameworkElement.MarginProperty, new ThicknessAnimation(new Thickness(0), Ms(CollapseMs)));
        card.BeginAnimation(FrameworkElement.MaxHeightProperty, shrink);
    }

    private static void Fade(FrameworkElement card, int ms, Action then)
    {
        var fade = new DoubleAnimation(0, Ms(ms));
        fade.Completed += (_, _) => then();
        card.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>The accent glow fading to the card's own colour (Android markRevived + FxCard highlight).</summary>
    private static void Glow(FrameworkElement card)
    {
        if (card is not Border border) return;
        var local = border.ReadLocalValue(Border.BackgroundProperty);
        var to = border.Background is SolidColorBrush solid ? solid.Color : Colors.Transparent;
        var from = (Ui.Res("AccentSoftBrush") as SolidColorBrush)?.Color ?? Colors.LightSteelBlue;
        var brush = new SolidColorBrush(from);
        border.Background = brush;
        if (Reduced) { Restore(); return; }
        var anim = new ColorAnimation(from, to, Ms(GlowMs)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        anim.Completed += (_, _) => Restore();
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);

        void Restore()
        {
            if (local == DependencyProperty.UnsetValue) border.ClearValue(Border.BackgroundProperty);
            else border.SetValue(Border.BackgroundProperty, local);
        }
    }

    /// <summary>The green layer and popping tick drawn over the card without changing its layout.</summary>
    private sealed class TickAdorner : Adorner
    {
        private readonly Border _box;
        private readonly TextBlock _tick;
        private readonly VisualCollection _visuals;

        public TickAdorner(FrameworkElement card) : base(card)
        {
            IsHitTestVisible = false;
            var green = (Ui.Res("SuccessBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x15, 0x80, 0x3D);
            _tick = Ui.Glyph(Glyphs.Check, 30, Brushes.White);
            _tick.HorizontalAlignment = HorizontalAlignment.Center;
            _tick.RenderTransformOrigin = new Point(0.5, 0.5);
            _tick.RenderTransform = new ScaleTransform(0, 0);
            _box = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD1, green.R, green.G, green.B)),
                CornerRadius = card is Border b ? b.CornerRadius : new CornerRadius(8),
                Child = _tick, Opacity = 0,
            };
            _visuals = new VisualCollection(this) { _box };
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _box;
        protected override Size MeasureOverride(Size constraint) { _box.Measure(AdornedElement.RenderSize); return AdornedElement.RenderSize; }
        protected override Size ArrangeOverride(Size finalSize) { _box.Arrange(new Rect(AdornedElement.RenderSize)); return finalSize; }

        public void Play(int ms)
        {
            _box.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(Math.Min(200, ms))));
            var pop = new DoubleAnimation(0, 1, Ms(Math.Max(1, ms - 60))) { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } };
            ((ScaleTransform)_tick.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            ((ScaleTransform)_tick.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
    }

    /// <summary>The Active / Done count pop (Android's count bump).</summary>
    public static void Pulse(FrameworkElement target)
    {
        if (Reduced) return;
        target.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        target.RenderTransform = scale;
        var pop = new DoubleAnimation(1, 1.12, Ms(140)) { AutoReverse = true, EasingFunction = new QuadraticEase() };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }
}
