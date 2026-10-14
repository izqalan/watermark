using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PhotoWatermarker
{
    static class WatermarkRenderer
    {
        const float HaloRadius = 0.09f; // glow blur radius, as a fraction of the font size

        /// <summary>
        /// Draws <paramref name="source"/> scaled to outW x outH with the watermark on top.
        /// The watermark is laid out in the photo's full-size coordinates (logicalW x logicalH), so a small
        /// preview and the full-resolution export place it identically.
        /// </summary>
        public static Bitmap Render(Image source, int outW, int outH, int logicalW, int logicalH,
            WatermarkSettings s, bool opaqueWhite)
        {
            var result = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
            try
            {
                using (var g = Graphics.FromImage(result))
                {
                    if (opaqueWhite) g.Clear(Color.White);
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    var dest = new Rectangle(0, 0, outW, outH);
                    if (source.Width == outW && source.Height == outH)
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.DrawImage(source, dest);
                    }
                    else
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        using var wrap = new ImageAttributes();
                        wrap.SetWrapMode(WrapMode.TileFlipXY); // avoids dark fringes at the edges when scaling
                        g.DrawImage(source, dest, 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, wrap);
                    }

                    using var glyphs = BuildGlyphs(logicalW, logicalH, s, out SizeF size, out float em);
                    if (glyphs == null) return result;

                    // Draw the mark fully opaque on its own layer, then blend the layer once at the chosen
                    // opacity, so the glow and the fill don't stack into darker edges.
                    float scale = outW / (float)logicalW;
                    using var layer = new Bitmap(outW, outH, PixelFormat.Format32bppPArgb);
                    using (var lg = Graphics.FromImage(layer))
                    {
                        if (s.Tiled) DrawTiled(lg, glyphs, size, em, scale, outW, outH, s);
                        else DrawSingle(lg, glyphs, size, em, scale, logicalW, logicalH, outW, outH, s);
                    }
                    using var alpha = new ImageAttributes();
                    alpha.SetColorMatrix(new ColorMatrix { Matrix33 = s.Opacity / 100f });
                    g.DrawImage(layer, dest, 0, 0, outW, outH, GraphicsUnit.Pixel, alpha);
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        /// <summary>The text outline centered on the origin, unrotated, in full-image units.</summary>
        static GraphicsPath BuildGlyphs(int w, int h, WatermarkSettings s, out SizeF size, out float em)
        {
            size = SizeF.Empty;
            em = 0;
            string text = (s.Text ?? "").Trim();
            if (text.Length == 0 || w <= 0 || h <= 0) return null;

            em = Math.Max(4f, Math.Min(w, h) * s.Size / 100f);
            using var family = ResolveFamily(s.FontFamily);
            var glyphs = new GraphicsPath();
            glyphs.AddString(text, family, (int)ResolveStyle(family, s.Bold, s.Italic), em, PointF.Empty, StringFormat.GenericTypographic);
            RectangleF b = glyphs.GetBounds();
            if (b.Width <= 0 || b.Height <= 0)
            {
                glyphs.Dispose();
                return null;
            }
            using (var center = new Matrix())
            {
                center.Translate(-(b.X + b.Width / 2f), -(b.Y + b.Height / 2f));
                glyphs.Transform(center);
            }
            size = b.Size;
            return glyphs;
        }

        static void DrawSingle(Graphics g, GraphicsPath glyphs, SizeF size, float em, float scale,
            int w, int h, int outW, int outH, WatermarkSettings s)
        {
            double rad = s.Rotation * Math.PI / 180.0;
            float cos = (float)Math.Abs(Math.Cos(rad)), sin = (float)Math.Abs(Math.Sin(rad));
            float bw = size.Width * cos + size.Height * sin;
            float bh = size.Width * sin + size.Height * cos;
            float margin = Math.Min(w, h) * s.Margin / 100f;
            int row = s.Position / 3, col = s.Position % 3;
            float cx = col == 0 ? margin + bw / 2f : col == 1 ? w / 2f : w - margin - bw / 2f;
            float cy = row == 0 ? margin + bh / 2f : row == 1 ? h / 2f : h - margin - bh / 2f;

            using var path = (GraphicsPath)glyphs.Clone();
            using (var m = new Matrix())
            {
                m.Scale(scale, scale);
                m.Translate(cx, cy);
                m.Rotate(s.Rotation);
                path.Transform(m); // now in layer pixels
            }

            if (s.Outline) DrawHalo(g, path, em * scale * HaloRadius, outW, outH, HaloColor(s.Color));

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var brush = new SolidBrush(Color.FromArgb(255, s.Color));
            g.FillPath(brush, path);
        }

        /// <summary>
        /// Soft glow behind a single mark. Only the area around the text is blurred, at half resolution
        /// when the glow is wide enough not to show it.
        /// </summary>
        static void DrawHalo(Graphics g, GraphicsPath path, float radiusPx, int outW, int outH, Color halo)
        {
            var bounds = path.GetBounds();
            float pad = radiusPx * 3 + 2;
            var region = Rectangle.Intersect(
                Rectangle.FromLTRB((int)Math.Floor(bounds.Left - pad), (int)Math.Floor(bounds.Top - pad),
                                   (int)Math.Ceiling(bounds.Right + pad), (int)Math.Ceiling(bounds.Bottom + pad)),
                new Rectangle(0, 0, outW, outH));
            if (region.Width <= 0 || region.Height <= 0) return;

            float k = radiusPx > 4 ? 0.5f : 1f;
            int mw = Math.Max(1, (int)Math.Ceiling(region.Width * k)), mh = Math.Max(1, (int)Math.Ceiling(region.Height * k));
            int r = Math.Max(1, (int)Math.Round(radiusPx * k));

            using var mask = new Bitmap(mw, mh, PixelFormat.Format32bppPArgb);
            using (var mg = Graphics.FromImage(mask))
            {
                mg.SmoothingMode = SmoothingMode.AntiAlias;
                mg.ScaleTransform(k, k);
                mg.TranslateTransform(-region.X, -region.Y);
                mg.FillPath(Brushes.White, path);
            }
            WithPixels(mask, (px, stride) =>
            {
                var a = ExtractAlpha(px, stride, mw, mh);
                Blur(a, mw, mh, r);
                for (int y = 0; y < mh; y++)
                    for (int x = 0; x < mw; x++)
                        PutPremultiplied(px, y * stride + x * 4, halo, HaloAlpha(a[y * mw + x]));
            });

            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(mask, new RectangleF(region.X, region.Y, mw / k, mh / k));
        }

        /// <summary>
        /// Tiled pattern: one tile is rendered once, rotated and with its glow, then stamped at every grid
        /// position. Stamping is a plain pixel copy, so the cost barely depends on how many tiles there are.
        /// </summary>
        static void DrawTiled(Graphics g, GraphicsPath glyphs, SizeF size, float em, float scale,
            int outW, int outH, WatermarkSettings s)
        {
            float gap = em * s.Spacing / 100f;
            float stepX = (size.Width + gap) * scale, stepY = (size.Height + gap) * scale; // layer pixels
            float radius = em * scale * HaloRadius;

            using var path = (GraphicsPath)glyphs.Clone();
            using (var m = new Matrix())
            {
                m.Scale(scale, scale);
                m.Rotate(s.Rotation);
                path.Transform(m); // rotated tile, centered on the origin, in layer pixels
            }
            RectangleF b = path.GetBounds();
            float pad = s.Outline ? radius * 3 + 2 : 2;
            int sw = (int)Math.Ceiling(b.Width + pad * 2), sh = (int)Math.Ceiling(b.Height + pad * 2);
            float ox = pad - b.X, oy = pad - b.Y; // where the tile center sits inside the sprite

            using var sprite = new Bitmap(sw, sh, PixelFormat.Format32bppPArgb);
            using (var sg = Graphics.FromImage(sprite))
            {
                sg.SmoothingMode = SmoothingMode.AntiAlias;
                sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                sg.TranslateTransform(ox, oy);
                sg.FillPath(Brushes.White, path);
            }

            Color text = Color.FromArgb(255, s.Color), halo = HaloColor(s.Color);
            int r = Math.Max(1, (int)Math.Round(radius));
            WithPixels(sprite, (px, stride) =>
            {
                var at = ExtractAlpha(px, stride, sw, sh);
                byte[] ah = null;
                if (s.Outline)
                {
                    ah = (byte[])at.Clone();
                    Blur(ah, sw, sh, r);
                }
                for (int y = 0; y < sh; y++)
                    for (int x = 0; x < sw; x++)
                    {
                        int i = y * sw + x, o = y * stride + x * 4;
                        int ta = at[i];
                        int ha = ah == null ? 0 : HaloAlpha(ah[i]) * (255 - ta) / 255; // glow shows only outside the text
                        px[o] = (byte)((text.B * ta + halo.B * ha) / 255);
                        px[o + 1] = (byte)((text.G * ta + halo.G * ha) / 255);
                        px[o + 2] = (byte)((text.R * ta + halo.R * ha) / 255);
                        px[o + 3] = (byte)(ta + ha);
                    }
            });

            // Brick pattern centered on the photo; odd rows shift by half a step.
            double rad = s.Rotation * Math.PI / 180.0;
            float cos = (float)Math.Cos(rad), sin = (float)Math.Sin(rad);
            float cx = outW / 2f, cy = outH / 2f;
            float reach = (float)Math.Sqrt((double)outW * outW + (double)outH * outH) / 2f + Math.Max(sw, sh);
            int rows = (int)Math.Ceiling(reach / stepY) + 1, cols = (int)Math.Ceiling(reach / stepX) + 2;

            g.CompositingMode = CompositingMode.SourceOver;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.None;
            var srcRect = new Rectangle(0, 0, sw, sh);
            for (int row = -rows; row <= rows; row++)
            {
                float shift = (Math.Abs(row) % 2) * stepX / 2f;
                for (int col = -cols; col <= cols; col++)
                {
                    float lx = col * stepX + shift, ly = row * stepY;
                    int x = (int)Math.Round(cx + lx * cos - ly * sin - ox);
                    int y = (int)Math.Round(cy + lx * sin + ly * cos - oy);
                    if (x >= outW || y >= outH || x + sw <= 0 || y + sh <= 0) continue;
                    g.DrawImage(sprite, new Rectangle(x, y, sw, sh), srcRect, GraphicsUnit.Pixel);
                }
            }
        }

        // ------------------------------------------------------------------ pixel helpers

        static void WithPixels(Bitmap bmp, Action<byte[], int> edit)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            try
            {
                var px = new byte[data.Stride * bmp.Height];
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                edit(px, data.Stride);
                Marshal.Copy(px, 0, data.Scan0, px.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        static byte[] ExtractAlpha(byte[] px, int stride, int w, int h)
        {
            var a = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride + 3, dst = y * w;
                for (int x = 0; x < w; x++) a[dst + x] = px[row + x * 4];
            }
            return a;
        }

        // Strengthen the blurred falloff so the glow reads, capped at 75% so it never looks like a box.
        static int HaloAlpha(int blurred) => Math.Min(255, blurred * 2) * 3 / 4;

        static void PutPremultiplied(byte[] px, int o, Color c, int alpha)
        {
            px[o] = (byte)(c.B * alpha / 255);
            px[o + 1] = (byte)(c.G * alpha / 255);
            px[o + 2] = (byte)(c.R * alpha / 255);
            px[o + 3] = (byte)alpha;
        }

        /// <summary>Three box blurs in each direction approximate a Gaussian.</summary>
        static void Blur(byte[] a, int w, int h, int r)
        {
            var tmp = new byte[a.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(a, tmp, w, h, r, horizontal: true);
                BoxBlur(tmp, a, w, h, r, horizontal: false);
            }
        }

        // Running-sum box blur along one axis; O(pixels) whatever the radius.
        static void BoxBlur(byte[] src, byte[] dst, int w, int h, int r, bool horizontal)
        {
            int lines = horizontal ? h : w, len = horizontal ? w : h;
            int step = horizontal ? 1 : w, lineStep = horizontal ? w : 1;
            int div = r * 2 + 1;
            int Index(int i) => Math.Max(0, Math.Min(len - 1, i));
            for (int l = 0; l < lines; l++)
            {
                int start = l * lineStep, sum = 0;
                for (int k = -r; k <= r; k++) sum += src[start + Index(k) * step];
                for (int i = 0; i < len; i++)
                {
                    dst[start + i * step] = (byte)(sum / div);
                    sum += src[start + Index(i + r + 1) * step] - src[start + Index(i - r) * step];
                }
            }
        }

        // ------------------------------------------------------------------ fonts and colors

        static FontFamily ResolveFamily(string name)
        {
            foreach (var candidate in new[] { name, "Segoe UI", "Arial" })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try { return new FontFamily(candidate); }
                catch (ArgumentException) { /* not installed */ }
            }
            return new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif);
        }

        static FontStyle ResolveStyle(FontFamily family, bool bold, bool italic)
        {
            var wanted = (bold ? FontStyle.Bold : 0) | (italic ? FontStyle.Italic : 0);
            foreach (var style in new[] { wanted, FontStyle.Regular, FontStyle.Bold, FontStyle.Italic, FontStyle.Bold | FontStyle.Italic })
                if (family.IsStyleAvailable(style)) return style;
            return FontStyle.Regular;
        }

        static Color HaloColor(Color text) => IsLight(text) ? Color.Black : Color.White;

        public static bool IsLight(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0 > 0.5;
    }
}
