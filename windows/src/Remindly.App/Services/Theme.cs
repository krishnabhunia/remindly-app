using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Remindly.Core;

namespace Remindly.App.Services;

/// <summary>Accent trio for one mode: the accent itself, a soft tint and an ink colour readable on the tint.</summary>
public sealed record AccentSet(string Accent, string Soft, string Ink);

/// <summary>Everything that changes between the four designs: colours, type and corner radii.</summary>
public sealed record Palette(
    bool Dark,
    string Surface, string Layer, string Card, string Border, string Ink, string InkSubtle, string InkHint,
    string Nav, string NavSelected, string Input, string Hover, string OnAccent,
    AccentSet Task, AccentSet Shop,
    string Danger, string DangerSoft, string Success, string SuccessSoft, string Amber, string AmberSoft, string Blue, string BlueSoft,
    string Inverse, string InverseInk, string InverseSub,
    string Font, string Mono, double CardRadius, double ControlRadius);

/// <summary>
/// Applies a design (Settings → Appearance) and a mode (Task / Shop) to the application resources. Styles read these
/// keys with DynamicResource, and the code-built screens are rebuilt after a switch, so the whole window follows.
/// </summary>
public static class Theme
{
    private const string FontsBase = "pack://application:,,,/";

    public static readonly IReadOnlyDictionary<string, Palette> Palettes = new Dictionary<string, Palette>
    {
        // A · Fluent — Windows 11: grey Mica-like ground, white layer, Segoe UI Variable, indigo / green.
        [Designs.Fluent] = new(false,
            "#EEF0F3", "#FBFBFC", "#FFFFFF", "#E2E4E9", "#1B1B1F", "#5A5C66", "#8A8C95",
            "#EEF0F3", "#FFFFFF", "#FFFFFF", "#0F000000", "#FFFFFF",
            new("#4F46E5", "#ECEBFD", "#3B32B8"), new("#0B7A55", "#E3F4EC", "#0B6447"),
            "#B42318", "#FDECEA", "#15803D", "#E6F4EC", "#8A5A00", "#FFF4DC", "#1D4ED8", "#E3EAFC",
            "#1B1B1F", "#FFFFFF", "#C9CBD3",
            "Segoe UI Variable Text, Segoe UI", "Cascadia Mono, Consolas", 8, 6),

        // B · Day Board — bright, Manrope, blue / orange.
        [Designs.Board] = new(false,
            "#F5F6F8", "#F5F6F8", "#FFFFFF", "#E4E6EB", "#14161B", "#5B606B", "#8C919B",
            "#FFFFFF", "#EEF0F4", "#FFFFFF", "#0F14161B", "#FFFFFF",
            new("#1F4FD1", "#E3EAFC", "#173B9E"), new("#B4480A", "#FFE9DA", "#8A3A08"),
            "#A3211A", "#FCEDEC", "#166534", "#E6F4EC", "#8A5A00", "#FFF3D6", "#1F4FD1", "#E3EAFC",
            "#14161B", "#FFFFFF", "#C7CBD3",
            "./Assets/Fonts/#Manrope, Segoe UI", "Cascadia Mono, Consolas", 12, 10),

        // C · Command Dark — dark, dense, IBM Plex, violet / mint.
        [Designs.Command] = new(true,
            "#101217", "#101217", "#181B22", "#2B303B", "#E8EAEF", "#A0A6B3", "#737A88",
            "#14171D", "#232036", "#181B22", "#1FFFFFFF", "#14121F",
            new("#9B8CFF", "#232036", "#C9C0FF"), new("#34D399", "#12291F", "#6EE7B7"),
            "#F87171", "#3A1D1F", "#34D399", "#12291F", "#FBBF24", "#33290F", "#60A5FA", "#172338",
            "#E8EAEF", "#101217", "#3A3F4B",
            "./Assets/Fonts/#IBM Plex Sans, Segoe UI", "./Assets/Fonts/#IBM Plex Mono, Consolas", 8, 6),

        // D · Today Hub — rounded tiles, Plus Jakarta Sans, violet / green, a dark hero tile.
        [Designs.Hub] = new(false,
            "#F1F2F6", "#F1F2F6", "#FFFFFF", "#E3E4EA", "#17151F", "#5E5A6B", "#8E8A9B",
            "#FFFFFF", "#EFEFF3", "#FFFFFF", "#0F17151F", "#FFFFFF",
            new("#6941C6", "#EEE8FB", "#5B34B5"), new("#0B7A55", "#DDF1E8", "#0B6447"),
            "#9F1F17", "#FDEDEC", "#15803D", "#E6F4EC", "#7A5200", "#FFF3D6", "#2563EB", "#E3EAFC",
            "#17151F", "#FFFFFF", "#CFCBDD",
            "./Assets/Fonts/#Plus Jakarta Sans, Segoe UI", "Cascadia Mono, Consolas", 18, 12),
    };

    public static string Design { get; private set; } = Designs.Default;

    public static string Mode { get; private set; } = "TASK";

    public static Palette P => Palettes[Design];

    public static bool IsShop => Mode == "SHOP";

    public static AccentSet Accent => IsShop ? P.Shop : P.Task;

    /// <summary>Raised after the resources change (design or mode).</summary>
    public static event Action? Changed;

    public static FontFamily FontOf(string family) => family.StartsWith("./", StringComparison.Ordinal)
        ? new FontFamily(new Uri(FontsBase), family)
        : new FontFamily(family);

    public static void Apply(string design, string mode)
    {
        Design = Designs.Normalize(design);
        Mode = mode == "SHOP" ? "SHOP" : "TASK";
        var p = P;
        var a = Accent;
        var r = Application.Current.Resources;
        void B(string key, string hex) => r[key] = Brush(hex);

        B("SurfaceBrush", p.Surface);
        B("LayerBrush", p.Layer);
        B("CardBrush", p.Card);
        B("BorderBrush", p.Border);
        B("InkBrush", p.Ink);
        B("InkSubtleBrush", p.InkSubtle);
        B("InkHintBrush", p.InkHint);
        B("NavBrush", p.Nav);
        B("NavSelectedBrush", p.NavSelected);
        B("InputBrush", p.Input);
        B("HoverBrush", p.Hover);
        B("OnAccentBrush", p.OnAccent);
        B("ControlInkBrush", "#1B1B1F");
        B("AccentBrush", a.Accent);
        B("AccentSoftBrush", a.Soft);
        B("AccentInkBrush", a.Ink);
        B("HeaderBrush", a.Accent);
        B("DangerBrush", p.Danger);
        B("DangerSoftBrush", p.DangerSoft);
        B("SuccessBrush", p.Success);
        B("SuccessSoftBrush", p.SuccessSoft);
        B("AmberBrush", p.Amber);
        B("AmberSoftBrush", p.AmberSoft);
        B("BlueBrush", p.Blue);
        B("BlueSoftBrush", p.BlueSoft);
        B("InverseBrush", p.Inverse);
        B("InverseInkBrush", p.InverseInk);
        B("InverseSubBrush", p.InverseSub);
        r["AppFont"] = FontOf(p.Font);
        r["MonoFont"] = FontOf(p.Mono);
        r["CardRadius"] = new CornerRadius(p.CardRadius);
        r["ControlRadius"] = new CornerRadius(p.ControlRadius);
        Changed?.Invoke();
    }

    public static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>Gives a window the design's font and ground (editors, dialogs and the reminder card call this).</summary>
    public static void Dress(Window w, bool ground = true)
    {
        w.SetResourceReference(Control.FontFamilyProperty, "AppFont");
        w.SetResourceReference(Control.ForegroundProperty, "InkBrush");
        if (ground) w.SetResourceReference(Control.BackgroundProperty, "CardBrush");
    }
}
