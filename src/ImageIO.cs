using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using ImageMagick;

namespace PhotoWatermarker
{
    /// <summary>A decoded, upright photo plus the metadata worth carrying over to the saved copy.</summary>
    sealed class DecodedImage : IDisposable
    {
        public Bitmap Bitmap;
        public IExifProfile Exif;
        public IColorProfile ColorProfile;
        public bool HasAlpha;
        public string FormatName;
        public int Width, Height;

        public void Dispose() => Bitmap?.Dispose();
    }

    /// <summary>Reading and writing goes through ImageMagick, so HEIC, AVIF, WebP and TIFF work without
    /// any Windows codec packs. Drawing happens in GDI+.</summary>
    static class ImageIO
    {
        public static readonly string[] Extensions =
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".heic", ".heif", ".hif", ".avif", ".webp",
            ".bmp", ".gif", ".tif", ".tiff", ".dng",
        };

        public const string OpenFilter =
            "Photos|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.heic;*.heif;*.hif;*.avif;*.webp;*.bmp;*.gif;*.tif;*.tiff;*.dng|All files|*.*";

        public static bool IsSupported(string path) =>
            Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        /// <summary>Decodes the first frame, rotated upright. When maxSide is set, the bitmap is shrunk to fit.</summary>
        public static DecodedImage Load(string path, int maxSide = 0)
        {
            using var img = new MagickImage(path);
            var result = new DecodedImage
            {
                FormatName = FormatLabel(img.Format, path),
                Exif = img.GetExifProfile(),
                ColorProfile = img.GetColorProfile(),
                HasAlpha = img.HasAlpha,
            };
            img.AutoOrient();
            result.Width = (int)img.Width;
            result.Height = (int)img.Height;
            if (img.ColorSpace == ColorSpace.CMYK) img.ColorSpace = ColorSpace.sRGB;
            if (maxSide > 0 && (img.Width > maxSide || img.Height > maxSide))
                img.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide) { Greater = true });
            result.Bitmap = ToBitmap(img);
            return result;
        }

        public static OutputFormat ResolveFormat(OutputFormat chosen, string sourcePath)
        {
            if (chosen != OutputFormat.Original) return chosen;
            switch (Path.GetExtension(sourcePath).ToLowerInvariant())
            {
                case ".png": case ".gif": case ".bmp": return OutputFormat.Png;
                case ".webp": return OutputFormat.WebP;
                default: return OutputFormat.Jpeg; // JPEG, plus HEIC/AVIF/TIFF/DNG, which most apps and sites can't take
            }
        }

        public static string ExtensionFor(OutputFormat f) => f switch
        {
            OutputFormat.Png => ".png",
            OutputFormat.WebP => ".webp",
            _ => ".jpg",
        };

        public static void Save(Bitmap rendered, string outPath, OutputFormat format, int quality, bool keepAlpha,
            IExifProfile exif, IColorProfile colorProfile, bool removeGps)
        {
            using var img = FromBitmap(rendered);
            if (!keepAlpha || format == OutputFormat.Jpeg) img.Alpha(AlphaAction.Off);
            img.Orientation = Orientation.TopLeft;

            if (colorProfile != null) img.SetProfile(colorProfile);
            if (exif != null)
            {
                exif.SetValue(ExifTag.Orientation, (ushort)1); // pixels are already upright
                exif.RemoveThumbnail();                        // the old thumbnail has no watermark
                if (removeGps) RemoveGps(exif);
                img.SetProfile(exif);
            }

            switch (format)
            {
                case OutputFormat.Png:
                    img.Format = MagickFormat.Png;
                    break;
                case OutputFormat.WebP:
                    img.Format = MagickFormat.WebP;
                    img.Quality = (uint)quality;
                    break;
                default:
                    img.Format = MagickFormat.Jpeg;
                    img.Quality = (uint)quality;
                    break;
            }

            // Write to a temp file first so a failure never leaves a half-written photo behind.
            string tmp = outPath + ".partial";
            try
            {
                img.Write(tmp);
                if (File.Exists(outPath)) File.Delete(outPath);
                File.Move(tmp, outPath);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }

        static readonly Lazy<ExifTag[]> GpsTags = new Lazy<ExifTag[]>(() =>
            typeof(ExifTag).GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(p => p.Name.StartsWith("GPS", StringComparison.Ordinal) && typeof(ExifTag).IsAssignableFrom(p.PropertyType))
                .Select(p => (ExifTag)p.GetValue(null))
                .ToArray());

        static void RemoveGps(IExifProfile exif)
        {
            foreach (var tag in GpsTags.Value) exif.RemoveValue(tag);
        }

        static string FormatLabel(MagickFormat format, string path)
        {
            switch (format)
            {
                case MagickFormat.Jpeg: case MagickFormat.Jpg: case MagickFormat.Pjpeg: return "JPEG";
                case MagickFormat.Heic: case MagickFormat.Heif: return "HEIC";
                case MagickFormat.WebP: return "WebP";
                case MagickFormat.Avif: return "AVIF";
                case MagickFormat.Png: case MagickFormat.Png24: case MagickFormat.Png32: case MagickFormat.Png8: return "PNG";
                case MagickFormat.Tif: case MagickFormat.Tiff: case MagickFormat.Tiff64: return "TIFF";
                default:
                    string ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
                    return ext.Length > 0 ? ext : format.ToString().ToUpperInvariant();
            }
        }

        static Bitmap ToBitmap(MagickImage img)
        {
            int w = (int)img.Width, h = (int)img.Height;
            img.Depth = 8;
            byte[] pixels = img.ToByteArray(MagickFormat.Bgra);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = w * 4;
                for (int y = 0; y < h; y++)
                    Marshal.Copy(pixels, y * rowBytes, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }

        static MagickImage FromBitmap(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height, rowBytes = w * 4;
            var pixels = new byte[rowBytes * h];
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            var img = new MagickImage();
            img.ReadPixels(pixels, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.BGRA));
            return img;
        }
    }
}
