using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PhotoWatermarker
{
    /// <summary>
    /// Makes PhotoWatermarker.exe self-contained. The Magick.NET assemblies are embedded as resources and
    /// loaded from memory; the native ImageMagick DLL is embedded too and unpacked once to
    /// %LOCALAPPDATA%\PhotoWatermarker\native\&lt;version&gt;.
    /// </summary>
    static class EmbeddedLibraries
    {
        const string ManagedPrefix = "lib.";
        const string NativeResource = "native.Magick.Native-Q8-x64.dll";
        static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        public static void Install()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            string dir = ExtractNative();
            if (dir != null) PointMagickAt(dir);
        }

        static Assembly OnAssemblyResolve(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name;
            lock (Loaded)
            {
                if (Loaded.TryGetValue(name, out var cached)) return cached;
                using var stream = typeof(EmbeddedLibraries).Assembly.GetManifestResourceStream(ManagedPrefix + name + ".dll");
                if (stream == null) return null;
                var asm = Assembly.Load(ReadAll(stream));
                Loaded[name] = asm;
                return asm;
            }
        }

        static string ExtractNative()
        {
            using var stream = typeof(EmbeddedLibraries).Assembly.GetManifestResourceStream(NativeResource);
            if (stream == null) return null; // development build: the DLL sits next to the exe

            string version = typeof(EmbeddedLibraries).Assembly.GetName().Version + "-" + stream.Length;
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoWatermarker", "native", version);
            string target = Path.Combine(dir, "Magick.Native-Q8-x64.dll");
            if (File.Exists(target) && new FileInfo(target).Length == stream.Length)
            {
                RemoveOtherVersions(dir);
                return dir;
            }

            Directory.CreateDirectory(dir);
            string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = File.Create(tmp)) stream.CopyTo(file);
            try
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
            }
            catch (IOException)
            {
                // Another instance finished unpacking first; its copy is identical.
                File.Delete(tmp);
            }
            RemoveOtherVersions(dir);
            return dir;
        }

        // Each app version unpacks into its own folder; clear out the ones left by earlier versions.
        static void RemoveOtherVersions(string keep)
        {
            foreach (var old in Directory.GetDirectories(Path.GetDirectoryName(keep)))
            {
                if (string.Equals(old, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(old, recursive: true); }
                catch (IOException) { /* an older version is still running */ }
                catch (UnauthorizedAccessException) { }
            }
        }

        // Kept in its own method so Magick.NET is only resolved after the AssemblyResolve hook is in place.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void PointMagickAt(string dir) => ImageMagick.MagickNET.SetNativeLibraryDirectory(dir);

        static byte[] ReadAll(Stream s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
