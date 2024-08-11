using SS.Core.ComponentInterfaces;
using System;
using System.Collections.Generic;

namespace SS.Core.Configuration
{
    /// <summary>
    /// Reads lines from <see cref="ConfFile"/> objects, starting from a base/root <see cref="ConfFile"/>,
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
    /// <para>Macros are NOT expanded.</para>
    /// </summary>
    /// <remarks>
    /// Similar to a <see cref="System.IO.TextReader"/>, but instead of returning strings, 
    /// it returns <see cref="LineReference"/> objects which tell the line itself and the <see cref="ConfFile"/> it came from.
    /// <para>
    /// This is the equivalent of app.[c|h], the [a]sss [p]re[p]rocessor, in ASSS.
    /// However, the parsing/tokenizing logic is separated out, see <see cref="ConfFile"/>.
    /// In other words, this class focuses solely on interpreting preprocessor directives.
    /// </para>
    /// </remarks>
    public sealed class PreprocessorReader : IDisposable
    {
        private readonly IConfFileProvider _fileProvider;
        private readonly IConfigLogger? _logger = null;

        public PreprocessorReader(
            IConfFileProvider fileProvider,
            ConfFile baseFile) : this(fileProvider, baseFile, null)
        {
        }

        public PreprocessorReader(
            IConfFileProvider fileProvider,
            ConfFile baseFile,
            IConfigLogger? logger)
        {
            ArgumentNullException.ThrowIfNull(baseFile);

            _fileProvider = fileProvider ?? throw new ArgumentNullException(nameof(fileProvider));
            CurrentFile = new FileEntry(baseFile, null);
            _logger = logger;

            Processing = true;
        }

        /// <summary>
        /// Stack of files to read from.
        /// </summary>
        private FileEntry? CurrentFile { get; set; }

        /// <summary>
        /// The current #include file depth.
        /// </summary>
        public int Depth { get; private set; }

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
        /// Stack of representing #ifdef, #ifndef, #else, #endif
        /// </summary>
        private IfBlock? Ifs { get; set; }

        /// <summary>
        /// Whether the next line is in a context where it should be 'processed', 
        /// based on the position of the line with regards to <see cref="Ifs"/> 
        /// and what is defined in <see cref="Defs"/>.
        /// </summary>
        private bool Processing { get; set; }

        /// <summary>
        /// Key/value pairs from #define lines.
        /// </summary>
        private Dictionary<string, string> Defs { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<ConfFile> processedFiles = new();
        public IReadOnlySet<ConfFile> ProcessedFiles => processedFiles;

        /// <summary>
        /// Adds a file to the top of the stack.
        /// </summary>
        /// <param name="file">The file to add.</param>
        private void PushFile(ConfFile file)
        {
            ArgumentNullException.ThrowIfNull(file);

            if (Depth == MaxRecursionDepth)
            {
                ReportError($"Maximum #include recursion depth reached while adding '{file.Path}'.");
                return;
            }

            CurrentFile = new FileEntry(file, CurrentFile);
            Depth++;

            processedFiles.Add(file);
        }

        /// <summary>
        /// Removes a file from the top of the stack.
        /// </summary>
        private void PopFile()
        {
            if (CurrentFile == null)
                return;

            CurrentFile.Enumerator.Dispose();
            CurrentFile = CurrentFile.Previous;
            Depth--;
        }

        /// <summary>
        /// Push for an #ifdef or #ifndef
        /// </summary>
        /// <param name="cond"></param>
        private void PushIf(bool cond)
        {
            IfBlock i = new();
            i.Cond = cond ? IfBlock.CondType.IsTrue : IfBlock.CondType.IsFalse;
            i.Where = IfBlock.WhereType.InIf;
            i.Prev = Ifs;
            Ifs = i;

            UpdateProcessing();
        }

        /// <summary>
        /// Pops for an #endif
        /// </summary>
        private void PopIf()
        {
            if (Ifs != null)
            {
                Ifs = Ifs.Prev;
            }
            else
            {
                ReportError($"No #if blocks to end ({CurrentFile!.File?.Path}:{CurrentFile.LineNumber}).");
            }

            UpdateProcessing();
        }

        /// <summary>
        /// Switches for an #else
        /// </summary>
        private void SwitchIf()
        {
            if (Ifs != null)
            {
                if (Ifs.Where == IfBlock.WhereType.InIf)
                {
                    Ifs.Where = IfBlock.WhereType.InElse;
                }
                else
                {
                    ReportError($"Multiple #else directives ({CurrentFile!.File?.Path}:{CurrentFile.LineNumber}).");
                }
            }
            else
            {
                ReportError($"Unexpected #else directive ({CurrentFile!.File?.Path}:{CurrentFile.LineNumber}).");
            }

            UpdateProcessing();
        }

        /// <summary>
        /// Refreshes <see cref="Processing"/>.
        /// </summary>
        private void UpdateProcessing()
        {
            IfBlock? i = Ifs;

            Processing = true;

            while (i != null)
            {
                if ((int)i.Cond == (int)i.Where)
                {
                    Processing = false;
                }

                i = i.Prev;
            }
        }

        private LineReference? ReadNext()
        {
            while (CurrentFile != null)
            {
                if (!CurrentFile.Enumerator.MoveNext())
                {
                    // end of the current file, pop it off and try the next
                    PopFile();
                }
                else
                {
                    CurrentFile.LineNumber++;

                    return new LineReference(CurrentFile.Enumerator.Current, CurrentFile.File);
                }
            }

            return null;
        }

        /// <summary>
        /// Reads a line.
        /// </summary>
        /// <returns>
        /// A <see cref="LineReference"/> containing info about the line that was read, or <see langword="null"/> if all lines have been read.
        /// </returns>
        public LineReference? ReadLine()
        {
            LineReference? lineReference;
            while ((lineReference = ReadNext()) != null)
            {
                RawLine rawLine = lineReference.Line;

                if (rawLine.LineType == ConfLineType.PreprocessorIfdef)
                {
                    RawPreprocessorIfdef ifdef = (RawPreprocessorIfdef)rawLine;
                    PushIf(Defs.ContainsKey(ifdef.Name));
                }
                else if (rawLine.LineType == ConfLineType.PreprocessorIfndef)
                {
                    RawPreprocessorIfndef ifndef = (RawPreprocessorIfndef)rawLine;
                    PushIf(!Defs.ContainsKey(ifndef.Name));
                }
                else if (rawLine.LineType == ConfLineType.PreprocessorElse)
                {
                    SwitchIf();
                }
                else if (rawLine.LineType == ConfLineType.PreprocessorEndif)
                {
                    PopIf();
                }
                else if (Processing)
                {
                    if (rawLine.LineType == ConfLineType.PreprocessorDefine)
                    {
                        RawPreprocessorDefine define = (RawPreprocessorDefine)rawLine;
                        Defs[define.Name] = define.Value ?? "1";
                    }
                    else if (rawLine.LineType == ConfLineType.PreprocessorUndef)
                    {
                        RawPreprocessorUndef undef = (RawPreprocessorUndef)rawLine;
                        Defs.Remove(undef.Name);
                    }
                    else if (rawLine.LineType == ConfLineType.PreprocessorInclude)
                    {
                        RawPreprocessorInclude include = (RawPreprocessorInclude)rawLine;
                        ConfFile? includeFile = _fileProvider.GetFile(include.FilePath);
                        if (includeFile != null)
                        {
                            PushFile(includeFile);
                        }
                    }
                    else
                    {
                        switch (rawLine.LineType)
                        {
                            case ConfLineType.Section:
                            case ConfLineType.Property:
                            case ConfLineType.Empty:
                            case ConfLineType.Comment:
                                return lineReference;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Releases all resources used by the <see cref="PreprocessorReader"/> object.
        /// </summary>
        public void Dispose()
        {
            while (CurrentFile != null)
            {
                CurrentFile.Enumerator.Dispose();
                CurrentFile = CurrentFile.Previous;
            }

            GC.SuppressFinalize(this);
        }

        private void ReportError(string message)
        {
            _logger?.Log(LogLevel.Warn, message);
        }

        #region Helper classes

        private class IfBlock
        {
            public enum WhereType
            {
                InIf = 0,
                InElse = 1
            }

            public WhereType Where;

            public enum CondType
            {
                IsFalse = 0,
                IsTrue = 1
            }

            public CondType Cond;

            /// <summary>
            /// The previous IfBlock (linked list style stack).
            /// </summary>
            public IfBlock? Prev
            {
                get;
                set;
            }
        }

        private sealed class FileEntry : IDisposable
        {
            public FileEntry(ConfFile file, FileEntry? previous)
            {
                File = file ?? throw new ArgumentNullException(nameof(file));
                Enumerator = file.Lines.GetEnumerator();
                LineNumber = 0;
                Previous = previous;
            }

            public ConfFile File { get; }

            public IEnumerator<RawLine> Enumerator { get; }

            public int LineNumber { get; set; }

            public FileEntry? Previous { get; set; }

            public void Dispose()
            {
                Enumerator.Dispose();
                GC.SuppressFinalize(this);
            }
        }

        #endregion
    }
}
