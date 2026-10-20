using System;
using System.IO;
using System.Linq;
using ArtistFixer;

static class EngineTests
{
    static int failures;

    static SepChoice M(string v) { return SepChoice.MainChoices.First(c => c.Value == v); }
    static SepChoice Feat(string v) { return SepChoice.FeatChoices.First(c => c.Value == v); }
    static SepChoice FeatParen(string w) { return SepChoice.FeatChoices.First(c => c.ParenWord == w); }

    static void Check(string name, string actual, string expected)
    {
        bool ok = actual == expected;
        if (!ok) failures++;
        Console.WriteLine("{0} {1}\n      got:      {2}{3}", ok ? "PASS" : "FAIL", name, actual, ok ? "" : "\n      expected: " + expected);
    }

    static string Found(string artist)
    {
        return ArtistParser.Describe(ArtistParser.Detect(artist, Settings.DefaultNames));
    }

    static string Fix(string artist, SepChoice main, SepChoice feat)
    {
        return Song.Join(ArtistParser.Rebuild(artist, ArtistParser.Detect(artist, Settings.DefaultNames), main, feat));
    }

    static int Main()
    {
        var comma = M(", "); var amp = M(" & "); var semi = M("; "); var multi = M(SepChoice.Multi); var keep = M(SepChoice.Keep);
        var same = Feat(SepChoice.Same); var featDot = Feat(" feat. "); var ftDot = Feat(" ft. "); var fkeep = Feat(SepChoice.Keep);

        // Detection (the songs from the screenshot plus edge cases)
        Check("detect & + feat.", Found("Chief Keef & DJ KENN AON feat. Gino Marley"), "&  feat.");
        Check("detect slashes", Found("6nk / SMUEX / Thoomrsxoh"), "/");
        Check("detect X (case-insensitive)", Found("AK47 X SYQRA"), "x");
        Check("detect two kinds", Found("6EJOU / GUS & JORG"), "/  &");
        Check("detect with", Found("Daedelus with Busdriver"), "with");
        Check("detect and/FEAT upper", Found("Brandy AND Monica FEAT. Mase"), "and  feat.");
        Check("detect ft. no space", Found("Drake ft.Rihanna"), "feat.");
        Check("detect comma+semicolon", Found("A, B; C"), ",  ;");
        Check("detect (feat.)", Found("Artist (Feat. Other)"), "feat.");
        Check("no false positive: Xavier", Found("Xavier Wulf"), "");
        Check("no false positive: Alexander", Found("Alexander O'Neal"), "");
        Check("no false positive: Daft Punk", Found("Daft Punk"), "");
        Check("no false positive: Andy", Found("Andy Mineo"), "");
        Check("protected: Simon & Garfunkel", Found("Simon & Garfunkel"), "");
        Check("protected: AC/DC", Found("AC/DC"), "");
        Check("protected name inside list", Found("Earth, Wind & Fire, The Emotions"), ",");
        Check("mp3tag multi-value", Found("A\\\\B"), "\\\\");
        Check("protected name ending in X", Found("Lil Nas X with Billy Ray Cyrus"), "with");
        Check("protected name x at start", Found("Brand X & Phil Collins"), "&");

        // Rebuilding
        Check("& + feat -> comma, keep feat", Fix("Chief Keef & DJ KENN AON feat. Gino Marley", comma, fkeep), "Chief Keef, DJ KENN AON feat. Gino Marley");
        Check("& + feat -> all semicolon", Fix("Chief Keef & DJ KENN AON feat. Gino Marley", semi, same), "Chief Keef; DJ KENN AON; Gino Marley");
        Check("slashes -> &", Fix("6nk / SMUEX / Thoomrsxoh", amp, same), "6nk & SMUEX & Thoomrsxoh");
        Check("X -> comma", Fix("AK47 X SYQRA", comma, same), "AK47, SYQRA");
        Check("with -> feat.", Fix("Daedelus with Busdriver", M(" feat. "), same), "Daedelus feat. Busdriver");
        Check("(feat.) -> ft.", Fix("Artist (feat. Other)", comma, ftDot), "Artist ft. Other");
        Check("list + feat -> (feat. ...)", Fix("A, B feat. C & D", amp, FeatParen("feat.")), "A & B (feat. C & D)");
        Check("keep main, change feat", Fix("A / B ft. C", keep, featDot), "A / B feat. C");
        Check("keep everything", Fix("A / B (ft. C)", keep, fkeep), "A / B (ft. C)");
        Check("multi-value", Fix("A, B & C", multi, same), "A\\\\B\\\\C");
        Check("multi-value + feat", Fix("A, B feat. C", multi, featDot), "A\\\\B feat. C");
        Check("protected kept intact", Fix("Earth, Wind & Fire, The Emotions", amp, same), "Earth, Wind & Fire & The Emotions");

        // Manual edits: ignore the "&" in a duo name, then add a missed separator.
        string s = "6EJOU / GUS & JORG";
        var seps = ArtistParser.Detect(s, Settings.DefaultNames);
        seps.First(x => x.Name == "&").Enabled = false;
        Check("ignore & in duo name", Song.Join(ArtistParser.Rebuild(s, seps, comma, same)), "6EJOU, GUS & JORG");
        string t = "Tom + Jerry";
        var manual = ArtistParser.Detect(t, Settings.DefaultNames);
        manual.Add(ArtistParser.Manual(t, 3, 3));
        Check("manual separator", Song.Join(ArtistParser.Rebuild(t, manual, comma, same)), "Tom, Jerry");

        // Writing tags to a real file
        string dir = Path.Combine(Path.GetTempPath(), "ArtistFixerTest");
        Directory.CreateDirectory(dir);
        string mp3 = Path.Combine(dir, "test.mp3");
        WriteSilentMp3(mp3);
        using (var f = TagLib.File.Create(mp3))
        {
            TagLib.Id3v2.Tag.DefaultVersion = 3; TagLib.Id3v2.Tag.ForceDefaultVersion = true;
            var tag = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
            tag.Performers = new[] { "Chief Keef & DJ KENN AON feat. Gino Marley" };
            tag.Title = "Just in Case";
            f.RemoveTags(TagLib.TagTypes.Id3v1);
            f.Save();
            TagLib.Id3v2.Tag.ForceDefaultVersion = false;
        }
        var song = TagIO.Read(mp3);
        Check("read artist", song.Artist, "Chief Keef & DJ KENN AON feat. Gino Marley");
        TagIO.Write(mp3, ArtistParser.Rebuild(song.Artist, ArtistParser.Detect(song.Artist, Settings.DefaultNames), comma, fkeep));
        Check("write + re-read", TagIO.Read(mp3).Artist, "Chief Keef, DJ KENN AON feat. Gino Marley");
        using (var f = TagLib.File.Create(mp3))
        {
            Check("no ID3v1 added", ((f.TagTypesOnDisk & TagLib.TagTypes.Id3v1) != 0).ToString(), "False");
            Check("title untouched", f.Tag.Title, "Just in Case");
            Check("ID3 version kept", ((TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2)).Version.ToString(), "3");
            Check("audio intact", f.Properties.AudioBitrate.ToString(), "128");
        }
        string v24 = Path.Combine(dir, "v24.mp3");
        WriteSilentMp3(v24);
        using (var f = TagLib.File.Create(v24))
        {
            TagLib.Id3v2.Tag.DefaultVersion = 4; TagLib.Id3v2.Tag.ForceDefaultVersion = true;
            f.GetTag(TagLib.TagTypes.Id3v2, true).Performers = new[] { "x" };
            f.Save();
            TagLib.Id3v2.Tag.ForceDefaultVersion = false;
        }
        TagIO.Write(v24, new[] { "A", "B" });
        Check("v2.4 multi-value round trip", TagIO.Read(v24).Artist, "A\\\\B");
        TagIO.Write(v24, new[] { "AC/DC" });
        Check("v2.4 slash intact", TagIO.Read(v24).Artist, "AC/DC");
        string slashy = Path.Combine(dir, "acdc.mp3");
        WriteSilentMp3(slashy);
        TagIO.Write(slashy, new[] { "AC/DC" });
        Check("v2.3 slash not split on read", TagIO.Read(slashy).Artist, "AC/DC");
        TagIO.Write(slashy, new[] { "6nk / SMUEX / Thoomrsxoh" });
        Check("v2.3 spaced slashes intact", TagIO.Read(slashy).Artist, "6nk / SMUEX / Thoomrsxoh");
        TagIO.Write(slashy, new[] { "A", "B" });
        Check("v2.3 multi-value written as A/B", TagIO.Read(slashy).Artist, "A/B");

        string deep = Path.Combine(dir, new string('d', 120), new string('e', 120));
        Directory.CreateDirectory(TagIO.LongPath(deep));
        string longMp3 = Path.Combine(deep, "6EJOU, GUS & JORG - Blood On My Hands.mp3");
        WriteSilentMp3(TagIO.LongPath(longMp3));
        TagIO.Write(longMp3, new[] { "6EJOU / GUS & JORG" });
        Check("path over 260 chars (" + longMp3.Length + ")", TagIO.Read(longMp3).Artist, "6EJOU / GUS & JORG");

        Console.WriteLine(failures == 0 ? "\nALL PASSED" : "\n" + failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }

    // 40 silent MPEG-1 Layer III frames, 128 kbps, 44.1 kHz.
    static void WriteSilentMp3(string path)
    {
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++)
            {
                var frame = new byte[417];
                frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x64;
                fs.Write(frame, 0, frame.Length);
            }
    }
}
