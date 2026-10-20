using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Fix Multiple Artists for Mp3tag")]
[assembly: System.Reflection.AssemblyProduct("Fix Multiple Artists")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace ArtistFixer
{
    /// <summary>
    /// Mp3tag starts a tool once per selected file, so the first copy opens the window and every
    /// later copy hands its file over through a named pipe and exits.
    /// </summary>
    static class Program
    {
        public static readonly ConcurrentQueue<string> Incoming = new ConcurrentQueue<string>();
        static readonly string PipeName = "ArtistFixer.Pipe." + Environment.UserName;

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);

        [STAThread]
        static void Main(string[] args)
        {
            bool first;
            using (var mutex = new Mutex(true, @"Local\ArtistFixer.SingleInstance." + Environment.UserName, out first))
            {
                if (!first && SendToRunning(args)) return;
                if (first) StartServer();

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var form = new MainForm();
                foreach (var a in args) form.Queue(FullPath(a));
                Application.Run(form);
            }
        }

        static string FullPath(string p)
        {
            try { return Path.GetFullPath(p.Trim().Trim('"')); } catch (Exception) { return p; }
        }

        static bool SendToRunning(string[] args)
        {
            try
            {
                AllowSetForegroundWindow(-1); // let the open window come to the front
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(15000);
                    var w = new StreamWriter(client, new UTF8Encoding(false));
                    foreach (var a in args) w.WriteLine(FullPath(a));
                    w.Flush();
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        static void StartServer()
        {
            var t = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                        {
                            server.WaitForConnection();
                            using (var r = new StreamReader(server, Encoding.UTF8))
                            {
                                string line;
                                bool any = false;
                                while ((line = r.ReadLine()) != null)
                                    if (line.Length > 0) { Incoming.Enqueue(line); any = true; }
                                if (!any) Incoming.Enqueue(""); // bare launch: still bring the window forward
                            }
                        }
                    }
                    catch (Exception) { Thread.Sleep(100); }
                }
            }) { IsBackground = true, Name = "single instance" };
            t.Start();
        }
    }
}
