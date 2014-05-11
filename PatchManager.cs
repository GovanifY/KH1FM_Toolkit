using System;
using System.Collections.Generic;
using System.IO;

/* KH1 Patch File Format
 * 0    UInt32  Magic 0x5031484B "KH1P"
 * 4    UInt32  Patch version
 * 8    UInt32  # of files
 * 12   byte    Author string length                (Max length is 127)
 * 13   string  Author                              (Note that this and the length are handled by .NET in the Binary-Reader\Writer; See http://dpatrickcaldwell.blogspot.se/2011/09/7-bit-encoding-with-binarywriter-in-net.html)
 *      for each file:
 *          UInt32  hashed filename
 *          UInt32  Flags
 *          UInt32  Absolute offset in patch file   (If 0, relink)
 *          UInt32  Size                            (If relinking, target hash)
 *      for each non-relinking file:
 *          byte*?  Raw file data
 * 
 * Relinking files is possible by setting Offset to 0 and Size to the
 *   target hash. In this case, Flags is ignored (target flags are used).
 * 
 * Notes:
 *   If a file should be compressed, it needs to be done so in the patch file.
 *     Files from a patch file will not be compressed when added to the ISO.
 *   29 bytes is the smallest patch file possible: Author string is 0 bytes, and 1 file is relinked.
 */

namespace KH1FM_Toolkit
{

    internal sealed class Substream : Stream
    {
        /// <summary>Source stream</summary>
        private readonly Stream stream;
        /// <summary>Position in source stream to start at</summary>
        private readonly long start;
        /// <summary>Position in source stream to end at</summary>
        private readonly long end;
        /// <summary>Length of this substream</summary>
        private long pos;
        public Substream(Stream stream, UInt32 origin, UInt32 length)
        {
            this.stream = stream;
            this.pos = this.start = origin;
            this.end = origin + length;
        }
        //protected override void Dispose(bool disposing) { stream.Dispose(); }
        public override bool CanWrite { get { return false; } }
        public override bool CanSeek { get { return true; } }   //Length goes by CanSeek
        public override bool CanRead { get { return true; } }
        public override long Length { get { return end - start; } }
        public override long Position { get { return pos - start; } set { throw new NotImplementedException(); } }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotImplementedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotImplementedException(); }
        public override void SetLength(long value) { throw new NotImplementedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (stream.Position != pos) { stream.Position = pos; }
            if (pos + count > end) { count += (int)(end - (pos + count)); }
            int bytesRead = stream.Read(buffer, offset, count);
            pos += bytesRead;
            return bytesRead;
        }
        public override int ReadByte() { if (pos + 1 > end) { return -1; } else { return stream.ReadByte(); } }
    }
    public class Tuple<T1, T2>
    {
        public T1 First { get; private set; }
        public T2 Second { get; private set; }
        public Tuple(T1 first, T2 second)
        {
            First = first;
            Second = second;
        }
    }
    public class PatchManager : IDisposable
    {
        public static void NGYXor(byte[] buffer)
        {
            byte[] v84 = { 0xa4, 0x1c, 0x6b, 0x81, 0x30, 0xd, 0x23, 0x5b };
            int i = -1, l = buffer.Length;
            while (l > 0)
            {
                buffer[++i] ^= v84[(--l & 7)];
            }
        }

        private List<BinaryReader> patches = new List<BinaryReader>();
        private Dictionary<uint, Tuple<int, long>> patchedFiles = new Dictionary<uint, Tuple<int, long>>();
        private string rawSearch;
        public PatchManager(string[] files)
        {
            {
                patches.Capacity = files.Length;
                foreach (string s in files)
                {
                    BinaryReader file = null;
                    try
                    {
                        if (s.EndsWith(".kh1patch", StringComparison.InvariantCultureIgnoreCase))
                        {
                            file = new BinaryReader(File.Open(s, FileMode.Open, FileAccess.Read, FileShare.Read));
                            string author;
                            UInt32 version;
                            if (file.BaseStream.Length < 29 || file.ReadUInt32() != 0x5031484B ||
                                parsePatch(file, patches.Count, out author, out version) == 0)
                            {
                                throw new Exception("Invalid or empty patch file");
                            }
                            patches.Add(file);
                            string patchname = Path.GetFileName(s);
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine("Loading patch {0} version {1} by {2}\n", patchname, version, author);
                            Console.ResetColor();
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Failed to open patch file {0}: {1}\n", s, e.Message);
                        if (file != null) { file.Close(); }
                    }
                }
            }
        }
        ~PatchManager(){Dispose(false);}
        public void Dispose(){Dispose(true);}
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                patchedFiles.Clear();
                foreach (var f in patches) { f.Close(); }
                patches.Clear();
            }
        }
        private UInt32 parsePatch(BinaryReader br, int listPos, out string author, out UInt32 version)
        {
            version = br.ReadUInt32();
            UInt32 fileC = br.ReadUInt32();
            author = br.ReadString();
            for (int i = 0; i < fileC; ++i)
            {
                UInt32 hash = br.ReadUInt32();
                // Later definitions override older
                if (patchedFiles.ContainsKey(hash))
                {
                    patchedFiles.Remove(hash);
                }
                patchedFiles.Add(hash, new Tuple<int, long>(listPos, br.BaseStream.Position - 4));
                br.BaseStream.Position += 12;
            }
            return fileC;
        }
        /// <summary>Locates a file in patches or loose folder</summary>
        /// <param name="hash">Hash to locate</param>
        /// <param name="flags"><para>Output flags: 0 if not compressed, 1 if compressed</para><para>If return is null and <c>flags</c> > 0, flags is a hash to relink to</para></param>
        /// <returns><para>A stream containing only the target file.</para><para><c>null</c> if no file found, or if relinking</para></returns>
        public Stream findFile(UInt32 hash, out UInt32 flags)
        {
            flags = 0;
            Tuple<int, long> pData;
            if (patchedFiles.TryGetValue(hash, out pData))
            {
                BinaryReader br = patches[pData.First];
                br.BaseStream.Position = pData.Second;
                if (br.ReadUInt32() != hash) { throw new Exception("Bad offset stored"); }
                hash = br.ReadUInt32();   //Flags
                if ((hash & 0x1) == 1) { flags = 1; }
                hash = br.ReadUInt32();   //Abs Offset
                UInt32 size = br.ReadUInt32();
                if (hash == 0)  //Relink
                {
                    flags = size;
                }
                else
                {
                    return new Substream(br.BaseStream, hash, size);
                }
            }
            else if (rawSearch.Length > 1)
            {
                string name;
                if (HashList.pairs.TryGetValue(hash, out name)) { name = rawSearch + name; }
                else { name = String.Format("{0}@noname/{1:x8}.bin", rawSearch, hash); }
                if (File.Exists(name))
                {
                    return File.Open(name, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
            }
            return null;
        }
    }
}
