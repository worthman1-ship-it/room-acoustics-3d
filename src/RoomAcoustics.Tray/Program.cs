using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Windows.Forms;

namespace RoomAcoustics.Tray;

internal static class Program
{
    private const string Url = "http://localhost:5080";

    [STAThread]
    private static void Main()
    {
        // one copy at a time: a second launch just opens the page of the first
        using var single = new Mutex(true, "RoomAcousticsWebTray", out var first);
        if (!first) { Open(); return; }

        var dir = AppContext.BaseDirectory;
        var engineExe = Path.Combine(dir, "RoomAcoustics.Api.exe");
        if (!File.Exists(engineExe))
        {
            MessageBox.Show($"RoomAcoustics.Api.exe was not found beside this program ({dir}).", "Room Acoustics", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Process? started;
        try
        {
            started = Process.Start(new ProcessStartInfo(engineExe) { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The engine would not start: {ex.Message}", "Room Acoustics", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (started is null) return;
        var engine = started;

        ApplicationConfiguration.Initialize();
        using var icon = MakeIcon();
        var menu = new ContextMenuStrip();
        var tray = new NotifyIcon { Icon = icon, Text = "Room Acoustics (web)", Visible = true, ContextMenuStrip = menu };
        void Quit()
        {
            tray.Visible = false;
            try { if (!engine.HasExited) engine.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            Application.Exit();
        }
        menu.Items.Add("Open Room Acoustics", null, (_, _) => Open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit (stops the engine)", null, (_, _) => Quit());
        tray.DoubleClick += (_, _) => Open();

        // if the engine stops by itself (a port already in use, a crash), say so and leave
        engine.EnableRaisingEvents = true;
        engine.Exited += (_, _) =>
        {
            if (!tray.Visible) return;
            tray.ShowBalloonTip(5000, "Room Acoustics", "The engine stopped (is another copy using port 5080?).", ToolTipIcon.Warning);
            Task.Delay(6000).ContinueWith(_ => { tray.Visible = false; Application.Exit(); });
        };

        // open the page once the engine answers
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            for (var i = 0; i < 120; i++)
            {
                try { if ((await http.GetAsync(Url + "/api/health")).IsSuccessStatusCode) { Open(); return; } } catch { }
                await Task.Delay(500);
            }
        });

        Application.Run();
        try { if (!engine.HasExited) engine.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    private static void Open() => Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });

    /// <summary>A small icon drawn here, so there is nothing to ship beside the program: a violet disc with sound waves.</summary>
    private static Icon MakeIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var disc = new SolidBrush(Color.FromArgb(124, 58, 237));
            g.FillEllipse(disc, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.FillEllipse(Brushes.White, 6, 13, 6, 6);
            g.DrawArc(pen, 9, 9, 12, 14, -55, 110);
            g.DrawArc(pen, 6, 5, 20, 22, -55, 110);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
