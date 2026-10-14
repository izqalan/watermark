using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;

namespace PhotoWatermarker
{
    enum OutputFormat { Original, Jpeg, Png, WebP }

    /// <summary>Everything that controls how the watermark looks and how files are saved.</summary>
    sealed class WatermarkSettings
    {
        public string Text = "© 2026 Your Name";
        public bool Tiled;
        public int Position = 8;            // 0..8, row-major 3x3 grid; 8 = bottom right
        public float Margin = 3f;           // % of the shorter side
        public float Spacing = 150f;        // % of the font size, tiled mode
        public float RotationSingle;
        public float RotationTiled = -30f;
        public string FontFamily = "Segoe UI";
        public bool Bold = true;
        public bool Italic;
        public float Size = 5f;             // font size as % of the shorter side
        public int Opacity = 70;            // %
        public Color Color = Color.White;
        public bool Outline = true;
        public OutputFormat Format = OutputFormat.Original;
        public int Quality = 92;
        public bool RemoveGps = true;
        public string OutputFolder = "";    // empty = "Watermarked" folder next to each photo

        public float Rotation
        {
            get => Tiled ? RotationTiled : RotationSingle;
            set { if (Tiled) RotationTiled = value; else RotationSingle = value; }
        }

        public WatermarkSettings Clone() => (WatermarkSettings)MemberwiseClone();

        static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoWatermarker", "settings.ini");

        public static WatermarkSettings Load()
        {
            var s = new WatermarkSettings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
                }
                string Str(string k, string d) => map.TryGetValue(k, out var v) ? v : d;
                float Num(string k, float d) => map.TryGetValue(k, out var v) &&
                    float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : d;
                bool Flag(string k, bool d) => map.TryGetValue(k, out var v) ? v == "1" : d;

                s.Text = Str("text", s.Text);
                s.Tiled = Flag("tiled", s.Tiled);
                s.Position = Clamp((int)Num("position", s.Position), 0, 8);
                s.Margin = Num("margin", s.Margin);
                s.Spacing = Num("spacing", s.Spacing);
                s.RotationSingle = Num("rotationSingle", s.RotationSingle);
                s.RotationTiled = Num("rotationTiled", s.RotationTiled);
                s.FontFamily = Str("font", s.FontFamily);
                s.Bold = Flag("bold", s.Bold);
                s.Italic = Flag("italic", s.Italic);
                s.Size = Num("size", s.Size);
                s.Opacity = Clamp((int)Num("opacity", s.Opacity), 5, 100);
                if (int.TryParse(Str("color", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                    s.Color = Color.FromArgb(255, Color.FromArgb(rgb));
                s.Outline = Flag("outline", s.Outline);
                if (Enum.TryParse(Str("format", ""), out OutputFormat fmt)) s.Format = fmt;
                s.Quality = Clamp((int)Num("quality", s.Quality), 50, 100);
                s.RemoveGps = Flag("removeGps", s.RemoveGps);
                s.OutputFolder = Str("outputFolder", "");
            }
            catch { /* unreadable settings: fall back to defaults */ }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                string F(float v) => v.ToString(CultureInfo.InvariantCulture);
                string B(bool v) => v ? "1" : "0";
                File.WriteAllLines(FilePath, new[]
                {
                    "text=" + (Text ?? "").Replace("\r", " ").Replace("\n", " "),
                    "tiled=" + B(Tiled),
                    "position=" + Position,
                    "margin=" + F(Margin),
                    "spacing=" + F(Spacing),
                    "rotationSingle=" + F(RotationSingle),
                    "rotationTiled=" + F(RotationTiled),
                    "font=" + FontFamily,
                    "bold=" + B(Bold),
                    "italic=" + B(Italic),
                    "size=" + F(Size),
                    "opacity=" + Opacity,
                    "color=" + (Color.ToArgb() & 0xFFFFFF).ToString("X6"),
                    "outline=" + B(Outline),
                    "format=" + Format,
                    "quality=" + Quality,
                    "removeGps=" + B(RemoveGps),
                    "outputFolder=" + OutputFolder,
                });
            }
            catch { /* settings are a convenience; ignore write failures */ }
        }

        static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));
    }
}
