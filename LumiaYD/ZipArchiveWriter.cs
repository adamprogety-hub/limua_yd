using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LumiaYD
{
    /// <summary>
    /// Минимальный потоковый ZIP-writer без сторонних библиотек.
    /// Файлы сохраняются без сжатия (store), имена — в UTF-8.
    /// </summary>
    internal sealed class ZipArchiveWriter : IDisposable
    {
        private sealed class Entry
        {
            public byte[] NameBytes;
            public ushort Time;
            public ushort Date;
            public ushort Flags;
            public long Offset;
            public long Size;
            public uint Crc;
        }

        private readonly Stream _output;
        private readonly List<Entry> _entries = new List<Entry>();
        private long _offset;
        private Entry _current;
        private bool _finished;

        public ZipArchiveWriter(Stream output)
        {
            if (output == null)
            {
                throw new ArgumentNullException("output");
            }

            _output = output;
        }

        public Stream BeginEntry(string entryName)
        {
            if (_finished)
            {
                throw new InvalidOperationException("Архив уже завершён.");
            }

            if (_current != null)
            {
                throw new InvalidOperationException("Предыдущий файл ещё не закрыт.");
            }

            string name = NormalizeName(entryName);
            if (name.Length == 0)
            {
                throw new ArgumentException("Пустое имя файла в архиве.", "entryName");
            }

            ushort time;
            ushort date;
            GetDosTime(DateTime.Now, out time, out date);

            var entry = new Entry
            {
                NameBytes = Encoding.UTF8.GetBytes(name),
                Time = time,
                Date = date,
                Flags = 0x0808, // UTF-8 + data descriptor
                Offset = _offset
            };

            if (entry.NameBytes.Length > 65535)
            {
                throw new ArgumentException("Слишком длинное имя файла в архиве.", "entryName");
            }

            WriteLocalHeader(entry, 0, 0);
            _current = entry;
            return new EntryStream(this, entry);
        }

        public void AddDirectory(string entryName)
        {
            if (_finished)
            {
                throw new InvalidOperationException("Архив уже завершён.");
            }

            if (_current != null)
            {
                throw new InvalidOperationException("Предыдущий файл ещё не закрыт.");
            }

            string name = NormalizeName(entryName);
            if (name.Length == 0)
            {
                return;
            }

            if (!name.EndsWith("/", StringComparison.Ordinal))
            {
                name += "/";
            }

            ushort time;
            ushort date;
            GetDosTime(DateTime.Now, out time, out date);

            var entry = new Entry
            {
                NameBytes = Encoding.UTF8.GetBytes(name),
                Time = time,
                Date = date,
                Flags = 0x0800, // UTF-8, без data descriptor
                Offset = _offset
            };

            if (entry.NameBytes.Length > 65535)
            {
                throw new ArgumentException("Слишком длинное имя папки в архиве.", "entryName");
            }

            WriteLocalHeader(entry, 0, 0);
            _entries.Add(entry);
        }

        private void WriteData(byte[] buffer, int offset, int count)
        {
            _output.Write(buffer, offset, count);
            _offset += count;
        }

        private void EndEntry(Entry entry, uint crc, long size)
        {
            if (_current != entry)
            {
                throw new InvalidOperationException("Файл архива закрыт в неверном порядке.");
            }

            if (size > uint.MaxValue)
            {
                throw new NotSupportedException("Файл больше 4 ГБ нельзя упаковать в этот архив.");
            }

            // Data descriptor: CRC и размеры идут после данных.
            WriteUInt32(0x08074b50);
            WriteUInt32(crc);
            WriteUInt32((uint)size);
            WriteUInt32((uint)size);

            entry.Crc = crc;
            entry.Size = size;
            _entries.Add(entry);
            _current = null;
        }

        public void Finish()
        {
            if (_finished)
            {
                return;
            }

            if (_current != null)
            {
                throw new InvalidOperationException("Нельзя завершить архив, пока открыт файл.");
            }

            if (_entries.Count > 65535)
            {
                throw new NotSupportedException("В папке слишком много файлов для ZIP-архива.");
            }

            if (_offset > uint.MaxValue)
            {
                throw new NotSupportedException("Архив больше 4 ГБ нельзя сохранить в этом формате.");
            }

            long centralStart = _offset;

            foreach (Entry entry in _entries)
            {
                WriteUInt32(0x02014b50);
                WriteUInt16(20); // version made by
                WriteUInt16(20); // version needed
                WriteUInt16(entry.Flags);
                WriteUInt16(0); // store
                WriteUInt16(entry.Time);
                WriteUInt16(entry.Date);
                WriteUInt32(entry.Crc);
                WriteUInt32((uint)entry.Size);
                WriteUInt32((uint)entry.Size);
                WriteUInt16((ushort)entry.NameBytes.Length);
                WriteUInt16(0); // extra
                WriteUInt16(0); // comment
                WriteUInt16(0); // disk
                WriteUInt16(0); // internal attributes
                WriteUInt32(0); // external attributes
                WriteUInt32((uint)entry.Offset);
                WriteBytes(entry.NameBytes);
            }

            long centralSize = _offset - centralStart;

            WriteUInt32(0x06054b50);
            WriteUInt16(0);
            WriteUInt16(0);
            WriteUInt16((ushort)_entries.Count);
            WriteUInt16((ushort)_entries.Count);
            WriteUInt32((uint)centralSize);
            WriteUInt32((uint)centralStart);
            WriteUInt16(0);

            _finished = true;
        }

        public void Dispose()
        {
            if (!_finished)
            {
                Finish();
            }
        }

        private void WriteLocalHeader(Entry entry, uint crc, uint size)
        {
            WriteUInt32(0x04034b50);
            WriteUInt16(20);
            WriteUInt16(entry.Flags);
            WriteUInt16(0); // store
            WriteUInt16(entry.Time);
            WriteUInt16(entry.Date);
            WriteUInt32(crc);
            WriteUInt32(size);
            WriteUInt32(size);
            WriteUInt16((ushort)entry.NameBytes.Length);
            WriteUInt16(0);
            WriteBytes(entry.NameBytes);
        }

        private void WriteUInt16(ushort value)
        {
            _output.WriteByte((byte)(value & 0xFF));
            _output.WriteByte((byte)((value >> 8) & 0xFF));
            _offset += 2;
        }

        private void WriteUInt32(uint value)
        {
            _output.WriteByte((byte)(value & 0xFF));
            _output.WriteByte((byte)((value >> 8) & 0xFF));
            _output.WriteByte((byte)((value >> 16) & 0xFF));
            _output.WriteByte((byte)((value >> 24) & 0xFF));
            _offset += 4;
        }

        private void WriteBytes(byte[] bytes)
        {
            _output.Write(bytes, 0, bytes.Length);
            _offset += bytes.Length;
        }

        private static string NormalizeName(string entryName)
        {
            if (string.IsNullOrEmpty(entryName))
            {
                return "";
            }

            string value = entryName.Replace('\\', '/').Trim();
            string[] parts = value.Split('/');
            var result = new StringBuilder();

            foreach (string rawPart in parts)
            {
                string part = rawPart.Trim();
                if (part.Length == 0 || part == ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    part = "_";
                }

                part = part.Replace('<', '_')
                           .Replace('>', '_')
                           .Replace(':', '_')
                           .Replace('"', '_')
                           .Replace('|', '_')
                           .Replace('?', '_')
                           .Replace('*', '_');

                part = part.TrimEnd('.', ' ');
                if (part.Length == 0)
                {
                    continue;
                }

                if (result.Length > 0)
                {
                    result.Append('/');
                }

                result.Append(part);
            }

            return result.ToString();
        }

        private static void GetDosTime(DateTime value, out ushort time, out ushort date)
        {
            if (value.Year < 1980)
            {
                value = new DateTime(1980, 1, 1);
            }
            else if (value.Year > 2107)
            {
                value = new DateTime(2107, 12, 31, 23, 59, 58);
            }

            date = (ushort)(((value.Year - 1980) << 9) | (value.Month << 5) | value.Day);
            time = (ushort)((value.Hour << 11) | (value.Minute << 5) | (value.Second / 2));
        }

        private sealed class EntryStream : Stream
        {
            private readonly ZipArchiveWriter _owner;
            private readonly Entry _entry;
            private uint _crc = 0xFFFFFFFF;
            private long _size;
            private bool _closed;

            public EntryStream(ZipArchiveWriter owner, Entry entry)
            {
                _owner = owner;
                _entry = entry;
            }

            public override bool CanRead
            {
                get { return false; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return !_closed; }
            }

            public override long Length
            {
                get { return _size; }
            }

            public override long Position
            {
                get { return _size; }
                set { throw new NotSupportedException(); }
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (_closed)
                {
                    throw new ObjectDisposedException("EntryStream");
                }

                if (count <= 0)
                {
                    return;
                }

                _owner.WriteData(buffer, offset, count);
                _crc = Crc32.Update(_crc, buffer, offset, count);
                _size += count;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            protected override void Dispose(bool disposing)
            {
                if (!_closed)
                {
                    _closed = true;

                    if (disposing)
                    {
                        _owner.EndEntry(_entry, _crc ^ 0xFFFFFFFF, _size);
                    }
                }

                base.Dispose(disposing);
            }
        }

        private static class Crc32
        {
            private static readonly uint[] Table = CreateTable();

            public static uint Update(uint crc, byte[] buffer, int offset, int count)
            {
                for (int i = offset; i < offset + count; i++)
                {
                    crc = (crc >> 8) ^ Table[(crc ^ buffer[i]) & 0xFF];
                }

                return crc;
            }

            private static uint[] CreateTable()
            {
                var table = new uint[256];

                for (int i = 0; i < 256; i++)
                {
                    uint value = (uint)i;

                    for (int bit = 0; bit < 8; bit++)
                    {
                        value = (value & 1) != 0
                            ? 0xEDB88320 ^ (value >> 1)
                            : value >> 1;
                    }

                    table[i] = value;
                }

                return table;
            }
        }
    }
}
