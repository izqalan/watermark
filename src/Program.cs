using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PhotoWatermarker
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                EmbeddedLibraries.Install();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Photo Watermarker couldn't start because its image library failed to load.\n\n" + ex.Message,
                    "Photo Watermarker", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            if (args.Length > 0 && args[0].Equals("--render", StringComparison.OrdinalIgnoreCase))
                return CommandLine.Render(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => ShowCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowCrash(e.ExceptionObject as Exception);
            Application.Run(new MainForm(args));
            return 0;
        }

        static void ShowCrash(Exception ex) =>
            MessageBox.Show("Something went wrong:\n\n" + ex?.Message, "Photo Watermarker", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// PhotoWatermarker.exe --render "watermark text" "output folder" photo1.heic photo2.jpg ...
    /// Uses the settings last chosen in the app, with the given text. Handy for scripts.
    /// </summary>
    static class CommandLine
    {
        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        public static int Render(string[] args)
        {
            AttachConsole(-1);
            if (args.Length < 4)
            {
                Console.Error.WriteLine("Usage: PhotoWatermarker.exe --render \"watermark text\" \"output folder\" photo1 [photo2 ...]");
                return 2;
            }
            var settings = WatermarkSettings.Load();
            settings.Text = args[1];
            settings.OutputFolder = Path.GetFullPath(args[2]);
            int failed = 0;
            for (int i = 3; i < args.Length; i++)
            {
                try
                {
                    var item = PhotoItem.Load(Path.GetFullPath(args[i]), new System.Drawing.Size(8, 8));
                    using (item) Console.WriteLine(Exporter.Export(item, settings));
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.Error.WriteLine(args[i] + ": " + ex.Message);
                }
            }
            return failed == 0 ? 0 : 1;
        }
    }
}
