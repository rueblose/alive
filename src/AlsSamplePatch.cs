using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AbletonManager
{
    /// <summary>Where one file reference should start pointing.</summary>
    public sealed class NewRef
    {
        /// <summary>"Samples/Imported/kick.wav" — forward slashes, the way Live writes
        /// them.</summary>
        public string RelativePath = "";

        /// <summary>The full path in the new place, forward slashes too.</summary>
        public string AbsolutePath = "";

        /// <summary>3 — "inside the project folder", see RefResolver.</summary>
        public int RelativePathType = 3;

        /// <summary>Clear LivePackName and LivePackId: the file no longer comes from a
        /// pack.</summary>
        public bool ClearPack = true;
    }

    /// <summary>
    /// A copy of a set with the paths of selected references replaced.
    ///
    /// Nodes are addressed by their NUMBER in document order, not by their content. In the text
    /// one &lt;FileRef&gt; is indistinguishable from its neighbour: the path of a clip's sample
    /// and of its "memory of origin" can be literally the same, and only the first must be
    /// rewritten. AlsFile already walks the file and fills info.Files in order, so the i-th
    /// entry in that list is the i-th &lt;FileRef&gt; in the text. That keeps the "which
    /// container is this" logic in one place: were the two parsers to drift apart, the
    /// reference rewritten would not be the one that was shown.
    ///
    /// The mechanics are the same as AlsPatch: streaming, line by line, with only the current
    /// node in memory. The original is never opened for writing.
    /// </summary>
    public static class AlsSamplePatch
    {
        /// <summary>
        /// Writes a copy of src into dst with the paths replaced. Returns the number of nodes
        /// touched.
        ///
        /// expectedRefCount is how many FileRefs AlsFile counted in this same file. A mismatch
        /// means the numbering has drifted and the edit would land on the wrong reference: dst
        /// is deleted and InvalidDataException is thrown. Writing a wrong path into a sample
        /// reference is worse than writing nothing — the set will open, but it will sound like
        /// something else.
        /// </summary>
        public static int Rewrite(string src, string dst, Dictionary<int, NewRef> byIndex,
                                  int expectedRefCount)
        {
            if (byIndex == null) throw new ArgumentNullException("byIndex");

            int index = -1, patched = 0;
            bool ok = false;
            try
            {
                using (FileStream fin = new FileStream(src, FileMode.Open, FileAccess.Read,
                                                       FileShare.ReadWrite, 64 * 1024))
                using (GZipStream gin = new GZipStream(fin, CompressionMode.Decompress))
                // detectEncodingFromByteOrderMarks: false — otherwise the BOM would be eaten on
                // reading and not written back, and the copy must differ from the original by
                // exactly the values we change and by nothing else.
                using (StreamReader rin = new StreamReader(gin, new UTF8Encoding(false), false, 64 * 1024))

                using (FileStream fout = new FileStream(dst, FileMode.Create, FileAccess.Write,
                                                        FileShare.None, 64 * 1024))
                using (GZipStream gout = new GZipStream(fout, CompressionMode.Compress))
                using (StreamWriter wout = new StreamWriter(gout, new UTF8Encoding(false), 64 * 1024))
                {
                    AlsPatch.LineReader lines = new AlsPatch.LineReader(rin);
                    StringBuilder node = null;

                    string line;
                    while ((line = lines.Next()) != null)
                    {
                        if (node == null)
                        {
                            int at = line.IndexOf("<FileRef", StringComparison.Ordinal);
                            // A self-closing <FileRef /> is skipped by both parsers: AlsFile
                            // only makes an entry when !IsEmptyElement.
                            if (at < 0 || SelfClosing(line, at)) { wout.Write(line); continue; }
                            index++;
                            node = new StringBuilder(line);
                        }
                        else node.Append(line);

                        // The closing tag is looked for in the current line rather than in
                        // everything accumulated: otherwise the whole node string would be
                        // rebuilt for every one of its lines.
                        if (line.IndexOf("</FileRef>", StringComparison.Ordinal) < 0) continue;

                        string text = node.ToString();
                        node = null;

                        NewRef nr;
                        if (byIndex.TryGetValue(index, out nr)) { wout.Write(Apply(text, nr)); patched++; }
                        else wout.Write(text);
                    }

                    // The file ended mid-node — write it as is so the tail is not lost.
                    if (node != null) wout.Write(node.ToString());
                }

                int count = index + 1;
                if (count != expectedRefCount)
                    throw new InvalidDataException(string.Format(
                        "FileRef count mismatch: found {0}, expected {1}", count, expectedRefCount));

                ok = true;
            }
            finally
            {
                if (!ok) { try { File.Delete(dst); } catch { } }
            }

            Diag.Line("collect: rewrote " + patched + " FileRef in " + Path.GetFileName(dst));
            return patched;
        }

        /// <summary>Whether a "/" stands before the tag's closing bracket — that is, the node
        /// is empty.</summary>
        static bool SelfClosing(string line, int at)
        {
            int close = line.IndexOf('>', at);
            return close > at && line[close - 1] == '/';
        }

        static string Apply(string node, NewRef nr)
        {
            string s = node;
            // The leading "<" is not decoration: without it "<RelativePath Value=" would be
            // found by a search for "Path Value=" and the path would go into the wrong tag.
            s = SetValue(s, "<RelativePathType Value=\"",
                         nr.RelativePathType.ToString(CultureInfo.InvariantCulture));
            s = SetValue(s, "<RelativePath Value=\"", Escape(nr.RelativePath));
            s = SetValue(s, "<Path Value=\"", Escape(nr.AbsolutePath));
            if (nr.ClearPack)
            {
                s = SetValue(s, "<LivePackName Value=\"", "");
                s = SetValue(s, "<LivePackId Value=\"", "");
            }
            // Type, OriginalFileSize and OriginalCrc are left alone: they are about the same
            // file, which has merely moved.
            return s;
        }

        /// <summary>Replace the value of the first such tag in the node. No tag — the node
        /// stays as it was.</summary>
        static string SetValue(string node, string tag, string value)
        {
            int at = node.IndexOf(tag, StringComparison.Ordinal);
            if (at < 0) return node;
            int from = at + tag.Length;
            int close = node.IndexOf('"', from);
            if (close < 0) return node;

            StringBuilder sb = new StringBuilder(node.Length + value.Length + 8);
            sb.Append(node, 0, from).Append(value).Append(node, close, node.Length - close);
            return sb.ToString();
        }

        /// <summary>
        /// XML-escaping of an attribute value. Not a formality: a folder called "Drum &amp;
        /// Bass" turns up all the time, and written as is it tears the document apart.
        ///
        /// It also normalises "\" into "/": paths in an .als are always forward-slashed (see
        /// NewRef), and today's callers do not guarantee that formally — only by building
        /// RelativePath through "/" themselves. NewRef is public, and its next consumer
        /// (replacing lost samples) will take a path from another source where the slash
        /// direction is not a given. Closing the trap here with one Replace is cheaper than
        /// doing it at every caller.
        /// </summary>
        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\\', '/');
            StringBuilder sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '&') sb.Append("&amp;");
                else if (c == '<') sb.Append("&lt;");
                else if (c == '>') sb.Append("&gt;");
                else if (c == '"') sb.Append("&quot;");
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
