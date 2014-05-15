using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using ISO_Tools;

namespace KH1FM_Toolkit
{
    internal class IDXFile
    {
        private BinaryReader file;
        public uint Count { get; private set; }
        public uint Position { get; private set; }
        public IDXFile(Stream input, bool newidx = false, bool leaveOpen = false)
        {
            file = new BinaryReader(input);
            input.Position = 0;
            if (newidx)
            {
                Count = 0;
                input.Write(new byte[] { 0, 0, 0, 0 }, 0, 4);
            }
            else
            {
                Count = file.ReadUInt32();
            }
            Position = 0;
        }
    }
    public class IDXEntry
    {
        public UInt32 hash,flags,LBA,size;
        public IDXEntry(UInt32 _hash,UInt32 _flags,UInt32 _LBA,UInt32 _size)
        {
            hash = _hash;
            flags = _flags;
            LBA = _LBA;
            size = _size;
        }
    }
    public class KH1ISOReader : IDisposable
    {
        public static readonly FileVersionInfo program = FileVersionInfo.GetVersionInfo(Assembly.GetEntryAssembly().Location);
        public FileStream iso;
        private BinaryReader br;
        /// <summary><para>Offset, in bytes, of all data.</para><para>This value is added to the IDX offset to get a file's offset.</para></summary>
        public long dataOffset { get; private set;}
        public long idxOffset, idxSize;
        /// <summary><para>Sorted list of IDX entries</para><para>Sorted by LBA</para></summary>
        public List<IDXEntry> idxEntries;
        /// <summary>Create a new <c>KH1ISOReader</c></summary>
        /// <param name="filename">Path to the ISO file to open</param>
        /// <exception cref="InvalidDataException">Thrown when the ISO is invalid (too small)</exception>
        /// <exception cref="InvalidDataException">Thrown when finding offsets fails</exception>
        public KH1ISOReader(string filename)
        {
            iso = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (iso.Length < 2048)
            {
                Dispose();
                throw new InvalidDataException("Invalid ISO, too small");
            }
            if (iso.Length % 2048 != 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("ISO size isn't a multiple of 2048! Surely a bad rip!");
                Console.ResetColor();
            }
            br = new BinaryReader(iso);
            findOffsets();
            if (dataOffset == 0 || idxOffset==0) {
                throw new InvalidDataException("Failed to find data or IDX offsets");
            }
            Console.WriteLine("BOOT2 was found at the offset {0} in the iso\nIDX was found at the offset {1} in the iso with {2} as size", dataOffset, idxOffset, idxSize);
            parseIDX();
        }
        public void Dispose()
        {
            if (br != null) { br.Close(); }
            if (iso != null) { iso.Close(); }
        }
        /// <summary>Find <c>dataOffset</c>, <c>idxOffset</c>, and <c>idxSize</c></summary>
        private void findOffsets()
        {
            try
            {
                var parser = new ISOParser.ISOFile();
                ISOParser.ISONode node;
                iso.Position = 0;
                parser.Parse(iso);
                if (parser.Root.Children.TryGetValue("SYSTEM.CNF;1", out node))
                {
                    dataOffset = node.Offset * 2048;
                }
                if (parser.Root.Children.TryGetValue("KINGDOM.IDX;1", out node))
                {
                    idxOffset = node.Offset * 2048;
                    idxSize = node.Length;
                }
                if (dataOffset!=0 && idxOffset!=0) { return; }
            }
            catch (Exception e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("KH1ISOReader:findOffsets: Error while reading ISO, trying fallback! ({0})", e.Message);
                Console.ResetColor();
            }
            if (dataOffset==0)
            {
                iso.Position = 0;
                do
                {
                    if (br.ReadUInt64() == 0x203D2032544F4F42) //BOOT2 =
                    {
                        dataOffset = iso.Position - 8;
                        break;
                    }
                } while ((iso.Position += 2040) < iso.Length);
            }
            if (idxOffset==0)
            {
                iso.Position = dataOffset;
                do
                {
                    if (br.ReadUInt64() == 0x000000010000171D) //First Hash of the IDX + first flag
                    {
                        idxOffset = iso.Position - 8;
                        iso.Position += 8;
                        do
                        {
                            if (br.ReadUInt32() == 0x0393eba4)//Finding the size by looking into the idx
                            {
                                iso.Position += 8;
                                idxSize = br.ReadUInt32();
                                break;
                            }
                        } while ((iso.Position += 12) < iso.Length);
                        break;
                    }
                } while ((iso.Position += 2040) < iso.Length);
            }
        }
        /// <summary>Read the IDX and extract the entries to <c>idxEntries</c></summary>
        private void parseIDX()
        {
            iso.Position = idxOffset;
            int entryC = (int)idxSize / 16;
            idxEntries = new List<IDXEntry>(entryC);
            while (--entryC>=0)
            {
                UInt32 hash = br.ReadUInt32();
                if (hash == 0) { iso.Position += 12; continue; }
                idxEntries.Add(new IDXEntry(hash, br.ReadUInt32(), br.ReadUInt32(), br.ReadUInt32()));
            }
            idxEntries.Sort((a, b) => a.LBA < b.LBA ? -1 : (a.LBA > b.LBA ? 1 : 0));
        }
        /// <summary>Read a file from the ISO</summary>
        /// <param name="entry">IDX entry describing the file</param>
        /// <returns>Byte array of the file</returns>
        /// <exception cref="System.Exception">Thrown when file is too large</exception>
        public byte[] readFile(IDXEntry entry)
        {
            if (entry.size > 0x7FFFFFFF) { throw new Exception("File too large to read"); }
            iso.Position = dataOffset + (2048 * entry.LBA);
            return br.ReadBytes((int)entry.size);
        }   
        /// <summary>Read raw bytes from the ISO</summary>
        /// <param name="offset">Offset to beginning of data</param>
        /// <param name="size">Size of data to return</param>
        /// <returns></returns>
        public byte[] readRaw(long offset, int size)
        {
            iso.Position = offset;
            return br.ReadBytes(size);
        }
    }
    class KH1ISOWriter : IDisposable
    {
        /// <summary>Buffer to use when copying (prevent constant re-allocs)</summary>
        private byte[] copyBuffer = new byte[8192];    // 8 KB buffer was fastest in basic copying tests (no import folder)
        private FileStream iso;
        private BinaryWriter bw;
        /// <summary><para>Original ISO.</para><para>Used for copying data from</para></summary>
        private KH1ISOReader origISO;
        private bool finalized, updateISOHeaders;
        private long dataOffset, idxOffset, idxSize, headerIMGoffset, headerIMGEnd;
        /// <summary>Bitfield that specifies additional operations to apply to header.</summary>
        /// <remarks>
        /// 00000001 kingdom.img added to the ISO filesystem<br />
        /// 00000010 demo.dat added to the ISO filesystem<br />
        /// 00000100 opn.dat added to the ISO filesystem<br />
        /// 00001000 end.dat added to the ISO filesystem<br />
        /// 00010000 end2.dat added to the ISO filesystem<br />
        /// 00100000 end3.dat added to the ISO filesystem<br />
        /// 01000000 ffx2.dat added to the ISO filesystem<br />
        /// 10000000 Currently unused
        /// </remarks>
        private UInt32 headerFlags;

        /// <summary><para>Unsorted list of IDX entries</para><para>Sorted in <c>finalize</c> by Hash</para></summary>
        /// <remarks><para>Default size of 16 is because there are normally 16 items in the ISO up to, and including, the IDX.</para><para>Once at the IDX, a more realistic Capacity is set.</para></remarks>
        private List<IDXEntry> idxEntries = new List<IDXEntry>(16);
        /// <summary>List of hashes to relink during finalization</summary>
        private Dictionary<UInt32, UInt32> idxRelinks = new Dictionary<UInt32, UInt32>();

        /// <summary>Create a new <c>KH1ISOWriter</c></summary>
        /// <param name="filename">Path to the ISO file to create</param>
        /// <param name="orig">A <c>KH1ISOReader</c> instance, used to copy data from</param>
        /// <param name="upHeaders">Whether to update ISO 9660 headers (Recommended to do so)</param>
        public KH1ISOWriter(string filename, KH1ISOReader orig = null, bool upHeaders = true)
        {

            iso = File.Open(filename, FileMode.Create, FileAccess.Write, FileShare.None);
            bw = new BinaryWriter(iso);
            origISO = orig;
            updateISOHeaders = upHeaders;
        }
        /*public bool setOriginal(KH1ISOReader orig){
            if (origISO != null) { return false; }
            origISO = orig;
            return true;
        }
        public void setUpdateHeaders(bool nv){
            if (idxOffset != 0) { throw new NotSupportedException("Too late to start updating headers"); }
            updateISOHeaders = nv;
        }*/
        ~KH1ISOWriter() { Dispose(false); }
        public void Dispose(){Dispose(true);/*GC.SuppressFinalize(this);*/}
        /// <summary><para>Clean up unmanaged resources</para><para>Also try to delete ISO is not finalized</para></summary>
        /// <param name="disposing">True is called from <c>Dispose</c>, False if from finalizer</param>
        protected virtual void Dispose(bool disposing)
        {
            if (bw != null) { bw.Flush(); bw.Close(); bw = null; }
            if (iso != null)
            {
                iso.Close();
                if (!finalized)
                {
                    try
                    {
                        File.Delete(iso.Name);
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("ISO was never finalized, deleting incomplete file!");
                        Console.ResetColor();
                    }
                    catch (Exception e)
                    {
                        Console.Write("Cannot delete {0}!\nDebug infos: {1}", iso.Name, e);
                    }
                }
                iso = null;
            }
            if (origISO != null) { origISO = null; }
        }
        /// <summary>Converts a standard UInt to a ISO 9660 style UInt (Little endian + Big endian)</summary>
        /// <param name="i">Number to convert</param>
        /// <returns>ISO 9660 representation of input</returns>
        /// <remarks>Not that we reverse the bit shifts, since the value is saves as little endian</remarks>
        private static UInt64 _ISO9660Number(UInt32 i) { return i | (UInt64)(i & 0x000000FFU) << 56 | (UInt64)(i & 0x0000FF00U) << 40 | (UInt64)(i & 0x00FF0000U) << 24 | (UInt64)(i & 0xFF000000U) << 8; }
        /// <summary>Copies raw bytes from <c>origISO</c> to <c>iso</c> using a buffer</summary>
        /// <param name="length">Size of data to copy</param>
        /// <param name="srcOffset">Offset in <c>origISO</c> to begin copying from</param>
        /// <param name="dstOffset">Offset in <c>iso</c> to begin copying to</param>
        private void copyBytes(long length, long srcOffset = -1, long dstOffset = -1)
        {
            if (length <= 0) { return; }
            if (srcOffset >= 0) { origISO.iso.Position = srcOffset; }
            if (dstOffset >= 0) { iso.Position = dstOffset; }
            int bytesRead;
            while (length > 0 && (bytesRead = origISO.iso.Read(copyBuffer, 0, copyBuffer.Length < length ? copyBuffer.Length : (int)length)) > 0)
            {
                iso.Write(copyBuffer, 0, bytesRead);
                length -= bytesRead;
            }
        }
        /// <summary><para>Update file information in the ISO 9660 filesystem</para><para>Also tracks IMG end offset</para></summary>
        /// <param name="hash">File hash to update</param>
        /// <param name="newPos">New position of file</param>
        /// <param name="newLen">New length of file</param>
        /// <exception cref="System.Exception">Thrown when file doesn't start on a 2048-byte boundary</exception>
        private void updateISOFileInfo(UInt32 hash, long newPos, UInt32 newLen)
        {
            if ((newPos % 2048) != 0) { throw new Exception("File not on an ISO boundary"); }
            UInt32 offI = 0, offU = 0;
            switch (hash)
            {
                case 0x009fa157: offI = 534626; offU = 544824; break; //system.cnf
                case 0x0100414e: offI = 534686; offU = 546872; break; //SLPS_251.98
                case 0x07fa52d4: offI = 534746; offU = 548920; break; //ioprp250.img
                case 0x013df21a: offI = 534808; offU = 550968; break; //sio2man.irx
                case 0x0046762a: offI = 534868; offU = 553016; break; //sio2d.irx
                case 0x00249f1a: offI = 534926; offU = 555064; break; //dbcman.irx
                case 0x0002088a: offI = 534986; offU = 557112; break; //ds2o.irx
                case 0x00a5a51a: offI = 535044; offU = 559160; break; //mcman.irx
                case 0x01117bda: offI = 535102; offU = 561208; break; //mcserv.irx
                case 0x00c04a8a: offI = 535162; offU = 563256; break; //libsd.irx
                case 0x018d391a: offI = 535220; offU = 565304; break; //libssl.irx
                case 0x0002cefa: offI = 535280; offU = 567352; break; //dev9.irx
                case 0x002283ea: offI = 535338; offU = 569400; break; //atad.irx
                case 0x003af68a: offI = 535396; offU = 571448; break; //hdd.irx
                case 0x003aaf8a: offI = 535452; offU = 573496; break; //pfs.irx
                case 0x001ec6d9: if (headerIMGEnd < 1) { headerIMGEnd = newPos; } if ((headerFlags & 0x02) != 0) { offI = 535628; } break; //demo.dat
                case 0x001a7629: if (headerIMGEnd < 1) { headerIMGEnd = newPos; } if ((headerFlags & 0x04) != 0) { offI = 535672; } break; //opn.dat
                case 0x00132c19: if (headerIMGEnd < 1) { headerIMGEnd = newPos; } if ((headerFlags & 0x08) != 0) { offI = 535714; } break; //end.dat
                case 0x002118c9: if (headerIMGEnd < 1) { headerIMGEnd = newPos; } if ((headerFlags & 0x10) != 0) { offI = 535756; } break; //end2.dat
                case 0x003118c9: if (headerIMGEnd < 1) { headerIMGEnd = newPos; } if ((headerFlags & 0x20) != 0) { offI = 535800; } break; //end3.dat
                case 0x00dd0889: if (headerIMGEnd < 1) { headerIMGEnd = newPos; } if ((headerFlags & 0x40) != 0) { offI = 535844; } break; //ffx2.dat
                default: return;
            }
            if (updateISOHeaders && offI != 0)
            {
                iso.Position = offI;
                bw.Write(_ISO9660Number((UInt32)(newPos / 2048)));//file LBA
                bw.Write(_ISO9660Number(newLen));//file Size
                if (offU != 0)
                {
                    iso.Position = offU;
                    bw.Write(newLen);
                }
                iso.Seek(0, SeekOrigin.End);
            }
        }
        /// <summary>Copy an ISO header to <c>iso</c></summary>
        /// <param name="exthead">Path to an external header to load</param>
        /// <exception cref="System.NotSupportedException">Thrown when there is already data in <c>iso</c></exception>
        /// <exception cref="System.Exception">Thrown when no header was detected for use</exception>
        /// <returns>True if the external header used, false if internal</returns>
        public bool writeHeader(string exthead = "")
        {
            if (iso.Length > 0) { throw new NotSupportedException("Can't write header when there's already datas!"); }
            if (exthead.Length > 0 && File.Exists(exthead))
            {
                using (var head = new BinaryReader(File.Open(exthead, FileMode.Open, FileAccess.Read, FileShare.Read)))
                {
                    int len = (int)head.BaseStream.Length -4;
                    headerFlags = head.ReadUInt32();
                    iso.Write(head.ReadBytes(len), 0, len);
                    dataOffset = len;
                    return true;
                }
            }
            if (origISO != null) {
                copyBytes(577536, 0);
                dataOffset = 577536;
                return false;
            }
            throw new Exception("No external or internal header detected for use");
        }
        /// <summary>Write a temporary IDX</summary>
        /// <param name="entryC">Number of entries to reserve space for</param>
        /// <exception cref="System.NotSupportedException">Thrown when no header has been written</exception>
        /// <exception cref="System.NotSupportedException">Thrown when a dummy IDX has already been written</exception>
        public void writeDummyIDX(int entryC)
        {
            if (dataOffset == 0) { throw new NotSupportedException("No header written"); }
            if (idxOffset != 0) { throw new NotSupportedException("IDX already written"); }
            idxOffset = iso.Position;
            idxSize = (long)Math.Ceiling((decimal)entryC * 16 / 2048) * 2048;   //Round up to nearest 2048
            iso.Position += idxSize - 1; iso.WriteByte(0);
            idxEntries.Capacity = entryC;
            idxEntries.Add(new IDXEntry(0x0393eba4U, 0, (UInt32)((idxOffset - dataOffset) / 2048), (UInt32)idxSize));//kingdom.idx
            //Add IMG IDX
            headerIMGoffset = iso.Position;
            idxEntries.Add(new IDXEntry(0x0392ebe4U, 0, (UInt32)((headerIMGoffset - dataOffset) / 2048), 0));

            if (updateISOHeaders)
            {
                iso.Position = 535508;
                    bw.Write(_ISO9660Number((UInt32)idxOffset/2048));//IDX LBA
                    bw.Write(_ISO9660Number((UInt32)idxSize));//IDX Size
                iso.Position = 575544;
                    bw.Write((UInt32)idxSize);//UDF IDX Size
                if ((headerFlags & 0x01) != 0)
                {
                    iso.Position = 535568;
                    bw.Write(_ISO9660Number((UInt32)headerIMGoffset / 2048));//IMG LBA
                }
                iso.Seek(0, SeekOrigin.End);
            }
        }
        /// <summary>Write raw data to current position in <c>iso</c> as a file</summary>
        /// <param name="data">File data to write</param>
        /// <param name="hash">Hash of filename</param>
        /// <param name="flags">Flags for file (Compressed)</param>
        /// <exception cref="System.ObjectDisposedException">Thrown when ISO has been finalized</exception>
        /// <exception cref="System.Exception">Thrown when data is too large to write</exception>
        public void writeBytes(byte[] data, UInt32 hash, UInt32 flags)
        {
            if (finalized) { throw new ObjectDisposedException("ISO has been finalized"); }
            if (data.Length > 0x7FFFFFFF) { throw new Exception("File too large to write"); }
            long pos = iso.Position,
                padding = (data.Length % 2048) == 0 ? 0 : (2048 - (data.Length % 2048));
            idxEntries.Add(new IDXEntry(hash, flags, (UInt32)(pos - dataOffset) / 2048, (UInt32)data.Length));
            bw.Write(data);
            if (padding > 0) { iso.Position += padding - 1; iso.WriteByte(0); }
            updateISOFileInfo(hash, pos, (UInt32)data.Length);
        }
        /// <summary>Copy file from <c>origISO</c> to <c>iso</c></summary>
        /// <param name="entry">IDX entry describing the file in <c>origISO</c></param>
        /// <exception cref="System.ObjectDisposedException">Thrown when ISO has been finalized</exception>
        /// <exception cref="System.Exception">Thrown when <c>origISO</c> isn't set</exception>
        /// <exception cref="System.Exception">Thrown when <c>entry</c> isn't in <c>origISO</c></exception>
        public void copyFile(IDXEntry entry)
        {
            if (finalized) { throw new ObjectDisposedException("ISO has been finalized"); }
            if (origISO == null) { throw new Exception("No original ISO to copy from"); }
            if (!origISO.idxEntries.Contains(entry)) { throw new Exception("IDX entry not in original ISO"); }
            long pos = iso.Position,
                padding = (entry.size % 2048) == 0 ? 0 : (2048 - (entry.size % 2048));
            idxEntries.Add(new IDXEntry(entry.hash, entry.flags, (UInt32)(pos - dataOffset) / 2048, entry.size));
            copyBytes(entry.size + padding, origISO.dataOffset + entry.LBA * 2048);
            updateISOFileInfo(entry.hash, pos, entry.size);
        }
        /// <summary>Copy file from <c>origISO</c> to <c>iso</c></summary>
        /// <param name="hash">Hash of filename</param>
        /// <exception cref="System.Exception">Thrown when no matching hash is found in <c>origISO</c></exception>
        public void copyFile(UInt32 hash)
        {
            IDXEntry entry = origISO.idxEntries.Find(a => a.hash == hash);
            if (entry == null) { throw new Exception("Failed to find IDX entry in original ISO"); }
            copyFile(entry);
        }
        /// <summary>Copy file from Stream to <c>iso</c></summary>
        /// <param name="str">Source stream</param>
        /// <param name="hash">Hash of filename</param>
        /// <param name="flags">Flags for file</param>
        /// <exception cref="System.ObjectDisposedException">Thrown when ISO has been finalized</exception>
        public void importFile(Stream str, UInt32 hash, UInt32 flags = 0)
        {
            if (finalized) { throw new ObjectDisposedException("ISO has been finalized"); }
            if (str.Length <= 0) { return; }
            long pos = iso.Position,
                length = str.Length,
                padding = (length % 2048) == 0 ? 0 : (2048 - (length % 2048));
            idxEntries.Add(new IDXEntry(hash, flags, (UInt32)(pos - dataOffset) / 2048, (UInt32)length));
            int bytesRead;
            while (length > 0 && (bytesRead = str.Read(copyBuffer, 0, copyBuffer.Length < length ? copyBuffer.Length : (int)length)) > 0)
            {
                iso.Write(copyBuffer, 0, bytesRead);
                length -= bytesRead;
            }
            if (padding > 0) { iso.Position += padding - 1; iso.WriteByte(0); }
            updateISOFileInfo(hash, pos, (UInt32)str.Length);
        }
        /// <summary>Relinks one file to another, so they share the same data</summary>
        /// <param name="sHash">Source file; What to relink</param>
        /// <param name="tHash">Target file; What to relink to</param>
        /// <returns>True if the relink is done, False if it was queued</returns>
        public bool addRelink(UInt32 sHash, UInt32 tHash)
        {
            IDXEntry t = idxEntries.Find(a => a.hash == tHash);
            if (t != null)
            {
                idxEntries.Add(new IDXEntry(sHash, t.flags, t.LBA, t.size));
                return true;
            }
            idxRelinks.Add(sHash, tHash);
            return false;
        }
        /// <summary>Write the IDX to the ISO, update a few final ISO 9660 values, and Dispose of self</summary>
        /// <exception cref="System.ObjectDisposedException">Thrown when ISO has already been finalized</exception>
        /// <exception cref="System.NotSupportedException">Thrown when no IDX dummy has been written</exception>
        /// <exception cref="System.NotSupportedException">Thrown when there are more IDX entries to write then space was reserved for</exception>
        public void finalize()
        {
            if (finalized) { throw new ObjectDisposedException("ISO has been finalized"); }
            if (idxOffset == 0) { throw new NotSupportedException("No IDX to finalize"); }
            if ((idxEntries.Count + idxRelinks.Count) * 16 > idxSize) { throw new NotSupportedException("Trying to write more entries then space was allocated for"); }
            // Add delayed relinks
            foreach (KeyValuePair<UInt32, UInt32> item in idxRelinks)
            {
                IDXEntry t = idxEntries.Find(a => a.hash == item.Value);
                if (t.hash == item.Value)
                {
                    idxEntries.Add(new IDXEntry(item.Key, t.flags, t.LBA, t.size));
                }
                else
                {
                    Console.WriteLine("WARNING: Failed to relink {0:X8} to {1:X8}!", item.Key, item.Value);
                }
            }
            idxRelinks.Clear();
            // Sort IDXs by hash
            idxEntries.Sort((a, b) => a.hash < b.hash ? -1 : (a.hash > b.hash ? 1 : 0));
            // Write IDXs
            iso.Position = idxOffset;
            var IMGLen = (UInt32)((headerIMGEnd > 0 ? headerIMGEnd : iso.Length) - headerIMGoffset);
            foreach (IDXEntry entry in idxEntries)
            {
                bw.Write(entry.hash);
                bw.Write(entry.flags);
                bw.Write(entry.LBA);
                if (entry.hash == 0x0392ebe4)//kingdom.img needs length saved
                {
                    bw.Write(IMGLen);
                }
                else
                {
                    bw.Write(entry.size);
                }
            }
            idxEntries.Clear();

            if (updateISOHeaders)
            {
                UInt32 fileLBA = (UInt32)(iso.Length / 2048),
                    UDFLBA = fileLBA - 263;
                iso.Position = 32848;
                bw.Write(_ISO9660Number(fileLBA));/*ISO9660 Volume Space*/
                iso.Position = 69824;
                bw.Write(UDFLBA);/*UDF Partition size*/
                iso.Position = 102592;
                bw.Write(UDFLBA);/*UDF Partition size (Reserve header)*/

                if ((headerFlags & 0x01) != 0)
                {
                    iso.Position = 535568+8;
                    bw.Write(_ISO9660Number(IMGLen));/*IMG Size*/
                }
            }
            Console.Write("Flushing data to disk...");
            bw.Flush();
            if (!NativeMethods.FlushFileBuffers(iso.SafeFileHandle))
            {
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "Win32 FlushFileBuffers returned error in KH1ISOWriter");
            }
            Console.WriteLine("    Done!");

            finalized = true;
            Dispose();
        }
    }
    internal static class NativeMethods
    {
        /// <summary>Adds or removes an application-defined HandlerRoutine function from the list of handler functions for the calling process.</summary>
        /// <param name="h">Application-defined HandlerRoutine function to be added or removed. This parameter can be NULL.</param>
        /// <param name="a">If this parameter is TRUE, the handler is added; if it is FALSE, the handler is removed.</param>
        /// <returns>True if the function succeeds.</returns>
        [System.Runtime.InteropServices.DllImport("Kernel32")]
        public static extern bool SetConsoleCtrlHandler(HandlerRoutine h, bool a);
        public delegate bool HandlerRoutine(UInt32 t);
        /// <summary>Flushes the buffers of a specified file and causes all buffered data to be written to a file.</summary>
        /// <remarks>.NET 4 and higher have this built-in to the <c>FileStream.Flush</c> function, but some early implementations of it are known to be buggy.</remarks>
        /// <param name="hFile">A handle to the open file.</param>
        /// <returns>True if the function succeeds.</returns>
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool FlushFileBuffers(Microsoft.Win32.SafeHandles.SafeFileHandle hFile);
    }
    class Program
    {
        public static void WriteWarning(string format, params object[] arg)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(format, arg);
            Console.ResetColor();
        }
        public static void WriteError(string format, params object[] arg)
        {
            WriteWarning(format, arg);
            //Let the user see the error
            Console.Write(@"Press enter to continue anyway... ");
            Console.ReadLine();
        }
        private static void PatchISO(KH1ISOReader input, KH1ISOWriter output, PatchManager files, bool ocompress, string oextHead)
        {
            int number = 1;
                                                Console.WriteLine("Adding header using {0} source", output.writeHeader(oextHead) ? "external" : "internal");
                                    for (int i = 0, idxC = input.idxEntries.Count; i < idxC; ++i)
                                    {
                                        if (killReceived) { return; }
                                        IDXEntry entry = input.idxEntries[i];
                                        if (entry.hash == 0x0392ebe4) { continue; }//kingdom.img
                                        if (entry.hash == 0x0393eba4)//kingdom.idx
                                        {
                                            Console.WriteLine("[KINGDOM: {0}/{1}] KINGDOM.IDX", number, input.idxEntries.Count - 1);//-1 'cause of the file KINGDOM.IMG
                                            number++;
                                            output.writeDummyIDX(idxC);
                                            continue;
                                        }

                                        //Loading the patch
                                        UInt32 flags;
                                        using (Stream s = files.findFile(entry.hash, out flags))
                                        {
                                            string name;
                                            if (!HashList.pairs.TryGetValue(entry.hash, out name)) { name = String.Format("@noname/{0:x8}.bin", entry.hash); }
                                            if (s == null)
                                            {
                                                if (flags != 0)
                                                {
                                                    Console.WriteLine("[KINGDOM: {0}/{1}] {2}\tRelinking...", number, input.idxEntries.Count - 1, name);//-1 'cause of the file KINGDOM.IMG
                                                    number++;
                                                    if (!HashList.pairs.TryGetValue(flags, out name)) { name = String.Format("@noname/{0:x8}.bin", flags); }
                                                    Console.WriteLine("{0}", name);
                                                    output.addRelink(entry.hash, flags);
                                                }
                                                else
                                                {
                                                    Console.WriteLine("[KINGDOM: {0}/{1}] {2}", number, input.idxEntries.Count - 1, name);//-1 'cause of the file KINGDOM.IMG
                                                    number++;
                                                    output.copyFile(entry);
                                                }
                                            }
                                            else
                                            {
                                                Console.WriteLine("[KINGDOM: {0}/{1}] {2}\tPatching...", number, input.idxEntries.Count, name);//-1 'cause of the file KINGDOM.IMG
                                                number++;
                                                if (flags == 0 && ((ocompress && (entry.flags & 0x01) == 1) || entry.hash == 0x0000171d))   //Older versions + the fallback method find the IDX via compressed pi00_04.bin entry
                                                {
                                                    var bytes = new byte[s.Length];
                                                    s.Read(bytes, 0, (int)s.Length);
                                                    try
                                                    {
                                                        bytes = KHCompress.KH1Compressor.compress(bytes);
                                                        flags |= 1;
                                                    }
                                                    catch (KHCompress.NotCompressableException e)
                                                    {
                                                        Console.WriteLine("  Cannot compress file: {0}", e.Message);
                                                    }
                                                    output.writeBytes(bytes, entry.hash, flags);
                                                }
                                                else
                                                {
                                                    output.importFile(s, entry.hash, flags);
                                                }
                                            }
                                        }
                                    }
                                    output.finalize();
                                    Console.ForegroundColor = ConsoleColor.Green;
                                    Console.WriteLine("New ISO finished!");
                                    Console.ResetColor();
        }

        private static DateTime RetrieveLinkerTimestamp()
        {
            string filePath = Assembly.GetCallingAssembly().Location;
            const int cPeHeaderOffset = 60;
            const int cLinkerTimestampOffset = 8;
            var b = new byte[2048];
            Stream s = null;

            try
            {
                s = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                s.Read(b, 0, 2048);
            }
            finally
            {
                if (s != null)
                {
                    s.Close();
                }
            }

            int i = BitConverter.ToInt32(b, cPeHeaderOffset);
            int secondsSince1970 = BitConverter.ToInt32(b, i + cLinkerTimestampOffset);
            var dt = new DateTime(1970, 1, 1, 0, 0, 0);
            dt = dt.AddSeconds(secondsSince1970);
            dt = dt.AddHours(TimeZone.CurrentTimeZone.GetUtcOffset(dt).Hours);
            return dt;
        }

        /// <summary>When true, return properly as fast as possible</summary>
        volatile static bool killReceived;
        /// <summary><para>Function to handle Ctrl+C and clicking the "Close X" button</para><para>Can be added or removed as a handler as needed</para></summary>
        static readonly NativeMethods.HandlerRoutine killHandler = delegate(uint t)
        {
            switch (t)
            {
                case 0:
                    goto case 1; /*Ctrl+C*/
                case 1:
                    killReceived = true;
                    return true; /*Ctrl+Break*/
                case 2: /*CLOSE*/
                    killReceived = true;
                    System.Threading.Thread.Sleep(5000);
                        //Keeps the app from insta-dying for 5 secs, enough time to return below
                    return true;
            }
            return false;
        };
        private static void ExtractISO(string iso2, string tfolder = "export/")
        {
            FileStream isofile = new FileStream(iso2, FileMode.Open, FileAccess.Read);
            using (var iso = new ISOFileReader(isofile))
            {
                var idxs = new List<IDXEntry>();
                var idxnames = new List<string>();
                int i = 0;
                foreach (FileDescriptor file in iso)
                {
                    ++i;
                    string filename = file.FullName;
                    if (filename.EndsWith(".IDX"))
                    {
                        //KH1ISOReader();
                        //idxs.Add(new IDXFile(iso.GetFileStream(file)));
                        //idxnames.Add(Path.GetFileNameWithoutExtension(filename));
                        //continue;
                        //Write the IDX too
                    }
                    else if (filename.EndsWith(".IMG") && idxnames.Contains(Path.GetFileNameWithoutExtension(filename)))
                    {
                        continue;
                    }
                    Console.WriteLine("[ISO: {0,3}]\tExtracting {1}", i, filename);
                    filename = Path.GetFullPath(tfolder + "ISO/" + filename);
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(filename));
                    }
                    catch (IOException e)
                    {
                        WriteError("Failed creating directory: {0}", e.Message);
                        continue;
                    }
                    using (var output = new FileStream(filename, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                    {
                        iso.CopyFile(file, output);
                    }
                }
                for (i = 0; i < idxs.Count; ++i)
                {
                    try
                    {
                        FileDescriptor file = iso.FindFile(idxnames[i] + ".IMG");
                        using (GovanifY.Utility.Substream img = iso.GetFileStream(file))
                        {
                            //ExtractIDX(idxs[i], img, true, tfolder + "" + idxnames[i] + "/", idxnames[i]);
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        WriteError("ERROR: Failed to find matching IMG for IDX");
                    }
                }
            }
        }
        static void Main(string[] args)
        {
            bool obatch = false;
            try
            {
                // Some functions work with raw bits, so we HAVE to be using little endian.
                // .NET, however, supports running the same code on both types.
                if (BitConverter.IsLittleEndian != true) { throw new PlatformNotSupportedException("Platform not supported, not using a little endian bitconverter"); }
                HashList.loadHashPairs();
                Console.Title = KH1ISOReader.program.ProductName + " " + KH1ISOReader.program.FileVersion + " [" + KH1ISOReader.program.CompanyName + "]";
                bool ocompress = true, oupdateHeads = true, extract = false;
                string iso = "", oextHead = "", NewIso = "";
                #region Arguments
                for (int i = 0, argc = args.Length; i < argc; ++i)
                {
                    switch (args[i].ToLower())
                    {
                        case "-batch": obatch = true; break;
                        case "-nocompress": ocompress = false; break;
                        case "-extractor":
                            extract = true;
                            break;
#if DEBUG
                        case "-noupisohead": oupdateHeads = false; break;
                        case "-externalhead":
#endif
                            if (++i < argc && (oextHead = args[i]).Length != 0) { break; }
                            oextHead = "KH1ISOMake-head.bin";
                            break;
                        case "-patchmaker":
                            KH1_Patch_Maker.Program.Mainp(args);
                            break;
                        default:
                            if (File.Exists(args[i]))
                            {
                                if (args[i].EndsWith(".iso", StringComparison.InvariantCultureIgnoreCase))
                                { iso = args[i]; }
                            }
                            break;
                    }
                }
                #endregion
                #region Description
                using (var files = new PatchManager())
                {
                if (iso.Length == 0)
                {
                    iso = "KHFM.ISO";
                }
                Console.ForegroundColor = ConsoleColor.Gray;
                var Builddate = RetrieveLinkerTimestamp();
                Console.Write("{0}\nBuild Date: {2}\nVersion {1}", KH1ISOReader.program.ProductName, KH1ISOReader.program.FileVersion, Builddate);
                string Platform;
                if (IntPtr.Size == 8) { Platform = "x64"; }
                else { Platform = "x86"; }
                Console.Write("\n{0} build", Platform);
                Console.ResetColor();
#if DEBUG
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("\nPRIVATE RELEASE\n");
                Console.ResetColor();
#else
                Console.Write("\nPUBLIC RELEASE\n");
#endif

                Console.ForegroundColor = ConsoleColor.DarkMagenta;
                Console.Write("\nProgrammed by {0}\nhttp://www.govanify.blogspot.fr\nhttp://www.govanify.x10host.com",
                    KH1ISOReader.program.CompanyName);
                Console.ForegroundColor = ConsoleColor.Gray;
                if (extract) { Console.Write("\n\nThis tool is able to extract the files of the game Kingdom Hearts 1(Final Mix).\nHe's using a list for extracting files with their real name which isn't completeBut this is the most complete one for now.\nHe can extract the files stored which got a reference into KINGDOM.IDX.\n\n"); }
                else
                {
                    Console.Write("\n\nThis tool is able to patch the game Kingdom Hearts 1(Final Mix).\nHe can modify iso files, like the elf and internal files,\nwich are stored inside the hidden file KINGDOM.IMG\nThis tool is recreating too new hashes into the idx files for avoid\na corrupted game. He can add some files too.\n\n");
                }
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("\nPress enter to run using the file:");
                Console.ResetColor();
                Console.Write(" {0}", iso);
                Console.ReadLine();
                #endregion
                // Enable the close handler
                NewIso = Path.ChangeExtension(iso, "NEW.ISO");
                NativeMethods.SetConsoleCtrlHandler(killHandler, true);
                using (var input = new KH1ISOReader(iso))
                {
                    if (extract) { ExtractISO(iso); }
                    else
                    {
                            try
                            {
                                using (var output = new KH1ISOWriter(NewIso, input, oupdateHeads))
                                {
                                    PatchISO(input, output, files, ocompress, oextHead);
                                }
                            }
                            catch (Exception)
                            {
                                //Delete the new "incomplete" iso
                                File.Delete(NewIso);
                                throw;
                            }
                        }

                        // Disable the close handler
                        NativeMethods.SetConsoleCtrlHandler(killHandler, false);
                    }
                }
            }
            catch (Exception e)
            {
                /*// Disable the close handler
                NativeMethods.SetConsoleCtrlHandler(killHandler, false);

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("A fatal error has occured: {0}", e.Message);
                Console.ResetColor();
                Console.Write("Press enter to exit or \"debug\" for more information... ");
                if (killReceived || (!obatch && Console.ReadLine().ToLower() != "debug") ) { return; }
                Console.WriteLine("\nType: {0}\n{1}\n", e.GetType(), e.StackTrace);
            }
            if (!obatch && !killReceived)
            {
                Console.Write("Press enter to exit..."); Console.ReadLine();*/
              throw;
            }

            }
        }
    }
