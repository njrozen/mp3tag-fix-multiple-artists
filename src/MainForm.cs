using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace ArtistFixer
{
    /// <summary>The song list. Clicking a checkbox inside a multi-row selection toggles every selected row.</summary>
    public class SongGrid : DataGridView
    {
        public event Action<int> CheckClick;
        public event Action SpacePressed;

        public SongGrid() { DoubleBuffered = true; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var hit = HitTest(e.X, e.Y);
            if (e.Button == MouseButtons.Left && hit.Type == DataGridViewHitTestType.Cell && hit.ColumnIndex == 0
                && ModifierKeys == Keys.None)
            {
                if (Rows[hit.RowIndex].Selected && SelectedRows.Count > 1)
                {
                    // Keep the selection; toggle all of it.
                    swallowUp = true;
                    if (CheckClick != null) CheckClick(hit.RowIndex);
                    return;
                }
                base.OnMouseDown(e);
                if (CheckClick != null) CheckClick(hit.RowIndex);
                return;
            }
            base.OnMouseDown(e);
        }

        bool swallowUp;

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (swallowUp) { swallowUp = false; return; } // its MouseDown never reached the grid
            base.OnMouseUp(e);
        }

        protected override bool ProcessDataGridViewKey(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && e.Modifiers == Keys.None)
            {
                if (SpacePressed != null) SpacePressed();
                return true;
            }
            return base.ProcessDataGridViewKey(e);
        }
    }

    public class MainForm : Form
    {
        readonly Theme theme = Theme.Current();
        readonly List<Song> songs = new List<Song>();
        readonly HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> protectedNames = Settings.LoadNames();
        List<Song> lastSaved = new List<Song>();

        readonly BlockingCollection<string> pending = new BlockingCollection<string>();
        readonly ConcurrentQueue<Song> loaded = new ConcurrentQueue<Song>();
        int loading;

        SongGrid grid;
        ComboBox cbMain, cbFeat, cbSongMain, cbSongFeat;
        CheckBox chkOnlyMulti;
        RichTextBox rtb;
        Label lblEditing, lblResult, lblCounts, lblStatus;
        Button btnSave, btnUndo, btnMark, btnToggle, btnRemember, btnReset;
        System.Windows.Forms.Timer pump;
        bool suppress;
        Font monoBold;

        const int ColCheck = 0, ColFile = 1, ColTitle = 2, ColArtist = 3, ColFound = 4, ColNew = 5, ColStatus = 6;

        public MainForm()
        {
            Text = "Fix Multiple Artists — for Mp3tag";
            Font = new Font("Segoe UI", 9f);
            Size = new Size(1280, 760);
            MinimumSize = new Size(900, 520);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = theme.Back;
            AllowDrop = true;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            BuildUi();
            LoadSettings();
            theme.Apply(this);
            StyleGrid();

            var loader = new Thread(LoaderLoop) { IsBackground = true, Name = "tag loader" };
            loader.Start();

            pump = new System.Windows.Forms.Timer { Interval = 200 };
            pump.Tick += (s, e) => Pump();
            pump.Start();

            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (s, e) =>
            {
                var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths != null) foreach (var p in paths) Queue(p);
            };
            HandleCreated += (s, e) => theme.ApplyTitleBar(this);
            FormClosing += (s, e) => SaveSettings();
            UpdateCounts();
            UpdateEditor();
        }

        // ───────────────────────────── UI construction ─────────────────────────────

        Button MakeButton(string text, EventHandler click, string tag = null)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 28, Margin = new Padding(0, 0, 6, 0), Padding = new Padding(6, 0, 6, 0), Tag = tag };
            b.Click += click;
            return b;
        }

        Label MakeLabel(string text, string tag = null)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(10, 7, 4, 0), Tag = tag };
        }

        ComboBox MakeCombo(IEnumerable<SepChoice> items, int width, bool withGlobal)
        {
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = width, Margin = new Padding(0, 3, 6, 0), MaxDropDownItems = 20 };
            if (withGlobal) cb.Items.Add(new SepChoice("(use the setting above)", SepChoice.Global));
            foreach (var c in items) cb.Items.Add(c);
            cb.SelectedIndex = 0;
            return cb;
        }

        void BuildUi()
        {
            var tips = new ToolTip { AutoPopDelay = 15000 };
            monoBold = new Font("Segoe UI Semibold", 9f);

            // Row 1: list management.
            var bar1 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(8, 6, 8, 0), WrapContents = false, Tag = "panel" };
            bar1.Controls.Add(MakeButton("Add files…", (s, e) => AddFiles()));
            bar1.Controls.Add(MakeButton("Add folder…", (s, e) => AddFolder()));
            bar1.Controls.Add(MakeButton("Clear list", (s, e) => ClearList()));
            bar1.Controls.Add(new Label { Width = 12 });
            bar1.Controls.Add(MakeButton("Check all with multiple artists", (s, e) => SetChecked(songs.Where(x => x.HasMultiple), true)));
            bar1.Controls.Add(MakeButton("Uncheck all", (s, e) => SetChecked(songs, false)));
            chkOnlyMulti = new CheckBox { Text = "Only show songs with multiple artists", AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
            chkOnlyMulti.CheckedChanged += (s, e) => RebuildRows();
            bar1.Controls.Add(chkOnlyMulti);
            var btnNames = MakeButton("Single-artist names…", (s, e) => EditNames());
            btnNames.Margin = new Padding(16, 0, 0, 0);
            tips.SetToolTip(btnNames, "Names like \"Simon & Garfunkel\" that contain a separator but are one artist.\nSeparators inside these names are never highlighted.");
            bar1.Controls.Add(btnNames);

            // Row 2: what to replace separators with (applies to all checked songs).
            var bar2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 2, 8, 4), WrapContents = false, Tag = "panel" };
            var l1 = MakeLabel("Replace separators with:"); l1.Margin = new Padding(0, 7, 6, 0);
            bar2.Controls.Add(l1);
            cbMain = MakeCombo(SepChoice.MainChoices, 260, false);
            tips.SetToolTip(cbMain, "Pick a separator, or type your own.\nTyped text is used exactly — include spaces, e.g. \" + \".");
            bar2.Controls.Add(cbMain);
            bar2.Controls.Add(MakeLabel("Featured artists (feat. / ft.):"));
            cbFeat = MakeCombo(SepChoice.FeatChoices, 200, false);
            tips.SetToolTip(cbFeat, "How the part after \"feat.\" / \"ft.\" / \"featuring\" is written.\n\"Same as other artists\" treats feat. like any other separator.");
            bar2.Controls.Add(cbFeat);
            bar2.Controls.Add(MakeLabel("Applies to every checked song unless you change it for a song below.", "dim"));
            cbMain.TextChanged += (s, e) => { if (!suppress) RefreshAllRows(); };
            cbFeat.TextChanged += (s, e) => { if (!suppress) RefreshAllRows(); };

            // Grid.
            grid = new SongGrid
            {
                Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, ReadOnly = true,
                RowHeadersVisible = false, AutoGenerateColumns = false, BorderStyle = BorderStyle.None,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleVertical, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 28, StandardTab = true,
            };
            grid.RowTemplate.Height = 26;
            var colCheck = new DataGridViewCheckBoxColumn { HeaderText = "", Width = 34, SortMode = DataGridViewColumnSortMode.NotSortable, Resizable = DataGridViewTriState.False };
            grid.Columns.Add(colCheck);
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Filename", Width = 280 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Title", Width = 190 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Artist", Width = 300 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Separators", Width = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "New artist", Width = 280 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 80 });
            grid.CheckClick += OnCheckClick;
            grid.SpacePressed += () => ToggleSelected();
            grid.SelectionChanged += (s, e) => UpdateEditor();
            grid.CellPainting += OnCellPainting;
            grid.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Check selected", null, (s, e) => SetChecked(SelectedSongs(), true));
            menu.Items.Add("Uncheck selected", null, (s, e) => SetChecked(SelectedSongs(), false));
            menu.Items.Add("Re-detect separators", null, (s, e) => { foreach (var x in SelectedSongs()) x.Redetect(protectedNames); RefreshAllRows(); UpdateEditor(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Remove from list", null, (s, e) => RemoveSelected());
            grid.ContextMenuStrip = menu;

            // Editor for the selected song(s).
            var editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10, 8, 10, 6), Tag = "panel" };
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            lblEditing = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5f), Margin = new Padding(0, 0, 0, 4) };
            editor.Controls.Add(lblEditing);

            rtb = new RichTextBox
            {
                Multiline = false, ReadOnly = true, Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI", 13f),
                BorderStyle = BorderStyle.FixedSingle, DetectUrls = false, Cursor = Cursors.IBeam, ShortcutsEnabled = true,
            };
            rtb.MouseUp += OnEditorClick;
            rtb.SelectionChanged += (s, e) => { if (!suppress) UpdateEditorButtons(); };
            editor.Controls.Add(rtb);

            var hint = new Label
            {
                AutoSize = true, Tag = "dim", Margin = new Padding(0, 4, 0, 4),
                Text = "Click a highlighted separator to ignore it (e.g. the \"&\" in a duo's name) — click again to use it.   " +
                       "To add one the program missed, select the text and press \"Mark as separator\"."
            };
            editor.Controls.Add(hint);

            var tools = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 4) };
            btnMark = MakeButton("Mark selection as separator", (s, e) => MarkSelection());
            btnToggle = MakeButton("Ignore separator", (s, e) => ToggleAtCaret());
            btnRemember = MakeButton("Always treat selection as one artist", (s, e) => RememberSelection());
            btnReset = MakeButton("Re-detect", (s, e) => { var x = CurrentSong(); if (x != null) { x.Redetect(protectedNames); RefreshSong(x); UpdateEditor(); } });
            tips.SetToolTip(btnRemember, "Adds the selected text to your single-artist names (e.g. \"Simon & Garfunkel\")\nso it is never split in any song.");
            tools.Controls.AddRange(new Control[] { btnMark, btnToggle, btnRemember, btnReset });
            editor.Controls.Add(tools);

            var perSong = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 2) };
            var l3 = MakeLabel("For the selected song(s), replace with:"); l3.Margin = new Padding(0, 7, 6, 0);
            perSong.Controls.Add(l3);
            cbSongMain = MakeCombo(SepChoice.MainChoices, 230, true);
            perSong.Controls.Add(cbSongMain);
            perSong.Controls.Add(MakeLabel("Featured:"));
            cbSongFeat = MakeCombo(SepChoice.FeatChoices, 200, true);
            perSong.Controls.Add(cbSongFeat);
            cbSongMain.TextChanged += (s, e) => { if (!suppress) SetOverride(true); };
            cbSongFeat.TextChanged += (s, e) => { if (!suppress) SetOverride(false); };
            editor.Controls.Add(perSong);

            lblResult = new Label { AutoSize = true, Font = new Font("Segoe UI", 11f), Margin = new Padding(0, 6, 0, 0) };
            editor.Controls.Add(lblResult);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5, FixedPanel = FixedPanel.Panel2 };
            split.Panel1.Controls.Add(grid);
            split.Panel2.Controls.Add(editor);

            // Bottom bar.
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 44, ColumnCount = 4, Padding = new Padding(8, 6, 8, 6), Tag = "panel" };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lblCounts = new Label { AutoSize = true, Margin = new Padding(0, 7, 16, 0) };
            lblStatus = new Label { AutoSize = true, Margin = new Padding(0, 7, 0, 0), Tag = "dim" };
            btnUndo = MakeButton("Undo last save", (s, e) => UndoLastSave());
            btnUndo.Enabled = false;
            btnSave = MakeButton("Save checked songs", (s, e) => SaveChecked(), "accent");
            btnSave.Font = new Font("Segoe UI Semibold", 9.5f);
            btnSave.Margin = new Padding(0);
            bottom.Controls.Add(lblCounts, 0, 0);
            bottom.Controls.Add(lblStatus, 1, 0);
            bottom.Controls.Add(btnUndo, 2, 0);
            bottom.Controls.Add(btnSave, 3, 0);

            Controls.Add(split);
            Controls.Add(bottom);
            Controls.Add(bar2);
            Controls.Add(bar1);

            Shown += (s, e) =>
            {
                try { split.SplitterDistance = Math.Max(150, split.Height - 230); } catch (Exception) { }
            };
        }

        void StyleGrid()
        {
            grid.EnableHeadersVisualStyles = false;
            grid.BackgroundColor = theme.Grid;
            grid.GridColor = theme.Border;
            grid.DefaultCellStyle.BackColor = theme.Grid;
            grid.DefaultCellStyle.ForeColor = theme.Text;
            grid.DefaultCellStyle.SelectionBackColor = theme.Select;
            grid.DefaultCellStyle.SelectionForeColor = theme.SelectText;
            grid.DefaultCellStyle.Padding = new Padding(2, 0, 2, 0);
            grid.AlternatingRowsDefaultCellStyle.BackColor = theme.GridAlt;
            grid.ColumnHeadersDefaultCellStyle.BackColor = theme.Header;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = theme.Text;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = theme.Header;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 0, 0);
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            rtb.BackColor = theme.Grid;
            rtb.ForeColor = theme.Text;
        }

        // ───────────────────────────── Loading ─────────────────────────────

        public void Queue(string path)
        {
            Interlocked.Increment(ref loading);
            pending.Add(path);
        }

        void LoaderLoop()
        {
            foreach (var path in pending.GetConsumingEnumerable())
            {
                try
                {
                    if (path.Length == 0) continue;
                    string io = TagIO.LongPath(path);
                    if (Directory.Exists(io))
                    {
                        IEnumerable<string> files;
                        try
                        {
                            files = Directory.EnumerateFiles(io, "*", SearchOption.AllDirectories).Where(TagIO.IsAudio)
                                .Select(f => f.StartsWith(@"\\?\UNC\") ? @"\\" + f.Substring(8) : f.StartsWith(@"\\?\") ? f.Substring(4) : f)
                                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
                        }
                        catch (Exception) { files = new string[0]; }
                        foreach (var f in files) LoadOne(f);
                    }
                    else if (File.Exists(io)) LoadOne(path);
                    else loaded.Enqueue(new Song { Path = path, FileName = Path.GetFileName(path), Title = "", Status = "File not found" });
                }
                finally { Interlocked.Decrement(ref loading); }
            }
        }

        void LoadOne(string path)
        {
            Song s;
            try { s = TagIO.Read(path); }
            catch (Exception ex)
            {
                s = new Song { Path = path, FileName = Path.GetFileName(path), Title = "", Status = "Can't read: " + ex.Message };
            }
            loaded.Enqueue(s);
        }

        void Pump()
        {
            bool fromMp3tag = false;
            string p;
            while (Program.Incoming.TryDequeue(out p)) { Queue(p); fromMp3tag = true; }
            if (fromMp3tag && IsHandleCreated)
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
            }

            Song s;
            var batch = new List<Song>();
            while (batch.Count < 2000 && loaded.TryDequeue(out s))
            {
                if (!known.Add(s.Path)) continue;
                s.Seps = ArtistParser.Detect(s.Artist, protectedNames);
                s.Checked = s.HasMultiple;
                batch.Add(s);
            }
            if (batch.Count > 0)
            {
                songs.AddRange(batch);
                bool wasEmpty = grid.Rows.Count == 0;
                AddRows(batch);
                if (wasEmpty && grid.Rows.Count > 0) { grid.ClearSelection(); grid.Rows[0].Selected = true; grid.CurrentCell = grid.Rows[0].Cells[ColFile]; }
                UpdateCounts();
            }
            if (loading > 0) lblStatus.Text = "Reading tags…";
            else if (lblStatus.Text == "Reading tags…") lblStatus.Text = "";
        }

        void AddRows(IEnumerable<Song> batch)
        {
            var rows = new List<DataGridViewRow>();
            foreach (var s in batch)
            {
                if (chkOnlyMulti.Checked && !s.HasMultiple) continue;
                var row = new DataGridViewRow();
                row.CreateCells(grid);
                row.Tag = s;
                FillRow(row, s);
                rows.Add(row);
            }
            grid.Rows.AddRange(rows.ToArray());
        }

        void RebuildRows()
        {
            var current = CurrentSong();
            grid.Rows.Clear();
            AddRows(songs);
            if (current != null)
                foreach (DataGridViewRow r in grid.Rows)
                    if (r.Tag == current) { grid.CurrentCell = r.Cells[ColFile]; break; }
            UpdateCounts();
        }

        void AddFiles()
        {
            using (var d = new OpenFileDialog { Multiselect = true, Title = "Add songs" })
            {
                d.Filter = "Audio files|" + string.Join(";", TagIO.Extensions.Select(x => "*" + x)) + "|All files|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) foreach (var f in d.FileNames) Queue(f);
            }
        }

        void AddFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "Add every song in this folder (including subfolders)" })
                if (d.ShowDialog(this) == DialogResult.OK) Queue(d.SelectedPath);
        }

        void ClearList()
        {
            songs.Clear(); known.Clear(); grid.Rows.Clear(); lastSaved.Clear();
            btnUndo.Enabled = false; lblStatus.Text = "";
            UpdateCounts(); UpdateEditor();
        }

        void RemoveSelected()
        {
            foreach (DataGridViewRow r in grid.SelectedRows.Cast<DataGridViewRow>().ToList())
            {
                var s = (Song)r.Tag;
                songs.Remove(s); known.Remove(s.Path);
                grid.Rows.Remove(r);
            }
            UpdateCounts(); UpdateEditor();
        }

        // ───────────────────────────── Rows & painting ─────────────────────────────

        SepChoice Resolve(ComboBox cb)
        {
            var item = cb.SelectedItem as SepChoice;
            if (item != null && cb.Text == item.Label) return item;
            foreach (SepChoice c in cb.Items) if (c.Label == cb.Text) return c;
            if (cb.Text.Length == 0) return null;
            return new SepChoice(cb.Text, cb.Text); // typed by the user, used exactly
        }

        SepChoice GlobalMain { get { return Resolve(cbMain); } }
        SepChoice GlobalFeat { get { return Resolve(cbFeat); } }

        string NewArtistText(Song s, out bool changed)
        {
            var values = s.NewValues(GlobalMain, GlobalFeat);
            string text = Song.Join(values);
            changed = text != s.Artist || values.Length != s.Values.Length;
            return text;
        }

        void FillRow(DataGridViewRow row, Song s)
        {
            bool changed;
            string newText = NewArtistText(s, out changed);
            row.Cells[ColCheck].Value = s.Checked;
            row.Cells[ColFile].Value = s.FileName;
            row.Cells[ColTitle].Value = s.Title;
            row.Cells[ColArtist].Value = s.Artist;
            row.Cells[ColFound].Value = ArtistParser.Describe(s.Seps);
            row.Cells[ColNew].Value = changed ? newText : (s.HasMultiple ? "(no change)" : "");
            row.Cells[ColNew].Style.ForeColor = changed ? theme.Good : theme.Dim;
            row.Cells[ColStatus].Value = s.Status;
            row.Cells[ColStatus].Style.ForeColor = s.Status.StartsWith("Saved") || s.Status.StartsWith("Restored") ? theme.Good
                : s.Status.Length > 0 && !s.Status.StartsWith("Will") ? theme.Bad : theme.Dim;
        }

        void RefreshSong(Song s)
        {
            foreach (DataGridViewRow r in grid.Rows)
                if (r.Tag == s) { FillRow(r, s); grid.InvalidateRow(r.Index); break; }
            UpdateCounts();
        }

        void RefreshAllRows()
        {
            foreach (DataGridViewRow r in grid.Rows) FillRow(r, (Song)r.Tag);
            grid.Invalidate();
            UpdateCounts();
            UpdateEditor();
        }

        void OnCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColArtist) return;
            var s = grid.Rows[e.RowIndex].Tag as Song;
            if (s == null || s.Seps.Count == 0) return;

            bool selected = (e.State & DataGridViewElementStates.Selected) != 0;
            e.PaintBackground(e.CellBounds, selected);
            var g = e.Graphics;
            var bounds = e.CellBounds;
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            Color normal = selected ? e.CellStyle.SelectionForeColor : e.CellStyle.ForeColor;
            int x = bounds.X + 5;
            var oldClip = g.Clip;
            g.SetClip(new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height));

            int pos = 0;
            Action<string, Sep> draw = (text, sep) =>
            {
                if (text.Length == 0 || x > bounds.Right) return;
                var font = sep == null ? e.CellStyle.Font : monoBold;
                int w = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, bounds.Height), flags).Width;
                var r = new Rectangle(x, bounds.Y + 4, w, bounds.Height - 8);
                if (sep != null)
                {
                    using (var b = new SolidBrush(sep.Enabled ? theme.SepBack : theme.IgnBack)) g.FillRectangle(b, r);
                    TextRenderer.DrawText(g, text, font, new Rectangle(x, bounds.Y, w, bounds.Height), sep.Enabled ? theme.SepText : theme.IgnText, flags);
                    if (!sep.Enabled)
                        using (var pen = new Pen(theme.IgnText)) g.DrawLine(pen, x + 1, bounds.Y + bounds.Height / 2, x + w - 1, bounds.Y + bounds.Height / 2);
                }
                else TextRenderer.DrawText(g, text, font, new Rectangle(x, bounds.Y, w + 2, bounds.Height), normal, flags);
                x += w;
            };
            foreach (var sep in s.Seps)
            {
                draw(s.Artist.Substring(pos, sep.Start - pos), null);
                draw(s.Artist.Substring(sep.Start, sep.Length), sep);
                pos = sep.End;
            }
            draw(s.Artist.Substring(pos), null);
            g.Clip = oldClip;
            e.Handled = true;
        }

        // ───────────────────────────── Checking ─────────────────────────────

        IEnumerable<Song> SelectedSongs()
        {
            return grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).Select(r => (Song)r.Tag).ToList();
        }

        Song CurrentSong()
        {
            if (grid.CurrentRow != null && grid.CurrentRow.Selected) return grid.CurrentRow.Tag as Song;
            var sel = grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).FirstOrDefault();
            return sel == null ? null : sel.Tag as Song;
        }

        void OnCheckClick(int rowIndex)
        {
            var row = grid.Rows[rowIndex];
            bool value = !((Song)row.Tag).Checked;
            if (row.Selected) SetChecked(SelectedSongs(), value);
            else SetChecked(new[] { (Song)row.Tag }, value);
        }

        void ToggleSelected()
        {
            var sel = SelectedSongs().ToList();
            if (sel.Count == 0) return;
            SetChecked(sel, !sel.All(s => s.Checked));
        }

        void SetChecked(IEnumerable<Song> which, bool value)
        {
            var set = new HashSet<Song>(which);
            foreach (var s in set) s.Checked = value;
            foreach (DataGridViewRow r in grid.Rows)
                if (set.Contains((Song)r.Tag)) r.Cells[ColCheck].Value = value;
            UpdateCounts();
        }

        void UpdateCounts()
        {
            int multi = songs.Count(s => s.HasMultiple);
            int check = songs.Count(s => s.Checked);
            int willChange = 0;
            foreach (var s in songs.Where(x => x.Checked)) { bool c; NewArtistText(s, out c); if (c) willChange++; }
            lblCounts.Text = string.Format("{0} songs  ·  {1} with multiple artists  ·  {2} checked  ·  {3} will change",
                songs.Count, multi, check, willChange);
            btnSave.Text = willChange > 0 ? string.Format("Save {0} checked song{1}", willChange, willChange == 1 ? "" : "s") : "Save checked songs";
            btnSave.Enabled = willChange > 0;
        }

        // ───────────────────────────── Editor panel ─────────────────────────────

        void UpdateEditor()
        {
            var s = CurrentSong();
            int count = grid.SelectedRows.Count;
            suppress = true;
            try
            {
                if (s == null)
                {
                    lblEditing.Text = songs.Count == 0
                        ? "No songs yet. In Mp3tag, select songs, right-click → Tools → Fix multiple artists. Or drop files/folders here."
                        : "Select a song to see and edit its separators.";
                    rtb.Text = ""; lblResult.Text = "";
                    rtb.Enabled = cbSongMain.Enabled = cbSongFeat.Enabled = false;
                    UpdateEditorButtons();
                    return;
                }
                rtb.Enabled = cbSongMain.Enabled = cbSongFeat.Enabled = true;
                lblEditing.Text = "Artist field of: " + s.FileName + (count > 1 ? string.Format("     ({0} songs selected — the dropdowns below change all of them)", count) : "");
                PaintEditor(s);
                SelectChoice(cbSongMain, s.MainOverride);
                SelectChoice(cbSongFeat, s.FeatOverride);
                bool changed;
                string text = NewArtistText(s, out changed);
                lblResult.ForeColor = changed ? theme.Good : theme.Dim;
                lblResult.Text = changed ? "New artist:  " + text : (s.HasMultiple ? "New artist:  (no change)" : "No separators found — select text and press \"Mark selection as separator\" to add one.");
            }
            finally { suppress = false; }
            UpdateEditorButtons();
        }

        void SelectChoice(ComboBox cb, SepChoice choice)
        {
            if (choice == null) { cb.SelectedIndex = 0; return; }
            int i = cb.Items.IndexOf(choice);
            if (i >= 0) cb.SelectedIndex = i;
            else { cb.SelectedIndex = -1; cb.Text = choice.Label; }
        }

        void PaintEditor(Song s)
        {
            int selStart = rtb.SelectionStart, selLen = rtb.SelectionLength;
            bool same = rtb.Text == s.Artist;
            rtb.Text = s.Artist;
            rtb.SelectAll();
            rtb.SelectionBackColor = theme.Grid;
            rtb.SelectionColor = theme.Text;
            rtb.SelectionFont = rtb.Font;
            foreach (var sep in s.Seps)
            {
                rtb.Select(sep.Start, sep.Length);
                rtb.SelectionBackColor = sep.Enabled ? theme.SepBack : theme.IgnBack;
                rtb.SelectionColor = sep.Enabled ? theme.SepText : theme.IgnText;
                rtb.SelectionFont = new Font(rtb.Font, sep.Enabled ? FontStyle.Bold : FontStyle.Strikeout);
            }
            if (same) rtb.Select(selStart, selLen); else rtb.Select(0, 0);
        }

        static Sep SepAt(Song s, int charIndex)
        {
            return s.Seps.FirstOrDefault(x => charIndex >= x.Start && charIndex < x.End);
        }

        Sep SepAtCaret(Song s)
        {
            return SepAt(s, rtb.SelectionStart);
        }

        void UpdateEditorButtons()
        {
            var s = CurrentSong();
            bool hasSel = s != null && rtb.SelectionLength > 0;
            btnMark.Enabled = hasSel;
            btnRemember.Enabled = hasSel;
            btnReset.Enabled = s != null;
            var sep = s == null ? null : SepAtCaret(s);
            btnToggle.Enabled = sep != null;
            btnToggle.Text = sep != null && !sep.Enabled ? "Use separator" : "Ignore separator";
        }

        void OnEditorClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || rtb.SelectionLength > 0) return;
            var s = CurrentSong();
            if (s == null || s.Artist.Length == 0) return;
            // The character under the mouse (the caret lands between characters, which is ambiguous).
            int i = rtb.GetCharIndexFromPosition(e.Location);
            var last = rtb.GetPositionFromCharIndex(s.Artist.Length - 1);
            if (e.X > last.X + 20) return; // clicked past the end of the text
            Toggle(s, SepAt(s, i));
        }

        void ToggleAtCaret()
        {
            var s = CurrentSong();
            if (s != null) Toggle(s, SepAtCaret(s));
        }

        void Toggle(Song s, Sep sep)
        {
            if (sep == null) return;
            sep.Enabled = !sep.Enabled;
            s.ManualEdits = true;
            AfterSongEdit(s);
        }

        void MarkSelection()
        {
            var s = CurrentSong();
            if (s == null || rtb.SelectionLength == 0) return;
            int start = rtb.SelectionStart, len = rtb.SelectionLength;
            // Widen to the surrounding spaces so "A x B" -> " x ".
            while (start > 0 && s.Artist[start - 1] == ' ') { start--; len++; }
            while (start + len < s.Artist.Length && s.Artist[start + len] == ' ') len++;
            if (start == 0 || start + len >= s.Artist.Length)
            {
                MessageBox.Show(this, "A separator needs an artist name on both sides of it.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            s.Seps.RemoveAll(x => x.Start < start + len && start < x.End);
            s.Seps.Add(ArtistParser.Manual(s.Artist, start, len));
            s.Seps.Sort((a, b) => a.Start.CompareTo(b.Start));
            s.ManualEdits = true;
            if (!s.Checked) SetChecked(new[] { s }, true);
            rtb.Select(start + len, 0);
            AfterSongEdit(s);
        }

        void RememberSelection()
        {
            var s = CurrentSong();
            if (s == null || rtb.SelectionLength == 0) return;
            string name = rtb.SelectedText.Trim();
            if (name.Length == 0) return;
            if (!protectedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                protectedNames.Add(name);
                Settings.SaveNames(protectedNames);
            }
            ApplyProtection(name);
            lblStatus.Text = "\"" + name + "\" will be treated as one artist from now on.";
        }

        void ApplyProtection(string name)
        {
            foreach (var song in songs)
            {
                int idx = 0;
                bool hit = false;
                while ((idx = song.Artist.IndexOf(name, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    int end = idx + name.Length;
                    foreach (var sep in song.Seps)
                        if (sep.Enabled && sep.CoreWithin(song.Artist, idx, end)) { sep.Enabled = false; sep.Protected = true; hit = true; }
                    idx = end;
                }
                if (hit && !song.HasMultiple && song.Checked) song.Checked = false;
            }
            RefreshAllRows();
        }

        void AfterSongEdit(Song s)
        {
            RefreshSong(s);
            UpdateEditor();
        }

        void SetOverride(bool main)
        {
            var cb = main ? cbSongMain : cbSongFeat;
            var choice = Resolve(cb);
            if (choice != null && choice.Value == SepChoice.Global) choice = null;
            foreach (var s in SelectedSongs())
            {
                if (main) s.MainOverride = choice; else s.FeatOverride = choice;
                RefreshSong(s);
            }
            bool changed;
            var cur = CurrentSong();
            if (cur != null)
            {
                string text = NewArtistText(cur, out changed);
                lblResult.ForeColor = changed ? theme.Good : theme.Dim;
                lblResult.Text = changed ? "New artist:  " + text : "New artist:  (no change)";
            }
        }

        void EditNames()
        {
            using (var f = new Form { Text = "Single-artist names", Size = new Size(460, 520), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, BackColor = theme.Back, Font = Font, ShowInTaskbar = false })
            {
                var info = new Label { Dock = DockStyle.Top, Height = 54, Padding = new Padding(10, 10, 10, 0), Text = "One name per line. Separators inside these names are ignored in every song (not case sensitive).", Tag = "dim" };
                var box = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Text = string.Join(Environment.NewLine, protectedNames), Font = new Font("Segoe UI", 10f) };
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
                var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true, Tag = "accent" };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
                var reset = new Button { Text = "Restore defaults", AutoSize = true };
                reset.Click += (s, e) => box.Text = string.Join(Environment.NewLine, Settings.DefaultNames);
                buttons.Controls.AddRange(new Control[] { ok, cancel, reset });
                var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 0) };
                pad.Controls.Add(box);
                f.Controls.Add(pad); f.Controls.Add(buttons); f.Controls.Add(info);
                f.AcceptButton = null; f.CancelButton = cancel;
                theme.Apply(f);
                f.HandleCreated += (s, e) => theme.ApplyTitleBar(f);
                if (f.ShowDialog(this) != DialogResult.OK) return;
                protectedNames = box.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                Settings.SaveNames(protectedNames);
                foreach (var s in songs.Where(x => !x.ManualEdits)) s.Redetect(protectedNames);
                foreach (var n in protectedNames) ApplyProtection(n);
                RefreshAllRows();
            }
        }

        // ───────────────────────────── Saving ─────────────────────────────

        void SaveChecked()
        {
            var work = new List<KeyValuePair<Song, string[]>>();
            foreach (var s in songs.Where(x => x.Checked))
            {
                bool changed;
                NewArtistText(s, out changed);
                if (changed) work.Add(new KeyValuePair<Song, string[]>(s, s.NewValues(GlobalMain, GlobalFeat)));
            }
            if (work.Count == 0) return;

            var preview = string.Join(Environment.NewLine, work.Take(8).Select(w => "  " + w.Key.Artist + "   →   " + Song.Join(w.Value)));
            if (work.Count > 8) preview += Environment.NewLine + string.Format("  … and {0} more", work.Count - 8);
            var answer = MessageBox.Show(this,
                string.Format("Write the new artist field to {0} song{1}?\n\n{2}\n\nTip: save any pending changes in Mp3tag first, and press F5 in Mp3tag afterwards to reload.",
                    work.Count, work.Count == 1 ? "" : "s", preview),
                "Save artist fields", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK) return;

            Cursor = Cursors.WaitCursor;
            lastSaved = new List<Song>();
            int ok = 0, failed = 0;
            foreach (var w in work)
            {
                var s = w.Key;
                try
                {
                    TagIO.Write(s.Path, w.Value);
                    s.UndoValues = s.Values;
                    s.Values = w.Value;
                    s.Artist = Song.Join(w.Value);
                    s.Redetect(protectedNames);
                    s.MainOverride = s.FeatOverride = null;
                    s.Checked = false;
                    s.Status = "Saved";
                    lastSaved.Add(s);
                    ok++;
                }
                catch (Exception ex)
                {
                    s.Status = "Error: " + ex.Message;
                    failed++;
                }
            }
            Cursor = Cursors.Default;
            btnUndo.Enabled = lastSaved.Count > 0;
            lblStatus.Text = string.Format("Saved {0} song{1}{2}. Press F5 in Mp3tag to reload them.", ok, ok == 1 ? "" : "s",
                failed > 0 ? string.Format(", {0} failed (see Status column)", failed) : "");
            RefreshAllRows();
        }

        void UndoLastSave()
        {
            if (lastSaved.Count == 0) return;
            if (MessageBox.Show(this, string.Format("Put back the previous artist field on {0} song{1}?", lastSaved.Count, lastSaved.Count == 1 ? "" : "s"),
                "Undo last save", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            int ok = 0;
            foreach (var s in lastSaved)
            {
                try
                {
                    TagIO.Write(s.Path, s.UndoValues);
                    s.Values = s.UndoValues;
                    s.Artist = Song.Join(s.Values);
                    s.Redetect(protectedNames);
                    s.Checked = s.HasMultiple;
                    s.Status = "Restored";
                    ok++;
                }
                catch (Exception ex) { s.Status = "Undo failed: " + ex.Message; }
            }
            lastSaved.Clear();
            btnUndo.Enabled = false;
            lblStatus.Text = string.Format("Restored {0} song{1}. Press F5 in Mp3tag to reload them.", ok, ok == 1 ? "" : "s");
            RefreshAllRows();
        }

        // ───────────────────────────── Settings ─────────────────────────────

        void LoadSettings()
        {
            var ini = Settings.LoadIni();
            suppress = true;
            string v;
            if (ini.TryGetValue("main", out v)) cbMain.Text = v;
            if (ini.TryGetValue("feat", out v)) cbFeat.Text = v;
            if (ini.TryGetValue("onlyMulti", out v)) chkOnlyMulti.Checked = v == "1";
            suppress = false;
        }

        void SaveSettings()
        {
            Settings.SaveIni(new Dictionary<string, string>
            {
                { "main", cbMain.Text }, { "feat", cbFeat.Text }, { "onlyMulti", chkOnlyMulti.Checked ? "1" : "0" },
            });
        }
    }
}
