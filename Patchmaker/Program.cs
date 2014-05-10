using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using KH1FM_Toolkit;

namespace KH1_Patch_Maker
{
    class Program
    {
        static UInt32 calcHash(byte[] name){
            int v0 = 0;
            uint i = 0;
            byte c;
            while ((c = name[i++]) != 0)
            {
                v0 = (2 * v0) ^ (((int)c << 16) % 69665);
            }
            return (uint)v0;
        }
        static UInt32 calcHash(string name) { return calcHash(Encoding.ASCII.GetBytes(name + '\0')); }
        static bool yesnoInput(string prompt)
        {
            Console.Write(prompt+" [y,n] ");
            bool ret = Console.ReadKey().Key.ToString() == "Y";
            Console.WriteLine("");
            return ret;
        }
        static UInt32 uintFromInput(string prompt, UInt32 def = 0)
        {
            UInt32 val = 0;
            do
            {
                Console.Write("{0} [blank={1}]: ",prompt,def);
                string input = Console.ReadLine().Trim();
                if(input.Length == 0 && def > 0){return def;}
                else if (UInt32.TryParse(input, out val) && val > 0){return val;}
                else{Console.WriteLine("  Failed to parse that as a number!");}
            } while (true);
        }
        static UInt32 hashFromInput(string prompt, out string input, KH1FM_Toolkit.KH1ISOReader iso = null, bool allowFail = false)
        {
            UInt32 hash = 0;
            do
            {
                Console.Write(prompt+" [hex or filename]: ");
                input = Console.ReadLine().Trim();
                if (!UInt32.TryParse(input, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out hash))
                {
                    foreach (KeyValuePair<UInt32, string> pair in KH1FM_Toolkit.HashList.pairs)
                    {
                        if (input.Equals(pair.Value)) { hash = pair.Key; break; }
                    }
                    if (hash == 0) { input = ""; }
                }
                else
                {
                    if (!KH1FM_Toolkit.HashList.pairs.TryGetValue(hash, out input)) { input = String.Format("@noname/{0:x8}.bin", hash); }
                    if (iso != null && iso.idxEntries.Find(a => a.hash == hash) == null && !yesnoInput("WARNING: That file isn't in KH.ISO; Use it anyway?"))
                    {
                        hash = 0; continue;
                    }
                }
                if (allowFail || hash != 0) { return hash; }
                else
                {
                    Console.WriteLine("  Failed to parse that as hex or a filename in Hashpairs!");
                }
            } while (true);
            //Console.Write("WARNING: That file isn't in HashList! Include it anyway? [y,n] "); if (Console.ReadKey().KeyChar != 'y') { ++pFiles; continue; }

        }
        static UInt32 hashFromInput(string prompt, KH1FM_Toolkit.KH1ISOReader iso = null)
        {
            string tmp; return hashFromInput(prompt, out tmp, iso);
        }
        static readonly string programVer = System.Diagnostics.FileVersionInfo.GetVersionInfo(System.Reflection.Assembly.GetEntryAssembly().Location).FileVersion;
        public static void Mainp(string[] args)
        {
            string pName = null;
            try
            {
                if (BitConverter.IsLittleEndian != true) { throw new PlatformNotSupportedException("This program relies on running as little endian"); }
                KH1FM_Toolkit.HashList.loadHashPairs();
                Console.Title = KH1ISOReader.program.ProductName + " " + KH1ISOReader.program.FileVersion + " [" + KH1ISOReader.program.CompanyName + "]";
                string oisoName = "kh.iso",
                    pAuthor = null,
                    tS;
                UInt32 pVersion = 0;
                for (int i = 0, argc = args.Length; i < argc; ++i)
                {
                    switch (args[i].ToLower())
                    {
                        case "-name":
                            pName = args[++i].Trim();
                            break;
                        case "-version":
                            UInt32.TryParse(args[++i].Trim(), out pVersion);
                            break;
                        case "-author":
                            pAuthor = args[++i].Trim();
                            break;
                        default:
                            if (oisoName.Length == 0 && args[i].EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
                            {
                                oisoName = args[i];
                            }
                            break;
                    }
                }
                if (oisoName.Length == 0) { oisoName = "kh.iso"; }

                if (pName == null)
                {
                    Console.Write("Patch filename [output]: ");
                    pName = Console.ReadLine().Trim();
                }
                if (pName.Length == 0) { pName = "output.kh1patch"; } else { pName += ".kh1patch"; }
                if (File.Exists(pName))
                {
                    Console.WriteLine("{0} already exists!", pName); pName = "";
                }
                using (BinaryWriter bw = new BinaryWriter(File.Open(pName, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
                using (MemoryStream ms = new MemoryStream())
                {
                    KH1FM_Toolkit.KH1ISOReader input;
                    try { input = new KH1FM_Toolkit.KH1ISOReader(oisoName); }
                    catch { input = null; }

                    bw.Write(0x5031484Bu);  //Magic
                    //Version
                    if (pVersion == 0) { pVersion = uintFromInput("Patch version", 1); }
                    bw.Write(pVersion);
                    //# of Files
                    int pFiles = (int)uintFromInput("# of files", 1);
                    bw.Write(pFiles);
                    //Author
                    if (pAuthor == null) { Console.Write("Author name []: "); pAuthor = Console.ReadLine().Trim(); }
                    bw.Write(pAuthor);

                    long dataOff = bw.BaseStream.Position + pFiles*16;
                    while (--pFiles >= 0)
                    {
                        pVersion = hashFromInput("\nFile to add", out tS, input);
                        UInt32 relinkU;
                        relinkU = hashFromInput("Relink to this hash? [blank=none]", out pAuthor, input, true);
                        if (relinkU != 0)
                        {
                            bw.Write(pVersion);
                            bw.Write(0);
                            bw.Write(0);
                            bw.Write(relinkU);
                            Console.WriteLine("Added relink of {0:X8} to {1:X8}", pVersion, relinkU);
                            continue;
                        }
                        if (!File.Exists("import/" + tS))
                        {
                            Console.Write("ERROR: Cannot find import/" + tS); ++pFiles; continue;
                        }

                        bool compress = false; KH1FM_Toolkit.IDXEntry entry;
                        if (input != null && (entry = input.idxEntries.Find(a => a.hash == pVersion)) != null)
                        {
                            if ((entry.flags & 0x1) == 1)
                            {
                                compress = true;
                            }
                        }
                        else
                        {
                            compress = yesnoInput("Compress file?");
                        }
                        byte[] bytes = File.ReadAllBytes("import/" + tS);
                        if (compress)
                        {
                            Console.Write("Compressing...");
                            try
                            {
                                bytes = KHCompress.KH1Compressor.compress(bytes);
                            }
                            catch (KHCompress.NotCompressableException e)
                            {
                                compress = false;
                                Console.WriteLine("\nCannot compress file: {0}", e.Message);
                            }
                        }
                        bw.Write(pVersion);
                        bw.Write(compress ? 1u : 0);
                        bw.Write((UInt32)(dataOff + ms.Position));
                        bw.Write((UInt32)(bytes.Length));
                        ms.Write(bytes, 0, bytes.Length);
                        Console.WriteLine("\nAdded {0:X8}", pVersion);
                    }
                    ms.Position = 0;
                    ms.WriteTo(bw.BaseStream);
                    Console.WriteLine("\n\nDone!");
                    if (input != null) { input.Dispose(); }
                }
            }
            catch (Exception e)
            {
                if (pName != null && pName.Length != 0) { File.Delete(pName); }
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("A fatal error has occured: {0}", e.Message);
                Console.ResetColor();
                Console.WriteLine("\nType: {0}\n{1}\n", e.GetType(), e.StackTrace);
            }
            Console.Write("Press enter to exit..."); Console.ReadLine();
        }
    }
}
