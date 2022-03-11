using Ionic.Zlib;
using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace SS.Core.Map.Lvz
{
    /// <summary>
    /// Helper class for reading LVZ files.
    /// </summary>
    public static class LvzReader
    {
        /// <summary>
        /// Delegate for handling when <see cref="ObjectData"/> is read.
        /// </summary>
        /// <param name="objectDataSpan">The data read.</param>
        public delegate void ObjectDataReadDelegate(ReadOnlySpan<ObjectData> objectDataSpan);

        /// <summary>
        /// Reads the object data from a LVZ file.
        /// </summary>
        /// <param name="path">The path of the LVZ file.</param>
        /// <param name="objectDataReadCallback">A callback that will be called when the object data is read.</param>
        /// <exception cref="ArgumentException"><paramref name="path"/> was null or white-space.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="objectDataReadCallback"/> was null.</exception>
        /// <exception cref="Exception">There was an error reading the lvz file.</exception>
        public static void ReadObjects(
            string path,
            ObjectDataReadDelegate objectDataReadCallback)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Cannot be null or white-space.", nameof(path));

            if (objectDataReadCallback == null)
                throw new ArgumentNullException(nameof(objectDataReadCallback));

            long length = new FileInfo(path).Length;
            if (length <= FileHeader.Length)
                return;

            using MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using MemoryMappedViewAccessor va = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            long position = 0;

            va.Read(position, out FileHeader fileHeader);
            position += FileHeader.Length;
            length -= FileHeader.Length;

            if (fileHeader.Magic != FileHeader.LvzMagic)
                throw new Exception("File is not an lvz. File header did not begin with 'CONT'.");

            int numSections = fileHeader.CompressedSectionCount;
            while (numSections-- > 0)
            {
                va.Read(position, out CompressionHeader compressionHeader);
                position += CompressionHeader.Length;
                length -= CompressionHeader.Length;

                if (compressionHeader.Magic != CompressionHeader.LvzMagic)
                    throw new Exception("Malformed lvz file. Compression header did not begin with 'CONT'.");

                // The next part of the header is the filename. However, it's variable in length and null terminated; so wasn't part of the CompressionHeader struct.
                // Get the filename length (we don't need the actual filename, just the length).
                long fileNameStart = position;
                long fileNameLength;
                for (fileNameLength = 0; fileNameLength < length; fileNameLength++)
                {
                    if (va.ReadByte(fileNameStart + fileNameLength) == 0)
                        break;
                }

                position += fileNameLength + 1; // + 1 for the null-terminator byte
                length -= fileNameLength + 1;

                // Now we're in position to read the compressed data.

                if (compressionHeader.CompressedSize > length)
                    throw new Exception($"Malformed lvz file. Compression header specifies a compressed size of {compressionHeader.CompressedSize}, but there are only {length} bytes left.");

                // We only want to read the sections that are for objects.
                // This means we're looking for an file time of 0 and an empty file name.
                if (compressionHeader.FileTime == 0
                    && fileNameLength == 0)
                {
                    // It's an object section, so let's decompress it.
                    if (compressionHeader.DecompressSize > int.MaxValue)
                        throw new Exception($"The decompress size was too large to handle ({compressionHeader.DecompressSize} bytes).");

                    int decompressSize = (int)compressionHeader.DecompressSize;
                    if (decompressSize < ObjectSectionHeader.Length)
                        throw new Exception($"Malformed lvz file. Decompressed data is too small to be an object section ({decompressSize} bytes).");

                    byte[] decompressedBuffer = ArrayPool<byte>.Shared.Rent(decompressSize);

                    try
                    {
                        using MemoryStream ms = new(decompressedBuffer, 0, decompressSize);
                        {
                            using MemoryMappedViewStream vs = mmf.CreateViewStream(position, compressionHeader.CompressedSize, MemoryMappedFileAccess.Read);
                            {
                                using ZlibStream zlibStream = new(vs, CompressionMode.Decompress, CompressionLevel.Default);
                                zlibStream.CopyTo(ms);
                            }

                            if (ms.Position != compressionHeader.DecompressSize)
                                throw new Exception($"Malformed lvz file. Compression header specified a decompressed size of {compressionHeader.CompressedSize}, but only got {ms.Position} after decompression.");
                        }

                        Span<byte> remainingBytes = decompressedBuffer.AsSpan(0, decompressSize);

                        ref ObjectSectionHeader objectSectionHeader = ref MemoryMarshal.AsRef<ObjectSectionHeader>(remainingBytes);
                        if (objectSectionHeader.Type == ObjectSectionHeader.CLV1
                            || objectSectionHeader.Type == ObjectSectionHeader.CLV2)
                        {
                            remainingBytes = remainingBytes[ObjectSectionHeader.Length..];

                            uint objectCount = objectSectionHeader.ObjectCount;
                            long objectDataLength = objectCount * ObjectData.Length;
                            if (remainingBytes.Length < objectDataLength)
                                throw new Exception($"Malformed lvz file. Object section specified {objectCount} objects which is {objectDataLength} bytes, but there were only {remainingBytes.Length} bytes.");

                            Span<ObjectData> objectDataSpan = MemoryMarshal.Cast<byte, ObjectData>(remainingBytes[..(int)objectDataLength]);
                            objectDataReadCallback(objectDataSpan);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(decompressedBuffer);
                    }
                }

                position += compressionHeader.CompressedSize;
                length -= compressionHeader.CompressedSize;
            }
        }
    }
}
