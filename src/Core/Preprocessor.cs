using SS.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SS.Core
{
    public delegate string APPFileFinderFunc(string arena, string name);
    public delegate void APPReportErrFunc(string message);

    /// <summary>
    /// A reader that can read lines of text from .conf files,
    /// with support for a subset of features of the C preprocessor, including:
    /// <list type="bullet">
    /// <item>#include</item>
    /// <item>#define</item>
    /// <item>#undef</item>
    /// <item>#ifdef</item>
    /// <item>#ifndef</item>
    /// <item>#else</item>
    /// <item>#endif</item>
    /// </list>
    /// <para>An initial ; or / denotes comments.</para>
    /// <para>Macros are NOT expanded.</para>
    /// </summary>
    /// <remarks>
    /// This is the equivalent of app.[c|h], the [a]sss [p]re[p]rocessor, in ASSS.
    /// </remarks>
    public class PreprocessorReader : IDisposable
    {
        private const char DirectiveChar = '#';
        private const char ContinueChar = '\\';
        private static readonly char[] CommentChars = { '/', ';' };

        private readonly APPFileFinderFunc fileFinder;
        private readonly APPReportErrFunc reportError;
        private readonly string arena;

        /// <summary>
        /// Stack of file entries.
        /// </summary>
        private FileEntry file;

        /// <summary>
        /// Paths of the files read from.
        /// </summary>
        private readonly List<string> filePathList = new List<string>();

        /// <summary>
        /// Stack of blocks that represent a #if[n]def, #else, #endif.
        /// </summary>
        private IfBlock ifs;

        /// <summary>
        /// Whether lines should be returned for the current block.
        /// </summary>
        private bool processing;

        /// <summary>
        /// Key/value pairs from #define.
        /// </summary>
        private readonly Dictionary<string, string> defs;

        private int maxRecursionDepth = 50;

        /// <summary>
        /// Maximum depth of #include files to open. The default is 50.
        /// </summary>
        public int MaxRecursionDepth
        {
            get { return maxRecursionDepth; }
            set
            {
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(value), "The maximum depth cannot be less than 1.");

                maxRecursionDepth = value;
            }
        }

        /// <summary>
        /// The current #include file depth.
        /// </summary>
        private int depth;

        private bool disposed = false;

        public PreprocessorReader(APPFileFinderFunc fileFinder, APPReportErrFunc reportError, string arena)
        {
            this.fileFinder = fileFinder ?? throw new ArgumentNullException(nameof(fileFinder));
            this.reportError = reportError;
            this.arena = arena; // can be null
            processing = true;
            defs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            depth = 0;
        }

        public (string FilePath, int LineNumber)? CurrentFileLine
        {
            get
            {
                if (file == null)
                    return null;

                return (file.FilePath, file.LineNumber);
            }
        }

        /// <summary>
        /// Paths of the files processed.
        /// This is populated as lines are read with <see cref="GetLine(out string)"/>.
        /// Use this after reading all lines to determine all the files that were used.
        /// </summary>
        public IEnumerable<string> FilePaths
        {
            get { return filePathList; }
        }

        /// <summary>
        /// Adds a #define.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        public void AddDef(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Cannot be null or white-space.", nameof(key));

            defs[key] = value;
        }

        /// <summary>
        /// Removes a #define.
        /// </summary>
        /// <param name="key"></param>
        public void RemoveDef(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Cannot be null or white-space.", nameof(key));

            defs.Remove(key);
        }

        /// <summary>
        /// Adds a file to read from, to the bottom of the 'file' stack.
        /// </summary>
        /// <param name="name">Name of the file to open.</param>
        public void AddFile(string name)
        {
            AddFile(name, false);
        }

        /// <summary>
        /// Adds a file to read from.
        /// </summary>
        /// <param name="name">Name of the file to open.</param>
        /// <param name="pushToTop">
        /// <see langword="true"/> to add to the top of the stack.
        /// Otherwise, <see langword="false"/> to add to the bottom.
        /// </param>
        private void AddFile(string name, bool pushToTop)
        {
            if (depth >= MaxRecursionDepth)
            {
                ReportError($"Maximum {DirectiveChar}include recursion depth reached while adding '{name}'.");
                return;
            }

            //
            // Determine the file to open.
            //

            string path = fileFinder(arena, name);

            if (path == null)
            {
                ReportError($"File not found for arena '{arena}', name '{name}'.");
                return;
            }

            //
            // Open the file.
            //

            StreamReader sr;

            try
            {
                // Use the default encoding unless a there is BOM.
                sr = new StreamReader(path, StringUtils.DefaultEncoding, true);
            }
            catch (Exception ex)
            {
                ReportError($"Unable to open file '{path}' for reading. {ex.Message}");
                return;
            }

            //
            // Keep track of the opened file.
            //

            filePathList.Add(path);
            FileEntry newEntry = new FileEntry(sr, path);

            if (file == null)
            {
                file = newEntry;
            }
            else if (pushToTop)
            {
                newEntry.Prev = file;
                file = newEntry;
            }
            else
            {
                FileEntry tfe = file;
                while (tfe.Prev != null)
                {
                    tfe = tfe.Prev;
                }
                tfe.Prev = newEntry;
            }

            depth++;
        }

        /// <summary>
        /// Reads the next line.
        /// </summary>
        /// <returns>The line read, or <see langword="null"/> if the end of file was reached.</returns>
        public virtual string ReadLine()
        {
            StringBuilder sb = new StringBuilder(4096); // TODO: don't allocate a new one for each read, maybe use Microsoft.Extensions.ObjectPool?

            while (true)
            {
                sb.Length = 0;

                while (true)
                {
                    // first find an actual line to process
                    while (true)
                    {
                        if (file == null)
                        {
                            return null;
                        }

                        string line = file.ReadLine();
                        if (line == null)
                        {
                            // we hit eof on this file, pop it off and try next
                            FileEntry of = file;
                            file = of.Prev;
                            of.Dispose();
                            depth--;
                        }
                        else
                        {
                            sb.Append(line);
                            break;
                        }
                    }

                    if ((sb.Length > 0) && (sb[^1] == ContinueChar))
                        sb.Length--;
                    else
                        break;
                }

                string trimmedLine = sb.ToTrimmedString();

                // check for directives
                if ((trimmedLine != string.Empty) && (trimmedLine[0] == DirectiveChar))
                {
                    HandleDirective(trimmedLine);
                    continue;
                }

                // then empty lines and comments
                if ((trimmedLine == string.Empty) || CommentChars.Contains(trimmedLine[0]))
                {
                    continue;
                }

                // here we have an actual line
                // if we're not processing it, skip it
                if (processing)
                {
                    return trimmedLine;
                }
            }
        }

        private void UpdateProcessing()
        {
            IfBlock i = ifs;

            processing = true;

            while (i != null)
            {
                if ((int)i.Cond == (int)i.Where)
                {
                    processing = false;
                }

                i = i.Prev;
            }
        }

        private void PushIf(bool cond)
        {
            IfBlock i = new IfBlock();
            i.Cond = cond ? IfBlock.CondType.is_true : IfBlock.CondType.is_false;
            i.Where = IfBlock.WhereType.in_if;
            i.Prev = ifs;
            ifs = i;

            UpdateProcessing();
        }

        private void PopIf()
        {
            if (ifs != null)
            {
                ifs = ifs.Prev;
            }
            else
            {
                ReportError($"No {DirectiveChar}if blocks to end ({file.FilePath}:{file.LineNumber}).");
            }

            UpdateProcessing();
        }

        private void SwitchIf()
        {
            if (ifs != null)
            {
                if (ifs.Where == IfBlock.WhereType.in_if)
                {
                    ifs.Where = IfBlock.WhereType.in_else;
                }
                else
                {
                    ReportError($"Multiple {DirectiveChar}else directives ({file.FilePath}:{file.LineNumber}).");
                }
            }
            else
            {
                ReportError($"Unexpected {DirectiveChar}else directive ({file.FilePath}:{file.LineNumber}).");
            }

            UpdateProcessing();
        }

        private void HandleDirective(ReadOnlySpan<char> line)
        {
            if (line.IsEmpty || line[0] != DirectiveChar)
                throw new ArgumentException($"Line must start with '{DirectiveChar}'.", nameof(line));

            line = line[1..]; // skip DIRECTIVECHAR

            if (line.StartsWith("ifdef", StringComparison.Ordinal)
                || line.StartsWith("ifndef", StringComparison.Ordinal))
            {
                bool isNot = line[2] == 'n';
                line = line[(isNot ? "ifndef".Length : "ifdef".Length)..];

                if (line.Length > 0 && char.IsWhiteSpace(line[0]))
                {
                    // trim whitespace, leading { and (, trailing } and )
                    line = line
                        .Trim()
                        .TrimStart('{')
                        .TrimStart('(')
                        .TrimEnd('}')
                        .TrimEnd(')');

                    if (line.Length > 0)
                    {
                        bool cond = defs.ContainsKey(line.ToString());
                        PushIf(isNot ? !cond : cond);
                    }
                    else
                    {
                        ReportError(
                            $"{DirectiveChar}{(isNot ? "ifndef" : "ifdef")} " +
                            $"is missing an identifier ({file.FilePath}:{file.LineNumber}).");
                    }
                }
            }
            else if (line.StartsWith("else", StringComparison.Ordinal))
            {
                SwitchIf();
            }
            else if (line.StartsWith("endif", StringComparison.Ordinal))
            {
                PopIf();
            }
            else
            {
                // now handle the stuff valid while processing
                if (!processing)
                {
                    return;
                }

                if (line.StartsWith("define", StringComparison.Ordinal))
                {
                    line = line["define".Length..];

                    if (line.Length > 0 && char.IsWhiteSpace(line[0]))
                    {
                        line = line.TrimStart();

                        if (line.Length > 0)
                        {
                            // find the end of the name
                            int i = 0;
                            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;

                            ReadOnlySpan<char> nameSpan = line.Slice(0, i);
                            line = line[i..].TrimStart();

                            if (line.Length > 0)
                            {
                                // define with a name and value
                                AddDef(nameSpan.ToString(), line.ToString());
                            }
                            else
                            {
                                // define with a name only
                                AddDef(nameSpan.ToString(), "1");
                            }
                        }
                        else
                        {
                            ReportError($"{DirectiveChar}define is missing a name ({file.FilePath}:{file.LineNumber}).");
                        }
                    }
                }
                else if (line.StartsWith("undef", StringComparison.Ordinal))
                {
                    line = line["undef".Length..];

                    if (line.Length > 0 && char.IsWhiteSpace(line[0]))
                    {
                        line = line.TrimStart();

                        if (line.Length > 0)
                        {
                            RemoveDef(line.ToString());
                        }
                        else
                        {
                            ReportError($"{DirectiveChar}undef is missing a name ({file.FilePath}:{file.LineNumber}).");
                        }
                    }
                }
                else if (line.StartsWith("include", StringComparison.Ordinal))
                {
                    line = line["include".Length..];

                    if (line.Length > 0 && char.IsWhiteSpace(line[0]))
                    {
                        // trim whitespace, single quotes, leading <, trailing >
                        line = line
                            .Trim()
                            .Trim('"')
                            .TrimStart('<')
                            .TrimEnd('>');

                        if (line.Length > 0)
                        {
                            AddFile(line.ToString(), true);
                        }
                        else
                        {
                            ReportError($"{DirectiveChar}include is missing a file name ({file.FilePath}:{file.LineNumber}).");
                        }
                    }
                }
            }
        }

        private void ReportError(string message)
        {
            reportError?.Invoke(message);
        }

        #region IDisposable Members

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed)
                return;

            if (disposing)
            {
                FileEntry f = file;
                while (f != null)
                {
                    f.Dispose();
                    f = f.Prev;
                }
                file = null;
            }

            disposed = true;
        }

        #endregion

        #region Helper classes

        private sealed class FileEntry : IDisposable
        {
            private StreamReader reader;
            private bool disposed = false;

            public FileEntry(StreamReader reader, string filePath)
            {
                this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
                FilePath = filePath;
            }

            public string FilePath
            {
                get;
            }

            public int LineNumber
            {
                get;
                private set;
            } = 0;

            /// <summary>
            /// The previous entry (linked list style stack).
            /// </summary>
            public FileEntry Prev
            {
                get;
                set;
            }

            public string ReadLine()
            {
                if (reader == null)
                    return null;

                string line = reader.ReadLine();

                if (line != null)
                {
                    LineNumber++;
                }

                return line;
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                if (reader != null)
                {
                    reader.Dispose();
                    reader = null;
                }

                disposed = true;
            }
        }

        private class IfBlock
        {
            public enum WhereType
            {
                in_if = 0,
                in_else = 1
            }

            public WhereType Where;

            public enum CondType
            {
                is_false = 0,
                is_true = 1
            }

            public CondType Cond;

            /// <summary>
            /// The previous IfBlock (linked list style stack).
            /// </summary>
            public IfBlock Prev
            {
                get;
                set;
            }
        }

        #endregion
    }
}
