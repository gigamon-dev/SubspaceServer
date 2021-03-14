using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SS.Core.Configuration
{
    public enum ConfLineType
    {
        Empty,
        Comment,
        Section,
        Property,
        PreprocessorInclude,
        PreprocessorDefine,
        PreprocessorUndef,
        PreprocessorIfdef,
        PreprocessorIfndef,
        PreprocessorElse,
        PreprocessorEndif,
        ParseError,
    }

    public abstract class RawLine
    {
        public readonly static RawEmptyLine Empty = new RawEmptyLine();

        public string Raw { get; init; }

        public abstract ConfLineType LineType { get; }

        public abstract void WriteTo(TextWriter writer);

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

    public class RawParseError : RawLine
    {
        public RawParseError(string raw) => Raw = raw ?? throw new ArgumentNullException(nameof(raw));

        public override ConfLineType LineType => ConfLineType.ParseError;

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            TryWriteRaw(writer); // guaranteed to write since Raw cannot be null
        }
    }

    public class RawEmptyLine : RawLine
    {
        public override ConfLineType LineType => ConfLineType.Empty;

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if(!TryWriteRaw(writer))
                writer.WriteLine();
        }
    }

    public class RawComment : RawLine
    {
        public override ConfLineType LineType => ConfLineType.Comment;

        /// <summary>
        /// # or /
        /// </summary>
        public char CommentChar { get; init; }
        public string Text { get; init; }

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
                writer.WriteLine("{0} {1}", CommentChar, Text);
        }
    }

    public class RawSection : RawLine
    {
        public override ConfLineType LineType => ConfLineType.Section;

        public string Name { get; init; }

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
                writer.WriteLine("[{0}]", Name);
        }
    }

    public class RawProperty : RawLine
    {
        public override ConfLineType LineType => ConfLineType.Property;

        public string SectionOverride { get; init; } // for <section>:<key>
        public string Key { get; init; }
        public string Value { get; set; }
        public bool HasDelimiter { get; init; }

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (TryWriteRaw(writer))
                return;

            if (SectionOverride != null) // note: allowing empty string for no section
            {
                writer.Write(SectionOverride);
                writer.Write(':');
            }

            writer.Write(Key);

            if (HasDelimiter || !string.IsNullOrWhiteSpace(Value))
            {
                writer.Write(" = ");
                writer.WriteLine(Value);
            }
        }
    }

    public abstract class RawPreprocessor : RawLine
    {
        public static readonly RawPreprocessorElse Else = new RawPreprocessorElse();
        public static readonly RawPreprocessorEndif Endif = new RawPreprocessorEndif();
        
        //private const char DirectiveChar = '#';

        //public abstract string Command { get; }
    }

    public class RawPreprocessorInclude : RawPreprocessor
    {
        public override ConfLineType LineType => ConfLineType.PreprocessorInclude;

        //public override string Command => "include";

        public string FilePath { get; init; }

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
            {
                if (FilePath.Any(c => char.IsWhiteSpace(c)))
                    writer.WriteLine("#include \"{0}\"", FilePath);
                else
                    writer.WriteLine("#include {0}", FilePath);
            }
        }
    }

    public class RawPreprocessorDefine : RawPreprocessor
    {
        public override ConfLineType LineType => ConfLineType.PreprocessorDefine;

        public string Name { get; init; }
        public string Value { get; init; }

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
            {
                if(string.IsNullOrWhiteSpace(Value))
                    writer.WriteLine("#define {0}", Name);
                else
                    writer.WriteLine("#define {0} {1}", Name, Value);
            }
        }
    }

    public class RawPreprocessorUndef : RawPreprocessor
    {
        public override ConfLineType LineType => ConfLineType.PreprocessorUndef;

        public string Name { get; init; }

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
            {
                writer.WriteLine("#undef {0}", Name);
            }
        }
    }

    public class RawPreprocessorIfdef : RawPreprocessor
    {
        public override ConfLineType LineType => ConfLineType.PreprocessorIfdef;

        public string Name { get; init; }

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
            {
                writer.WriteLine("#ifdef {0}", Name);
            }
        }
    }

    public class RawPreprocessorIfndef : RawPreprocessor
    {
        public override ConfLineType LineType => ConfLineType.PreprocessorIfndef;

        public string Name { get; init; }

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
            {
                writer.WriteLine("#ifndef {0}", Name);
            }
        }
    }

    public class RawPreprocessorElse : RawPreprocessor
    {
        public override ConfLineType LineType => ConfLineType.PreprocessorElse;

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
            {
                writer.WriteLine("#else");
            }
        }
    }

    public class RawPreprocessorEndif : RawPreprocessor
    {
        public override ConfLineType LineType => ConfLineType.PreprocessorEndif;

        public override void WriteTo(TextWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));

            if (!TryWriteRaw(writer))
            {
                writer.WriteLine("#endif");
            }
        }
    }
}
