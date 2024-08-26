using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SS.Core.Configuration
{
    /// <summary>
    /// Base class for objects representing lines of a <see cref="ConfFile"/>.
    /// This object model is immutable by design.
    /// </summary>
    public abstract class RawLine
    {
        public const char ContinueChar = '\\';

        public readonly static RawEmptyLine Empty = new();

        /// <summary>
        /// Constructor for a line that was not read from a file.
        /// </summary>
        public RawLine()
        {
        }

        /// <summary>
        /// Constructor for a line that was read from a file.
        /// </summary>
        /// <param name="raw"></param>
        public RawLine(string raw)
        {
            Raw = raw ?? throw new ArgumentNullException(nameof(raw));
        }

        /// <summary>
        /// The raw text of the line.  Only for lines read from a file, otherwise <see langword="null"/>.
        /// This allows the line to be re-written exactly as it originally was (including any white-space) when saving a <see cref="ConfFile"/>.
        /// </summary>
        public string? Raw { get; init; }

        /// <summary>
        /// The type of line. Derived classes must override this.
        /// </summary>
        public abstract ConfLineType LineType { get; }

        /// <summary>
        /// Writes the line to a <see cref="TextWriter"/>.
        /// </summary>
        /// <param name="writer">The <see cref="TextWriter"/> to write to.</param>
        public abstract Task WriteToAsync(TextWriter writer);

        /// <summary>
        /// Writes the <see cref="Raw"/> text to a <see cref="TextWriter"/>, if there is any.
        /// </summary>
        /// <param name="writer">The <see cref="TextWriter"/> to write to.</param>
        /// <returns>True if there was <see cref="Raw"/> text to write. Otherwise, false.</returns>
        protected async Task<bool> TryWriteRawAsync(TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (Raw != null)
            {
                await writer.WriteLineAsync(Raw).ConfigureAwait(false);
                return true;
            }
            else
            {
                return false;
            }
        }
    }

    /// <summary>
    /// For lines read from a file that could not be parsed.
    /// </summary>
    public class RawParseError(string raw) : RawLine(raw)
    {
        public override ConfLineType LineType => ConfLineType.ParseError;

        public override Task WriteToAsync(TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            return TryWriteRawAsync(writer); // guaranteed to write since Raw cannot be null
        }
    }

    /// <summary>
    /// An empty line.
    /// </summary>
    public class RawEmptyLine : RawLine
    {
        public RawEmptyLine() : base()
        {
        }

        public RawEmptyLine(string raw) : base(raw)
        {
            if (raw == null || !string.IsNullOrWhiteSpace(raw))
                throw new ArgumentException("Cannot be null and must be all white-space.", nameof(raw));
        }

        public override ConfLineType LineType => ConfLineType.Empty;

        public override async Task WriteToAsync(TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (!await TryWriteRawAsync(writer).ConfigureAwait(false))
                await writer.WriteLineAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A line that is a comment.
    /// </summary>
    public class RawComment : RawLine
    {
        public const char DefaultCommentChar = ';';
        public const char AltCommentChar = '/';
        public static readonly char[] CommentChars = [DefaultCommentChar, AltCommentChar];

        public RawComment(char commentChar, string text)
        {
            if (!CommentChars.Contains(commentChar))
                throw new ArgumentException("Value is not a comment character.", nameof(commentChar));

            CommentChar = commentChar;
            Text = text;
        }

        public override ConfLineType LineType => ConfLineType.Comment;

        /// <summary>
        /// Which character in <see cref="CommentChars"/> to use.
        /// </summary>
        public char CommentChar { get; }

        /// <summary>
        /// The comment text itself.
        /// </summary>
        public string Text { get; }

        public override async Task WriteToAsync(TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (!await TryWriteRawAsync(writer).ConfigureAwait(false))
            {
                await writer.WriteAsync(CommentChar).ConfigureAwait(false);
                await writer.WriteAsync(' ').ConfigureAwait(false);
                await writer.WriteLineAsync(Text).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A line representing the start of a section.
    /// </summary>
    public class RawSection(string name) : RawLine
    {
        public const char StartChar = '[';
        public const char EndChar = ']';

        public override ConfLineType LineType => ConfLineType.Section;

        public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

        public override async Task WriteToAsync(TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (!await TryWriteRawAsync(writer).ConfigureAwait(false))
            {
                await writer.WriteAsync(StartChar).ConfigureAwait(false);
                await writer.WriteAsync(Name).ConfigureAwait(false);
                await writer.WriteLineAsync(EndChar).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A line with a key-value pair, where the value is optional.
    /// </summary>
    public class RawProperty(string? sectionOverride, string key, string value, bool hasDelimiter) : RawLine
    {
        public const char SectionOverrideDelimiter = ':';
        public const char KeyValueDelimiter = '=';

        public override ConfLineType LineType => ConfLineType.Property;

        public string? SectionOverride { get; } = sectionOverride; // for <section>:<key>
        public string Key { get; } = key;
        public string Value { get; } = value;
        public bool HasDelimiter { get; } = hasDelimiter || !string.IsNullOrWhiteSpace(value);

        public override async Task WriteToAsync(TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (await TryWriteRawAsync(writer).ConfigureAwait(false))
                return;

            if (SectionOverride != null) // note: allowing empty string for no section
            {
                await writer.WriteAsync(SectionOverride).ConfigureAwait(false);
                await writer.WriteAsync(SectionOverrideDelimiter).ConfigureAwait(false);
            }

            await writer.WriteAsync(Key).ConfigureAwait(false);

            if (HasDelimiter || !string.IsNullOrWhiteSpace(Value))
            {
                await writer.WriteAsync(' ').ConfigureAwait(false);
                await writer.WriteAsync(KeyValueDelimiter).ConfigureAwait(false);
                await writer.WriteAsync(' ').ConfigureAwait(false);
                await writer.WriteAsync(Value).ConfigureAwait(false);
            }

            await writer.WriteLineAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Base class for lines that are preprocessor directives.
    /// </summary>
    public abstract class RawPreprocessor : RawLine
    {
        public const char DirectiveChar = '#';

        public static readonly RawPreprocessorElse Else = new();
        public static readonly RawPreprocessorEndif Endif = new();
    }

    /// <summary>
    /// A line representing the #include preprocessor directive.
    /// </summary>
    public class RawPreprocessorInclude : RawPreprocessor
    {
        public const string Directive = "include";

        public RawPreprocessorInclude(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Cannot be null or white-space.", nameof(filePath));

            FilePath = filePath;
        }

        public override ConfLineType LineType => ConfLineType.PreprocessorInclude;

        //public override string Command => "include";

        public string FilePath { get; }

        public override async Task WriteToAsync(TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (!await TryWriteRawAsync(writer).ConfigureAwait(false))
            {
                if (FilePath.Any(c => char.IsWhiteSpace(c)))
                {
                    await writer.WriteAsync(DirectiveChar).ConfigureAwait(false);
                    await writer.WriteAsync(Directive).ConfigureAwait(false);
                    await writer.WriteAsync(' ').ConfigureAwait(false);
                    await writer.WriteAsync('"').ConfigureAwait(false);
                    await writer.WriteAsync(FilePath).ConfigureAwait(false);
                    await writer.WriteLineAsync('"').ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteAsync(DirectiveChar).ConfigureAwait(false);
                    await writer.WriteAsync(Directive).ConfigureAwait(false);
                    await writer.WriteAsync(' ').ConfigureAwait(false);
                    await writer.WriteLineAsync(FilePath).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// A line representing the #define preprocessor directive.
    /// </summary>
    public class RawPreprocessorDefine : RawPreprocessor
    {
        public const string Directive = "define";

        public RawPreprocessorDefine(string name) : this(name, null)
        {
        }

        public RawPreprocessorDefine(string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Cannot be null or white-space.", nameof(name));

            Name = name;
            Value = value;
        }

        public override ConfLineType LineType => ConfLineType.PreprocessorDefine;

        public string Name { get; }
        public string? Value { get; }

        public override async Task WriteToAsync(TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (!await TryWriteRawAsync(writer).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(Value))
                {
                    await writer.WriteAsync(DirectiveChar).ConfigureAwait(false);
                    await writer.WriteAsync(Directive).ConfigureAwait(false);
                    await writer.WriteAsync(' ').ConfigureAwait(false);
                    await writer.WriteLineAsync(Name).ConfigureAwait(false);
                }
                else
                {
                    await writer.WriteAsync(DirectiveChar).ConfigureAwait(false);
                    await writer.WriteAsync(Directive).ConfigureAwait(false);
                    await writer.WriteAsync(' ').ConfigureAwait(false);
                    await writer.WriteAsync(Name).ConfigureAwait(false);
                    await writer.WriteAsync(' ').ConfigureAwait(false);
                    await writer.WriteLineAsync(Value).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>
    /// A line representing the #undef preprocessor directive.
    /// </summary>
    public class RawPreprocessorUndef(string name) : RawPreprocessor
    {
        public const string Directive = "undef";

        public override ConfLineType LineType => ConfLineType.PreprocessorUndef;

        public string Name { get; } = name;

        public override async Task WriteToAsync(TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (!await TryWriteRawAsync(writer).ConfigureAwait(false))
            {
                await writer.WriteAsync(DirectiveChar).ConfigureAwait(false);
                await writer.WriteAsync(Directive).ConfigureAwait(false);
                await writer.WriteAsync(' ').ConfigureAwait(false);
                await writer.WriteLineAsync(Name).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A line representing the #ifdef preprocessor directive.
    /// </summary>
    public class RawPreprocessorIfdef(string name) : RawPreprocessor
    {
        public const string Directive = "ifdef";

        public override ConfLineType LineType => ConfLineType.PreprocessorIfdef;

        public string Name { get; } = name;

        public override async Task WriteToAsync(TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (!await TryWriteRawAsync(writer).ConfigureAwait(false))
            {
                await writer.WriteAsync(DirectiveChar).ConfigureAwait(false);
                await writer.WriteAsync(Directive).ConfigureAwait(false);
                await writer.WriteAsync(' ').ConfigureAwait(false);
                await writer.WriteLineAsync(Name).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A line representing the #ifndef preprocessor directive.
    /// </summary>
    public class RawPreprocessorIfndef(string name) : RawPreprocessor
    {
        public const string Directive = "ifndef";

        public override ConfLineType LineType => ConfLineType.PreprocessorIfndef;

        public string Name { get; } = name;

        public override async Task WriteToAsync(TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (!await TryWriteRawAsync(writer).ConfigureAwait(false))
            {
                await writer.WriteAsync(DirectiveChar).ConfigureAwait(false);
                await writer.WriteAsync(Directive).ConfigureAwait(false);
                await writer.WriteAsync(' ').ConfigureAwait(false);
                await writer.WriteLineAsync(Name).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A line representing the #else preprocessor directive.
    /// </summary>
    public class RawPreprocessorElse : RawPreprocessor
    {
        public const string Directive = "else";

        public override ConfLineType LineType => ConfLineType.PreprocessorElse;

        public override async Task WriteToAsync(TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (!await TryWriteRawAsync(writer).ConfigureAwait(false))
            {
                await writer.WriteAsync(DirectiveChar).ConfigureAwait(false);
                await writer.WriteLineAsync(Directive).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A line representing the #endif preprocessor directive.
    /// </summary>
    public class RawPreprocessorEndif : RawPreprocessor
    {
        public const string Directive = "endif";

        public override ConfLineType LineType => ConfLineType.PreprocessorEndif;

        public override async Task WriteToAsync(TextWriter writer)
        {
            ArgumentNullException.ThrowIfNull(writer);

            if (!await TryWriteRawAsync(writer).ConfigureAwait(false))
            {
                await writer.WriteAsync(DirectiveChar).ConfigureAwait(false);
                await writer.WriteLineAsync(Directive).ConfigureAwait(false);
            }
        }
    }
}
