using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

namespace SS.Core
{
    public static class PathUtil
    {
        /// <summary>
        /// Expands a string containing two-character macro sequences into a
        /// destination buffer, using a provided table of replacements.
        /// The source string can contain arbitrary text, as well as
        /// two-character sequences, like "%x", which will be expanded according
        /// to the replacement table. The escape character ("%" in the above
        /// example) is determined by the caller. There should be one
        /// replace_table struct for each sequence you want to replace. Double
        /// the macro character in the source string to insert it into the output
        /// by itself.
        /// </summary>
        /// <param name="dest">where to put the result</param>
        /// <param name="source">the source string</param>
        /// <param name="repls">characters and the strings to replace them with</param>
        /// <param name="macrochar">which character to use as the escape code</param>
        /// <returns>the number of characters in the destionation string, or -1 on error</returns>
        public static int macro_expand_string(
            out string dest,
            string source,
            Dictionary<char, string> repls,
            char macrochar)
        {
            StringBuilder sb = new StringBuilder();

            for (int x = 0; x < source.Length; x++)
            {
                if (source[x] == macrochar)
                {
                    x++;
                    if (x >= source.Length)
                    {
                        // hit end of source string, so incomplete combo
                        dest = null;
                        return -1;
                    }

                    if (source[x] == macrochar)
                    {
                        // double macro char, so insert literal
                        sb.Append(macrochar);
                        continue;
                    }

                    // search for replacement
                    string replacement;
                    if (repls.TryGetValue(source[x], out replacement) == true)
                    {
                        // found
                        sb.Append(replacement);
                        continue;
                    }
                    else
                    {
                        // no replacement found
                        dest = null;
                        return -1;
                    }
                }
                else
                {
                    sb.Append(source[x]);
                }
            }

            dest = sb.ToString();
            return dest.Length;

            /*  
            // this wont work if the replacement strings contain the macrochar

            StringBuilder sb = new StringBuilder(source);

            // replace each combo with its string replacement
            foreach (KeyValuePair<char, string> kvp in repls)
            {
                sb.Replace(string.Format("{1}{2}", macrochar, kvp.Key), kvp.Value);
            }

            // only should have double macro character combos left
            // search for invalid combos
            for (int x = 0; x < sb.Length; x++)
            {
                if (sb[x] == macrochar)
                {
                    if (x + 1 >= sb.Length)
                    {
                        dest = null;
                        return -1; // ran out of characters to complete the combo
                    }

                    if (sb[x + 1] != macrochar)
                    {
                        dest = null;
                        return -1; // found an invalid combo
                    }

                    // double macro character, move over it
                    x++;
                }
            }

            // double macro character designates to insert the macro character into the output itself
            sb.Replace(string.Format("{1}{2}", macrochar, macrochar), macrochar.ToString());

            dest = sb.ToString();

            return dest.Length;
            */
        }

        /// <summary>
        /// Finds the first of a set of pattern-generated filenames that exist.
        /// This walks through a search path, expanding each element with
        /// macro_expand_string, and checking if the resulting string refers to a
        /// file that exists. If so, it puts the result in dest. If none match,
        /// or if there was an error expanding a source string, it returns an
        /// error.
        /// </summary>
        /// <param name="dest">where to put the result</param>
        /// <param name="searchpath">a colon-delimited list of strings, where each string is a source string acceptable by macro_expand_string</param>
        /// <param name="repls">the replacement table to use</param>
        /// <returns>0 on success, -1 on failure</returns>
        public static int find_file_on_path(
            out string dest,
            string searchpath,
            Dictionary<char, string> repls)
        {
            string[] paths = searchpath.Split(':');
            string file;

            foreach (string path in paths)
            {
                if (macro_expand_string(out file, path, repls, '%') == -1)
                    continue;  // bad expand, try next path

                if (File.Exists(file))
                {
                    // check if we have read permissions
                    try
                    {
                        // HACK: find better way
                        File.OpenRead(file).Close();
                        dest = file;
                        return 0;
                    }
                    catch
                    {
                    }
                }
            }

            dest = null;
            return -1;
        }

        /// <summary>
        /// Checks if the given path is secure against trying to access files
        /// outside of the server root.
        /// Currently this checks for initial or trailing /, nonprintable chars,
        /// colons, double dots, double slashes, and double backslashes.
        /// Any of the above conditions makes it return failure.
        /// </summary>
        /// <param name="path">the path to check</param>
        /// <returns>true if the given path is "safe", false otherwise</returns>
        public static bool is_valid_path(string path)
        {
            // HACK: implement this
            return true;
        }

        /// <summary>
        /// Gets a pointer to the basename of a path.
        /// If the path contains several segments separated by slash or backslash
        /// delimiters, get_basename returns a pointer to the last segment.
        /// If it's only one segment, it returns the whole string.
        /// </summary>
        /// <param name="path">the path to get the basename of</param>
        /// <returns>the basename of the given path</returns>
        public static string get_basename(string path)
        {
            int index = path.LastIndexOfAny(new char[] { '/', '\\' });
            if (index == -1)
                return path;
            else
                return path.Substring(index);
        }
    }
}
