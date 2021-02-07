using SS.Core.ComponentInterfaces;
using SS.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SS.Core.Configuration
{
    /// <summary>
    /// A line by line object model representation of a .conf file.
    /// </summary>
    public class ConfFile
    {
        private const char DirectiveChar = '#';
        private const char ContinueChar = '\\';
        private static readonly char[] CommentChars = { '/', ';' };

        private const char SectionStartChar = '[';
        private const char SectionEndChar = ']';
        private const char PropertySectionOverrideChar = ':';

        public ConfFile()
        {
        }

        public ConfFile(string path) : this(path, null)
        {
        }

        public ConfFile(string path, IConfigLogger logger)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Cannot be null or white-space.", nameof(path));

            Path = path;
            this.logger = logger;
        }

        public string Path { get; private set; }
        public DateTime? LastModified { get; private set; }
        public bool IsReloadNeeded => LastModified != File.GetLastWriteTimeUtc(Path);
        public List<RawLine> Lines { get; } = new List<RawLine>();

        /// <summary>
        /// Whether changes have been made that have not yet been saved to <see cref="Path"/>.
        /// </summary>
        public bool IsDirty { get; private set; } = false;

        public event EventHandler Changed;

        private int lineNumber = 0;
        private readonly IConfigLogger logger = null;

        /// <summary>
        /// Loads or reloads from the <see cref="Path"/>.
        /// </summary>
        public void Load()
        {
            if (string.IsNullOrWhiteSpace(Path))
                throw new InvalidOperationException("A path is required.");

            LastModified = null;
            Lines.Clear();
            IsDirty = false;
            lineNumber = 0;

            using FileStream fileStream = File.OpenRead(Path);
            using StreamReader reader = new StreamReader(fileStream, StringUtils.DefaultEncoding, true);

            RefreshLastModified();

            string line;
            StringBuilder lineBuilder = new StringBuilder();
            StringBuilder rawBuilder = new StringBuilder(); // the original

            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;

                lineBuilder.Append(line);

                if (line.EndsWith(ContinueChar))
                {
                    lineBuilder.Length--;
                    rawBuilder.AppendLine(line);
                    continue;
                }

                string raw;

                if (rawBuilder.Length > 0)
                {
                    rawBuilder.AppendLine(line);
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

            OnChanged();
        }

        private void RefreshLastModified()
        {
            LastModified = File.GetLastWriteTimeUtc(Path);
        }

        private RawLine ParseLine(string raw, ReadOnlySpan<char> line)
        {
            if (line.IsWhiteSpace())
            {
                return RawLine.Empty;
            }
            else if (CommentChars.Contains(line[0]))
            {
                return new RawComment()
                {
                    Raw = raw,
                    CommentChar = line[0],
                    Text = line[1..].TrimStart().ToString(),
                };
            }
            else if (line[0] == DirectiveChar)
            {
                line = line[1..]; // skip DirectiveChar

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
                            return isNot
                                ? new RawPreprocessorIfndef() { Raw = raw, Name = line.ToString(), }
                                : new RawPreprocessorIfdef() { Raw = raw, Name = line.ToString(), };
                        }
                        else
                        {
                            ReportError(
                                $"{DirectiveChar}{(isNot ? "ifndef" : "ifdef")} " +
                                $"is missing an identifier ({Path}:{lineNumber}).");
                        }
                    }
                }
                else if (line.StartsWith("else", StringComparison.Ordinal))
                {
                    return RawPreprocessor.Else;
                }
                else if (line.StartsWith("endif", StringComparison.Ordinal))
                {
                    return RawPreprocessor.Endif;
                }
                else if (line.StartsWith("define", StringComparison.Ordinal))
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

                            return new RawPreprocessorDefine()
                            {
                                Raw = raw,
                                Name = nameSpan.ToString(),
                                Value = (line.Length > 0) ? line.ToString() : null,
                            };
                        }
                        else
                        {
                            ReportError($"{DirectiveChar}define is missing a name ({Path}:{lineNumber}).");
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
                            return new RawPreprocessorUndef()
                            {
                                Raw = raw,
                                Name = line.ToString(),
                            };
                        }
                        else
                        {
                            ReportError($"{DirectiveChar}undef is missing a name ({Path}:{lineNumber}).");
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
                            return new RawPreprocessorInclude()
                            {
                                Raw = raw,
                                FilePath = line.ToString(),
                            };
                        }
                        else
                        {
                            ReportError($"{DirectiveChar}include is missing a file name ({Path}:{lineNumber}).");
                        }
                    }
                }
            }
            else if (line[0] == SectionStartChar)
            {
                if (line[^1] == SectionEndChar)
                {
                    line = line[1..^1].Trim();
                }
                else
                {
                    ReportError($"Section is missing ']' ({Path}:{lineNumber})");

                    // use the remainder of the line as the section name
                    line = line[1..].Trim();
                }

                return new RawSection()
                {
                    Raw = raw,
                    Name = line.ToString(),
                };
            }
            else
            {
                ParseConfProperty(
                    line, 
                    out string sectionOverride,
                    out string key, 
                    out ReadOnlySpan<char> value,
                    out bool hasValueDelimiter);

                return new RawProperty()
                {
                    Raw = raw,
                    SectionOverride = sectionOverride,
                    Key = key,
                    Value = value.IsEmpty ? string.Empty : value.ToString(),
                    HasDelimiter = hasValueDelimiter,
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
        /// <param name="value">The resulting value. <see cref="Span{char}.Empty"/> if there is no value.</param>
        /// <param name="hasValueDelimiter"></param>
        private static void ParseConfProperty(
            ReadOnlySpan<char> line,
            out string sectionOverride,
            out string key,
            out ReadOnlySpan<char> value,
            out bool hasValueDelimiter)
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

            ParseKey(sb, out sectionOverride, out key);

            line = line[i..];
            hasValueDelimiter = line.Length > 0 && line[0] == '=';
            value = line.TrimStart('=').Trim();
        }

        private static void ParseKey(StringBuilder sb, out string sectionOverride, out string key)
        {
            if (sb == null)
                throw new ArgumentNullException(nameof(sb));

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
                if (sb[delimiter] == PropertySectionOverrideChar)
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
            logger.Log(LogLevel.Warn, message);
        }

        protected virtual void OnChanged()
        {
            EventHandler handler = Changed;
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

        public void Save()
        {
            if (string.IsNullOrWhiteSpace(Path))
                throw new InvalidOperationException("Path required.");

            Save(Path);
        }

        public void Save(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A path is required.", nameof(path));

            using (StreamWriter writer = new StreamWriter(path, false, StringUtils.DefaultEncoding))
            {
                foreach (var line in Lines)
                {
                    line.WriteTo(writer);
                }
            }

            if (Path != path)
                Path = path;

            IsDirty = false;
            RefreshLastModified();
        }
    }
}
