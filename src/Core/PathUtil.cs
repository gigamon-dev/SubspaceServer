using System;
using System.Collections.Generic;
using System.IO;

namespace SS.Core
{
    public static class PathUtil
    {
        /// <summary>
        /// Finds the first readable file that exists from of set of paths that are composite format string.
        /// </summary>
        /// <param name="searchPaths">Search paths that are composite format strings.</param>
        /// <param name="replacements">Replacements to use with the compsite format strings.</param>
        /// <returns>The path of the first readable file if one is found.  Otherwise, null.</returns>
        public static string FindFileOnPath(IEnumerable<string> searchPaths, params string[] replacements)
        {
            if (searchPaths == null)
                throw new ArgumentNullException(nameof(searchPaths));

            foreach (string path in searchPaths)
            {
                string filePath = string.Format(path, replacements);

                if (File.Exists(filePath))
                {
                    // check if we have read permissions
                    try
                    {
                        File.OpenRead(filePath).Close();
                        return filePath;
                    }
                    catch
                    {
                    }
                }
            }

            return null;
        }

        /*
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
        */
    }
}
