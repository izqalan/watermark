using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PhotoWatermarker
{
    static class Theme
    {
        public static readonly Color Accent = Color.FromArgb(11, 108, 126);
        public static readonly Color AccentSoft = Color.FromArgb(215, 235, 239);
        public static readonly Color Muted = Color.FromArgb(88, 98, 106);
        public static readonly Color Line = Color.FromArgb(210, 216, 220);
        public static readonly Color StageA = Color.FromArgb(40, 45, 48);
        public static readonly Color StageB = Color.FromArgb(46, 51, 55);
    }

    /// <summary>The dark, checkered stage the photo preview sits on. Doubles as the empty-state drop target.</summary>
    sealed class PreviewCanvas : Control
    {
        Bitmap image;
        bool dragActive;
        string busyMessage;
        TextureBrush checker;

        public event EventHandler EmptyClicked;

        public PreviewCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.StageA;
            AccessibleName = "Photo preview";
        }

        public Bitmap Image
        {
            get => image;
            set { image = value; Cursor = image == null ? Cursors.Hand : Cursors.Default; Invalidate(); }
        }

        public bool DragActive
        {
            get => dragActive;
            set { if (dragActive != value) { dragActive = value; Invalidate(); } }
        }

        /// <summary>Shown in the empty state while photos are opening.</summary>
        public string BusyMessage
        {
            get => busyMessage;
            set { busyMessage = value; Invalidate(); }
        }

        int Px(float v) => (int)Math.Round(v * DeviceDpi / 96f);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (checker == null)
            {
                int t = Px(10);
                var tile = new Bitmap(t * 2, t * 2);
                using (var tg = Graphics.FromImage(tile))
                {
                    tg.Clear(Theme.StageA);
                    using var b = new SolidBrush(Theme.StageB);
                    tg.FillRectangle(b, 0, 0, t, t);
                    tg.FillRectangle(b, t, t, t, t);
                }
                checker = new TextureBrush(tile);
            }
            g.FillRectangle(checker, ClientRectangle);

            if (image != null)
            {
                int x = (Width - image.Width) / 2, y = (Height - image.Height) / 2;
                using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                    g.FillRectangle(shadow, x + Px(2), y + Px(4), image.Width, image.Height);
                g.DrawImageUnscaled(image, x, y);
            }
            else
            {
                DrawEmptyState(g);
            }

            if (dragActive)
            {
                using var veil = new SolidBrush(Color.FromArgb(170, 8, 60, 70));
                g.FillRectangle(veil, ClientRectangle);
                var box = Rectangle.Inflate(ClientRectangle, -Px(24), -Px(24));
                using var pen = new Pen(Color.White, Px(2)) { DashStyle = DashStyle.Dash };
                g.DrawRectangle(pen, box);
                using var font = new Font("Segoe UI Semibold", 18f);
                TextRenderer.DrawText(g, "Drop to add photos", font, box, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        void DrawEmptyState(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = Math.Min(Px(460), Width - Px(40)), h = Px(190);
            var box = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
            using (var pen = new Pen(Color.FromArgb(120, 200, 210, 215), Px(1.5f)) { DashStyle = DashStyle.Dash })
                g.DrawRectangle(pen, box);

            using var title = new Font("Segoe UI Semibold", 17f);
            using var body = new Font("Segoe UI", 10f);
            var titleRect = new Rectangle(box.X, box.Y + Px(44), box.Width, Px(40));
            var bodyRect = new Rectangle(box.X + Px(16), box.Y + Px(88), box.Width - Px(32), Px(70));
            if (busyMessage != null)
            {
                TextRenderer.DrawText(g, busyMessage, title, new Rectangle(box.X, box.Y, box.Width, box.Height), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            TextRenderer.DrawText(g, "Drop photos here", title, titleRect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "JPEG, PNG, HEIC, WebP, AVIF or TIFF. Folders work too.\nOr click to choose files.", body, bodyRect,
                Color.FromArgb(185, 196, 202), TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (image == null && busyMessage == null && e.Button == MouseButtons.Left) EmptyClicked?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            checker?.Dispose();
            checker = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) checker?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A 3x3 grid shaped like a photo; the filled cell is where a single watermark goes.</summary>
    sealed class PositionPicker : Control
    {
        static readonly string[] Names =
            { "Top left", "Top center", "Top right", "Middle left", "Center", "Middle right", "Bottom left", "Bottom center", "Bottom right" };
        int selected = 8;
        int hover = -1;

        public event EventHandler SelectedChanged;

        public PositionPicker()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Size = new Size(108, 72);
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.List;
            AccessibleName = "Watermark position";
        }

        public int Selected
        {
            get => selected;
            set
            {
                value = Math.Max(0, Math.Min(8, value));
                if (selected == value) return;
                selected = value;
                AccessibleDescription = Names[selected];
                Invalidate();
            }
        }

        void Choose(int cell)
        {
            if (cell == selected) return;
            Selected = cell;
            SelectedChanged?.Invoke(this, EventArgs.Empty);
        }

        Rectangle Frame => new Rectangle(1, 1, Width - 3, Height - 3);

        Rectangle Cell(int i)
        {
            var f = Rectangle.Inflate(Frame, -3, -3);
            int cw = f.Width / 3, ch = f.Height / 3;
            return new Rectangle(f.X + (i % 3) * cw, f.Y + (i / 3) * ch, cw, ch);
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < 9; i++) if (Cell(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? SystemColors.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var fill = new SolidBrush(Color.FromArgb(244, 246, 247)))
                g.FillRectangle(fill, Frame);
            using (var border = new Pen(Focused ? Theme.Accent : Theme.Line, Focused ? 2 : 1))
                g.DrawRectangle(border, Frame);

            float unit = DeviceDpi / 96f;
            for (int i = 0; i < 9; i++)
            {
                var c = Cell(i);
                if (i == selected)
                {
                    using var soft = new SolidBrush(Theme.AccentSoft);
                    g.FillRectangle(soft, Rectangle.Inflate(c, -1, -1));
                }
                float d = (i == selected ? 10 : i == hover ? 8 : 6) * unit;
                Color dot = i == selected ? Theme.Accent : i == hover ? Theme.Muted : Theme.Line;
                using var b = new SolidBrush(dot);
                g.FillEllipse(b, c.X + (c.Width - d) / 2f, c.Y + (c.Height - d) / 2f, d, d);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitTest(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int h = HitTest(e.Location);
            if (h >= 0) Choose(h);
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int r = selected / 3, c = selected % 3;
            switch (e.KeyCode)
            {
                case Keys.Left: c = Math.Max(0, c - 1); break;
                case Keys.Right: c = Math.Min(2, c + 1); break;
                case Keys.Up: r = Math.Max(0, r - 1); break;
                case Keys.Down: r = Math.Min(2, r + 1); break;
                default: return;
            }
            Choose(r * 3 + c);
            e.Handled = true;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    /// <summary>Label, live value readout and a track bar, stacked.</summary>
    sealed class LabeledSlider : TableLayoutPanel
    {
        readonly Label valueLabel;
        readonly TrackBar bar;
        readonly Func<int, string> format;

        public event EventHandler ValueChanged;

        public LabeledSlider(string label, int min, int max, Func<int, string> format)
        {
            this.format = format;
            ColumnCount = 2;
            RowCount = 2;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = new Padding(0, 4, 0, 0);
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var name = new Label { Text = label, AutoSize = true, Margin = new Padding(0, 0, 0, 0), Anchor = AnchorStyles.Left };
            valueLabel = new Label
            {
                AutoSize = true, Margin = Padding.Empty, Anchor = AnchorStyles.Right, ForeColor = Theme.Muted,
                Font = new Font("Consolas", 9f),
            };
            bar = new TrackBar
            {
                Minimum = min, Maximum = max, TickStyle = TickStyle.None, AutoSize = false, Height = 30,
                Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 0),
                SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 10),
                AccessibleName = label,
            };
            bar.ValueChanged += (s, e) => { valueLabel.Text = format(bar.Value); ValueChanged?.Invoke(this, EventArgs.Empty); };
            Controls.Add(name, 0, 0);
            Controls.Add(valueLabel, 1, 0);
            Controls.Add(bar, 0, 1);
            SetColumnSpan(bar, 2);
            valueLabel.Text = format(bar.Value);
        }

        public int Value
        {
            get => bar.Value;
            set
            {
                bar.Value = Math.Max(bar.Minimum, Math.Min(bar.Maximum, value));
                valueLabel.Text = format(bar.Value);
            }
        }
    }
}
