using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NoodlesMcDoodles.Core.ConfigFiles
{
    /// <summary>
    /// A raw, BepInEx-independent model of a BepInEx 5 <c>.cfg</c> file, used only by the config migrator
    /// (it runs before any <c>ConfigFile</c> is bound, and must compile in tools/cfg-migrate-test without Unity,
    /// BepInEx or Harmony — System.* only).
    ///
    /// Reads exactly what BepInEx's own <c>ConfigFile.Reload</c> reads (a trimmed line starting with <c>#</c> is a
    /// comment, <c>[Name]</c> starts a section, anything else is split on the first <c>=</c> into a trimmed key and
    /// value) and additionally keeps the comment lines BepInEx writes above each entry (<c>## description</c>,
    /// <c># Setting type:</c>, <c># Default value:</c>, <c># Acceptable ...</c>) attached to that entry. A non-blank
    /// line that is none of those (BepInEx silently ignores it) is kept as an "unknown" line in place and written
    /// back unchanged, and counted in <see cref="UnknownLineCount"/>.
    /// </summary>
    internal sealed class CfgDocument
    {
        private const string DefaultValuePrefix = "# Default value:";

        /// <summary>Lines before the first section (BepInEx's "## Settings file was created by ..." header).</summary>
        public List<string> Preamble { get; } = new List<string>();

        public List<CfgSection> Sections { get; } = new List<CfgSection>();

        /// <summary>The line ending to write with; taken from the parsed text (\r\n if it had any), else \n.</summary>
        public string NewLine { get; set; } = "\n";

        /// <summary>Non-blank lines that were not a comment, a section header or a key = value line.</summary>
        public int UnknownLineCount { get; private set; }

        public IEnumerable<CfgEntry> AllEntries
        {
            get
            {
                foreach (CfgSection s in Sections)
                    foreach (CfgEntry e in s.Entries)
                        yield return e;
            }
        }

        public static CfgDocument Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return Parse(DecodeUtf8(bytes));
        }

        /// <summary>
        /// Decodes strictly as UTF-8 (BOM stripped). Throws <see cref="FormatException"/> on invalid UTF-8 or on a
        /// control character other than tab/CR/LF — a binary or corrupt file is never treated as a config.
        /// </summary>
        public static string DecodeUtf8(byte[] bytes)
        {
            int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException e)
            {
                throw new FormatException("not valid UTF-8 text", e);
            }

            foreach (char c in text)
            {
                if (c < 0x20 && c != '\t' && c != '\r' && c != '\n')
                    throw new FormatException($"contains control character 0x{(int)c:X2} (binary or corrupt file)");
            }
            return text;
        }

        public static CfgDocument Parse(string text)
        {
            var doc = new CfgDocument();
            if (text.IndexOf("\r\n", StringComparison.Ordinal) >= 0) doc.NewLine = "\r\n";

            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            CfgSection section = null;
            var pending = new List<string>(); // comment/unknown lines since the last blank line or entry

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();

                if (line.Length == 0)
                {
                    // A blank line ends a comment block that was not directly above an entry.
                    FlushPending(doc, section, pending);
                    continue;
                }

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    pending.Add(line);
                    continue;
                }

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    FlushPending(doc, section, pending);
                    string name = line.Substring(1, line.Length - 2);
                    section = doc.GetSection(name);
                    if (section == null)
                    {
                        section = new CfgSection(name);
                        doc.Sections.Add(section);
                    }
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq > 0)
                {
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (section == null)
                    {
                        // BepInEx reads this as section "" — keep it the same way.
                        section = doc.GetSection(string.Empty);
                        if (section == null)
                        {
                            section = new CfgSection(string.Empty);
                            doc.Sections.Add(section);
                        }
                    }

                    CfgEntry existing = section.Find(key);
                    if (existing != null)
                    {
                        // BepInEx keeps the last occurrence; so do we.
                        existing.Value = value;
                        existing.Comments.Clear();
                        existing.Comments.AddRange(pending);
                    }
                    else
                    {
                        var entry = new CfgEntry(section.Name, key, value);
                        entry.Comments.AddRange(pending);
                        section.Entries.Add(entry);
                    }
                    pending.Clear();
                    continue;
                }

                doc.UnknownLineCount++;
                pending.Add(line);
            }

            FlushPending(doc, section, pending);
            return doc;
        }

        private static void FlushPending(CfgDocument doc, CfgSection section, List<string> pending)
        {
            if (pending.Count == 0) return;
            if (section == null) doc.Preamble.AddRange(pending);
            else section.TrailingLines.AddRange(pending);
            pending.Clear();
        }

        public CfgSection GetSection(string name)
        {
            foreach (CfgSection s in Sections)
                if (string.Equals(s.Name, name, StringComparison.Ordinal)) return s;
            return null;
        }

        public CfgSection GetOrAddSection(string name)
        {
            CfgSection s = GetSection(name);
            if (s != null) return s;
            s = new CfgSection(name);
            Sections.Add(s);
            return s;
        }

        public CfgEntry Find(string section, string key) => GetSection(section)?.Find(key);

        /// <summary>Sets (or adds) an entry's value. Comments are replaced only when <paramref name="comments"/> is given.</summary>
        public CfgEntry Set(string section, string key, string value, IEnumerable<string> comments = null)
        {
            CfgSection s = GetOrAddSection(section);
            CfgEntry e = s.Find(key);
            if (e == null)
            {
                e = new CfgEntry(section, key, value);
                s.Entries.Add(e);
            }
            e.Value = value;
            if (comments != null)
            {
                e.Comments.Clear();
                e.Comments.AddRange(comments);
            }
            return e;
        }

        /// <summary>Writes the same layout BepInEx's own <c>ConfigFile.Save</c> does.</summary>
        public string Serialize()
        {
            var sb = new StringBuilder();
            foreach (string l in Preamble) sb.Append(l).Append(NewLine);
            if (Preamble.Count > 0) sb.Append(NewLine);

            foreach (CfgSection s in Sections)
            {
                sb.Append('[').Append(s.Name).Append(']').Append(NewLine);
                foreach (CfgEntry e in s.Entries)
                {
                    sb.Append(NewLine);
                    foreach (string c in e.Comments) sb.Append(c).Append(NewLine);
                    sb.Append(e.Key).Append(" = ").Append(e.Value).Append(NewLine);
                }
                if (s.TrailingLines.Count > 0)
                {
                    sb.Append(NewLine);
                    foreach (string l in s.TrailingLines) sb.Append(l).Append(NewLine);
                }
                sb.Append(NewLine);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Writes <paramref name="text"/> to <paramref name="path"/> via a temp file in the same directory, then
        /// swaps it in (File.Replace when the target exists, File.Move when it does not), so a failure part-way
        /// never leaves a half-written target behind. UTF-8 without BOM, like BepInEx.
        /// </summary>
        public static void WriteAtomic(string path, string text) => WriteAtomic(path, new UTF8Encoding(false).GetBytes(text));

        /// <summary>Byte-for-byte variant of <see cref="WriteAtomic(string, string)"/>.</summary>
        public static void WriteAtomic(string path, byte[] bytes)
        {
            string tmp = path + ".tmp-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
            try
            {
                File.WriteAllBytes(tmp, bytes);
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tmp, path, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Delete(path);
                        File.Move(tmp, path);
                    }
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>The value of the <c># Default value: X</c> comment BepInEx writes above an entry, or null.</summary>
        internal static string DefaultValueFromComments(List<string> comments)
        {
            foreach (string c in comments)
            {
                if (c.StartsWith(DefaultValuePrefix, StringComparison.Ordinal))
                    return c.Substring(DefaultValuePrefix.Length).Trim();
            }
            return null;
        }

        internal static string DefaultValueComment(string value) => DefaultValuePrefix + " " + value;
    }

    internal sealed class CfgSection
    {
        public CfgSection(string name) { Name = name; }

        public string Name { get; }
        public List<CfgEntry> Entries { get; } = new List<CfgEntry>();

        /// <summary>Comment/unknown lines after the section's last entry (or in a section with no entries).</summary>
        public List<string> TrailingLines { get; } = new List<string>();

        public CfgEntry Find(string key)
        {
            foreach (CfgEntry e in Entries)
                if (string.Equals(e.Key, key, StringComparison.Ordinal)) return e;
            return null;
        }
    }

    internal sealed class CfgEntry
    {
        public CfgEntry(string section, string key, string value)
        {
            Section = section;
            Key = key;
            Value = value;
        }

        public string Section { get; }
        public string Key { get; }
        public string Value { get; set; }

        /// <summary>The comment lines directly above the entry, as written (description, type, default, range).</summary>
        public List<string> Comments { get; } = new List<string>();

        /// <summary>From the entry's own <c># Default value:</c> comment; null if it has none.</summary>
        public string DefaultValue => CfgDocument.DefaultValueFromComments(Comments);

        /// <summary>
        /// True when the value is the entry's own default (from its comment) — compared as text, then as a number,
        /// then case-insensitively (BepInEx parses bools and enums ignoring case), so "0.70" equals "0.7" and
        /// "True" equals "true". False when there is no default comment: an unknown default is never assumed.
        /// </summary>
        public bool IsDefault
        {
            get
            {
                string d = DefaultValue;
                return d != null && ValuesEqual(Value, d);
            }
        }

        internal static bool ValuesEqual(string a, string b)
        {
            a = (a ?? string.Empty).Trim();
            b = (b ?? string.Empty).Trim();
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double da) &&
                double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double db))
            {
                return da.Equals(db) || Math.Abs(da - db) <= 1e-9 * Math.Max(1.0, Math.Max(Math.Abs(da), Math.Abs(db)));
            }
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
