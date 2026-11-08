using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WideLong;

public partial class MainWindow
{
    private readonly DispatcherTimer hoverDelay = new() { Interval = TimeSpan.FromMilliseconds(950) };
    private readonly DispatcherTimer hoverDismiss = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly Popup hoverPopup = new() { AllowsTransparency = true, StaysOpen = true, Placement = PlacementMode.Bottom, Focusable = false };
    private FrameworkElement? hoverAnchor;
    private void InitializeHover()
    {
        hoverDelay.Tick += (_, _) =>
        {
            hoverDelay.Stop();
            if (hoverAnchor == null || !hoverAnchor.IsMouseOver || busy || !IsActive || !IsEnabled || Mouse.LeftButton == MouseButtonState.Pressed || Preview.IsKeyboardFocusWithin && Keyboard.FocusedElement is TextBox) return;
            ContextMenu? menu = MenuFor(hoverAnchor);
            if (menu == null) return;
            var body = new StackPanel { MinWidth = 220 };
            foreach (var item in menu.Items)
            {
                if (item is Separator) { body.Children.Add(new Separator()); continue; }
                if (item is not MenuItem action) continue;
                string title = action.Header is TextBlock text ? text.Text : action.Header?.ToString() ?? "";
                var button = new Button { Content = title, IsEnabled = action.IsEnabled, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(11, 7, 11, 7), BorderThickness = new(0), Margin = new(0), Focusable = false };
                button.Click += (_, _) => { CloseHover(); action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); };
                body.Children.Add(button);
            }
            var border = new Border { Child = body, Background = Brushes.White, BorderBrush = (Brush)FindResource("Secondary"), BorderThickness = new(1), CornerRadius = new(5), Padding = new(4) };
            border.MouseEnter += (_, _) => hoverDismiss.Stop(); border.MouseLeave += (_, _) => hoverDismiss.Start();
            hoverPopup.Child = border; hoverPopup.PlacementTarget = hoverAnchor; hoverPopup.IsOpen = true;
        };
        hoverDismiss.Tick += (_, _) => CloseHover();
        Preview.AddHandler(MouseMoveEvent, new MouseEventHandler((_, e) =>
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed) { CloseHover(); return; }
            var header = Ancestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
            if (header?.Column != null) ScheduleHover(header);
            else if (hoverAnchor is DataGridColumnHeader) LeaveHover();
        }), true);
        Preview.MouseLeave += (_, _) => LeaveHover();
        Preview.ContextMenuOpening += (_, _) => CloseHover();
        Deactivated += (_, _) => CloseHover();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) CloseHover(); };
    }
    private void AttachHover(FrameworkElement element)
    {
        ToolTipService.SetIsEnabled(element, false);
        element.MouseEnter += (_, _) => ScheduleHover(element); element.MouseLeave += (_, _) => LeaveHover();
        element.ContextMenuOpening += (_, _) => CloseHover(); element.PreviewMouseDown += (_, _) => CloseHover();
    }
    private void ScheduleHover(FrameworkElement anchor)
    {
        hoverDismiss.Stop(); if (hoverAnchor == anchor) return;
        CloseHover(); hoverAnchor = anchor; hoverDelay.Start();
    }
    private void LeaveHover() { hoverDelay.Stop(); hoverDismiss.Start(); }
    private void CloseHover() { hoverDelay.Stop(); hoverDismiss.Stop(); hoverPopup.IsOpen = false; hoverAnchor = null; }
}
