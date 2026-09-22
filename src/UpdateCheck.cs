using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AbletonManager
{
    /// <summary>
    /// Asking GitHub what the newest release is — the only place in the program that touches
    /// the network at all.
    ///
    /// What goes out is an ordinary GET for a public page. Nothing about the person, the
    /// library or the machine is sent: no identifier, no set names, no counters. What comes
    /// back is a version and a link. The other end learns what any web server learns from
    /// anybody who opens a page — an address and a time — and the switch in the settings turns
    /// even that off.
    ///
    /// Everything here blocks: the caller runs it on a thread of its own and brings the answer
    /// back to the interface itself.
    /// </summary>
    public static class UpdateCheck
    {
        const string LatestUrl = "https://api.github.com/repos/rueblose/alive/releases/latest";
        const string PageUrl   = "https://github.com/rueblose/alive/releases/latest";

        /// <summary>How much newer the release out there is.</summary>
        public enum Step
        {
            /// <summary>Nothing to offer: the same version, an older one, or an answer we
            /// could not read.</summary>
            None,
            /// <summary>Only the third number moved — a fix. Mentioned when asked, never
            /// announced.</summary>
            Patch,
            /// <summary>The major or the minor number moved. This is what the dot on the gear
            /// is for.</summary>
            Big,
        }

        public sealed class Result
        {
            /// <summary>The version out there, without the v — empty if we never found out.</summary>
            public string Version = "";
            /// <summary>The page to open in a browser. Always filled in: even when the answer
            /// could not be read, the releases page is a reasonable place to send someone.</summary>
            public string Url = PageUrl;
            /// <summary>Why it did not work out, for the person who asked by hand. Empty while
            /// all is well; the background check never shows it.</summary>
            public string Error = "";
        }

        // Digits only, and the tag's v dropped: a release is "v1.2", the assembly says "1.1".
        static readonly Regex VersionPart = new Regex(@"^\d+$", RegexOptions.CultureInvariant);

        /// <summary>
        /// How the release <paramref name="found"/> compares with the one we are. Numbers, not
        /// text: the day 1.10 comes out, a string comparison would file it before 1.9 and the
        /// update would never be offered. Anything unreadable is None — silence is the only
        /// safe answer to a reply we do not understand.
        /// </summary>
        public static Step Compare(string current, string found)
        {
            int[] a = Parse(current), b = Parse(found);
            if (a == null || b == null) return Step.None;

            for (int i = 0; i < 4; i++)
            {
                if (b[i] == a[i]) continue;
                if (b[i] < a[i]) return Step.None;          // theirs is older — nothing to offer
                return i < 2 ? Step.Big : Step.Patch;       // major or minor moved, or a fix
            }
            return Step.None;                                // the very same release
        }

        /// <summary>Four numbers out of "v1.2" or "1.1.0.0"; null if it is not a version at
        /// all. The missing tail is zeroes, so 1.1 and 1.1.0.0 are one and the same.</summary>
        static int[] Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);
            if (s.Length == 0) return null;

            string[] parts = s.Split('.');
            if (parts.Length > 4) return null;

            int[] n = new int[4];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!VersionPart.IsMatch(parts[i])) return null;
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i]))
                    return null;
            }
            return n;
        }

        /// <summary>
        /// Ask, once. Never throws: every way this can fail ends up in Result.Error, because
        /// the caller is a thread nobody is watching.
        /// </summary>
        public static Result Fetch()
        {
            Result r = new Result();
            try
            {
                // TLS 1.2 by hand. The default on .NET Framework 4.x is whatever the machine
                // was configured for years ago, and GitHub has not answered anything older
                // since 2018 — without this line the request fails on a perfectly healthy
                // connection, which is the least helpful error there is.
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }
                catch { }

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(LatestUrl);
                req.Method = "GET";
                // GitHub refuses a request without one, and an honest name is better than a
                // borrowed browser's.
                req.UserAgent = "Alive/" + System.Windows.Forms.Application.ProductVersion;
                req.Accept = "application/vnd.github+json";
                req.Timeout = req.ReadWriteTimeout = 8000;

                string body;
                using (WebResponse resp = req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                    body = sr.ReadToEnd();

                r.Version = Field(body, "tag_name");
                if (r.Version.Length > 0 && (r.Version[0] == 'v' || r.Version[0] == 'V'))
                    r.Version = r.Version.Substring(1);

                string page = Field(body, "html_url");
                if (page.Length > 0) r.Url = page;

                if (r.Version.Length == 0) r.Error = "GitHub answered something unexpected";
            }
            catch (WebException ex)
            {
                HttpWebResponse http = ex.Response as HttpWebResponse;
                r.Error = http != null && (int)http.StatusCode == 403
                        ? "GitHub is rate-limiting this address — try again later"
                        : "Could not reach GitHub";
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        /// <summary>
        /// One string field out of the answer. A JSON parser is not worth a reference here —
        /// the whole program is one exe with nothing beside it, and two fields of a reply whose
        /// shape has not changed in a decade do not justify dragging System.Web.Extensions in.
        /// Anything unexpected comes back empty, and an empty version is treated as "did not
        /// find out" rather than as an update.
        /// </summary>
        static string Field(string json, string name)
        {
            if (string.IsNullOrEmpty(json)) return "";
            Match m = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"([^\"\\\\]*)\"",
                                  RegexOptions.CultureInvariant);
            return m.Success ? m.Groups[1].Value : "";
        }
    }
}
