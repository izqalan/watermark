using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace PhotoWatermarker
{
    /// <summary>
    /// A photo in the working list. Only a screen-sized copy is kept in memory; the full-resolution image
    /// is decoded again from disk when saving, so dozens of large HEIC files stay manageable.
    /// </summary>
    sealed class PhotoItem : IDisposable
    {
        public const int PreviewMaxSide = 2400;
        static int nextId;

        public readonly int Id = Interlocked.Increment(ref nextId);
        public string Path { get; private set; }
        public string Name { get; private set; }
        public string FormatName { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public long FileSize { get; private set; }
        public bool IsPasted { get; private set; }
        public Bitmap Preview { get; private set; }
        public Bitmap Thumb { get; private set; }

        public static string PasteFolder => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PhotoWatermarker");

        public static PhotoItem Load(string path, Size thumbSize)
        {
            var decoded = ImageIO.Load(path, PreviewMaxSide);
            var item = new PhotoItem
            {
                Path = path,
                Name = System.IO.Path.GetFileName(path),
                FormatName = decoded.FormatName,
                Width = decoded.Width,
                Height = decoded.Height,
                FileSize = new FileInfo(path).Length,
                IsPasted = path.StartsWith(PasteFolder, StringComparison.OrdinalIgnoreCase),
                Preview = decoded.Bitmap,
            };
            item.Thumb = MakeThumb(item.Preview, thumbSize);
            return item;
        }

        static Bitmap MakeThumb(Bitmap src, Size box)
        {
            var thumb = new Bitmap(box.Width, box.Height, PixelFormat.Format32bppArgb);
            double scale = Math.Min(box.Width / (double)src.Width, box.Height / (double)src.Height);
            int w = Math.Max(1, (int)Math.Round(src.Width * scale)), h = Math.Max(1, (int)Math.Round(src.Height * scale));
            using var g = Graphics.FromImage(thumb);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, (box.Width - w) / 2, (box.Height - h) / 2, w, h);
            return thumb;
        }

        public void Dispose()
        {
            Preview?.Dispose();
            Thumb?.Dispose();
        }
    }

    static class Exporter
    {
        public static string DefaultFolderFor(PhotoItem p) => p.IsPasted
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Watermarked")
            : Path.Combine(Path.GetDirectoryName(p.Path), "Watermarked");

        /// <summary>Watermarks one photo at full resolution and returns the saved file's path.</summary>
        public static string Export(PhotoItem p, WatermarkSettings s)
        {
            var format = ImageIO.ResolveFormat(s.Format, p.Path);
            using var full = ImageIO.Load(p.Path);
            using var rendered = WatermarkRenderer.Render(full.Bitmap, full.Width, full.Height, full.Width, full.Height,
                s, opaqueWhite: format == OutputFormat.Jpeg);

            string dir = string.IsNullOrWhiteSpace(s.OutputFolder) ? DefaultFolderFor(p) : s.OutputFolder;
            Directory.CreateDirectory(dir);
            string outPath = UniquePath(dir, Path.GetFileNameWithoutExtension(p.Name) + "-watermarked", ImageIO.ExtensionFor(format));
            ImageIO.Save(rendered, outPath, format, s.Quality, full.HasAlpha, full.Exif, full.ColorProfile, s.RemoveGps);
            return outPath;
        }

        // Never overwrite: an earlier export (or the original itself) may already use the name.
        static string UniquePath(string dir, string stem, string ext)
        {
            string candidate = Path.Combine(dir, stem + ext);
            for (int n = 2; File.Exists(candidate); n++)
                candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            return candidate;
        }
    }
}
