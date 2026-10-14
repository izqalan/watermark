using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhotoWatermarker
{
    sealed class MainForm : Form
    {
        static readonly (string Name, Color Color)[] Swatches =
        {
            ("White", Color.White),
            ("Black", Color.FromArgb(17, 17, 17)),
            ("Yellow", Color.FromArgb(232, 197, 71)),
            ("Red", Color.FromArgb(214, 69, 69)),
        };

        readonly WatermarkSettings settings = WatermarkSettings.Load();
        readonly List<PhotoItem> photos = new List<PhotoItem>();
        readonly Queue<string> loadQueue = new Queue<string>();
        readonly Timer renderTimer = new Timer { Interval = 15 };
        readonly string[] startupFiles;

        PhotoItem current;
        bool loading, exporting, syncing;
        Bitmap previewBitmap, scaledBase;
        (int Id, int W, int H) scaledBaseKey;
        string lastSavedPath;

        // Left side
        PreviewCanvas preview;
        Label infoLabel;
        ListView strip;
        ImageList thumbs;
        Button addButton, removeButton, clearButton;

        // Settings rail
        TextBox textBox;
        RadioButton singleRadio, tiledRadio;
        Control singleOptions;
        PositionPicker positionPicker;
        LabeledSlider marginSlider, spacingSlider, rotationSlider, sizeSlider, opacitySlider, qualitySlider;
        ComboBox fontCombo, formatCombo;
        CheckBox boldCheck, italicCheck, outlineCheck, gpsCheck;
        readonly List<Button> swatchButtons = new List<Button>();
        Button customColorButton;
        Label outputLabel;
        Button saveOneButton, saveAllButton;
        ToolTip tips;

        // Status bar
        ToolStripStatusLabel statusLabel, openFolderLink;
        ToolStripProgressBar progress;

        public MainForm(string[] startupFiles)
        {
            this.startupFiles = startupFiles ?? new string[0];
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = "Photo Watermarker";
            ClientSize = new Size(1240, 800);
            MinimumSize = new Size(920, 620);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* default icon */ }

            SuspendLayout();
            BuildUi();
            ResumeLayout(false);
            PerformLayout();

            SyncControls();
            UpdateButtons();
            UpdateInfo();
            HookDragDrop(this);

            renderTimer.Tick += (s, e) => { renderTimer.Stop(); RenderPreview(); };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (startupFiles.Length > 0) AddFiles(startupFiles);
        }

        // ------------------------------------------------------------------ UI construction

        void BuildUi()
        {
            tips = new ToolTip();

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 350));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(BuildWorkspace(), 0, 0);
            root.Controls.Add(BuildSettingsRail(), 1, 0);

            var status = new StatusStrip { SizingGrip = true };
            statusLabel = new ToolStripStatusLabel("Add photos to get started.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            openFolderLink = new ToolStripStatusLabel("Show in folder") { IsLink = true, Visible = false };
            openFolderLink.Click += (s, e) => ShowInFolder();
            progress = new ToolStripProgressBar { Visible = false, Width = 180 };
            status.Items.AddRange(new ToolStripItem[] { statusLabel, openFolderLink, progress });

            Controls.Add(root);
            Controls.Add(status);
        }

        Control BuildWorkspace()
        {
            var left = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, Padding = new Padding(12, 10, 8, 6),
            };
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));

            var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
            addButton = MakeButton("Add photos…", primary: true, width: 120);
            addButton.Click += (s, e) => OpenFiles();
            removeButton = MakeButton("Remove", primary: false, width: 90);
            removeButton.Click += (s, e) => RemoveSelected();
            clearButton = MakeButton("Remove all", primary: false, width: 100);
            clearButton.Click += (s, e) => RemoveAll();
            tips.SetToolTip(addButton, "Add photos (Ctrl+O). You can also drag photos or folders onto the window, or paste with Ctrl+V.");
            tips.SetToolTip(removeButton, "Remove the selected photos from the list (Delete). Your files are not touched.");
            var hint = new Label
            {
                Text = "Drag photos or folders onto this window. Files are never changed; copies are saved.",
                AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(12, 9, 0, 0),
            };
            toolbar.Controls.AddRange(new Control[] { addButton, removeButton, clearButton, hint });

            preview = new PreviewCanvas { Dock = DockStyle.Fill, Margin = Padding.Empty };
            preview.EmptyClicked += (s, e) => OpenFiles();
            preview.Resize += (s, e) => ScheduleRender();

            infoLabel = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 7, 0, 6), UseMnemonic = false };

            thumbs = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = ScaleSize(new Size(88, 64)) };
            strip = new ListView
            {
                Dock = DockStyle.Fill, View = View.LargeIcon, LargeImageList = thumbs, MultiSelect = true,
                HideSelection = false, Margin = Padding.Empty, ShowItemToolTips = true, BorderStyle = BorderStyle.FixedSingle,
            };
            strip.SelectedIndexChanged += (s, e) => OnStripSelection();
            strip.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) { RemoveSelected(); e.Handled = true; }
                else if (e.KeyCode == Keys.A && e.Control) { foreach (ListViewItem i in strip.Items) i.Selected = true; e.Handled = true; }
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Save this photo", null, (s, e) => _ = SaveAsync(SelectedPhotos()));
            menu.Items.Add("Show original in folder", null, (s, e) => { if (current != null) Reveal(current.Path); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Remove from list", null, (s, e) => RemoveSelected());
            strip.ContextMenuStrip = menu;

            left.Controls.Add(toolbar, 0, 0);
            left.Controls.Add(preview, 0, 1);
            left.Controls.Add(infoLabel, 0, 2);
            left.Controls.Add(strip, 0, 3);
            return left;
        }

        Control BuildSettingsRail()
        {
            var host = new Panel
            {
                Dock = DockStyle.Fill, AutoScroll = true, BackColor = SystemColors.Window,
                Margin = Padding.Empty, Padding = new Padding(18, 8, 18, 16),
            };
            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty,
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            void Add(Control c)
            {
                if (c.Dock == DockStyle.None) c.Dock = DockStyle.Fill;
                stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                stack.Controls.Add(c, 0, stack.RowCount++);
            }

            // Text
            Add(Header("Watermark text", first: true));
            textBox = new TextBox { Font = new Font("Segoe UI", 12F), Margin = new Padding(0, 2, 0, 0) };
            textBox.TextChanged += (s, e) => { if (syncing) return; settings.Text = textBox.Text; Changed(); };
            tips.SetToolTip(textBox, "The words stamped on every photo. Type © with Alt+0169.");
            Add(textBox);

            // Layout
            Add(Header("Layout"));
            var modeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            singleRadio = new RadioButton { Text = "Single mark", AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
            tiledRadio = new RadioButton { Text = "Tiled pattern", AutoSize = true, Margin = Padding.Empty };
            singleRadio.CheckedChanged += (s, e) => { if (syncing || !singleRadio.Checked) return; settings.Tiled = false; SyncControls(); Changed(); };
            tiledRadio.CheckedChanged += (s, e) => { if (syncing || !tiledRadio.Checked) return; settings.Tiled = true; SyncControls(); Changed(); };
            tips.SetToolTip(tiledRadio, "Repeats the text across the whole photo, so it can't simply be cropped out.");
            modeRow.Controls.AddRange(new Control[] { singleRadio, tiledRadio });
            Add(modeRow);

            var single = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 8, 0, 0) };
            single.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            single.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var posBox = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 4, 16, 0) };
            posBox.Controls.Add(new Label { Text = "Position", AutoSize = true, Margin = new Padding(0, 0, 0, 4) }, 0, 0);
            positionPicker = new PositionPicker { Margin = Padding.Empty };
            positionPicker.SelectedChanged += (s, e) => { if (syncing) return; settings.Position = positionPicker.Selected; Changed(); };
            posBox.Controls.Add(positionPicker, 0, 1);
            marginSlider = new LabeledSlider("Edge margin", 0, 30, v => Pct(v / 2f)) { Dock = DockStyle.Fill };
            marginSlider.ValueChanged += (s, e) => { if (syncing) return; settings.Margin = marginSlider.Value / 2f; Changed(); };
            single.Controls.Add(posBox, 0, 0);
            single.Controls.Add(marginSlider, 1, 0);
            singleOptions = single;
            Add(single);

            spacingSlider = new LabeledSlider("Spacing", 3, 40, v => Pct(v * 10));
            spacingSlider.ValueChanged += (s, e) => { if (syncing) return; settings.Spacing = spacingSlider.Value * 10; Changed(); };
            Add(spacingSlider);

            rotationSlider = new LabeledSlider("Rotation", -90, 90, v => v + "°");
            rotationSlider.ValueChanged += (s, e) => { if (syncing) return; settings.Rotation = rotationSlider.Value; Changed(); };
            Add(rotationSlider);

            // Style
            Add(Header("Style"));
            Add(new Label { Text = "Typeface", AutoSize = true, Margin = new Padding(0, 0, 0, 3) });
            fontCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Margin = Padding.Empty, MaxDropDownItems = 20 };
            using (var installed = new InstalledFontCollection())
                fontCombo.Items.AddRange(installed.Families.Select(f => f.Name).Where(n => n.Length > 0).Distinct().OrderBy(n => n).ToArray());
            fontCombo.SelectedIndexChanged += (s, e) => { if (syncing) return; settings.FontFamily = (string)fontCombo.SelectedItem; Changed(); };
            Add(fontCombo);
            var styleRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
            boldCheck = new CheckBox { Text = "Bold", AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
            italicCheck = new CheckBox { Text = "Italic", AutoSize = true, Margin = Padding.Empty };
            boldCheck.CheckedChanged += (s, e) => { if (syncing) return; settings.Bold = boldCheck.Checked; Changed(); };
            italicCheck.CheckedChanged += (s, e) => { if (syncing) return; settings.Italic = italicCheck.Checked; Changed(); };
            styleRow.Controls.AddRange(new Control[] { boldCheck, italicCheck });
            Add(styleRow);

            sizeSlider = new LabeledSlider("Size", 2, 60, v => Pct(v / 2f));
            sizeSlider.ValueChanged += (s, e) => { if (syncing) return; settings.Size = sizeSlider.Value / 2f; Changed(); };
            tips.SetToolTip(sizeSlider, "Text height as a share of the photo's shorter side, so it looks the same on every photo.");
            Add(sizeSlider);

            opacitySlider = new LabeledSlider("Opacity", 5, 100, v => v + "%");
            opacitySlider.ValueChanged += (s, e) => { if (syncing) return; settings.Opacity = opacitySlider.Value; Changed(); };
            Add(opacitySlider);

            Add(new Label { Text = "Color", AutoSize = true, Margin = new Padding(0, 8, 0, 3) });
            var colorRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            foreach (var (name, color) in Swatches)
            {
                var b = new Button
                {
                    Size = new Size(30, 30), FlatStyle = FlatStyle.Flat, BackColor = color, Margin = new Padding(0, 0, 8, 0),
                    Tag = color, AccessibleName = name, Cursor = Cursors.Hand,
                };
                b.FlatAppearance.MouseOverBackColor = color;
                b.FlatAppearance.MouseDownBackColor = color;
                b.Click += (s, e) => { settings.Color = (Color)((Button)s).Tag; SyncControls(); Changed(); };
                tips.SetToolTip(b, name);
                swatchButtons.Add(b);
                colorRow.Controls.Add(b);
            }
            customColorButton = new Button { Text = "Custom…", AutoSize = true, Height = 30, Margin = Padding.Empty, FlatStyle = FlatStyle.System };
            customColorButton.Click += (s, e) => PickCustomColor();
            colorRow.Controls.Add(customColorButton);
            Add(colorRow);

            outlineCheck = new CheckBox { Text = "Soft outline for contrast", AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            outlineCheck.CheckedChanged += (s, e) => { if (syncing) return; settings.Outline = outlineCheck.Checked; Changed(); };
            tips.SetToolTip(outlineCheck, "Adds a faint halo in the opposite tone, so the text stays readable on light and dark areas.");
            Add(outlineCheck);

            // Saving
            Add(Header("Saving"));
            Add(new Label { Text = "File format", AutoSize = true, Margin = new Padding(0, 0, 0, 3) });
            formatCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Margin = Padding.Empty };
            formatCombo.Items.AddRange(new object[] { "Same as original (HEIC saves as JPEG)", "JPEG", "PNG", "WebP" });
            formatCombo.SelectedIndexChanged += (s, e) =>
            {
                if (syncing) return;
                settings.Format = (OutputFormat)formatCombo.SelectedIndex;
                qualitySlider.Enabled = settings.Format != OutputFormat.Png;
            };
            Add(formatCombo);

            qualitySlider = new LabeledSlider("JPEG / WebP quality", 50, 100, v => v.ToString(CultureInfo.InvariantCulture));
            qualitySlider.ValueChanged += (s, e) => { if (syncing) return; settings.Quality = qualitySlider.Value; };
            Add(qualitySlider);

            gpsCheck = new CheckBox { Text = "Remove GPS location from saved copies", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            gpsCheck.CheckedChanged += (s, e) => { if (syncing) return; settings.RemoveGps = gpsCheck.Checked; };
            tips.SetToolTip(gpsCheck, "Camera details and the date are kept. Phone photos often record where they were taken.");
            Add(gpsCheck);

            Add(new Label { Text = "Save to", AutoSize = true, Margin = new Padding(0, 12, 0, 2) });
            outputLabel = new Label { AutoSize = false, AutoEllipsis = true, Height = 20, ForeColor = Theme.Muted, Margin = Padding.Empty, UseMnemonic = false };
            Add(outputLabel);
            var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 0) };
            var change = new LinkLabel { Text = "Change folder…", AutoSize = true, Margin = new Padding(0, 0, 14, 0), LinkColor = Theme.Accent };
            change.LinkClicked += (s, e) => ChooseOutputFolder();
            var reset = new LinkLabel { Text = "Next to each photo", AutoSize = true, Margin = Padding.Empty, LinkColor = Theme.Accent };
            reset.LinkClicked += (s, e) => { settings.OutputFolder = ""; UpdateOutputLabel(); };
            folderRow.Controls.AddRange(new Control[] { change, reset });
            Add(folderRow);
            host.Controls.Add(stack);

            // Save buttons live in a footer under the scrolling settings, so they are always visible.
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1,
                Margin = Padding.Empty, Padding = new Padding(18, 12, 18, 12), BackColor = SystemColors.Window,
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            footer.Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.Line);
                e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };

            saveOneButton = MakeButton("Save this photo", primary: true, width: 0);
            saveOneButton.Height = 40;
            saveOneButton.Margin = new Padding(0, 0, 6, 0);
            saveOneButton.Click += (s, e) => _ = SaveAsync(current == null ? new List<PhotoItem>() : new List<PhotoItem> { current });
            tips.SetToolTip(saveOneButton, "Save the photo shown, at full resolution (Ctrl+S). Originals are never changed.");

            saveAllButton = MakeButton("Save all photos", primary: false, width: 0);
            saveAllButton.Height = 40;
            saveAllButton.Margin = new Padding(6, 0, 0, 0);
            saveAllButton.Click += (s, e) => _ = SaveAsync(photos.ToList());
            tips.SetToolTip(saveAllButton, "Watermark every photo in the list (Ctrl+Shift+S).");

            footer.Controls.Add(saveOneButton, 0, 0);
            footer.Controls.Add(saveAllButton, 1, 0);

            var rail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, BackColor = SystemColors.Window };
            rail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rail.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rail.Controls.Add(host, 0, 0);
            rail.Controls.Add(footer, 0, 1);
            return rail;
        }

        Label Header(string text, bool first = false) => new Label
        {
            Text = text.ToUpperInvariant(), AutoSize = true, ForeColor = Theme.Muted,
            Font = new Font("Segoe UI Semibold", 8F), Margin = new Padding(0, first ? 6 : 22, 0, 6),
        };

        Button MakeButton(string text, bool primary, int width)
        {
            var b = new Button { Text = text, Height = 32, Margin = new Padding(0, 0, 8, 0), UseVisualStyleBackColor = !primary };
            if (width > 0) b.Width = width; else b.Dock = DockStyle.Fill;
            if (primary)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = Theme.Accent;
                b.ForeColor = Color.White;
                b.Font = new Font("Segoe UI Semibold", 9F);
                b.FlatAppearance.BorderSize = 0;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(9, 92, 108);
                b.FlatAppearance.MouseDownBackColor = Color.FromArgb(7, 78, 92);
                b.EnabledChanged += (s, e) => b.BackColor = b.Enabled ? Theme.Accent : Color.FromArgb(160, 190, 197);
            }
            return b;
        }

        static string Pct(float v) => v.ToString("0.#", CultureInfo.InvariantCulture) + "%";

        Size ScaleSize(Size s) => new Size((int)Math.Round(s.Width * DeviceDpi / 96f), (int)Math.Round(s.Height * DeviceDpi / 96f));

        // ------------------------------------------------------------------ Settings <-> controls

        void SyncControls()
        {
            syncing = true;
            try
            {
                textBox.Text = settings.Text;
                singleRadio.Checked = !settings.Tiled;
                tiledRadio.Checked = settings.Tiled;
                singleOptions.Visible = !settings.Tiled;
                spacingSlider.Visible = settings.Tiled;
                positionPicker.Selected = settings.Position;
                marginSlider.Value = (int)Math.Round(settings.Margin * 2);
                spacingSlider.Value = (int)Math.Round(settings.Spacing / 10);
                rotationSlider.Value = (int)Math.Round(settings.Rotation);

                int fontIndex = fontCombo.Items.IndexOf(settings.FontFamily);
                if (fontIndex < 0) fontIndex = fontCombo.Items.IndexOf("Segoe UI");
                fontCombo.SelectedIndex = Math.Max(0, fontIndex);
                settings.FontFamily = (string)fontCombo.SelectedItem;
                boldCheck.Checked = settings.Bold;
                italicCheck.Checked = settings.Italic;
                sizeSlider.Value = (int)Math.Round(settings.Size * 2);
                opacitySlider.Value = settings.Opacity;
                outlineCheck.Checked = settings.Outline;

                bool matched = false;
                foreach (var b in swatchButtons)
                {
                    bool on = ((Color)b.Tag).ToArgb() == settings.Color.ToArgb();
                    matched |= on;
                    b.FlatAppearance.BorderSize = on ? 3 : 1;
                    b.FlatAppearance.BorderColor = on ? Theme.Accent : Theme.Line;
                }
                customColorButton.Text = matched ? "Custom…" : "Custom ■";
                customColorButton.ForeColor = matched ? SystemColors.ControlText : settings.Color;

                formatCombo.SelectedIndex = (int)settings.Format;
                qualitySlider.Value = settings.Quality;
                qualitySlider.Enabled = settings.Format != OutputFormat.Png;
                gpsCheck.Checked = settings.RemoveGps;
                UpdateOutputLabel();
            }
            finally
            {
                syncing = false;
            }
        }

        void Changed()
        {
            UpdateButtons();
            ScheduleRender();
        }

        void PickCustomColor()
        {
            using var dlg = new ColorDialog { Color = settings.Color, FullOpen = true, AnyColor = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            settings.Color = Color.FromArgb(255, dlg.Color);
            SyncControls();
            Changed();
        }

        void ChooseOutputFolder()
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Choose the folder where watermarked copies are saved.",
                ShowNewFolderButton = true,
                SelectedPath = !string.IsNullOrEmpty(settings.OutputFolder) ? settings.OutputFolder
                    : current != null ? Exporter.DefaultFolderFor(current) : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            settings.OutputFolder = dlg.SelectedPath;
            UpdateOutputLabel();
        }

        void UpdateOutputLabel()
        {
            bool custom = !string.IsNullOrWhiteSpace(settings.OutputFolder);
            outputLabel.Text = custom ? settings.OutputFolder : "A “Watermarked” folder next to each photo";
            tips.SetToolTip(outputLabel, custom ? settings.OutputFolder : "For example: Pictures\\Holiday\\Watermarked\\IMG_0042-watermarked.jpg");
        }

        // ------------------------------------------------------------------ Preview

        // Throttle, not debounce: while a slider is dragged the preview keeps redrawing (at most once per
        // tick) instead of waiting for the mouse to pause. WM_TIMER is low priority, so input always wins.
        void ScheduleRender()
        {
            if (!renderTimer.Enabled) renderTimer.Start();
        }

        void RenderPreview()
        {
            Bitmap next = null;
            var p = current;
            if (p != null)
            {
                int pad = (int)(20 * DeviceDpi / 96f);
                int availW = preview.ClientSize.Width - pad * 2, availH = preview.ClientSize.Height - pad * 2;
                if (availW > 16 && availH > 16)
                {
                    double scale = Math.Min(1.0, Math.Min(availW / (double)p.Width, availH / (double)p.Height));
                    int w = Math.Max(1, (int)Math.Round(p.Width * scale)), h = Math.Max(1, (int)Math.Round(p.Height * scale));
                    try
                    {
                        next = WatermarkRenderer.Render(ScaledBase(p, w, h), w, h, p.Width, p.Height, settings, opaqueWhite: false);
                    }
                    catch (Exception ex)
                    {
                        SetStatus("Couldn't draw the preview: " + ex.Message);
                    }
                }
            }
            var old = previewBitmap;
            previewBitmap = next;
            preview.Image = next;
            old?.Dispose();
        }

        // The photo resized to the preview box, cached so slider drags only redraw the watermark.
        Bitmap ScaledBase(PhotoItem p, int w, int h)
        {
            if (scaledBase != null && scaledBaseKey == (p.Id, w, h)) return scaledBase;
            scaledBase?.Dispose();
            scaledBase = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(scaledBase))
            using (var wrap = new ImageAttributes())
            {
                wrap.SetWrapMode(WrapMode.TileFlipXY);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(p.Preview, new Rectangle(0, 0, w, h), 0, 0, p.Preview.Width, p.Preview.Height, GraphicsUnit.Pixel, wrap);
            }
            scaledBaseKey = (p.Id, w, h);
            return scaledBase;
        }

        // ------------------------------------------------------------------ Photo list

        void OpenFiles()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Add photos",
                Filter = ImageIO.OpenFilter,
                Multiselect = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
        }

        void AddFiles(IEnumerable<string> paths)
        {
            var known = new HashSet<string>(photos.Select(p => p.Path).Concat(loadQueue), StringComparer.OrdinalIgnoreCase);
            var found = new List<string>();
            int unsupported = 0;
            foreach (var path in paths)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        found.AddRange(Directory.EnumerateFiles(path).Where(ImageIO.IsSupported)
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                    }
                    else if (File.Exists(path))
                    {
                        if (ImageIO.IsSupported(path)) found.Add(path); else unsupported++;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    unsupported++;
                }
            }

            var fresh = found.Where(f => known.Add(f)).ToList();
            if (fresh.Count == 0)
            {
                SetStatus(unsupported > 0
                    ? "Those files aren't photos this app can open. Use JPEG, PNG, HEIC, WebP, AVIF or TIFF."
                    : found.Count > 0 ? "Those photos are already in the list." : "No photos found there.");
                return;
            }
            foreach (var f in fresh) loadQueue.Enqueue(f);
            if (!loading) _ = ProcessLoadQueue();
        }

        async Task ProcessLoadQueue()
        {
            loading = true;
            UpdateButtons();
            var failures = new List<string>();
            PhotoItem firstAdded = null;
            int done = 0, added = 0;
            var thumbSize = thumbs.ImageSize;

            while (loadQueue.Count > 0)
            {
                string path = loadQueue.Dequeue();
                int total = done + loadQueue.Count + 1;
                string msg = total == 1 ? $"Opening {Path.GetFileName(path)}…" : $"Opening {done + 1} of {total}: {Path.GetFileName(path)}…";
                SetProgress(done, total, msg);
                if (photos.Count == 0) preview.BusyMessage = total == 1 ? "Opening photo…" : $"Opening {done + 1} of {total}…";
                try
                {
                    var item = await Task.Run(() => PhotoItem.Load(path, thumbSize));
                    if (IsDisposed) { item.Dispose(); return; }
                    AddItem(item);
                    added++;
                    if (firstAdded == null)
                    {
                        firstAdded = item;
                        SelectPhoto(item); // show the first new photo right away
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(Path.GetFileName(path) + ": " + Describe(ex));
                }
                done++;
            }

            loading = false;
            preview.BusyMessage = null;
            HideProgress();
            UpdateButtons();
            UpdateInfo();
            if (added > 0) SetStatus(added == 1 ? $"Opened {firstAdded.Name}." : $"Opened {added} photos.");
            else if (failures.Count > 0) SetStatus("No photos could be opened.");

            if (failures.Count > 0)
            {
                string list = string.Join("\n", failures.Take(12)) + (failures.Count > 12 ? $"\n…and {failures.Count - 12} more" : "");
                MessageBox.Show(this, "These files couldn't be opened:\n\n" + list, "Photo Watermarker",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void AddItem(PhotoItem item)
        {
            photos.Add(item);
            string key = item.Id.ToString(CultureInfo.InvariantCulture);
            thumbs.Images.Add(key, item.Thumb);
            strip.Items.Add(new ListViewItem(item.Name) { ImageKey = key, Tag = item, ToolTipText = item.Path });
        }

        void SelectPhoto(PhotoItem item)
        {
            current = item;
            syncing = true;
            try
            {
                foreach (ListViewItem li in strip.Items)
                {
                    bool on = li.Tag == item;
                    li.Selected = on;
                    if (on) { li.Focused = true; li.EnsureVisible(); }
                }
            }
            finally
            {
                syncing = false;
            }
            UpdateInfo();
            UpdateButtons();
            ScheduleRender();
        }

        void OnStripSelection()
        {
            if (syncing) return;
            PhotoItem pick = null;
            if (strip.FocusedItem != null && strip.FocusedItem.Selected) pick = (PhotoItem)strip.FocusedItem.Tag;
            else if (strip.SelectedItems.Count > 0) pick = (PhotoItem)strip.SelectedItems[0].Tag;
            if (pick != null && pick != current)
            {
                current = pick;
                UpdateInfo();
                ScheduleRender();
            }
            UpdateButtons();
        }

        List<PhotoItem> SelectedPhotos()
        {
            var list = strip.SelectedItems.Cast<ListViewItem>().Select(li => (PhotoItem)li.Tag).ToList();
            if (list.Count == 0 && current != null) list.Add(current);
            return list;
        }

        void RemoveSelected() => Remove(SelectedPhotos());

        void RemoveAll() => Remove(photos.ToList());

        void Remove(List<PhotoItem> items)
        {
            if (items.Count == 0) return;
            int index = current != null ? photos.IndexOf(current) : 0;
            syncing = true;
            try
            {
                foreach (var item in items)
                {
                    var li = strip.Items.Cast<ListViewItem>().FirstOrDefault(x => x.Tag == item);
                    if (li != null) strip.Items.Remove(li);
                    thumbs.Images.RemoveByKey(item.Id.ToString(CultureInfo.InvariantCulture));
                    photos.Remove(item);
                    item.Dispose();
                }
            }
            finally
            {
                syncing = false;
            }
            scaledBase?.Dispose();
            scaledBase = null;
            scaledBaseKey = default;

            if (photos.Count == 0)
            {
                current = null;
                UpdateInfo();
                UpdateButtons();
                RenderPreview();
                SetStatus("List cleared. Your files were not changed.");
                return;
            }
            SelectPhoto(photos[Math.Max(0, Math.Min(index, photos.Count - 1))]);
            SetStatus(items.Count == 1 ? $"Removed {items[0].Name} from the list." : $"Removed {items.Count} photos from the list.");
        }

        void UpdateInfo()
        {
            if (current == null)
            {
                infoLabel.Text = loading ? "Opening photos…" : "No photo open";
                return;
            }
            var parts = new List<string>
            {
                current.Name,
                $"{current.Width} × {current.Height} px",
                current.FormatName,
                FormatBytes(current.FileSize),
            };
            if (photos.Count > 1) parts.Add($"{photos.IndexOf(current) + 1} of {photos.Count}");
            infoLabel.Text = string.Join("   ·   ", parts);
        }

        void UpdateButtons()
        {
            bool hasText = !string.IsNullOrWhiteSpace(settings.Text);
            saveOneButton.Enabled = current != null && hasText && !exporting;
            saveAllButton.Enabled = photos.Count > 1 && hasText && !exporting;
            saveAllButton.Text = photos.Count > 1 ? $"Save all {photos.Count} photos" : "Save all photos";
            removeButton.Enabled = photos.Count > 0;
            clearButton.Enabled = photos.Count > 0;
        }

        static string FormatBytes(long n)
        {
            if (n < 1024) return n + " B";
            if (n < 1024 * 1024) return (n / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
            return (n / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        static string Describe(Exception ex)
        {
            string m = (ex.InnerException ?? ex).Message ?? ex.GetType().Name;
            int nl = m.IndexOfAny(new[] { '\r', '\n' });
            if (nl > 0) m = m.Substring(0, nl);
            if (m.IndexOf("no decode delegate", StringComparison.OrdinalIgnoreCase) >= 0) m = "this file type isn't supported.";
            return m.Length > 160 ? m.Substring(0, 157) + "…" : m;
        }

        // ------------------------------------------------------------------ Saving

        async Task SaveAsync(List<PhotoItem> items)
        {
            if (exporting || items.Count == 0) return;
            if (string.IsNullOrWhiteSpace(settings.Text))
            {
                SetStatus("Type a watermark first.");
                textBox.Focus();
                return;
            }

            var snapshot = settings.Clone();
            exporting = true;
            UpdateButtons();
            openFolderLink.Visible = false;
            var saved = new List<string>();
            var failures = new List<string>();

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                SetProgress(i, items.Count, items.Count == 1 ? $"Saving {item.Name}…" : $"Saving {i + 1} of {items.Count}: {item.Name}…");
                try
                {
                    saved.Add(await Task.Run(() => Exporter.Export(item, snapshot)));
                }
                catch (Exception ex)
                {
                    failures.Add(item.Name + ": " + Describe(ex));
                }
            }

            exporting = false;
            HideProgress();
            UpdateButtons();

            if (saved.Count > 0)
            {
                lastSavedPath = saved[saved.Count - 1];
                var folders = saved.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string where = folders.Count == 1 ? folders[0] : "a Watermarked folder next to each original";
                SetStatus(saved.Count == 1 ? $"Saved {Path.GetFileName(lastSavedPath)} to {where}." : $"Saved {saved.Count} photos to {where}.");
                openFolderLink.Visible = true;
            }
            else
            {
                SetStatus("Nothing was saved.");
            }

            if (failures.Count > 0)
            {
                string list = string.Join("\n", failures.Take(12)) + (failures.Count > 12 ? $"\n…and {failures.Count - 12} more" : "");
                MessageBox.Show(this, "These photos couldn't be saved:\n\n" + list, "Photo Watermarker",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void ShowInFolder()
        {
            if (lastSavedPath != null) Reveal(lastSavedPath);
        }

        static void Reveal(string path)
        {
            try
            {
                if (File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(Path.GetDirectoryName(path))) Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(path) + "\"");
            }
            catch { /* Explorer unavailable */ }
        }

        // ------------------------------------------------------------------ Status bar

        void SetStatus(string text) => statusLabel.Text = text;

        void SetProgress(int value, int max, string text)
        {
            progress.Maximum = Math.Max(1, max);
            progress.Value = Math.Max(0, Math.Min(value, progress.Maximum));
            progress.Visible = true;
            SetStatus(text);
        }

        void HideProgress() => progress.Visible = false;

        // ------------------------------------------------------------------ Drag and drop, keyboard, paste

        void HookDragDrop(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnter;
            c.DragLeave += (s, e) => preview.DragActive = false;
            c.DragDrop += OnDragDrop;
            foreach (Control child in c.Controls) HookDragDrop(child);
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                preview.DragActive = true;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            preview.DragActive = false;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                BeginInvoke(new Action(() => AddFiles(files))); // let Explorer finish the drag first
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.O:
                    OpenFiles();
                    return true;
                case Keys.Control | Keys.S:
                    if (saveOneButton.Enabled) _ = SaveAsync(new List<PhotoItem> { current });
                    return true;
                case Keys.Control | Keys.Shift | Keys.S:
                    if (saveAllButton.Enabled) _ = SaveAsync(photos.ToList());
                    return true;
                case Keys.Control | Keys.V:
                    if (!textBox.Focused && PasteFromClipboard()) return true;
                    break;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        bool PasteFromClipboard()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    AddFiles(Clipboard.GetFileDropList().Cast<string>().ToList());
                    return true;
                }
                if (Clipboard.ContainsImage())
                {
                    using var img = Clipboard.GetImage();
                    if (img == null) return false;
                    Directory.CreateDirectory(PhotoItem.PasteFolder);
                    string path = Path.Combine(PhotoItem.PasteFolder, $"Pasted image {DateTime.Now:yyyy-MM-dd HHmmss}.png");
                    img.Save(path, ImageFormat.Png);
                    AddFiles(new[] { path });
                    return true;
                }
            }
            catch (Exception ex)
            {
                SetStatus("Couldn't paste: " + ex.Message);
            }
            return false;
        }

        // ------------------------------------------------------------------ Lifetime

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (exporting && e.CloseReason == CloseReason.UserClosing &&
                MessageBox.Show(this, "Photos are still being saved. Close anyway?", "Photo Watermarker",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            settings.Save();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            try { if (Directory.Exists(PhotoItem.PasteFolder)) Directory.Delete(PhotoItem.PasteFolder, true); }
            catch { /* temp cleanup is best effort */ }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                renderTimer.Dispose();
                previewBitmap?.Dispose();
                scaledBase?.Dispose();
                foreach (var p in photos) p.Dispose();
                tips?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
