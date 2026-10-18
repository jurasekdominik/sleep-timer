using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace SleepTimer
{
    static class TaskbarPlacement
    {
        // Physical pixels throughout. Never cover tray controls or application buttons.
        public static Rectangle? LeftOfTray(Rectangle bar, Rectangle screen, Rectangle tray,
            int width, int height, int gap, IEnumerable<Rectangle> buttons)
        {
            if (width <= 0 || height <= 0 || gap < 0 || bar.Width <= bar.Height
                || !bar.IntersectsWith(tray)) return null;
            var visible = Rectangle.Intersect(bar, screen);
            var target = new Rectangle(tray.Left - gap - width, bar.Top + (bar.Height - height) / 2, width, height);
            return visible.Contains(target) && !buttons.Any(button => button.IntersectsWith(target))
                ? target : (Rectangle?)null;
        }
    }
}
