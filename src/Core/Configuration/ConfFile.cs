using Microsoft.Extensions.ObjectPool;
using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SS.Core.Configuration
{
    /// <summary>
    /// A line by line object model representation of a .conf file.
    /// </summary>
    public class ConfFile
    {
        private static readonly ObjectPool<StringBuilder> s_stringBuilderPool = new DefaultObjectPoolProvider().CreateStringBuilderPool(1024, 1024 * 8);

        /// <summary>
        /// Constructor for a brand new conf file, not on disk.
        /// </summary>
        public ConfFile()
        {
        }

        /// <summary>
        /// Constructor for a conf file that is on disk.
        /// </summary>
        /// <param name="path">The path of the file.</param>
        public ConfFile(string path) : this(path, null)
        {
        }

        /// <summary>
        /// Constructor for a conf file that is on disk, with a logger.
        /// </summary>
        /// <param name="path">The path of the file.</param>
        /// <param name="logger">A logger to report errors to.</param>
        public ConfFile(string path, IConfigLogger? logger)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Cannot be null or white-space.", nameof(path));

            Path = path;
            _logger = logger;
        }

        private string? _path;

        /// <summary>
        /// Gets the path of the <see cref="ConfFile"/>.
        /// <see langword="null"/> for a <see cref="ConfFile"/> that is not yet saved to disk.
        /// </summary>
        public string? Path
        {
            get => _path;
            private set
            {
                _path = value;
                _fileInfo = string.IsNullOrWhiteSpace(_path) ? null : new FileInfo(_path);
            }
        }

        private FileInfo? _fileInfo;

        /// <summary>
        /// Gets the last known timestamp that a <see cref="ConfFile"/> was last modified on disk.
        /// <see langword="null"/> for a <see cref="ConfFile"/> that is not yet saved to disk.
        /// </summary>
        public DateTime? LastModified { get; private set; }

        /// <summary>
        /// Gets whether the file needs to be reloaded from disk, based on the last modified timestamp of the file.
        /// <see cref="false"/> for a <see cref="ConfFile"/> that is not yet saved to disk.
        /// </summary>
        public bool IsReloadNeeded
        {
            get
            {
                if (!TryGetLastModified(out DateTime value))
                    return false;

                return LastModified != value;
            }
        }

        public List<RawLine> Lines { get; } = new List<RawLine>();

        /// <summary>
        /// Whether changes have been made that have not yet been saved to <see cref="Path"/>.
        /// </summary>
        public bool IsDirty { get; private set; } = false;

        public event EventHandler? Changed;

        private int _lineNumber = 0;
        private readonly IConfigLogger? _logger = null;

        /// <summary>
        /// Loads or reloads from the <see cref="Path"/>.
        /// </summary>
        public async Task LoadAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(Path))
                throw new InvalidOperationException("A path is required.");

            LastModified = null;
            Lines.Clear();
            IsDirty = false;
            _lineNumber = 0;

            await using FileStream fileStream = await Task.Factory.StartNew(
                static (obj) => new FileStream((string)obj!, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true),
                Path).ConfigureAwait(false);

            using StreamReader reader = new(fileStream, StringUtils.DefaultEncoding, true, -1, true); // leave open so that the FileStream.DisposeAsync will take care of it

            await Task.Run(RefreshLastModified, cancellationToken).ConfigureAwait(false);

            string? line;
            StringBuilder lineBuilder = s_stringBuilderPool.Get();
            StringBuilder rawBuilder = s_stringBuilderPool.Get(); // the original

            try
            {
                while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
                {
                    _lineNumber++;

                    lineBuilder.Append(line);

                    if (line.EndsWith(RawLine.ContinueChar))
                    {
                        lineBuilder.Length--;
                        rawBuilder.AppendLine(line);
                        continue;
                    }

                    string raw;

                    if (rawBuilder.Length > 0)
                    {
                        rawBuilder.Append(line);
                        raw = rawBuilder.ToString();
                        rawBuilder.Clear();
                    }
                    else
                    {
                        raw = line;
                    }

                    line = lineBuilder.ToTrimmedString();
                    lineBuilder.Clear();

                    Lines.Add(ParseLine(raw, line));
                }
            }
            finally
            {
                s_stringBuilderPool.Return(lineBuilder);
                s_stringBuilderPool.Return(rawBuilder);
            }

            OnChanged();
        }

        private void RefreshLastModified()
        {
            if (TryGetLastModified(out DateTime value))
            {
                LastModified = value;
            }
        }

        private bool TryGetLastModified(out DateTime value)
        {
            if (_fileInfo == null)
            {
                value = default;
                return false;
            }

            try
            {
                _fileInfo.Refresh();
                value = _fileInfo.LastWriteTimeUtc;
                return true;
            }
            catch (Exception ex)
            {
                ReportError($"Error getting last write time of '{Path}'. {ex.Message}");
                value = default;
                return false;
            }
        }

        private RawLine ParseLine(string raw, ReadOnlySpan<char> line)
        {
            if (line.IsWhiteSpace())
            {
                return RawLine.Empty;
            }
            else if (RawComment.CommentChars.Contains(line[0]))
            {
                return new RawComment(
                    line[0],
                    line[1..].TrimStart().ToString())
                {
                    Raw = raw
                };
            }
            else if (line[0] == RawPreprocessor.DirectiveChar)
            {
                line = line[1..]; // skip DirectiveChar

                if (line.StartsWith(RawPreprocessorIfdef.Directive, StringComparison.Ordinal)
                    || line.StartsWith(RawPreprocessorIfndef.Directive, StringComparison.Ordinal))
                {
                    bool isNot = line[2] == 'n';
                    line = line[(isNot ? RawPreprocessorIfndef.Directive.Length : RawPreprocessorIfdef.Directive.Length)..];

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
                            return isNot
                                ? new RawPreprocessorIfndef(line.ToString()) { Raw = raw }
                                : new RawPreprocessorIfdef(line.ToString()) { Raw = raw };
                        }
                        else
                        {
                            ReportError(
                                $"{RawPreprocessor.DirectiveChar}{(isNot ? RawPreprocessorIfndef.Directive : RawPreprocessorIfdef.Directive)} " +
                                $"is missing an identifier ({Path}:{_lineNumber}).");
                        }
                    }
                }
                else if (line.StartsWith(RawPreprocessorElse.Directive, StringComparison.Ordinal))
                {
                    return RawPreprocessor.Else;
                }
                else if (line.StartsWith(RawPreprocessorEndif.Directive, StringComparison.Ordinal))
                {
                    return RawPreprocessor.Endif;
                }
                else if (line.StartsWith(RawPreprocessorDefine.Directive, StringComparison.Ordinal))
                {
                    line = line[RawPreprocessorDefine.Directive.Length..];

                    if (line.Length > 0 && char.IsWhiteSpace(line[0]))
                    {
                        line = line.TrimStart();

                        if (line.Length > 0)
                        {
                            // find the end of the name
                            int i = 0;
                            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;

                            ReadOnlySpan<char> nameSpan = line[..i];
                            line = line[i..].TrimStart();

                            return new RawPreprocessorDefine(
                                nameSpan.ToString(),
                                (line.Length > 0) ? line.ToString() : null)
                            {
                                Raw = raw,
                            };
                        }
                        else
                        {
                            ReportError($"{RawPreprocessor.DirectiveChar}{RawPreprocessorDefine.Directive} is missing a name ({Path}:{_lineNumber}).");
                        }
                    }
                }
                else if (line.StartsWith(RawPreprocessorUndef.Directive, StringComparison.Ordinal))
                {
                    line = line[RawPreprocessorUndef.Directive.Length..];

                    if (line.Length > 0 && char.IsWhiteSpace(line[0]))
                    {
                        line = line.TrimStart();

                        if (line.Length > 0)
                        {
                            return new RawPreprocessorUndef(line.ToString())
                            {
                                Raw = raw,
                            };
                        }
                        else
                        {
                            ReportError($"{RawPreprocessor.DirectiveChar}{RawPreprocessorUndef.Directive} is missing a name ({Path}:{_lineNumber}).");
                        }
                    }
                }
                else if (line.StartsWith(RawPreprocessorInclude.Directive, StringComparison.Ordinal))
                {
                    line = line[RawPreprocessorInclude.Directive.Length..];

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
                            return new RawPreprocessorInclude(line.ToString())
                            {
                                Raw = raw,
                            };
                        }
                        else
                        {
                            ReportError($"{RawPreprocessor.DirectiveChar}{RawPreprocessorInclude.Directive} is missing a file name ({Path}:{_lineNumber}).");
                        }
                    }
                }
            }
            else if (line[0] == RawSection.StartChar)
            {
                if (line[^1] == RawSection.EndChar)
                {
                    line = line[1..^1].Trim();
                }
                else
                {
                    ReportError($"Section is missing '{RawSection.EndChar}' ({Path}:{_lineNumber})");

                    // use the remainder of the line as the section name
                    line = line[1..].Trim();
                }

                return new RawSection(line.ToString())
                {
                    Raw = raw,
                };
            }
            else
            {
                ParseConfProperty(
                    line,
                    out string? sectionOverride,
                    out string key,
                    out ReadOnlySpan<char> value,
                    out bool hasValueDelimiter);

                return new RawProperty(
                    sectionOverride,
                    key,
                    value.IsEmpty ? string.Empty : value.ToString(),
                    hasValueDelimiter)
                {
                    Raw = raw,
                };
            }

            return new RawParseError(raw);
        }

        /// <summary>
        /// Parses a line of text as a conf file property.
        /// Whitespace is trimmed off from both the key and value.
        /// Characters are unescaped for the key only.
        /// Valid escape character sequences include "\=" for = and "\\" for \.
        /// Invalid escape character sequences are ignored by skipping the \ character.
        /// </summary>
        /// <param name="line">The line to parse.</param>
        /// <param name="sectionOverride"></param>
        /// <param name="key">The resulting key. <see cref="string.Empty"/> if no key found.</param>
        /// <param name="value">The resulting value. <see cref="ReadOnlySpan{char}.Empty"/> if there is no value.</param>
        /// <param name="hasValueDelimiter"></param>
        public static void ParseConfProperty(
            ReadOnlySpan<char> line,
            out string? sectionOverride,
            out string key,
            out ReadOnlySpan<char> value,
            out bool hasValueDelimiter)
        {
            StringBuilder sb = s_stringBuilderPool.Get();
            int i;

            try
            {
                for (i = 0; i < line.Length; i++)
                {
                    if (line[i] == RawProperty.KeyValueDelimiter)
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

                ParseKey(sb, out sectionOverride, out key);
            }
            finally
            {
                s_stringBuilderPool.Return(sb);
            }

            line = line[i..];
            hasValueDelimiter = line.Length > 0 && line[0] == RawProperty.KeyValueDelimiter;
            value = hasValueDelimiter ? line[1..].Trim() : ReadOnlySpan<char>.Empty;
        }

        private static void ParseKey(StringBuilder sb, out string? sectionOverride, out string key)
        {
            ArgumentNullException.ThrowIfNull(sb);

            // Find the last non-whitespace character
            int end;
            for (end = sb.Length - 1; end >= 0; end--)
            {
                if (!char.IsWhiteSpace(sb[end]))
                    break;
            }

            if (end == -1)
            {
                // All whitespace
                sectionOverride = null;
                key = string.Empty;
                return;
            }

            // Find the first non-whitespace character
            int start;
            for (start = 0; start < end; start++)
            {
                if (!char.IsWhiteSpace(sb[start]))
                    break;
            }

            // Try to find the delimiter for a section override
            int delimiter;
            for (delimiter = start; delimiter <= end; delimiter++)
            {
                if (sb[delimiter] == RawProperty.SectionOverrideDelimiter)
                    break;
            }

            if (delimiter <= end)
            {
                // Found the delimiter, there is a section override
                // TODO: remove whitespace around the delimiter
                sectionOverride = sb.ToString(start, delimiter - start);
                key = sb.ToString(delimiter + 1, end - delimiter);
            }
            else
            {
                // No section override
                sectionOverride = null;
                key = sb.ToString(start, end - start + 1);
            }
        }

        private void ReportError(string message)
        {
            _logger?.Log(LogLevel.Warn, message);
        }

        protected virtual void OnChanged()
        {
            EventHandler? handler = Changed;
            handler?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Sets the <see cref="IsDirty"/> flag to be true
        /// and fires the <see cref="Changed"/> event.
        /// </summary>
        public void SetDirty()
        {
            IsDirty = true;
            OnChanged();
        }

        public Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(Path))
                throw new InvalidOperationException("Path required.");

            return SaveAsync(Path);
        }

        public async Task SaveAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A path is required.", nameof(path));

            await using (StreamWriter writer = new(path, false, StringUtils.DefaultEncoding))
            {
                foreach (var line in Lines)
                {
                    await line.WriteToAsync(writer).ConfigureAwait(false);
                }
            }

            if (Path != path)
                Path = path;

            IsDirty = false;
            await Task.Run(RefreshLastModified).ConfigureAwait(false);
        }
    }
}
