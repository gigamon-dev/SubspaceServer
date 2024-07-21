using System;
using System.IO;
using System.Linq;

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
        public string Raw { get; init; }

        /// <summary>
        /// The type of line. Derived classes must override this.
        /// </summary>
        public abstract ConfLineType LineType { get; }

        /// <summary>
        /// Writes the line to a <see cref="TextWriter"/>.
        /// </summary>
        /// <param name="writer">The <see cref="TextWriter"/> to write to.</param>
        public abstract void WriteTo(TextWriter writer);

        /// <summary>
        /// Writes the <see cref="Raw"/> text to a <see cref="TextWriter"/>, if there is any.
        /// </summary>
        /// <param name="writer">The <see cref="TextWriter"/> to write to.</param>
        /// <returns>True if there was <see cref="Raw"/> text to write. Otherwise, false.</returns>
        protected bool TryWriteRaw(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (Raw != null)
            {
                writer.WriteLine(Raw);
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
    public class RawParseError : RawLine
    {
        public RawParseError(string raw) : base(raw)
        {
        }

        public override ConfLineType LineType => ConfLineType.ParseError;

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            TryWriteRaw(writer); // guaranteed to write since Raw cannot be null
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

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
                writer.WriteLine();
        }
    }

    /// <summary>
    /// A line that is a comment.
    /// </summary>
    public class RawComment : RawLine
    {
        public const char DefaultCommentChar = ';';
        public const char AltCommentChar = '/';
        public static readonly char[] CommentChars = { DefaultCommentChar, AltCommentChar };

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

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
                writer.WriteLine($"{CommentChar} {Text}");
        }
    }

    /// <summary>
    /// A line representing the start of a section.
    /// </summary>
    public class RawSection : RawLine
    {
        public const char StartChar = '[';
        public const char EndChar = ']';

        public RawSection(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }

        public override ConfLineType LineType => ConfLineType.Section;

        public string Name { get; }

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
                writer.WriteLine($"{StartChar}{Name}{EndChar}");
        }
    }

    /// <summary>
    /// A line with a key-value pair, where the value is optional.
    /// </summary>
    public class RawProperty : RawLine
    {
        public const char SectionOverrideDelimiter = ':';
        public const char KeyValueDelimiter = '=';

        public RawProperty(string sectionOverride, string key, string value, bool hasDelimiter)
        {
            SectionOverride = sectionOverride;
            Key = key;
            Value = value;
            HasDelimiter = hasDelimiter || !string.IsNullOrWhiteSpace(value);
        }

        public override ConfLineType LineType => ConfLineType.Property;

        public string SectionOverride { get; } // for <section>:<key>
        public string Key { get; }
        public string Value { get; }
        public bool HasDelimiter { get; }

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (TryWriteRaw(writer))
                return;

            if (SectionOverride != null) // note: allowing empty string for no section
            {
                writer.Write(SectionOverride);
                writer.Write(SectionOverrideDelimiter);
            }

            writer.Write(Key);

            if (HasDelimiter || !string.IsNullOrWhiteSpace(Value))
            {
                writer.Write($" {KeyValueDelimiter} ");
                writer.Write(Value);
            }

            writer.WriteLine();
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

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
            {
                if (FilePath.Any(c => char.IsWhiteSpace(c)))
                    writer.WriteLine($"{DirectiveChar}{Directive} \"{FilePath}\"");
                else
                    writer.WriteLine($"{DirectiveChar}{Directive} {FilePath}");
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

        public RawPreprocessorDefine(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Cannot be null or white-space.", nameof(name));

            Name = name;
            Value = value;
        }

        public override ConfLineType LineType => ConfLineType.PreprocessorDefine;

        public string Name { get; }
        public string Value { get; }

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
            {
                if (string.IsNullOrWhiteSpace(Value))
                    writer.WriteLine($"{DirectiveChar}{Directive} {Name}");
                else
                    writer.WriteLine($"{DirectiveChar}{Directive} {Name} {Value}");
            }
        }
    }

    /// <summary>
    /// A line representing the #undef preprocessor directive.
    /// </summary>
    public class RawPreprocessorUndef : RawPreprocessor
    {
        public const string Directive = "undef";

        public RawPreprocessorUndef(string name)
        {
            Name = name;
        }

        public override ConfLineType LineType => ConfLineType.PreprocessorUndef;

        public string Name { get; }

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
            {
                writer.WriteLine($"{DirectiveChar}{Directive} {Name}");
            }
        }
    }

    /// <summary>
    /// A line representing the #ifdef preprocessor directive.
    /// </summary>
    public class RawPreprocessorIfdef : RawPreprocessor
    {
        public const string Directive = "ifdef";

        public RawPreprocessorIfdef(string name)
        {
            Name = name;
        }

        public override ConfLineType LineType => ConfLineType.PreprocessorIfdef;

        public string Name { get; }

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
            {
                writer.WriteLine($"{DirectiveChar}{Directive} {Name}");
            }
        }
    }

    /// <summary>
    /// A line representing the #ifndef preprocessor directive.
    /// </summary>
    public class RawPreprocessorIfndef : RawPreprocessor
    {
        public const string Directive = "ifndef";

        public RawPreprocessorIfndef(string name)
        {
            Name = name;
        }

        public override ConfLineType LineType => ConfLineType.PreprocessorIfndef;

        public string Name { get; }

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
            {
                writer.WriteLine($"{DirectiveChar}{Directive} {Name}");
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

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
            {
                writer.WriteLine($"{DirectiveChar}{Directive}");
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

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
            {
                writer.WriteLine($"{DirectiveChar}{Directive}");
            }
        }
    }
}
