using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace SS.Core
{
    public static class PathUtil
    {
        /// <summary>
        /// Finds the first readable file that exists from of set of <paramref name="searchPaths"/> 
        /// which may contain placeholders in the form of composite format strings.
        /// </summary>
        /// <param name="searchPaths">The search paths, which may be composite format strings.</param>
        /// <param name="replacements">Replacements to use with the composite format strings.</param>
        /// <returns>The path of the first readable file if one is found.  Otherwise, <see langword="null"/>.</returns>
        public static async Task<string?> FindFileOnPathAsync(IEnumerable<string> searchPaths, params string?[] replacements)
        {
            ArgumentNullException.ThrowIfNull(searchPaths);

            foreach (string path in searchPaths)
            {
                string filePath = replacements is not null
                    ? string.Format(CultureInfo.InvariantCulture, path, replacements)
                    : path;

                // File.Exists and the FileStream constructor perform file I/O.
                // We don't want to block, so do it on a worker thread.
                if (await Task<bool>.Factory.StartNew(
                    static (obj) =>
                    {
                        string checkPath = (string)obj!;

                        // Check if the file exists.
                        // This is done first so that we don't have to call the FileStream constructor and catch an exception if we don't need to.
                        if (!File.Exists(checkPath))
                            return false;

                        // Check that we have read permissions.
                        try
                        {
                            using FileStream stream = new(checkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            return true;
                        }
                        catch
                        {
                            return false;
                        }
                    },
                    filePath).ConfigureAwait(false))
                {
                    return filePath;
                }
            }

            return null;
        }
    }
}
