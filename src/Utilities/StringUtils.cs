using System;
using System.Buffers;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SS.Utilities
{
    /// <summary>
    /// Extension methods for strings.
    /// <list type="table">
    /// <item>
    ///     <term>ReadNullTerminated</term>
    ///     <description>Methods that read null terminated C-style strings from encoded byte buffers.</description>
    /// </item>
    /// <item>
    ///     <term>WriteNullTerminated</term>
    ///     <description>Methods that write null terminated C-style strings to encoded byte buffers.</description>
    /// </item>
    /// <item>
    ///     <term>WriteNullTerminated</term>
    ///     <description>Methods that write null terminated C-style strings to encoded byte buffers such that remaining bytes are zeroed out.</description>
    /// </item>
    /// <item>
    ///     <term>TruncateForEncodedByteLimit</term>
    ///     <description>Methods that truncate strings to fit into encoded byte buffers of fixed width.</description>
    /// </item>
    /// <item>
    ///     <term>GetWrappedText</term>
    ///     <description>Methods that assist with splitting text into multiple lines based on a character column limit.</description>
    /// </item>
    /// </list>
    /// </summary>
    public static class StringUtils
    {
        /// <summary>
        /// The default character encoding to use across the application.
        /// </summary>
        /// <remarks>
        /// This is currently set to Windows-1252 since chat messages appear to use a subset of Windows-1252.  See ChatPacket for more on that.
        /// </remarks>
        public static Encoding DefaultEncoding { get; } = Encoding.GetEncoding(1252); // Windows-1252

        #region IsAsciiPrintable

        /// <summary>
        /// Gets whether a character is a printable ASCII character.
        /// </summary>
        /// <param name="c">The character to check.</param>
        /// <returns><see langword="true"/> if the character is a printable ASCII character. Otherwise, <see langword="false"/>.</returns>
        public static bool IsAsciiPrintable(char c)
        {
            return char.IsBetween(c, (char)32, (char)126);
        }

        /// <summary>
        /// Gets whether a string only contains printable ASCII characters.
        /// </summary>
        /// <param name="str">The string to check.</param>
        /// <returns><see langword="true"/> if the string only contains printable ASCII characters. Otherwise, <see langword="false"/>.</returns>
        public static bool IsAsciiPrintable(ReadOnlySpan<char> str)
        {
            for (int i = 0; i < str.Length; i++)
            {
                if (!IsAsciiPrintable(str[i]))
                    return false;
            }

            return true;
        }

        #endregion

        #region IsPrintable

        /// <summary>
        /// Gets whether a character is a printable on a standard game client (VIE or Continuum).
        /// </summary>
        /// <param name="c">The character to check.</param>
        /// <returns><see langword="true"/> if the character is printable. Otherwise, <see langword="false"/>.</returns>
        public static bool IsPrintable(char c)
        {
            // Printable ASCII
            if (IsAsciiPrintable(c))
                return true;

            // ÀÁÂÃÄÅÆÇÈÉÊËÌÍÎÏÐÑÒÓÔÕÖ ØÙÚÛÜÝÞß
            // àáâãäåæçèéêëìíîïðñòóôõö øùúûüýþÿ
            if (char.IsBetween(c, (char)192, (char)255))
            {
                return c != (char)215 // not ×
                    && c != (char)247; // not ÷
            }

            // Characters from ISO-8859-15
            return c switch
            {
                '€' or 'Š' or 'Ž' or 'š' or 'ž' or 'Ÿ' => true,
                _ => false,
            };
        }

        /// <summary>
        /// Gets whether a string only contains printable characters for a standard game client (VIE or Continuum).
        /// </summary>
        /// <param name="str">The string to check.</param>
        /// <returns><see langword="true"/> if the string is printable. Otherwise, <see langword="false"/>.</returns>
        public static bool IsPrintable(ReadOnlySpan<char> str)
        {
            for (int i = 0; i < str.Length; i++)
            {
                if (!IsPrintable(str[i]))
                    return false;
            }

            return true;
        }

        #endregion

        /// <summary>
        /// Replaces control characters in <paramref name="replacement"/> with <paramref name="replacement"/>.
        /// </summary>
        /// <param name="text">The string to replace control characters in.</param>
        /// <param name="replacement">The character to use as a replacement for any control character that is found.</param>
        public static void ReplaceControlCharacters(Span<char> text, char replacement = '_')
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsControl(text[i]))
                {
                    text[i] = replacement;
                }
            }
        }

        #region Read Null Terminated

        /// <summary>
        /// Reads a string that is null terminated or all bytes contain characters, as ASCII.
        /// </summary>
        /// <param name="source">The bytes to read from.</param>
        /// <returns>The string.</returns>
        public static string ReadNullTerminatedASCII(this Span<byte> source)
        {
            return ReadNullTerminatedString((ReadOnlySpan<byte>)source, Encoding.ASCII);
        }

        /// <summary>
        /// Reads a string that is null terminated or all bytes contain characters, as ASCII.
        /// </summary>
        /// <param name="source">The bytes to read from.</param>
        /// <returns>The string.</returns>
        public static string ReadNullTerminatedASCII(this ReadOnlySpan<byte> source)
        {
            return ReadNullTerminatedString(source, Encoding.ASCII);
        }

        /// <summary>
        /// Reads a string that is null terminated or all bytes contain characters, as UTF-8.
        /// </summary>
        /// <param name="source">The bytes to read from.</param>
        /// <returns>The string.</returns>
        public static string ReadNullTerminatedUTF8(this Span<byte> source)
        {
            return ReadNullTerminatedString((ReadOnlySpan<byte>)source, Encoding.UTF8);
        }

        /// <summary>
        /// Reads a string that is null terminated or all bytes contain characters, as UTF-8.
        /// </summary>
        /// <param name="source">The bytes to read from.</param>
        /// <returns>The string.</returns>
        public static string ReadNullTerminatedUTF8(this ReadOnlySpan<byte> source)
        {
            return ReadNullTerminatedString(source, Encoding.UTF8);
        }

        /// <summary>
        /// Reads a string that is null terminated or all bytes contain characters, with the <see cref="DefaultEncoding"/>.
        /// </summary>
        /// <param name="source">The bytes to read from.</param>
        /// <returns>The string.</returns>
        public static string ReadNullTerminatedString(this Span<byte> source)
        {
            return ReadNullTerminatedString(source, DefaultEncoding);
        }

        /// <summary>
        /// Reads a string that is null terminated or all bytes contain characters, with a specified encoding.
        /// </summary>
        /// <param name="source">The bytes to read from.</param>
        /// <param name="encoding">The encoding to use.</param>
        /// <returns>The string.</returns>
        public static string ReadNullTerminatedString(this Span<byte> source, Encoding encoding)
        {
            return ReadNullTerminatedString((ReadOnlySpan<byte>)source, encoding);
        }

        /// <summary>
        /// Reads a string that is null terminated or all bytes contain characters, with the <see cref="DefaultEncoding"/>.
        /// </summary>
        /// <param name="source">The bytes to read from.</param>
        /// <returns>The string.</returns>
        public static string ReadNullTerminatedString(this ReadOnlySpan<byte> source)
        {
            return ReadNullTerminatedString(source, DefaultEncoding);
        }

        /// <summary>
        /// Reads a string that is null terminated or all bytes contain characters, with a specified encoding.
        /// </summary>
        /// <param name="source">The bytes to read from.</param>
        /// <param name="encoding">The encoding to use.</param>
        /// <returns>The string.</returns>
        public static string ReadNullTerminatedString(this ReadOnlySpan<byte> source, Encoding encoding)
        {
            if (encoding == null)
                throw new ArgumentNullException(nameof(encoding));

            int nullTerminationIndex = source.IndexOf((byte)0);

            return nullTerminationIndex == -1
                ? encoding.GetString(source)
                : encoding.GetString(source.Slice(0, nullTerminationIndex));
        }

        #endregion

        #region SliceNullTerminated

        /// <summary>
        /// Forms a slice out of a span representing a C-style string to only include the bytes before the null-terminator.
        /// If there is no null-terminator, then the original span is returned.
        /// </summary>
        /// <param name="source">The bytes to slice.</param>
        /// <returns>A slice of the <paramref name="source"/> span.</returns>
        public static Span<byte> SliceNullTerminated(this Span<byte> source)
        {
            int index = source.IndexOf((byte)0);
            return index == -1 ? source : source.Slice(0, index);
        }

        /// <summary>
        /// Forms a slice out of a span representing a C-style string to only include the bytes before the null-terminator.
        /// If there is no null-terminator, then the original span is returned.
        /// </summary>
        /// <param name="source">The bytes to slice.</param>
        /// <returns>A slice of the <paramref name="source"/> span.</returns>
        public static ReadOnlySpan<byte> SliceNullTerminated(this ReadOnlySpan<byte> source)
        {
            int index = source.IndexOf((byte)0);
            return index == -1 ? source : source.Slice(0, index);
        }

        #endregion

        #region Write Null Terminated

        /// <summary>
        /// Writes a string into a Span&lt;byte&gt; as a null terminated C-style string encoded as ASCII.
        /// </summary>
        /// <remarks>
        /// If you need to set all bytes within <paramref name="destination"/>, see <see cref="WriteNullPaddedASCII(Span{byte}, string, bool)"/> instead.
        /// </remarks>
        /// <param name="destination">The buffer to write the string bytes into.</param>
        /// <param name="value">The string to write.</param>
        /// <returns>The number of bytes written, including the null terminator.</returns>
        public static int WriteNullTerminatedASCII(this Span<byte> destination, ReadOnlySpan<char> value)
        {
            return WriteNullTerminatedString(destination, value, Encoding.ASCII);
        }

        /// <summary>
        /// Writes a string into a Span&lt;byte&gt; as a null terminated C-style string encoded as UTF-8.
        /// </summary>
        /// <remarks>
        /// If you need to set all bytes within <paramref name="destination"/>, see <see cref="WriteNullPaddedUTF8(Span{byte}, string, bool)"/> instead.
        /// </remarks>
        /// <param name="destination">The buffer to write the string bytes into.</param>
        /// <param name="value">The string to write.</param>
        /// <returns>The number of bytes written, including the null terminator.</returns>
        public static int WriteNullTerminatedUTF8(this Span<byte> destination, ReadOnlySpan<char> value)
        {
            return WriteNullTerminatedString(destination, value, Encoding.UTF8);
        }

        /// <summary>
        /// Writes a string into a Span&lt;byte&gt; as a null terminated C-style string with the <see cref="DefaultEncoding"/>.
        /// </summary>
        /// <remarks>
        /// If you need to set all bytes within <paramref name="destination"/>, see <see cref="WriteNullPaddedString(Span{byte}, string, Encoding, bool)"/> instead.
        /// </remarks>
        /// <param name="destination">The buffer to write the string bytes into.</param>
        /// <param name="value">The string to write.</param>
        /// <returns>The number of bytes written, including the null terminator.</returns>
        public static int WriteNullTerminatedString(this Span<byte> destination, ReadOnlySpan<char> value)
        {
            return WriteNullTerminatedString(destination, value, DefaultEncoding);
        }

        /// <summary>
        /// Writes a string into a Span&lt;byte&gt; as a null terminated C-style string with a specified encoding.
        /// </summary>
        /// <remarks>
        /// If you need to set all bytes within <paramref name="destination"/>, see <see cref="WriteNullPaddedString(Span{byte}, string, Encoding, bool)"/> instead.
        /// </remarks>
        /// <param name="destination">The buffer to write the string bytes into.</param>
        /// <param name="value">The string to write.</param>
        /// <param name="encoding">The encoding to use.</param>
        /// <returns>The number of bytes written, including the null terminator.</returns>
        public static int WriteNullTerminatedString(this Span<byte> destination, ReadOnlySpan<char> value, Encoding encoding)
        {
            if (encoding == null)
                throw new ArgumentNullException(nameof(encoding));

            if (value.Length > 0)
            {
                // has characters (not null or empty), but can be white-space characters only

                // make sure it will fit
                if (encoding.GetByteCount(value) + 1 > destination.Length)
                    throw new ArgumentException("Encoded bytes do not fit into the buffer.", nameof(value));

                int bytesWritten = encoding.GetBytes(value, destination);
                destination[bytesWritten] = 0;
                return bytesWritten + 1;
            }
            else
            {
                // null or empty string, consider it to be empty string (no way to represent null)
                if (destination.Length >= 1)
                {
                    // write the null terminator
                    destination[0] = 0;
                    return 1;
                }
                else
                {
                    // can't write anything
                    return 0;
                }
            }
        }

        #endregion

        #region Write Null Padded

        /// <summary>
        /// Writes a string into a Span&lt;byte&gt; as a null terminated C-style string encoded as ASCII.
        /// Any remaining bytes in the <paramref name="destination"/> are zeroed out.
        /// </summary>
        /// <param name="destination"></param>
        /// <param name="value"></param>
        public static void WriteNullPaddedASCII(this Span<byte> destination, ReadOnlySpan<char> value, bool nullTerminatorRequired = true)
        {
            WriteNullPaddedString(destination, value, Encoding.ASCII, nullTerminatorRequired);
        }

        /// <summary>
        /// Writes a string into a Span&lt;byte&gt; as a null terminated C-style string encoded as UTF-8.
        /// Any remaining bytes in the <paramref name="destination"/> are zeroed out.
        /// </summary>
        /// <param name="destination"></param>
        /// <param name="value"></param>
        public static void WriteNullPaddedUTF8(this Span<byte> destination, ReadOnlySpan<char> value, bool nullTerminatorRequired = true)
        {
            WriteNullPaddedString(destination, value, Encoding.UTF8, nullTerminatorRequired);
        }

        /// <summary>
        /// Writes a string into a Span&lt;byte&gt; as a null terminated C-style string, with a the <see cref="DefaultEncoding"/>.
        /// Any remaining bytes in the <paramref name="destination"/> are zeroed out.
        /// </summary>
        /// <param name="destination">The buffer to write string bytes into.</param>
        /// <param name="value">The string to write.</param>
        /// <param name="nullTerminatorRequired">
        /// True if the buffer's last byte must be a null terminator.
        /// False if the buffer can be completely filled, such that there is no null terminating byte at the end.
        /// </param>
        public static void WriteNullPaddedString(this Span<byte> destination, ReadOnlySpan<char> value, bool nullTerminatorRequired = true)
        {
            WriteNullPaddedString(destination, value, DefaultEncoding, nullTerminatorRequired);
        }

        /// <summary>
        /// Writes a string into a Span&lt;byte&gt; as a null terminated C-style string, with a specified encoding.
        /// Any remaining bytes in the <paramref name="destination"/> are zeroed out.
        /// </summary>
        /// <param name="destination">The buffer to write string bytes into.</param>
        /// <param name="value">The string to write.</param>
        /// <param name="encoding">The encoding to use.</param>
        /// <param name="nullTerminatorRequired">
        /// True if the buffer's last byte must be a null terminator.
        /// False if the buffer can be completely filled, such that there is no null terminating byte at the end.
        /// </param>
        public static void WriteNullPaddedString(this Span<byte> destination, ReadOnlySpan<char> value, Encoding encoding, bool nullTerminatorRequired = true)
        {
            if (encoding == null)
                throw new ArgumentNullException(nameof(encoding));

            if (nullTerminatorRequired)
            {
                WriteNullPaddedString(destination[0..^1], value, encoding, false);
                destination[^1] = 0;
                return;
            }

            int bytesWritten = 0;

            if (value.Length > 0)
            {
                // has characters (not null or empty), but can be white-space characters only
                if (encoding.GetByteCount(value) > destination.Length)
                    throw new ArgumentException("Encoded bytes do not fit into the buffer.", nameof(value));

                bytesWritten = encoding.GetBytes(value, destination);
            }

            if (bytesWritten < destination.Length)
                destination[bytesWritten..].Clear(); // pad the rest with zero bytes
        }

        #endregion

        #region Truncate For Encoded Byte Limit

        /// <summary>
        /// Truncates a string so that it can fit within a specified number of encoded bytes.
        /// </summary>
        /// <param name="str">The string to truncate.</param>
        /// <param name="byteLimit">The number of bytes to limit encoded bytes to.</param>
        /// <param name="encoding">The encoding to use. <see langword="null"/> means use the <see cref="DefaultEncoding"/>.</param>
        /// <returns>The truncated string.</returns>
        public static ReadOnlySpan<char> TruncateForEncodedByteLimit(this string str, int byteLimit, Encoding? encoding = null)
        {
            return TruncateForEncodedByteLimit(str.AsSpan(), byteLimit, encoding);
        }

        /// <summary>
        /// Truncates a string so that it can fit within a specified number of encoded bytes.
        /// </summary>
        /// <param name="str">The string to truncate.</param>
        /// <param name="byteLimit">The number of bytes to limit encoded bytes to.</param>
        /// <param name="encoding">The encoding to use. <see langword="null"/> means use the <see cref="DefaultEncoding"/>.</param>
        /// <returns>The truncated string.</returns>
        public static ReadOnlySpan<char> TruncateForEncodedByteLimit(this ReadOnlySpan<char> str, int byteLimit, Encoding? encoding = null)
        {
            if (byteLimit < 0)
                throw new ArgumentOutOfRangeException(nameof(byteLimit), "Value cannot be negative.");

            if (encoding == null)
                encoding = DefaultEncoding;

            if (encoding.IsSingleByte)
                return (str.Length <= byteLimit) ? str : str[..byteLimit];

            // Subspace uses a single-byte encoding, so this is just here for completeness.

            // multi-byte encoding
            if (encoding.GetByteCount(str) <= byteLimit)
                return str; // the simple case, it already fits

            // it doesn't fit, find up to which displayable character (grapheme cluster / text element) will fit
            ReadOnlySpan<char> remaining = str;
            int okLength = 0;
            int byteCount = 0;

            while (byteCount < byteLimit)
            {
                int nextLength = StringInfo.GetNextTextElementLength(remaining);
                if (nextLength == 0)
                    break;

                int nextByteCount = encoding.GetByteCount(remaining[..nextLength]);
                if (byteCount + nextByteCount > byteLimit)
                    break;

                // the next text element fits
                okLength += nextLength;
                byteCount += nextByteCount;
                remaining = remaining[nextLength..];
            }

            return str[..okLength];
        }

        #endregion

        #region Trimming

        /// <summary>
        /// Gets a <see cref="string"/> from a <see cref="StringBuilder"/> trimmed of whitespace.
        /// </summary>
        /// <remarks>The <see cref="StringBuilder"/> itself is not modified.</remarks>
        /// <param name="sb">The <see cref="StringBuilder"/> to read from.</param>
        /// <returns>The trimmed string.</returns>
        public static string ToTrimmedString(this StringBuilder sb)
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
                return string.Empty;
            }

            // Find the first non-whitespace character
            int start;
            for (start = 0; start < end; start++)
            {
                if (!char.IsWhiteSpace(sb[start]))
                    break;
            }

            return sb.ToString(start, end - start + 1);
        }

        public static string TrimWhitespaceAndExtras(this string str, params char[] characters)
        {
            int startIndex = 0;
            while (char.IsWhiteSpace(str[startIndex]) || characters.Contains(str[startIndex]))
            {
                startIndex++;
            }

            int endIndex = str.Length - 1;
            while (char.IsWhiteSpace(str[endIndex]) || characters.Contains(str[endIndex]))
            {
                endIndex--;
            }

            return str.Substring(startIndex, endIndex - startIndex + 1);
        }

        #endregion

        #region Get Wrapped Text

        /// <summary>
        /// Gets an enumerator which can be used to read text as multiple lines.
        /// </summary>
        /// <param name="text">The text to wrap.</param>
        /// <param name="width">The number of characters to allow on a line.</param>
        /// <param name="delimiter">The delimiter to split lines by.</param>
        /// <returns>An enumerator.</returns>
        public static WrapTextEnumerator GetWrappedText(this string text, int width = 80, char delimiter = ' ')
        {
            return new WrapTextEnumerator(text, width, delimiter);
        }

        /// <summary>
        /// Gets an enumerator which can be used to read text as multiple lines.
        /// </summary>
        /// <param name="text">The text to wrap.</param>
        /// <param name="width">The number of characters to allow on a line.</param>
        /// <param name="delimiter">The delimiter to split lines by.</param>
        /// <returns>An enumerator.</returns>
        public static WrapTextEnumerator GetWrappedText(this ReadOnlySpan<char> text, int width = 80, char delimiter = ' ')
        {
            return new WrapTextEnumerator(text, width, delimiter);
        }

        /// <summary>
        /// Gets an enumerator which can be used to read text as multiple lines.
        /// </summary>
        /// <param name="text">The text to wrap.</param>
        /// <param name="width">The number of characters to allow on a line.</param>
        /// <param name="delimiter">The delimiter to split lines by.</param>
        /// <returns>An enumerator.</returns>
        public static WrapTextStringBuilderEnumerator GetWrappedText(this StringBuilder text, int width = 80, char delimiter = ' ')
        {
            return new WrapTextStringBuilderEnumerator(text, width, delimiter);
        }

        #endregion

        #region GetToken

        /// <summary>
        /// Splits off a token from the beginning of a string stopping at a specified <paramref name="delimiter"/>.
        /// Any leading occurances of the <paramref name="delimiter"/> are skipped and not included in the return value.
        /// </summary>
        /// <param name="str">The string to get a token from.</param>
        /// <param name="delimiter">The delimiter to stop at.</param>
        /// <param name="remaining">The remaining part of the string, including the delimiter if one was found.</param>
        /// <returns>The token.</returns>
        public static ReadOnlySpan<char> GetToken(this ReadOnlySpan<char> str, char delimiter, out ReadOnlySpan<char> remaining)
        {
            str = str.TrimStart(delimiter);

            int index = str.IndexOf(delimiter);
            if (index == -1)
            {
                remaining = ReadOnlySpan<char>.Empty;
                return str;
            }
            else
            {
                remaining = str[index..];
                return str.Slice(0, index);
            }
        }

        /// <summary>
        /// Splits off a token from the beginning of a string stopping at a specified <paramref name="delimiter"/>.
        /// Any leading occurances of the <paramref name="delimiter"/> are skipped and not included in the return value.
        /// </summary>
        /// <param name="str">The string to get a token from.</param>
        /// <param name="delimiter">The delimiter to stop at.</param>
        /// <param name="remaining">The remaining part of the string, including the delimiter if one was found.</param>
        /// <returns>The token.</returns>
        public static Span<char> GetToken(this Span<char> str, char delimiter, out Span<char> remaining)
        {
            str = str.TrimStart(delimiter);

            int index = str.IndexOf(delimiter);
            if (index == -1)
            {
                remaining = Span<char>.Empty;
                return str;
            }
            else
            {
                remaining = str[index..];
                return str.Slice(0, index);
            }
        }

        /// <summary>
        /// Splits off a token from the beginning of a string stopping at any of the specified <paramref name="delimiters"/>.
        /// Any leading occurances of the <paramref name="delimiters"/> are skipped and not included in the return value.
        /// </summary>
        /// <param name="str">The string to get a token from.</param>
        /// <param name="delimiters">The set of delimiters to stop at.</param>
        /// <param name="remaining">The remaining part of the string, including the delimiter if one was found.</param>
        /// <returns>The token.</returns>
        public static ReadOnlySpan<char> GetToken(this ReadOnlySpan<char> str, ReadOnlySpan<char> delimiters, out ReadOnlySpan<char> remaining)
        {
            str = str.TrimStart(delimiters);

            int index = str.IndexOfAny(delimiters);
            if (index == -1)
            {
                remaining = ReadOnlySpan<char>.Empty;
                return str;
            }
            else
            {
                remaining = str[index..];
                return str.Slice(0, index);
            }
        }

        /// <summary>
        /// Splits off a token from the beginning of a string stopping at any of the specified <paramref name="delimiters"/>.
        /// Any leading occurances of the <paramref name="delimiters"/> are skipped and not included in the return value.
        /// </summary>
        /// <param name="str">The string to get a token from.</param>
        /// <param name="delimiters">The set of delimiters to stop at.</param>
        /// <param name="remaining">The remaining part of the string, including the delimiter if one was found.</param>
        /// <returns>The token.</returns>
        public static Span<char> GetToken(this Span<char> str, ReadOnlySpan<char> delimiters, out Span<char> remaining)
        {
            str = str.TrimStart(delimiters);

            int index = str.IndexOfAny(delimiters);
            if (index == -1)
            {
                remaining = Span<char>.Empty;
                return str;
            }
            else
            {
                remaining = str[index..];
                return str.Slice(0, index);
            }
        }

        #endregion

        #region StringBuilder extensions

        /// <summary>
        /// Appends a <see cref="TimeSpan"/> to a <see cref="StringBuilder"/> in a culture insensitive, user friendly format that adjusts based on how large the <paramref name="value"/> is.
        /// </summary>
        /// <param name="builder">The <see cref="StringBuilder"/> to append to.</param>
        /// <param name="value">The value to append.</param>
        /// <returns>The <paramref name="builder"/> that was passed in.</returns>
        public static StringBuilder AppendFriendlyTimeSpan(this StringBuilder builder, TimeSpan value)
        {
            if (value.Days > 0)
            {
                builder.Append($"{value:d'.'hh':'mm':'ss}");
            }
            else if (value.Hours > 0)
            {
                builder.Append($"{value:h':'mm':'ss}");
            }
            else
            {
                builder.Append($"{value:m':'ss}");
            }

            return builder;
        }

        #endregion
    }

    /// <summary>
    /// Provides the ability to enumerate on a string to get wrapped lines.
    /// </summary>
    public ref struct WrapTextEnumerator
    {
        private ReadOnlySpan<char> _text;
        private readonly int _width;
        private readonly char _delimiter;

        public WrapTextEnumerator(ReadOnlySpan<char> text, int width, char delimiter)
        {
            _text = text;
            _width = width;
            _delimiter = delimiter;
            Current = default;
        }

        public WrapTextEnumerator GetEnumerator() => this;

        public ReadOnlySpan<char> Current { get; private set; }

        public bool MoveNext()
        {
            if (_text.Length == 0)
                return false;

            int index = (_text.Length >= _width ? _width : _text.Length) - 1;

            if (_text[index] != _delimiter
                && _text.Length > (index + 1)
                && _text[index + 1] != _delimiter)
            {
                // mid-word, look for an earlier occurence of the delimiter
                int delimiterIndex;
                for (delimiterIndex = index - 1; delimiterIndex >= 0; delimiterIndex--)
                {
                    if (_text[delimiterIndex] == _delimiter)
                        break;
                }

                if (delimiterIndex != -1)
                {
                    index = delimiterIndex;
                }
            }

            Current = _text.Slice(0, index + 1);

            _text = _text[(index + 1)..];
            if (_text.Length > 0 && _text[0] == _delimiter)
                _text = _text.TrimStart(_delimiter);

            return true;
        }
    }

    /// <summary>
    /// Provides the ability to enumerate on a <see cref="StringBuilder"/> to get wrapped lines.
    /// </summary>
    public ref struct WrapTextStringBuilderEnumerator
    {
        private readonly StringBuilder _text;
        private readonly int _width;
        private readonly char _delimiter;
        private int _startIndex = 0; // the index to begin reading the next substring from
        private char[]? _buffer = null; // buffer for holding the current substring (rented from a pool)

        public WrapTextStringBuilderEnumerator(StringBuilder text, int width, char delimiter)
        {
            _text = text ?? throw new ArgumentNullException(nameof(text));
            _width = width;
            _delimiter = delimiter;
            Current = ReadOnlySpan<char>.Empty;
        }

        public WrapTextStringBuilderEnumerator GetEnumerator() => this;

        public ReadOnlySpan<char> Current { get; private set; }

        public bool MoveNext()
        {
            if (_startIndex >= _text.Length)
            {
                Current = ReadOnlySpan<char>.Empty;
                return false;
            }

            int endIndex = _startIndex + _width - 1;
            if (endIndex >= _text.Length)
                endIndex = _text.Length - 1;

            if (_text[endIndex] != _delimiter
                && _text.Length > (endIndex + 1)
                && _text[endIndex + 1] != _delimiter)
            {
                // mid-word, look for an earlier occurance of the delimiter
                int delimiterIndex;
                for (delimiterIndex = endIndex - 1; delimiterIndex >= _startIndex; delimiterIndex--)
                {
                    if (_text[delimiterIndex] == _delimiter)
                        break;
                }

                if (delimiterIndex >= _startIndex)
                {
                    endIndex = delimiterIndex;
                }
            }

            if (_buffer == null)
                _buffer = ArrayPool<char>.Shared.Rent(_width);

            int length = endIndex - _startIndex + 1;
            _text.CopyTo(_startIndex, _buffer, 0, length);
            Current = _buffer.AsSpan(0, length);

            _startIndex += length;
            while (_startIndex < _text.Length && _text[_startIndex] == _delimiter)
                _startIndex++; // move past delimiters

            return true;
        }

        public void Dispose()
        {
            if (_buffer != null)
            {
                ArrayPool<char>.Shared.Return(_buffer);
                _buffer = null;
            }
        }
    }
}
