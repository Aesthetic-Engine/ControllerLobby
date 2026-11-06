using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ControllerLobby.Lobby;

namespace ControllerLobby.Lobby.Windows;

internal sealed class ControllerDrawing : FrameworkElement
{
    public Layout Layout { get; set; }
    public InputFrame Frame { get; set; } = new(Buttons.None);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double scale = Math.Min(ActualWidth / 400, ActualHeight / 260);
        dc.PushTransform(new TranslateTransform((ActualWidth - 400 * scale) / 2, (ActualHeight - 260 * scale) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        var art = ControllerArtwork.For(Layout);
        dc.DrawGeometry(Paint(art.BodyFill), new Pen(Paint(ControllerArtwork.StrokeGrey), 2), Geometry.Parse(art.Body));
        foreach (var d in art.Details ?? [])
            dc.DrawGeometry(Paint(d.Fill), new Pen(Paint(d.Stroke), d.StrokeWidth), Geometry.Parse(d.Path));
        foreach (var b in art.Controls)
        {
            bool on = ControllerArtwork.IsLit(b, Frame.Held);
            dc.PushOpacity(b.Dim ? .42 : 1);
            var brush = on ? Brushes.White : Paint(b.Fill);
            var pen = b.Outline ? new Pen(Paint(on ? "#FFFFFF" : ControllerArtwork.StrokeGrey), 1) : null;
            var (x, y) = ControllerArtwork.Origin(b, Frame);
            double radius = Math.Min(b.W, b.H) / 2;
            if (b.Circle) dc.DrawEllipse(brush, pen, new(x + b.W / 2, y + b.H / 2), radius, radius);
            else dc.DrawRoundedRectangle(brush, pen, new(x, y, b.W, b.H), b.Radius, b.Radius);
            var ink = Paint(on ? "#22262C" : "#E4E7EB");
            if (b.Icon == "lever") dc.DrawEllipse(Paint("#7C8289"), null, new(x + b.W * .33, y + b.H * .3), 6, 4);
            else if (b.Icon is not null)
            {
                double size = Math.Min(b.W, b.H) * .65;
                dc.PushTransform(new TranslateTransform(x + (b.W - size) / 2, y + (b.H - size) / 2));
                dc.PushTransform(new ScaleTransform(size / 24, size / 24));
                dc.DrawGeometry(null, new Pen(ink, 2), Geometry.Parse(b.Icon));
                dc.Pop(); dc.Pop();
            }
            else if (b.Label.Length > 0)
            {
                var text = new FormattedText(b.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), b.FontSize, ink, 1);
                dc.DrawText(text, new(x + (b.W - text.Width) / 2, y + (b.H - text.Height) / 2));
            }
            dc.Pop();
        }
        dc.Pop(); dc.Pop();
    }
    // A point inside a button, away from its label, for the offscreen source-lighting check.
    public Point SamplePoint(Buttons button)
    {
        var b = ControllerArtwork.For(Layout).Controls.First(c => c.Button == button);
        double scale = Math.Min(ActualWidth / 400, ActualHeight / 260);
        return new((ActualWidth - 400 * scale) / 2 + (b.X + b.W * .75) * scale,
            (ActualHeight - 260 * scale) / 2 + (b.Y + b.H * .3) * scale);
    }
    internal static SolidColorBrush Paint(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
}

internal sealed class LobbyView : UserControl
{
    private readonly LobbySession session;
    private readonly Slot[] slots;
    private readonly TextBlock state;
    private readonly TextBlock hint;
    private readonly TextBlock available;
    private sealed record Slot(TextBlock Type, TextBlock Detail, ControllerDrawing Art, Border Tile,
        Button Left, Button Right, TextBlock Instruction);
    public event Action<string, int>? MoveRequested;
    public event Action? CancelRequested;
    public LobbyView(LobbySession session, string mode, UIElement? fixtureTools = null)
    {
        this.session = session;
        FontFamily = new("Segoe UI"); Foreground = ControllerDrawing.Paint("#F1F5FC");
        Background = ControllerDrawing.Paint("#0B1220");
        var root = new Grid { Margin = new(38, 28, 38, 24) };
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new() { Height = h });
        var top = new DockPanel();
        var badge = new Border { Background = ControllerDrawing.Paint("#182A3B"), CornerRadius = new(5), Padding = new(12, 7, 12, 7), Child = Text(mode, 13, "#9FD7E8") };
        DockPanel.SetDock(badge, Dock.Right); top.Children.Add(badge);
        top.Children.Add(Text("CONTROLLER LOBBY", 14, "#87A4C0"));
        root.Children.Add(top);
        var intro = new StackPanel { Margin = new(0, 22, 0, 25) };
        intro.Children.Add(Text("Choose your players", 36));
        intro.Children.Add(Text("Press Start to join. Use your D-pad to change seats.", 17, "#A9B8CE", new(0, 8, 0, 0)));
        Grid.SetRow(intro, 1); root.Children.Add(intro);
        var row = new Grid();
        slots = new Slot[4];
        for (int i = 0; i < 4; i++)
        {
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var content = new Grid { Margin = new(17, 19, 17, 18) };
            foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
                content.RowDefinitions.Add(new() { Height = h });
            var player = Text("PLAYER " + (i + 1), 14, "#9BB1C9"); content.Children.Add(player);
            var type = Text("Waiting to join", 20, margin: new(0, 18, 0, 0));
            type.TextWrapping = TextWrapping.Wrap; type.MinHeight = 52; Grid.SetRow(type, 1); content.Children.Add(type);
            var detail = Text("Start on a controller", 12, "#8598B1", new(0, 6, 0, 6));
            detail.MinHeight = 35; detail.TextWrapping = TextWrapping.Wrap; Grid.SetRow(detail, 2); content.Children.Add(detail);
            var drawing = new ControllerDrawing { MinHeight = 150, Margin = new(0, 6, 0, 10) };
            Grid.SetRow(drawing, 3); content.Children.Add(drawing);
            var arrows = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var left = SmallButton("<"); var right = SmallButton(">");
            left.Margin = new(0, 0, 12, 0); arrows.Children.Add(left); arrows.Children.Add(right);
            Grid.SetRow(arrows, 4); content.Children.Add(arrows);
            int slotIndex = i;
            left.Click += (_, _) => MoveNative(slotIndex, -1);
            right.Click += (_, _) => MoveNative(slotIndex, 1);
            var instruction = Text("", 12, "#9CB0C9", new(0, 12, 0, 0));
            instruction.TextAlignment = TextAlignment.Center; instruction.MinHeight = 32; instruction.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(instruction, 5); content.Children.Add(instruction);
            var tile = new Border { Background = ControllerDrawing.Paint("#121F31"), BorderBrush = ControllerDrawing.Paint("#293B53"), BorderThickness = new(1), CornerRadius = new(12), Margin = new(i == 0 ? 0 : 8, 0, i == 3 ? 0 : 8, 0), Child = content };
            slots[i] = new(type, detail, drawing, tile, left, right, instruction);
            Grid.SetColumn(tile, i); row.Children.Add(tile);
        }
        Grid.SetRow(row, 2); root.Children.Add(row);
        var footer = new Grid { Margin = new(0, 22, 0, 0) };
        footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var words = new StackPanel(); state = Text("", 19); hint = Text("", 13, "#9DB0C8", new(0, 6, 15, 0));
        hint.TextWrapping = TextWrapping.Wrap;
        words.Children.Add(state); words.Children.Add(hint); footer.Children.Add(words);
        var cancel = SmallButton("Esc  Cancel"); cancel.Width = 120; cancel.Click += (_, _) => CancelRequested?.Invoke();
        Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
        Grid.SetRow(footer, 3); root.Children.Add(footer);
        var bottom = new StackPanel { Margin = new(0, 15, 0, 0) };
        available = Text("", 12, "#7790AB"); available.TextWrapping = TextWrapping.Wrap; bottom.Children.Add(available);
        if (fixtureTools is not null) bottom.Children.Add(fixtureTools);
        Grid.SetRow(bottom, 4); root.Children.Add(bottom);
        Content = root;
        Refresh();
    }
    private void MoveNative(int index, int direction)
    {
        var seat = session.Seats.FirstOrDefault(s => s.Player == index + 1);
        if (seat?.Source.Kind == SourceKind.KeyboardMouse) MoveRequested?.Invoke(seat.Source.SourceId, direction);
    }
    public ControllerDrawing DrawingAt(int player) => slots[player - 1].Art;
    public void Refresh(string? readerHint = null)
    {
        var seats = session.Seats;
        for (int i = 0; i < 4; i++)
        {
            var slot = slots[i]; var seat = seats.FirstOrDefault(s => s.Player == i + 1);
            slot.Type.Text = seat?.Source.TypeLabel ?? "Waiting to join";
            slot.Detail.Text = seat is null ? "Start on a controller\nor Enter for keyboard + mouse"
                : seat.Source.Kind == SourceKind.KeyboardMouse ? "Enter / arrows / WASD / mouse" : "Button feedback";
            slot.Art.Layout = seat?.Source.Layout ?? Layout.GenericPad;
            slot.Art.Frame = seat?.Frame ?? new(Buttons.None);
            slot.Art.Opacity = seat is null ? .15 : 1;
            slot.Art.InvalidateVisual();
            slot.Tile.BorderBrush = ControllerDrawing.Paint(seat is null ? "#293B53" : "#428EA2");
            // Mouse arrows only move the one native keyboard/mouse seat. Pad arrows are feedback
            // and instructions; their own D-pad acts on their own identity through the model.
            bool native = seat?.Source.Kind == SourceKind.KeyboardMouse;
            slot.Left.IsEnabled = native && i > 0;
            slot.Right.IsEnabled = native && i < seats.Count - 1;
            slot.Left.Opacity = slot.Right.Opacity = seat is null ? .15 : .8;
            slot.Instruction.Text = seat is null ? "No player selected" : native ? "Arrow keys or click to swap" : "D-pad left / right to swap";
        }
        state.Text = session.Phase == LobbyPhase.ConfirmedDraft ? "Selections confirmed (draft)"
            : seats.Count == 0 ? "Waiting for players" : "Player 1: press Start again to confirm";
        if (seats.FirstOrDefault()?.Source.Kind == SourceKind.KeyboardMouse && session.Phase != LobbyPhase.ConfirmedDraft)
            state.Text = "Player 1: press Enter again to confirm";
        hint.Text = session.Phase == LobbyPhase.ConfirmedDraft ? "Selection preview complete. Controller changes reopen selection."
            : readerHint ?? "Release the joining button before confirming. Back or Esc cancels.";
        var joined = seats.Select(s => s.Source.SourceId).ToHashSet(StringComparer.Ordinal);
        int unseated = session.Sources.Count(s => !joined.Contains(s.SourceId));
        available.Text = unseated == 0 ? "" : $"{unseated} connected input source{(unseated == 1 ? "" : "s")} waiting to join";
    }
    internal static TextBlock Text(string text, double size, string color = "#F1F5FC", Thickness margin = default)
        => new() { Text = text, FontSize = size, Foreground = ControllerDrawing.Paint(color), Margin = margin };
    internal static Button SmallButton(string label)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        foreach (var property in new[] { Border.BackgroundProperty, Border.BorderBrushProperty, Border.BorderThicknessProperty })
            border.SetBinding(property, new Binding(property.Name) { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, .35)); template.Triggers.Add(disabled);
        return new()
        {
            Content = label, Width = 48, Height = 36, FontSize = 16,
            Background = ControllerDrawing.Paint("#20354B"), Foreground = Brushes.White,
            BorderBrush = ControllerDrawing.Paint("#3A5673"), BorderThickness = new(1), Focusable = false,
            Template = template
        };
    }
}
