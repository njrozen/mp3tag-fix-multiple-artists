using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ArtistFixer
{
    /// <summary>Colours. Follows the Windows light/dark app setting, like Mp3tag.</summary>
    public class Theme
    {
        public bool Dark;
        public Color Back, Panel, Grid, GridAlt, Text, Dim, Header, Border, Select, SelectText;
        public Color SepBack, SepText, IgnBack, IgnText, Good, Bad, Button, Accent, AccentText;

        public static Theme Current()
        {
            bool dark = false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    dark = v is int && (int)v == 0;
                }
            }
            catch (Exception) { }
            return dark ? DarkTheme() : LightTheme();
        }

        static Theme DarkTheme()
        {
            return new Theme
            {
                Dark = true,
                Back = Color.FromArgb(32, 32, 32), Panel = Color.FromArgb(40, 40, 40),
                Grid = Color.FromArgb(30, 30, 30), GridAlt = Color.FromArgb(36, 36, 36),
                Text = Color.FromArgb(225, 225, 225), Dim = Color.FromArgb(150, 150, 150),
                Header = Color.FromArgb(45, 45, 45), Border = Color.FromArgb(70, 70, 70),
                Select = Color.FromArgb(38, 79, 120), SelectText = Color.White,
                SepBack = Color.FromArgb(150, 110, 0), SepText = Color.FromArgb(255, 236, 170),
                IgnBack = Color.FromArgb(60, 60, 60), IgnText = Color.FromArgb(140, 140, 140),
                Good = Color.FromArgb(120, 210, 140), Bad = Color.FromArgb(240, 120, 110),
                Button = Color.FromArgb(55, 55, 55), Accent = Color.FromArgb(0, 120, 212), AccentText = Color.White,
            };
        }

        static Theme LightTheme()
        {
            return new Theme
            {
                Dark = false,
                Back = Color.FromArgb(243, 243, 243), Panel = Color.FromArgb(250, 250, 250),
                Grid = Color.White, GridAlt = Color.FromArgb(246, 248, 250),
                Text = Color.FromArgb(25, 25, 25), Dim = Color.FromArgb(100, 100, 100),
                Header = Color.FromArgb(235, 235, 235), Border = Color.FromArgb(205, 205, 205),
                Select = Color.FromArgb(204, 228, 247), SelectText = Color.Black,
                SepBack = Color.FromArgb(255, 214, 102), SepText = Color.FromArgb(70, 45, 0),
                IgnBack = Color.FromArgb(225, 225, 225), IgnText = Color.FromArgb(120, 120, 120),
                Good = Color.FromArgb(0, 128, 50), Bad = Color.FromArgb(190, 30, 30),
                Button = Color.FromArgb(253, 253, 253), Accent = Color.FromArgb(0, 103, 192), AccentText = Color.White,
            };
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public void ApplyTitleBar(Form f)
        {
            if (!Dark) return;
            int on = 1;
            try { DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch (Exception) { }
        }

        public void Apply(Control root)
        {
            foreach (Control c in root.Controls) ApplyOne(c);
        }

        void ApplyOne(Control c)
        {
            if (c is DataGridView) return; // styled separately
            if (c is Label) ((Label)c).UseMnemonic = false; // show "&" in artist names instead of treating it as a shortcut key
            if (c is Button)
            {
                var b = (Button)c;
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Border;
                bool accent = "accent".Equals(b.Tag);
                b.BackColor = accent ? Accent : Button;
                b.ForeColor = accent ? AccentText : Text;
                if (accent) b.FlatAppearance.BorderColor = Accent;
            }
            else if (c is ComboBox || c is TextBoxBase)
            {
                c.BackColor = Grid; c.ForeColor = Text;
                if (c is ComboBox) ((ComboBox)c).FlatStyle = FlatStyle.Flat;
            }
            else if (c is Label && "dim".Equals(c.Tag)) c.ForeColor = Dim;
            else if (c is CheckBox) { c.ForeColor = Text; ((CheckBox)c).FlatStyle = FlatStyle.Flat; }
            else c.ForeColor = Text;

            if (c is Panel || c is SplitContainer || c is SplitterPanel || c is FlowLayoutPanel || c is TableLayoutPanel || c is Label)
                c.BackColor = "panel".Equals(c.Tag) ? Panel : c.Parent.BackColor;
            foreach (Control child in c.Controls) ApplyOne(child);
        }
    }
}
