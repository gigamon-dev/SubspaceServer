using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SS.Utilities
{
    public static class StringUtils
    {
        public static Encoding DefaultEncoding { get; } = Encoding.GetEncoding(1252); // Windows-1252

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
                destination.Slice(bytesWritten).Clear(); // pad the rest with zero bytes
        }

        #endregion

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

        public static IEnumerable<string> WrapText(this string text, int width)
        {
            if (text == null)
                yield break;

            StringBuilder sb = new StringBuilder(text);
            int startIndex = 0;

            while (startIndex < sb.Length)
            {
                if (width > sb.Length - startIndex)
                    width = sb.Length - startIndex;

                yield return sb.ToString(startIndex, width);

                startIndex += width;
            }
        }
    }
}
