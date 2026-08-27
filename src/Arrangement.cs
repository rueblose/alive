using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;

namespace AbletonManager
{
    /// <summary>Одна нота. Время — в долях от начала содержимого клипа, не от начала сета.</summary>
    public struct NoteEvent
    {
        public float Time;
        public float Duration;
        public byte Pitch;
        public byte Velocity;
    }

    /// <summary>Клип на линейке аранжировки.</summary>
    public sealed class ClipBlock
    {
        public double Start, End;          // доли от начала сета
        public string Name = "";
        public int Color = -1;
        public bool IsMidi, Disabled;

        // Видимые маркеры клипа. Когда петля выключена, Live держит в LoopStart/LoopEnd
        // границы старт/энд-маркеров, а скрытую петлю уносит в HiddenLoop*.
        public double LoopStart, LoopEnd, StartRelative;
        public bool LoopOn;

        public List<NoteEvent> Notes;      // только у midi-клипов
        public int MinPitch = 127, MaxPitch = 0;

        public double Length { get { return End - Start; } }
        public double LoopLength { get { return LoopEnd - LoopStart; } }
    }

    public sealed class TrackLane
    {
        public string Name = "";
        public int Color = -1;
        public bool IsMidi, IsGroup, Frozen;
        public int Id = -1, GroupId = -1;
        public int Indent;                 // вложенность в группы
        public readonly List<ClipBlock> Clips = new List<ClipBlock>();
    }

    /// <summary>Аранжировка сета: дорожки, их цвета и клипы на линейке времени.</summary>
    public sealed class Arrangement
    {
        public string Path = "", Creator = "";
        public double Tempo;
        public double End;                 // последняя доля, на которой что-то есть
        public readonly List<TrackLane> Tracks = new List<TrackLane>();
        public int ClipCount, NoteCount;
        public string Error;

        public bool HasContent { get { return ClipCount > 0; } }

        /// <summary>Длина в тактах при 4/4 — для подписи, сетка считается так же.</summary>
        public int Bars { get { return (int)Math.Ceiling(End / 4.0); } }

        // Потолок на весь сет: у больших проектов нот сотни тысяч, а на превью
        // всё равно ложится в пару пикселей.
        public const int MaxNotes = 200000;

        public static Arrangement Read(string path)
        {
            Arrangement a = new Arrangement();
            a.Path = path;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite, 64 * 1024))
                using (GZipStream gz = new GZipStream(fs, CompressionMode.Decompress))
                using (XmlReader r = XmlReader.Create(gz, AlsFile.XmlSettings()))
                    Parse(r, a);
            }
            catch (Exception ex)
            {
                a.Error = ex.Message;
            }
            Finish(a);
            return a;
        }

        // ------------------------------------------------------------------ разбор

        static void Parse(XmlReader r, Arrangement a)
        {
            List<string> stack = new List<string>();

            int trackDepth = -1; TrackLane track = null;
            int trackNameDepth = -1;
            int arrangerDepth = -1;                 // клипы аранжировки живут только здесь
            int clipDepth = -1; ClipBlock clip = null;
            int loopDepth = -1;
            int keyTrackDepth = -1, keyNoteStart = 0;
            int mainTrackDepth = -1, tempoDepth = -1; bool tempoSeen = false;

            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.EndElement)
                {
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    int d = stack.Count;

                    if (keyTrackDepth >= 0 && d <= keyTrackDepth) keyTrackDepth = -1;
                    if (loopDepth >= 0 && d <= loopDepth) loopDepth = -1;
                    if (clipDepth >= 0 && d <= clipDepth)
                    {
                        CloseClip(a, track, clip);
                        clip = null; clipDepth = -1;
                    }
                    if (arrangerDepth >= 0 && d <= arrangerDepth) arrangerDepth = -1;
                    if (trackNameDepth >= 0 && d <= trackNameDepth) trackNameDepth = -1;
                    if (trackDepth >= 0 && d <= trackDepth) { track = null; trackDepth = -1; }
                    if (mainTrackDepth >= 0 && d <= mainTrackDepth) mainTrackDepth = -1;
                    if (tempoDepth >= 0 && d <= tempoDepth) tempoDepth = -1;
                    continue;
                }
                if (r.NodeType != XmlNodeType.Element) continue;

                string name = r.Name;
                bool empty = r.IsEmptyElement;
                string parent = stack.Count > 0 ? stack[stack.Count - 1] : "";
                int depth = stack.Count;

                switch (name)
                {
                    case "Ableton":
                        a.Creator = r.GetAttribute("Creator") ?? "";
                        break;

                    case "AudioTrack":
                    case "MidiTrack":
                    case "GroupTrack":
                        if (!empty)
                        {
                            track = new TrackLane();
                            track.IsMidi = name == "MidiTrack";
                            track.IsGroup = name == "GroupTrack";
                            track.Id = Int(r.GetAttribute("Id"), -1);
                            trackDepth = depth;
                            a.Tracks.Add(track);
                        }
                        break;

                    case "MainTrack":
                    case "MasterTrack":
                        if (!empty) mainTrackDepth = depth;
                        break;

                    // Клипы сессии лежат в ClipSlotList — на линейку они не попадают,
                    // поэтому берём только то, что внутри ArrangerAutomation.
                    case "ArrangerAutomation":
                        if (!empty && track != null) arrangerDepth = depth;
                        break;

                    case "AudioClip":
                    case "MidiClip":
                        if (!empty && arrangerDepth >= 0 && track != null)
                        {
                            clip = new ClipBlock();
                            clip.IsMidi = name == "MidiClip";
                            clipDepth = depth;
                        }
                        break;

                    case "Loop":
                        if (!empty && clip != null && depth == clipDepth + 1) loopDepth = depth;
                        break;

                    case "KeyTrack":
                        if (!empty && clip != null)
                        {
                            keyTrackDepth = depth;
                            keyNoteStart = clip.Notes == null ? 0 : clip.Notes.Count;
                        }
                        break;

                    case "MidiNoteEvent":
                        if (clip != null && keyTrackDepth >= 0)
                        {
                            if (a.NoteCount >= MaxNotes) break;   // потолок на весь сет, см. MaxNotes
                            NoteEvent n = new NoteEvent();
                            n.Time = (float)Dbl(r.GetAttribute("Time"), 0);
                            n.Duration = (float)Dbl(r.GetAttribute("Duration"), 0);
                            n.Velocity = (byte)Math.Max(0, Math.Min(127, Int(r.GetAttribute("Velocity"), 100)));
                            if (clip.Notes == null) clip.Notes = new List<NoteEvent>();
                            clip.Notes.Add(n);
                            a.NoteCount++;
                        }
                        break;

                    // Высота ноты приезжает уже после самих нот: <KeyTrack><Notes/><MidiKey/></KeyTrack>
                    case "MidiKey":
                        if (clip != null && keyTrackDepth >= 0 && clip.Notes != null)
                        {
                            int pitch = Math.Max(0, Math.Min(127, Int(r.GetAttribute("Value"), 60)));
                            for (int i = keyNoteStart; i < clip.Notes.Count; i++)
                            {
                                NoteEvent n = clip.Notes[i];
                                n.Pitch = (byte)pitch;
                                clip.Notes[i] = n;
                            }
                            if (clip.Notes.Count > keyNoteStart)
                            {
                                if (pitch < clip.MinPitch) clip.MinPitch = pitch;
                                if (pitch > clip.MaxPitch) clip.MaxPitch = pitch;
                            }
                        }
                        break;

                    case "Name":
                        if (!empty && IsTrack(parent)) trackNameDepth = depth;
                        break;

                    case "Tempo":
                        if (!empty && mainTrackDepth >= 0 && !tempoSeen) tempoDepth = depth;
                        break;
                }

                // --- значения внутри уже открытых узлов
                if (track != null && depth == trackDepth + 1)
                {
                    if (name == "Color" || name == "ColorIndex")
                        track.Color = Palette(Int(r.GetAttribute("Value"), -1));
                    else if (name == "TrackGroupId") track.GroupId = Int(r.GetAttribute("Value"), -1);
                    else if (name == "Freeze") track.Frozen = Bool(r.GetAttribute("Value"));
                }

                if (trackNameDepth >= 0 && track != null && name == "EffectiveName"
                    && depth == trackNameDepth + 1)
                {
                    string n = r.GetAttribute("Value");
                    if (!string.IsNullOrEmpty(n)) track.Name = n;
                }

                if (clip != null && depth == clipDepth + 1)
                {
                    switch (name)
                    {
                        case "CurrentStart": clip.Start = Dbl(r.GetAttribute("Value"), 0); break;
                        case "CurrentEnd": clip.End = Dbl(r.GetAttribute("Value"), 0); break;
                        case "Name": clip.Name = r.GetAttribute("Value") ?? ""; break;
                        case "Color":
                        case "ColorIndex": clip.Color = Palette(Int(r.GetAttribute("Value"), -1)); break;
                        case "Disabled": clip.Disabled = Bool(r.GetAttribute("Value")); break;
                    }
                }

                if (loopDepth >= 0 && clip != null && depth == loopDepth + 1)
                {
                    switch (name)
                    {
                        case "LoopStart": clip.LoopStart = Dbl(r.GetAttribute("Value"), 0); break;
                        case "LoopEnd": clip.LoopEnd = Dbl(r.GetAttribute("Value"), 0); break;
                        case "StartRelative": clip.StartRelative = Dbl(r.GetAttribute("Value"), 0); break;
                        case "LoopOn": clip.LoopOn = Bool(r.GetAttribute("Value")); break;
                    }
                }

                if (tempoDepth >= 0 && name == "Manual" && depth == tempoDepth + 1)
                {
                    double t = Dbl(r.GetAttribute("Value"), 0);
                    if (t > 0) { a.Tempo = t; tempoSeen = true; }
                }

                if (!empty) stack.Add(name);
            }
        }

        static void CloseClip(Arrangement a, TrackLane track, ClipBlock clip)
        {
            if (clip == null || track == null) return;
            if (clip.End <= clip.Start) return;                  // мусорный клип
            if (clip.MaxPitch < clip.MinPitch) { clip.MinPitch = 60; clip.MaxPitch = 60; }
            track.Clips.Add(clip);
            a.ClipCount++;
            if (clip.End > a.End) a.End = clip.End;
        }

        /// <summary>
        /// Номер цвета к виду «индекс в палитре 0..69».
        ///
        /// Live 11 и 12 пишут &lt;Color Value="23"/&gt; — это прямо индекс палитры.
        /// Сеты до Live 11 пишут &lt;ColorIndex&gt;, и там нумерация другая: у клипов она
        /// совпадает с палитрой, а у треков сдвинута. По 4300 парам «трек и его клипы»
        /// из старых сетов сдвиг оказался ровно 140 (85% пар; остальные — клипы, которым
        /// цвет меняли руками), и отдельным блоком встречается сдвиг 218: трек 282 при
        /// клипах 64. Всё, что не попадает ни в один блок, считаем неизвестным — цвет
        /// тогда берётся у клипов трека.
        /// </summary>
        static int Palette(int raw)
        {
            if (raw < 0) return -1;
            if (raw < LiveColors.Count) return raw;
            if (raw >= 218 && raw < 218 + LiveColors.Count) return raw - 218;
            if (raw >= 140 && raw < 140 + LiveColors.Count) return raw - 140;
            return -1;
        }

        static void Finish(Arrangement a)
        {
            // Отступ дорожки = сколько групп над ней. GroupId ссылается на Id группы.
            Dictionary<int, TrackLane> byId = new Dictionary<int, TrackLane>();
            foreach (TrackLane t in a.Tracks)
                if (t.Id >= 0 && !byId.ContainsKey(t.Id)) byId[t.Id] = t;

            foreach (TrackLane t in a.Tracks)
            {
                int depth = 0, guard = 0;
                TrackLane cur = t;
                while (cur != null && cur.GroupId >= 0 && guard++ < 16)
                {
                    TrackLane parent;
                    if (!byId.TryGetValue(cur.GroupId, out parent)) break;
                    depth++;
                    cur = parent;
                }
                t.Indent = depth;
            }

            // Цвет трека не разобрался — берём самый частый цвет его клипов: клип по
            // умолчанию наследует цвет дорожки, так что это тот же самый цвет.
            foreach (TrackLane t in a.Tracks)
            {
                if (t.Color >= 0 || t.Clips.Count == 0) continue;
                Dictionary<int, int> hist = new Dictionary<int, int>();
                foreach (ClipBlock c in t.Clips)
                {
                    if (c.Color < 0) continue;
                    hist[c.Color] = hist.ContainsKey(c.Color) ? hist[c.Color] + 1 : 1;
                }
                int best = 0;
                foreach (KeyValuePair<int, int> kv in hist)
                    if (kv.Value > best) { best = kv.Value; t.Color = kv.Key; }
            }

            if (a.End < 4) a.End = 4;
        }

        static bool IsTrack(string n)
        {
            return n == "AudioTrack" || n == "MidiTrack" || n == "GroupTrack"
                || n == "ReturnTrack" || n == "MainTrack" || n == "MasterTrack";
        }

        static int Int(string s, int fallback)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        static double Dbl(string s, double fallback)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        static bool Bool(string s)
        {
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
