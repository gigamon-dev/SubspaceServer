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
        internal readonly LinkedList<ConfigHandle> Handles;
        internal readonly Dictionary<string, string> _table;
        //StringChunk *strings;  // collection of strings to conserve allocations and free all at once, dont think i need
        //private LinkedList<object> dirty; // this must be used for setting config values (not implemented in this release)
        //private bool anychanged = false;
        private DateTime lastmod;
        internal readonly string filename, arena, name;

        internal ConfigFile(string filename, string arena, string name)
        {
            // TODO: when i figure out what goes in each list and dictionary
            Handles = new LinkedList<ConfigHandle>();
            _table = new Dictionary<string, string>();
            //strings = new
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

        internal void Lock()
        {
            Monitor.Enter(mutex);
        }

        internal void Unlock()
        {
            Monitor.Exit(mutex);
        }

        internal void doLoad(string arena, string name)
        {
            using (APPContext ctx = new APPContext(
                new APPFileFinderFunc(ConfigManager.locateConfigFile),
                new APPReportErrFunc(reportError),
                arena))
            {

                ctx.AddFile(name);

                string key = null;
                string line = null;
                string val = null;

                while (ctx.GetLine(out line))
                {
                    line = line.Trim();

                    if (line[0] == '[')
                    {
                        // new section: copy to key name
                        // skip leading brackets/spaces
                        key = StringUtils.TrimWhitespaceAndExtras(line, '[', ']');
                        key += ":";
                    }
                    else
                    {
                        string unescaped;
                        line = unescapeString(out unescaped, line, '=');

                        if (string.IsNullOrEmpty(unescaped))
                            continue;

                        if (!string.IsNullOrEmpty(line) && line[0] == '=')
                        {
                            line = line.Substring(1).Trim();
                            unescaped = unescaped.Trim();

                            unescapeString(out val, line, '\0');

                            if (unescaped.IndexOf(':') != -1)
                            {
                                // this syntax lets you specify a section and key on
                                // one line. it does _not_ modify the "current section"
                                _table[unescaped] = val;
                            }
                            else if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(unescaped))
                            {
                                _table[key + unescaped] = val;
                            }
                            else
                            {
                                reportError("ignoring value not in any section");
                            }
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(unescaped))
                            {
                                // there is no value for this key, so enter it with the empty string
                                _table[key + unescaped] = string.Empty;
                            }
                            else
                            {
                                reportError("ignoring value not in any section");
                            }
                        }
                    }
                }
            }
        }

        internal void writeDirtyValuesOne(bool callCallbacks)
        {
            // i think that this method writes changed settings to file
            // TODO: not necessary to get things running
        }

        private static void reportError(string error)
        {
            // TODO: use log manager
            Console.WriteLine("<config> " + error);
        }

        /// <summary>
        /// escapes the config file syntactic characters. currently just = and \
        /// </summary>
        /// <param name="src"></param>
        /// <returns></returns>
        private static string escapeString(string src)
        {
            StringBuilder sb = new StringBuilder(src);
            sb.Replace(@"=", @"\=");
            sb.Replace(@"\", @"\\");
            return sb.ToString();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="dst"></param>
        /// <param name="source"></param>
        /// <param name="stopon"></param>
        /// <returns>the remainder of source if stopped on a character</returns>
        private static string unescapeString(out string dst, string source, char stopon)
        {
            StringBuilder sb = new StringBuilder();
            int x;
            for (x = 0; x < source.Length; x++)
            {
                if (source[x] == stopon)
                    break;

                if (source[x] == '\\')
                {
                    x++;
                    if (x < source.Length)
                        sb.Append(source[x]);
                }
                else
                {
                    sb.Append(source[x]);
                }
            }

            dst = sb.ToString();
            if (x == source.Length)
                return string.Empty;
            else
                return source.Substring(x);
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
