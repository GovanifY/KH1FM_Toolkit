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
using System.Text;
using GovanifY.Utility;

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
    public sealed class PatchManager : IDisposable
    {
        public bool ISOChanged { get; private set; }
        public bool KINGDOMChanged { get; private set; }
        private readonly List<Stream> patchms = new List<Stream>();

        /// <summary>Mapping of Parent IDX -> new children hashes</summary>
        internal Dictionary<uint, List<uint>> newfiles = new Dictionary<uint, List<uint>>();

        /// <summary>Mapping of hash->Patch</summary>
        internal Dictionary<uint, Patch> patches = new Dictionary<uint, Patch>();
        public static void NGYXor(byte[] buffer)
        {
            byte[] v84 = { 164, 28, 107, 129, 48, 13, 35, 91, 92, 58, 167, 222, 219, 244, 115, 90, 160, 194, 112, 209, 40, 72, 170, 114, 98, 181, 154, 124, 124, 32, 224, 199, 34, 32, 114, 204, 38, 198, 188, 128, 45, 120, 181, 149, 219, 55, 33, 116, 6, 17, 181, 125, 239, 137, 72, 215, 1, 167, 110, 208, 110, 238, 124, 204 };
            int i = -1, l = buffer.Length;
            while (l > 0)
            {
                buffer[++i] ^= v84[(--l & 63)];
            }
        }
        static UInt32 calcHash(byte[] name)
        {
            int v0 = 0;
            uint i = 0;
            byte c;
            while ((c = name[i++]) != 0)
            {
                v0 = (2 * v0) ^ (((int)c << 16) % 69665);
            }
            return (uint)v0;
        }
        public static UInt32 calcHash(string name) { return calcHash(Encoding.ASCII.GetBytes(name + '\0')); }
        private Dictionary<uint, Tuple<int, long>> patchedFiles = new Dictionary<uint, Tuple<int, long>>();
        private string rawSearch;
        public PatchManager(string path = ".", string looseSearchPath = "import/")
        {

        }
        ~PatchManager(){Dispose(false);}
        public void Dispose(){Dispose(true);}
        protected void Dispose(bool disposing)
        {
            if (disposing)
            {
                patchedFiles.Clear();
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
        internal void AddToNewFiles(Patch nPatch)
        {
            nPatch.IsNew = true;
            if (!newfiles.ContainsKey(nPatch.Parent))
            {
                newfiles.Add(nPatch.Parent, new List<uint>(1));
            }
            if (!newfiles[nPatch.Parent].Contains(nPatch.Hash))
            {
                newfiles[nPatch.Parent].Add(nPatch.Hash);
            }
        }
        private void AddPatch(Stream ms, string patchname = "")
        {
            using (var br = new BinaryStream(ms, Encoding.ASCII, leaveOpen: true))
            {
                if (br.ReadUInt32() != 0x5031484b)
                {
                    br.Close();
                    ms.Close();
                    throw new InvalidDataException("Invalid KH1Patch file!");
                }
                patchms.Add(ms);
                uint oaAuther = br.ReadUInt32(),
                    obFileCount = br.ReadUInt32(),
                    num = br.ReadUInt32();
                patchname = Path.GetFileName(patchname);
                try
                {
                    string author = br.ReadCString();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("Loading patch {0} version {1} by {2}", patchname, num, author);
                    Console.ResetColor();
                    br.Seek(oaAuther, SeekOrigin.Begin);
                    uint os1 = br.ReadUInt32(),
                        os2 = br.ReadUInt32(),
                        os3 = br.ReadUInt32();
                    br.Seek(oaAuther + os1, SeekOrigin.Begin);
                    num = br.ReadUInt32();
                    if (num > 0)
                    {
                        br.Seek(num * 4, SeekOrigin.Current);
                        Console.WriteLine("Changelog:");
                        Console.ForegroundColor = ConsoleColor.Green;
                        while (num > 0)
                        {
                            --num;
                            Console.WriteLine(" * {0}", br.ReadCString());
                        }
                    }
                    br.Seek(oaAuther + os2, SeekOrigin.Begin);
                    num = br.ReadUInt32();
                    if (num > 0)
                    {
                        br.Seek(num * 4, SeekOrigin.Current);
                        Console.ResetColor();
                        Console.WriteLine("Credits:");
                        Console.ForegroundColor = ConsoleColor.Green;
                        while (num > 0)
                        {
                            --num;
                            Console.WriteLine(" * {0}", br.ReadCString());
                        }
                        Console.ResetColor();
                    }
                    br.Seek(oaAuther + os3, SeekOrigin.Begin);
                    author = br.ReadCString();
                    if (author.Length != 0)
                    {
                        Console.ResetColor();
                        Console.WriteLine("Other information:\r\n");
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("{0}", author);
                    }
                    Console.ResetColor();
                }
                catch (Exception e)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error reading kh1patch header: {0}: {1}\r\nAttempting to continue files...",
                        e.GetType(), e.Message);
                    Console.ResetColor();
                }
                Console.WriteLine("");
                br.Seek(obFileCount, SeekOrigin.Begin);
                num = br.ReadUInt32();
                while (num > 0)
                {
                    --num;
                    var nPatch = new Patch();
                    nPatch.Hash = br.ReadUInt32();
                    oaAuther = br.ReadUInt32();
                    nPatch.CompressedSize = br.ReadUInt32();
                    nPatch.UncompressedSize = br.ReadUInt32();
                    nPatch.Parent = br.ReadUInt32();
                    nPatch.Relink = br.ReadUInt32();
                    nPatch.Compressed = br.ReadUInt32() != 0;
                    nPatch.IsNew = br.ReadUInt32() == 1; //Custom
                    if (!nPatch.IsRelink)
                    {
                        if (nPatch.CompressedSize != 0)
                        {
                            nPatch.Stream = new Substream(ms, oaAuther, nPatch.CompressedSize);
                        }
                        else
                        {
                            throw new InvalidDataException("File length is 0, but not relinking.");
                        }
                    }
                    // Use the last file patch
                    if (patches.ContainsKey(nPatch.Hash))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("The file {0} has been included multiple times. Using the one from {1}.",
                            HashList.NameFromHash(nPatch.Hash), patchname);
                        patches[nPatch.Hash].Dispose();
                        patches.Remove(nPatch.Hash);
                        Console.ResetColor();
                    }
                    patches.Add(nPatch.Hash, nPatch);
                    //Global checks
                    if (!KINGDOMChanged && nPatch.IsInKINGDOM || nPatch.IsInKINGDOM)
                    {
                        KINGDOMChanged = true;
                    }
                    else if (!ISOChanged && nPatch.IsinISO)
                    {
                        ISOChanged = true;
                    }
                    if (nPatch.IsNew)
                    {
                        AddToNewFiles(nPatch);
                    }
                    br.Seek(60, SeekOrigin.Current);
                }
            }
        }

        public void AddPatch(string patchname)
        {
            FileStream fs = null;
            try
            {
                fs = new FileStream(patchname, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.ReadByte() == 0x4B && fs.ReadByte() == 0x48 && fs.ReadByte() == 0x31 && fs.ReadByte() == 0x50)
                {
                    fs.Position = 0;
                    AddPatch(fs, patchname);
                    return;
                }
                if (fs.Length > int.MaxValue)
                {
                    throw new OutOfMemoryException("File too large");
                }

                try
                {
                    fs.Position = 0;
                    var buffer = new byte[fs.Length];
                    fs.Read(buffer, 0, (int)fs.Length);
                    NGYXor(buffer);
                    AddPatch(new MemoryStream(buffer), patchname);
                }

                catch (Exception)
                {
                      
                }
                finally
                {
                    fs.Dispose();
                    fs = null;
                }
            }
            catch (Exception e)
            {
                if (fs != null)
                {
                    fs.Dispose();
                }
                Console.WriteLine("Failed to parse patch: {0}", e.Message);
            }
        }
        internal class Patch : IDisposable
        {
            public bool Compressed;
            public uint CompressedSize;
            public uint UncompressedSize;
            public uint Hash;
            public bool IsNew;
            public uint Parent;
            public uint Relink;
            public Substream Stream;

            public bool IsInKINGDOM
            {
                get { return Parent == 0; }
            }

            public bool IsinISO
            {
                get { return Parent == 1; }
            }

            public bool IsRelink
            {
                get { return Relink != 0; }
            }

            public void Dispose()
            {
                if (Stream != null)
                {
                    Stream.Dispose();
                    Stream = null;
                }
            }
        }
    }
}
