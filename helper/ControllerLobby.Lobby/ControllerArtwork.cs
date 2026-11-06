using System.Globalization;
using System.Security;
using System.Text;

namespace ControllerLobby.Lobby;

// Button is the Windows-semantic input; Label is the printed legend, which may use another convention.
public enum ArtMotion { None, LeftStick, RightStick, ArcadeLever }
public sealed record ArtButton(Buttons Button, string Label, double X, double Y, double W, double H,
    bool Circle = true, string Fill = "#252A30", bool Feedback = true, bool Dim = false,
    string? Icon = null, bool Outline = true, double Radius = 4, double FontSize = 11,
    ArtMotion Motion = ArtMotion.None);
public sealed record ArtShape(string Path, string Fill, string Stroke = "#A5ADB6", double StrokeWidth = 1);
public sealed record Artwork(string Body, string BodyFill, IReadOnlyList<ArtButton> Controls,
    IReadOnlyList<ArtShape>? Details = null);

// Original vector artwork, shared by the WPF renderer and exported SVGs; no licensed image assets.
public static class ControllerArtwork
{
    public const string BodyGrey = "#60656A", CapGrey = "#252A30", StrokeGrey = "#A5ADB6";
    private const string ViewIcon = "M3 4 H15 V14 H3 Z M9 10 H21 V20 H9 Z";
    private const string MenuIcon = "M3 5 H21 M3 12 H21 M3 19 H21";
    private const string HomeIcon = "M2 11 L12 2 L22 11 M5 9 V22 H19 V9 M10 22 V14 H14 V22";
    private const string StarIcon = "M12 2 L15 8 L22 9 L17 14 L18 21 L12 18 L6 21 L7 14 L2 9 L9 8 Z";
    private const string PairIcon = "M3 8 Q12 0 21 8 M6 12 Q12 6 18 12 M9 16 Q12 13 15 16 M11 20 H13";
    private const string KnobIcon = "M5 5 L19 19";
    private const string LinkIcon = "M2 12 H22 M12 7 V17";
    private const string GuideIcon = "M5 4 L19 20 M19 4 L5 20";
    private const string ShareIcon = "M3 15 V21 H21 V15 M12 17 V3 M7 8 L12 3 L17 8";
    private static ArtButton C(Buttons b, string text, double x, double y, double r = 13, string fill = CapGrey)
        => new(b, text, x - r, y - r, r * 2, r * 2, true, fill,
            Motion: b == Buttons.LS ? ArtMotion.LeftStick : b == Buttons.RS ? ArtMotion.RightStick : ArtMotion.None);
    private static ArtButton R(Buttons b, string text, double x, double y, double w, double h)
        => new(b, text, x, y, w, h, false);
    private static IEnumerable<ArtButton> Dpad(double x, double y) =>
        new[] { R(Buttons.Up, "", x - 8, y - 27, 16, 19), R(Buttons.Down, "", x - 8, y + 8, 16, 19),
            R(Buttons.Left, "", x - 27, y - 8, 19, 16), R(Buttons.Right, "", x + 8, y - 8, 19, 16) }
        .Select(b => b with { Outline = false, Radius = 0 });
    private static ArtButton Extra(string label, double x, double y, double r, string? icon = null)
        => C(Buttons.None, label, x, y, r) with { Feedback = false, Dim = true, Icon = icon, FontSize = 8 };
    private static string Oval(double x, double y, double r) => $"M{N(x-r)} {N(y)} A{N(r)} {N(r)} 0 1 0 {N(x+r)} {N(y)} A{N(r)} {N(r)} 0 1 0 {N(x-r)} {N(y)} Z";
    private static string Cross(double x, double y) => $"M{N(x-8)} {N(y-27)} H{N(x+8)} V{N(y-8)} H{N(x+27)} V{N(y+8)} H{N(x+8)} V{N(y+27)} H{N(x-8)} V{N(y+8)} H{N(x-27)} V{N(y-8)} H{N(x-8)} Z";

    public static Artwork For(Layout layout)
    {
        if (layout == Layout.KeyboardMouse) return new(
            "M25 57 Q25 45 38 45 H280 Q293 45 293 58 V201 Q293 214 280 214 H38 Q25 214 25 201 Z M315 100 Q315 71 345 71 Q375 71 375 100 V157 Q375 188 345 188 Q315 188 315 157 Z",
            BodyGrey,
            [R(Buttons.Back, "Esc", 40, 62, 48, 33), R(Buttons.Start, "Enter", 192, 62, 83, 50),
             R(Buttons.KeyW, "W", 82, 123, 32, 32), R(Buttons.KeyA, "A", 44, 165, 32, 32),
             R(Buttons.KeyS, "S", 82, 165, 32, 32), R(Buttons.KeyD, "D", 120, 165, 32, 32),
             R(Buttons.Up, "↑", 210, 127, 32, 32), R(Buttons.Left, "←", 172, 165, 32, 32),
             R(Buttons.Down, "↓", 210, 165, 32, 32), R(Buttons.Right, "→", 248, 165, 32, 32),
             R(Buttons.MouseLeft, "", 321, 89, 21, 43), R(Buttons.MouseRight, "", 348, 89, 21, 43)]);

        if (layout == Layout.ArcadeStick)
        {
            var controls = new List<ArtButton>
            {
                C(Buttons.None, "Lever", 99, 133, 21) with { Feedback = false, Motion = ArtMotion.ArcadeLever, Icon = "lever" },
                C(Buttons.X, "X", 203, 115, 17), C(Buttons.Y, "Y", 245, 103, 17),
                C(Buttons.RB, "RB", 287, 103, 17), C(Buttons.LB, "LB", 329, 103, 17),
                C(Buttons.A, "A", 197, 161, 17), C(Buttons.B, "B", 239, 149, 17),
                C(Buttons.RT, "RT", 281, 149, 17), C(Buttons.LT, "LT", 323, 149, 17),
                R(Buttons.Back, "Select", 123, 66, 24, 11) with { Radius = 5, FontSize = 7 },
                R(Buttons.Start, "Start", 150, 66, 24, 11) with { Radius = 5, FontSize = 7 },
                Extra("Mode", 53, 42, 10, KnobIcon), Extra("Stick", 80, 42, 10, KnobIcon),
                Extra("Pair", 107, 42, 8, PairIcon), Extra("Star", 134, 42, 8, StarIcon), Extra("Home", 161, 42, 8, HomeIcon),
                R(Buttons.None, "Link", 96, 66, 24, 11) with { Feedback = false, Dim = true, Icon = LinkIcon, Radius = 5 },
                Extra("P1", 305, 46, 14), Extra("P2", 343, 46, 14)
            };
            return new("M41 18 H359 Q371 18 371 30 V230 Q371 242 359 242 H41 Q29 242 29 230 V30 Q29 18 41 18 Z", BodyGrey, controls,
                [new("M175 82 H359 V192 H175 Z", "#30353B"), new("M41 82 H170 V192 H41 Z", "#696E74"),
                 new(Oval(99, 133, 42), "#71767C"), new(Oval(99, 133, 23), CapGrey),
                 new("M41 196 H359 V230 H41 Z", "#696E74")]);
        }

        bool sn = layout == Layout.Sn30Pro;
        double dx = sn ? 94 : 157, dy = sn ? 103 : 134;
        var pad = new List<ArtButton>(Dpad(dx, dy))
        {
            // Fixed Windows positions; SN30 prints Nintendo B/A/Y/X at bottom/right/left/top.
            // This fixture convention does not certify any real device's mode/firmware mapping.
            C(Buttons.A, sn ? "B" : "A", 299, sn ? 127 : 135, 14),
            C(Buttons.B, sn ? "A" : "B", 328, sn ? 98 : 106, 14),
            C(Buttons.X, sn ? "Y" : "X", 270, sn ? 98 : 106, 14),
            C(Buttons.Y, sn ? "X" : "Y", 299, sn ? 69 : 77, 14),
            C(Buttons.LS, "L", sn ? 151 : 112, sn ? 149 : 93, 21),
            C(Buttons.RS, "R", sn ? 242 : 229, sn ? 149 : 136, 21),
            R(Buttons.Back, "Back", sn ? 174 : 163, sn ? 87 : 79, 31, 19), R(Buttons.Start, "Start", sn ? 213 : 202, sn ? 87 : 79, 34, 19),
            R(Buttons.LB, sn ? "L" : "LB", 76, 37, 66, 16), R(Buttons.RB, sn ? "R" : "RB", 263, 37, 66, 16),
            R(Buttons.LT, sn ? "L2" : "LT", 88, sn ? 20 : 14, 42, 17), R(Buttons.RT, sn ? "R2" : "RT", 275, sn ? 20 : 14, 42, 17)
        };
        for (int i = 0; i < pad.Count; i++)
        {
            var b = pad[i];
            if (sn && b.Button == Buttons.Back) b = b with { Label = "Select", X = 165, Y = 100, W = 29, H = 10, Radius = 5, FontSize = 8 };
            if (sn && b.Button == Buttons.Start) b = b with { X = 204, Y = 100, W = 29, H = 10, Radius = 5, FontSize = 8 };
            if (layout == Layout.XboxSeries && b.Button == Buttons.Back) b = b with { Label = "View", Circle = true, Icon = ViewIcon };
            if (layout == Layout.XboxSeries && b.Button == Buttons.Start) b = b with { Label = "Menu", Circle = true, Icon = MenuIcon };
            if (b.Button is Buttons.LB or Buttons.RB) b = b with { Radius = 6 };
            if (b.Button is Buttons.LT or Buttons.RT) b = b with { Y = 20 };
            pad[i] = b;
        }
        var details = new List<ArtShape>();
        if (layout == Layout.XboxSeries) details.Add(new(Oval(dx, dy, 28), "#3A3F46"));
        details.Add(new(Cross(dx, dy), CapGrey));
        if (sn) { pad.Add(Extra("Star", 95, 170, 8, StarIcon)); pad.Add(Extra("Home", 306, 170, 8, HomeIcon)); }
        if (layout == Layout.XboxSeries) { pad.Add(Extra("Guide", 200, 60, 9, GuideIcon)); pad.Add(Extra("Share", 200, 111, 7, ShareIcon)); }
        string body = sn
            // Controller artwork: rounded dog-bone ends, straight middle band, no pointed lobes.
            ? "M100 42 C58 42 24 76 24 118 C24 160 58 194 100 194 C123 194 140 187 155 178 Q161 174 172 174 H228 Q239 174 245 178 C260 187 277 194 300 194 C342 194 376 160 376 118 C376 76 342 42 300 42 C277 42 260 49 245 58 Q239 62 228 62 H172 Q161 62 155 58 C140 49 123 42 100 42 Z"
            : "M92 49 C65 48 48 82 41 117 L24 196 Q21 224 43 229 Q61 235 78 212 L120 165 H278 L319 212 Q335 235 356 229 Q379 222 373 195 L358 116 C351 81 335 49 308 49 Z";
        return new(body, BodyGrey, pad, details);
    }

    public static bool IsLit(ArtButton b, Buttons held) => b.Feedback && b.Button != Buttons.None && held.HasFlag(b.Button);
    public static (double X, double Y) Origin(ArtButton b, InputFrame frame)
    {
        double x = 0, y = 0;
        if (b.Motion == ArtMotion.LeftStick) { x = frame.LeftX * 7; y = -frame.LeftY * 7; }
        if (b.Motion == ArtMotion.RightStick) { x = frame.RightX * 7; y = -frame.RightY * 7; }
        if (b.Motion == ArtMotion.ArcadeLever)
        {
            double ax = frame.LeftX, ay = frame.LeftY;
            if (Math.Abs(ax) + Math.Abs(ay) < .01) { ax = frame.RightX; ay = frame.RightY; }
            ax += (frame.Held.HasFlag(Buttons.Right) ? 1 : 0) - (frame.Held.HasFlag(Buttons.Left) ? 1 : 0);
            ay += (frame.Held.HasFlag(Buttons.Up) ? 1 : 0) - (frame.Held.HasFlag(Buttons.Down) ? 1 : 0);
            x = Math.Clamp(ax, -1, 1) * 9; y = -Math.Clamp(ay, -1, 1) * 9;
        }
        return (b.X + x, b.Y + y);
    }
    public static string Svg(Layout layout, Buttons held) => Svg(layout, new InputFrame(held));
    public static string Svg(Layout layout, InputFrame frame)
    {
        var art = For(layout);
        var s = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 400 260\" role=\"img\">");
        s.Append("<title>").Append(layout).Append(" controller drawing</title>");
        s.Append($"<path d=\"{art.Body}\" fill=\"{art.BodyFill}\" stroke=\"{StrokeGrey}\" stroke-width=\"2\"/>");
        foreach (var d in art.Details ?? []) s.Append($"<path d=\"{d.Path}\" fill=\"{d.Fill}\" stroke=\"{d.Stroke}\" stroke-width=\"{N(d.StrokeWidth)}\"/>");
        foreach (var b in art.Controls)
        {
            bool on = IsLit(b, frame.Held);
            var (x, y) = Origin(b, frame);
            string f = on ? "#FFFFFF" : b.Fill;
            s.Append($"<g opacity=\"{(b.Dim ? "0.42" : "1")}\">");
            double radius = Math.Min(b.W, b.H) / 2;
            string tag = b.Circle ? $"<ellipse cx=\"{N(x + b.W / 2)}\" cy=\"{N(y + b.H / 2)}\" rx=\"{N(radius)}\" ry=\"{N(radius)}\"" : $"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(b.W)}\" height=\"{N(b.H)}\" rx=\"{N(b.Radius)}\"";
            s.Append(tag).Append($" data-button=\"{b.Button}\" fill=\"{f}\" data-readable=\"{(b.Feedback ? "true" : "false")}\" data-name=\"{SecurityElement.Escape(b.Label)}\" stroke=\"{(b.Outline ? on ? "#FFFFFF" : StrokeGrey : "none")}\"/>");
            string ink = on ? "#22262C" : "#E4E7EB";
            if (b.Icon == "lever") s.Append($"<ellipse cx=\"{N(x+b.W*.33)}\" cy=\"{N(y+b.H*.3)}\" rx=\"6\" ry=\"4\" fill=\"#7C8289\"/>");
            else if (b.Icon is not null)
            {
                double size = Math.Min(b.W, b.H) * .65;
                s.Append($"<path d=\"{b.Icon}\" transform=\"translate({N(x+(b.W-size)/2)} {N(y+(b.H-size)/2)}) scale({N(size/24)})\" fill=\"none\" stroke=\"{ink}\" stroke-width=\"2\"/>");
            }
            else if (b.Label.Length > 0) s.Append($"<text x=\"{N(x + b.W / 2)}\" y=\"{N(y + b.H / 2 + (b.FontSize == 11 ? 4 : b.FontSize * .35))}\" text-anchor=\"middle\" font-family=\"Segoe UI, sans-serif\" font-size=\"{N(b.FontSize)}\" fill=\"{ink}\">{SecurityElement.Escape(b.Label)}</text>");
            s.Append("</g>");
        }
        return s.Append("</svg>").ToString();
    }
    private static string N(double n) => n.ToString(CultureInfo.InvariantCulture);
}
