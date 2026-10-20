using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// Without this the runtime uses legacy path handling, which rejects paths over 260 characters.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace ArtistFixer
{
    public class Song
    {
        public string Path;
        public string FileName;
        public string Title;
        public string[] Values = new string[0];  // artist values as stored in the file
        public string Artist = "";               // values joined the way Mp3tag shows them
        public List<Sep> Seps = new List<Sep>();
        public bool Checked;
        public bool ManualEdits;                 // user changed separators by hand; keep them on re-detect
        public SepChoice MainOverride;           // null = use the global choice
        public SepChoice FeatOverride;
        public string Status = "";
        public string[] UndoValues;              // values before the last save

        public bool HasMultiple { get { return ArtistParser.HasActive(Seps); } }

        public string[] NewValues(SepChoice globalMain, SepChoice globalFeat)
        {
            return ArtistParser.Rebuild(Artist, Seps, MainOverride ?? globalMain, FeatOverride ?? globalFeat);
        }

        public static string Join(string[] values)
        {
            return string.Join("\\\\", values);
        }

        public void Redetect(IEnumerable<string> protectedNames)
        {
            Seps = ArtistParser.Detect(Artist, protectedNames);
            ManualEdits = false;
        }
    }

    public static class TagIO
    {
        public static readonly string[] Extensions =
        {
            ".mp3", ".flac", ".m4a", ".mp4", ".aac", ".ogg", ".oga", ".opus", ".wma",
            ".wav", ".aif", ".aiff", ".ape", ".wv", ".mpc", ".dsf"
        };

        public static bool IsAudio(string path)
        {
            return Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());
        }

        /// <summary>Adds the \\?\ prefix so paths longer than 260 characters work.</summary>
        public static string LongPath(string path)
        {
            if (path.Length < 240 || path.StartsWith(@"\\?\")) return path;
            if (path.StartsWith(@"\\")) return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }

        public static Song Read(string path)
        {
            var song = new Song { Path = path, FileName = System.IO.Path.GetFileName(path) };
            using (var f = TagLib.File.Create(LongPath(path)))
            {
                song.Title = f.Tag.Title ?? "";
                song.Values = (f.Tag.Performers ?? new string[0]).Where(v => !string.IsNullOrEmpty(v)).ToArray();

                // TagLib splits ID3v2.3 artist frames on "/", Mp3tag doesn't ("AC/DC" is one value). Undo that.
                var id3 = f.GetTag(TagLib.TagTypes.Id3v2, false) as TagLib.Id3v2.Tag;
                if (id3 != null && id3.Version < 4)
                {
                    var frame = TagLib.Id3v2.TextInformationFrame.Get(id3, "TPE1", false);
                    if (frame != null && frame.Text.Length > 0) song.Values = new[] { string.Join("/", frame.Text) };
                }
            }
            song.Artist = Song.Join(song.Values);
            return song;
        }

        /// <summary>Writes the artist field, touching only the tag types already in the file.</summary>
        public static void Write(string path, string[] values)
        {
            TagLib.Id3v2.Tag.DefaultVersion = 3;
            using (var f = TagLib.File.Create(LongPath(path)))
            {
                var onDisk = f.TagTypesOnDisk;
                bool any = false;
                foreach (TagLib.TagTypes t in Enum.GetValues(typeof(TagLib.TagTypes)))
                {
                    if (t == TagLib.TagTypes.None || t == TagLib.TagTypes.AllTags) continue;
                    if ((onDisk & t) != t) continue;
                    TagLib.Tag tag = null;
                    try { tag = f.GetTag(t, false); } catch (Exception) { }
                    if (tag == null) continue;
                    tag.Performers = values;
                    any = true;
                }
                if (!any) f.Tag.Performers = values;
                else
                {
                    // Don't add tag types (e.g. ID3v1) the file didn't have before.
                    var added = f.TagTypes & ~onDisk;
                    if (added != TagLib.TagTypes.None) f.RemoveTags(added);
                }
                f.Save();
            }
        }
    }

    /// <summary>Settings and the list of names that should never be split, stored in %APPDATA%\ArtistFixer.</summary>
    public static class Settings
    {
        static readonly string Dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArtistFixer");
        static string NamesFile { get { return System.IO.Path.Combine(Dir, "single-artist-names.txt"); } }
        static string IniFile { get { return System.IO.Path.Combine(Dir, "settings.ini"); } }

        public static readonly string[] DefaultNames =
        {
            "AC/DC", "Simon & Garfunkel", "Earth, Wind & Fire", "Tyler, The Creator", "Mumford & Sons",
            "Hall & Oates", "Daryl Hall & John Oates", "Kool & the Gang", "Sly & the Family Stone",
            "Crosby, Stills, Nash & Young", "Crosby, Stills & Nash", "Peter, Bjorn and John",
            "Belle and Sebastian", "Florence and the Machine", "Of Monsters and Men", "Angus & Julia Stone",
            "Above & Beyond", "Chase & Status", "Brooks & Dunn", "Big & Rich", "Matt and Kim",
            "Coheed and Cambria", "Iron & Wine", "Me & My Monkey", "Sam & Dave", "Ike & Tina Turner",
            "Diplo & Friends", "Marina and the Diamonds", "Echo & the Bunnymen", "Hootie & the Blowfish",
            "Tom Petty and the Heartbreakers", "Bob Marley & The Wailers", "Huey Lewis and the News",
            "Bruce Springsteen & The E Street Band", "Prince and the Revolution", "Lil Nas X", "Brand X"
        };

        public static List<string> LoadNames()
        {
            try
            {
                if (File.Exists(NamesFile))
                    return File.ReadAllLines(NamesFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            }
            catch (Exception) { }
            return DefaultNames.ToList();
        }

        public static void SaveNames(IEnumerable<string> names)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(NamesFile, names.Where(n => n.Trim().Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            }
            catch (Exception) { }
        }

        public static Dictionary<string, string> LoadIni()
        {
            var d = new Dictionary<string, string>();
            try
            {
                if (File.Exists(IniFile))
                    foreach (var line in File.ReadAllLines(IniFile))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) d[line.Substring(0, eq)] = line.Substring(eq + 1);
                    }
            }
            catch (Exception) { }
            return d;
        }

        public static void SaveIni(Dictionary<string, string> d)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(IniFile, d.Select(kv => kv.Key + "=" + kv.Value).ToArray());
            }
            catch (Exception) { }
        }
    }
}
