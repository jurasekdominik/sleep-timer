using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SleepTimer
{
    static class ToolTips
    {
        static bool initialized, hadInput;
        static long lastInput;
        static ToolTip active;
        static bool Quiet => (!hadInput || (Stopwatch.GetTimestamp() - lastInput) / (double)Stopwatch.Frequency >= .6)
            && Mouse.LeftButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released
            && Mouse.MiddleButton == MouseButtonState.Released && Mouse.XButton1 == MouseButtonState.Released
            && Mouse.XButton2 == MouseButtonState.Released && Keyboard.Modifiers == ModifierKeys.None;

        public static void Init()
        {
            if (initialized) return;
            initialized = true;
            ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(int.MaxValue));
            EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent,
                new RoutedEventHandler((s, e) => { active = (ToolTip)s; if (!Quiet) active.IsOpen = false; }));
            EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.ClosedEvent,
                new RoutedEventHandler((s, e) => { if (active == s) active = null; }));
            EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipService.ToolTipOpeningEvent,
                new ToolTipEventHandler((s, e) => { if (!Quiet) e.Handled = true; }));
            // Observe input before controls handle it, including the main dial's preview-wheel routing.
            InputManager.Current.PreProcessInput += (s, e) =>
            {
                var input = e.StagingItem.Input;
                if (!(input is MouseButtonEventArgs) && !(input is MouseWheelEventArgs) && !(input is KeyEventArgs)) return;
                Dismiss();
            };
        }

        public static void Dismiss()
        {
            hadInput = true; lastInput = Stopwatch.GetTimestamp();
            if (active != null) active.IsOpen = false;
        }

        public static ToolTip TimerControls(TextBlock heading, bool taskbar = false)
        {
            heading.FontWeight = FontWeights.SemiBold;
            heading.TextWrapping = TextWrapping.Wrap;
            heading.MaxWidth = 300;
            heading.Margin = new Thickness(0, 0, 0, 6);
            var panel = new StackPanel(); panel.Children.Add(heading);
            var rows = new Grid();
            rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            void Row(string gesture, string action)
            {
                int index = rows.RowDefinitions.Count;
                rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Text = gesture, FontWeight = FontWeights.Normal, Margin = new Thickness(0, 2, 16, 2) };
                label.SetResourceReference(TextBlock.ForegroundProperty, "SecondBrush");
                var value = new TextBlock { Text = action, FontWeight = FontWeights.Normal, Margin = new Thickness(0, 2, 0, 2) };
                Grid.SetRow(label, index); Grid.SetRow(value, index); Grid.SetColumn(value, 1);
                rows.Children.Add(label); rows.Children.Add(value);
            }
            Row("Left-click", "Start / pause / resume");
            if (taskbar) Row("Double-click", "Open app");
            Row("Right-click", "Cancel timer");
            Row("Scroll", "Adjust timer");
            if (taskbar) Row("Middle-click", "Open menu");
            panel.Children.Add(rows);
            return new ToolTip { Content = panel, ContentTemplate = null };
        }
    }
}
