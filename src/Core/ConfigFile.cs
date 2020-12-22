using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading;
using SS.Core.Modules;
using SS.Utilities;

namespace SS.Core
{
    internal class ConfigFile : IDisposable
    {
        //private const int MAX_SECTION_LENGTH = 64;
        //private const int MAX_KEY_LENGTH = 64;

        private readonly object mutex = new object();
        internal readonly LinkedList<ConfigHandle> Handles = new LinkedList<ConfigHandle>();
        internal readonly Dictionary<string, string> _table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        //private LinkedList<object> dirty; // this must be used for setting config values (not implemented in this release)
        //private bool anychanged = false;
        private DateTime lastmod;
        internal readonly string filename, arena, name;

        internal ConfigFile(string filename, string arena, string name)
        {
            //dirty = new

            this.filename = filename;
            this.arena = arena;
            this.name = name;

            try
            {
                this.lastmod = File.GetLastWriteTimeUtc(filename);
            }
            catch
            {
                this.lastmod = DateTime.MinValue;
            }
        }

        public string FileName { get; }
        public string Arena { get; }
        public string Name { get; }

        internal void Lock()
        {
            Monitor.Enter(mutex);
        }

        internal void Unlock()
        {
            Monitor.Exit(mutex);
        }

        public void Load(APPFileFinderFunc fileFinderCallback, APPReportErrFunc reportErrorCallback = null)
        {
            if (fileFinderCallback == null)
                throw new ArgumentNullException(nameof(fileFinderCallback));

            using (PreprocessorReader reader = new PreprocessorReader(fileFinderCallback, reportErrorCallback, arena))
            {
                reader.AddFile(name);

                ReadOnlySpan<char> section = ReadOnlySpan<char>.Empty;
                string line;

                while ((line = reader.ReadLine()) != null)
                {
                    ReadOnlySpan<char> lineSpan = line.AsSpan().Trim();

                    if (lineSpan[0] == '[')
                    {
                        // new section
                        if (lineSpan[^1] == ']')
                        {
                            lineSpan = lineSpan[1..^1].Trim();
                        }
                        else
                        {
                            var currentFileLine = reader.CurrentFileLine;
                            reportErrorCallback($"Section is missing ']' ({currentFileLine?.FilePath}:{currentFileLine?.LineNumber})");

                            // use the remainder of the line as the section name
                            lineSpan = lineSpan[1..];
                        }

                        // Note: allowing section to be empty
                        section = lineSpan;
                    }
                    else
                    {
                        if (lineSpan.IsEmpty)
                        {
                            // empty line
                            continue;
                        }

                        ParseConfProperty(line, out string key, out ReadOnlySpan<char> value);

                        if (key.Contains(':'))
                        {
                            // <section>:<key> syntax
                            // the section overrides any section we're currently in
                            _table[key] = value.ToString();
                        }
                        else
                        {
                            _table[string.Concat(section, ":", key)] = value.ToString();
                        }
                    }
                }

                // TODO: watch these files for changes?
                // Note: ASSS only watches (via timer) the base file, not ones pulled in via #include
                //ctx.FilePaths
            }
        }

        internal void WriteDirtyValuesOne(bool callCallbacks)
        {
            // i think that this method writes changed settings to file
            // TODO: not necessary to get things running
        }

        /// <summary>
        /// Escapes the config file syntactic characters. Currently just = and \
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private static string EscapeString(string value)
        {
            StringBuilder sb = new StringBuilder(value);
            sb.Replace(@"=", @"\=");
            sb.Replace(@"\", @"\\");
            return sb.ToString();
        }

        /// <summary>
        /// Parses a line of text as a key=value property.
        /// Whitespace is trimmed off from both the key and value.
        /// Characters are unescaped for the key only.
        /// Valid escape character sequences include "\=" for = and "\\" for \.
        /// Invalid escape character sequences are ignored by skipping the \ character.
        /// </summary>
        /// <param name="line">The line to parse.</param>
        /// <param name="key">The resulting key. <see cref="string.Empty"/> if no key found.</param>
        /// <param name="value">The resulting value. <see cref="Span{char}.Empty"/> if there is no value.</param>
        private static void ParseConfProperty(ReadOnlySpan<char> line, out string key, out ReadOnlySpan<char> value)
        {
            StringBuilder sb = new StringBuilder(line.Length);

            int i;
            for (i = 0; i < line.Length; i++)
            {
                if (line[i] == '=')
                    break;

                if (line[i] == '\\')
                {
                    i++;
                    if (i < line.Length)
                        sb.Append(line[i]);
                }
                else
                {
                    sb.Append(line[i]);
                }
            }

            key = sb.ToTrimmedString();
            value = line[i..].TrimStart('=').Trim();
        }

        #region IDisposable Members

        public void Dispose()
        {
            if (Handles.Count > 0)
            {
                Handles.Clear();
            }

            _table.Clear();
            //filename = null;
            //arena = null;
            //name = null;
        }

        #endregion
    }
}
