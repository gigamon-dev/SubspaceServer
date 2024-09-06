using System;
using System.Text.Json;

namespace SS.Utilities.Json
{
    public static class JsonWriterExtensions
    {
        /// <summary>
        /// Tries to write a UTF-8 property name and <see cref="TimeSpan"/> value (as JSON string) as part of a name/value pair of a JSON object.
        /// </summary>
        /// <remarks>
        /// The data type in PostgreSQL is INTERVAL which is represented in ISO 8601 format.
        /// This doesn't exactly match System.TimeSpan, and therefore there isn't a built-in direct conversion.
        /// However, it appears representing a TimeSpan in ISO 8601 is possible.
        /// TimeSpan.MaxValue = 10675199.02:48:05.4775807
        /// Formatted as ISO 8601 is P10675199DT02H48M05.4775807S
        /// It's the other direction (ISO 8601 to TimeSpan) where it's ambiguous. E.g., 1 month can be 28, 30, or 31 days.
        /// </remarks>
        /// <param name="writer">The writer to write to.</param>
        /// <param name="utf8PropertyName">The UTF-8 encoded name of the property to write.</param>
        /// <param name="value">The value to write as ISO 8601.</param>
        /// <returns><see langword="true"/> if the the data was successfully converted and written, otherwise <see langword="false"/>.</returns>
        public static bool TryWriteTimeSpanAsISO8601(this Utf8JsonWriter writer, ReadOnlySpan<byte> utf8PropertyName, TimeSpan value)
        {
            Span<char> buffer = stackalloc char[30];
            if (!value.TryFormat(buffer, out int charsWritten, @"\Pd\D\Th\Hm\Ms\.FFFFFFF\S"))
                return false;

            writer.WriteString(utf8PropertyName, buffer[..charsWritten]);
            return true;
        }

        /// <summary>
        /// Tries to write a UTF-8 property name and <see cref="TimeSpan"/> value (as JSON string) as part of a name/value pair of a JSON object.
        /// </summary>
        /// <remarks>
        /// The data type in PostgreSQL is INTERVAL which is represented in ISO 8601 format.
        /// This doesn't exactly match System.TimeSpan, and therefore there isn't a built-in direct conversion.
        /// However, it appears representing a TimeSpan in ISO 8601 is possible.
        /// TimeSpan.MaxValue = 10675199.02:48:05.4775807
        /// Formatted as ISO 8601 is P10675199DT02H48M05.4775807S
        /// It's the other direction (ISO 8601 to TimeSpan) where it's ambiguous. E.g., 1 month can be 28, 30, or 31 days.
        /// </remarks>
        /// <param name="writer">The writer to write to.</param>
        /// <param name="propertyName">The name of the property to write.</param>
        /// <param name="value">The value to write as ISO 8601.</param>
        /// <returns><see langword="true"/> if the the data was successfully converted and written, otherwise <see langword="false"/>.</returns>
        public static bool TryWriteTimeSpanAsISO8601(this Utf8JsonWriter writer, ReadOnlySpan<char> propertyName, TimeSpan value)
        {
            Span<char> buffer = stackalloc char[30];
            if (!value.TryFormat(buffer, out int charsWritten, @"\Pd\D\Th\Hm\Ms\.FFFFFFF\S"))
                return false;

            writer.WriteString(propertyName, buffer[..charsWritten]);
            return true;
        }
    }
}
