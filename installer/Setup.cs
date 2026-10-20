using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Fix Multiple Artists Setup")]
[assembly: System.Reflection.AssemblyProduct("Fix Multiple Artists")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace ArtistFixerSetup
{
    /// <summary>
    /// One-file installer: the app and TagLibSharp.dll are embedded as resources.
    /// Re-launches itself elevated only when the chosen folder needs it (e.g. Program Files).
    /// </summary>
    static class Setup
    {
        public const string AppExe = "FixMultipleArtists.exe";
        public static readonly string[] Payload = { AppExe, "TagLibSharp.dll" };
        const string RegKey = @"Software\FixMultipleArtists";

        [STAThread]
        static int Main(string[] args)
        {
            // Elevated helper mode: Setup.exe --install "dir" | --uninstall "dir"
            if (args.Length == 2 && args[0] == "--install") return Run(() => Install(args[1]));
            if (args.Length == 2 && args[0] == "--uninstall") return Run(() => Uninstall(args[1]));

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var form = new SetupForm();
            if (args.Length == 2 && args[0] == "--preview-done") form.ShowDonePage(args[1]); // for docs screenshots
            Application.Run(form);
            return 0;
        }

        static int Run(Action a)
        {
            try { a(); return 0; }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Fix Multiple Artists Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        // ───────────── locations ─────────────

        public static string FindMp3tagExe()
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using (var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Mp3tag.exe"))
                    {
                        var v = k == null ? null : k.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(v) && File.Exists(v.Trim('"'))) return v.Trim('"');
                    }
                    foreach (var sub in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Mp3tag",
                                                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Mp3tag" })
                        using (var k = hive.OpenSubKey(sub))
                        {
                            var loc = k == null ? null : k.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrEmpty(loc) && File.Exists(Path.Combine(loc, "Mp3tag.exe"))) return Path.Combine(loc, "Mp3tag.exe");
                        }
                }
                catch (Exception) { }
            }
            foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                       Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                var p = Path.Combine(pf, "Mp3tag", "Mp3tag.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public static string DefaultDir()
        {
            var previous = PreviousDir();
            if (previous != null) return previous;
            var mp3tag = FindMp3tagExe();
            if (mp3tag != null) return Path.Combine(Path.GetDirectoryName(mp3tag), "Fix Multiple Artists");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Fix Multiple Artists");
        }

        public static string PreviousDir()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    var v = k == null ? null : k.GetValue("InstallDir") as string;
                    if (v != null && File.Exists(Path.Combine(v, AppExe))) return v;
                }
            }
            catch (Exception) { }
            return null;
        }

        public static void Remember(string dir)
        {
            try
            {
                if (dir == null) Registry.CurrentUser.DeleteSubKeyTree(RegKey, false);
                else using (var k = Registry.CurrentUser.CreateSubKey(RegKey)) k.SetValue("InstallDir", dir);
            }
            catch (Exception) { }
        }

        /// <summary>True if Mp3tag's settings already mention our exe (i.e. the Tools entry exists).</summary>
        public static bool InMp3tagTools()
        {
            try
            {
                var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mp3tag", "mp3tag.cfg");
                if (!File.Exists(cfg)) return false;
                var bytes = File.ReadAllBytes(cfg);
                foreach (var enc in new Encoding[] { Encoding.Unicode, Encoding.UTF8 })
                    if (enc.GetString(bytes).IndexOf(AppExe, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch (Exception) { }
            return false;
        }

        // ───────────── work ─────────────

        public static bool CanWrite(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        public static void Install(string dir)
        {
            if (Process.GetProcessesByName("FixMultipleArtists").Length > 0)
                throw new InvalidOperationException("Fix Multiple Artists is open. Close it and try again.");
            Directory.CreateDirectory(dir);
            var asm = typeof(Setup).Assembly;
            foreach (var name in Payload)
                using (var src = asm.GetManifestResourceStream(name))
                using (var dst = File.Create(Path.Combine(dir, name)))
                    src.CopyTo(dst);
        }

        public static void Uninstall(string dir)
        {
            if (Process.GetProcessesByName("FixMultipleArtists").Length > 0)
                throw new InvalidOperationException("Fix Multiple Artists is open. Close it and try again.");
            foreach (var name in Payload)
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) File.Delete(p);
            }
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch (Exception) { }
        }

        /// <summary>Runs this setup elevated for one step. Returns false if the user said no to the UAC prompt.</summary>
        public static bool RunElevated(string verb, string dir)
        {
            var psi = new ProcessStartInfo(Application.ExecutablePath, verb + " \"" + dir.TrimEnd('\\') + "\"")
            {
                Verb = "runas", UseShellExecute = true
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch (System.ComponentModel.Win32Exception) { return false; } // UAC cancelled
        }
    }

    class SetupForm : Form
    {
        readonly Panel page = new Panel { Dock = DockStyle.Fill };
        readonly FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 10, 16, 10) };
        TextBox dirBox;

        public SetupForm()
        {
            Text = "Fix Multiple Artists — Setup";
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(620, 470);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Controls.Add(page);
            Controls.Add(new Label { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.Gainsboro });
            Controls.Add(buttons);
            buttons.BackColor = Color.FromArgb(243, 243, 243);
            ShowChoosePage();
        }

        Label Heading(string text, int y)
        {
            var l = new Label { Text = text, Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, Location = new Point(0, y), UseMnemonic = false };
            page.Controls.Add(l);
            return l;
        }

        Label Para(string text, int y, int height)
        {
            var l = new Label { Text = text, Location = new Point(0, y), Size = new Size(570, height), UseMnemonic = false };
            page.Controls.Add(l);
            return l;
        }

        Button AddButton(string text, EventHandler click, bool primary = false)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(96, 30), Margin = new Padding(8, 0, 0, 0) };
            if (primary) { b.BackColor = Color.FromArgb(0, 103, 192); b.ForeColor = Color.White; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0; AcceptButton = b; }
            b.Click += click;
            buttons.Controls.Add(b);
            return b;
        }

        /// <summary>Controls are laid out from (0,0); this adds the page margin.</summary>
        void Indent()
        {
            foreach (Control c in page.Controls) c.Left += 24;
            foreach (Control c in page.Controls) c.Top += 18;
        }

        void Reset()
        {
            page.Controls.Clear();
            buttons.Controls.Clear();
            AcceptButton = null;
        }

        // ───────────── page 1: choose folder ─────────────

        void ShowChoosePage()
        {
            Reset();
            var mp3tag = Setup.FindMp3tagExe();
            var previous = Setup.PreviousDir();
            Heading("Install Fix Multiple Artists", 0);
            Para("Finds songs with several artists in the Artist tag (\"A & B feat. C\", \"A / B\", \"A x B\" …) " +
                 "and rewrites them with the separator you choose. It opens from Mp3tag's right-click menu.", 40, 44);

            Para("Install to:", 104, 20).Font = new Font("Segoe UI Semibold", 9.5f);
            dirBox = new TextBox { Text = Setup.DefaultDir(), Location = new Point(0, 128), Width = 470 };
            var browse = new Button { Text = "Browse…", Location = new Point(478, 126), Size = new Size(92, 28) };
            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Choose where to install Fix Multiple Artists. A \"Fix Multiple Artists\" folder is created inside it.", ShowNewFolderButton = true })
                {
                    if (d.ShowDialog(this) == DialogResult.OK)
                        dirBox.Text = d.SelectedPath.TrimEnd('\\').EndsWith("Fix Multiple Artists", StringComparison.OrdinalIgnoreCase)
                            ? d.SelectedPath : Path.Combine(d.SelectedPath, "Fix Multiple Artists");
                }
            };
            page.Controls.Add(dirBox);
            page.Controls.Add(browse);

            string note;
            if (previous != null) note = "Already installed here — installing again updates it.";
            else if (mp3tag != null) note = "Default: next to Mp3tag (" + Path.GetDirectoryName(mp3tag) + "). Windows will ask for permission to write there.";
            else note = "Mp3tag wasn't found on this PC. Install Mp3tag from mp3tag.de first, or install here and set it up later.";
            var n = Para(note, 160, 40);
            n.ForeColor = Color.DimGray;

            Para("What gets installed: FixMultipleArtists.exe and TagLibSharp.dll (the tag reader), nothing else. " +
                 "No services, no registry changes besides remembering this folder. Remove it any time by running Setup again.", 214, 60).ForeColor = Color.DimGray;

            AddButton("Cancel", (s, e) => Close());
            AddButton(previous != null ? "Update" : "Install", (s, e) => DoInstall(), true);
            if (previous != null) AddButton("Uninstall", (s, e) => DoUninstall(previous));
            Indent();
        }

        void DoInstall()
        {
            string dir;
            try { dir = Path.GetFullPath(dirBox.Text.Trim()); }
            catch (Exception) { MessageBox.Show(this, "That folder path isn't valid.", Text); return; }

            Cursor = Cursors.WaitCursor;
            try
            {
                if (Setup.CanWrite(dir)) Setup.Install(dir);
                else if (!Setup.RunElevated("--install", dir)) { Cursor = Cursors.Default; return; }
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Cursor = Cursors.Default;
            if (!File.Exists(Path.Combine(dir, Setup.AppExe))) return;
            Setup.Remember(dir);
            ShowDonePage(dir);
        }

        void DoUninstall(string dir)
        {
            if (MessageBox.Show(this, "Remove Fix Multiple Artists from\n" + dir + " ?\n\nAlso delete its Tools entry in Mp3tag (File → Options → Tools).",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try
            {
                if (Setup.CanWrite(dir)) Setup.Uninstall(dir);
                else if (!Setup.RunElevated("--uninstall", dir)) return;
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            if (File.Exists(Path.Combine(dir, Setup.AppExe))) return;
            Setup.Remember(null);
            MessageBox.Show(this, "Fix Multiple Artists was removed.", Text);
            Close();
        }

        // ───────────── page 2: add to Mp3tag ─────────────

        public void ShowDonePage(string dir)
        {
            Reset();
            string exe = Path.Combine(dir, Setup.AppExe);
            Heading("✔  Installed. One last step: add it to Mp3tag", 0);

            if (Setup.InMp3tagTools())
            {
                Para("Mp3tag already has it in its Tools menu, so you're done.\n\n" +
                     "In Mp3tag: select songs → right-click → Tools → Fix multiple artists.", 44, 80);
            }
            else
            {
                Para("In Mp3tag open  File → Options → Tools,  click the  New  icon (top right), and copy these in:", 44, 22);
                int y = 76;
                AddField("Name:", "Fix multiple artists", ref y);
                AddField("Path:", exe, ref y);
                AddField("Parameter:", "\"%_path%\"", ref y);
                Para("Then tick  ☑ for all selected files  and click OK.", y + 2, 22);
                Para("To use it: select songs in Mp3tag → right-click → Tools → Fix multiple artists " +
                     "(Ctrl+1 if it's your first tool). After saving, press F5 in Mp3tag to reload the tags.", y + 34, 44).ForeColor = Color.DimGray;
            }

            AddButton("Finish", (s, e) => Close(), true);
            var mp3tag = Setup.FindMp3tagExe();
            if (mp3tag != null) AddButton("Open Mp3tag", (s, e) => { try { Process.Start(mp3tag); } catch (Exception) { } });
            AddButton("Open folder", (s, e) => { try { Process.Start("explorer.exe", "\"" + dir + "\""); } catch (Exception) { } });
            Indent();
        }

        void AddField(string label, string value, ref int y)
        {
            page.Controls.Add(new Label { Text = label, Location = new Point(0, y + 4), AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f) });
            var box = new TextBox { Text = value, ReadOnly = true, Location = new Point(80, y), Width = 400, BackColor = Color.FromArgb(246, 246, 246), Font = new Font("Consolas", 9.5f) };
            var copy = new Button { Text = "Copy", Location = new Point(488, y - 2), Size = new Size(82, 28) };
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(value); copy.Text = "Copied ✔"; } catch (Exception) { }
                foreach (var other in page.Controls.OfType<Button>()) if (other != copy && other.Text.StartsWith("Copied")) other.Text = "Copy";
            };
            page.Controls.Add(box);
            page.Controls.Add(copy);
            y += 40;
        }
    }
}
