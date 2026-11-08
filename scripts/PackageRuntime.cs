using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

// Chunked Windows compression gives the native starter bounded memory use and
// byte-based progress without loading .NET to unpack .NET itself.
public static class KubRuntimePackage
{
    [DllImport("cabinet.dll", SetLastError=true)] static extern bool CreateCompressor(uint algorithm, IntPtr allocation, out IntPtr compressor);
    [DllImport("cabinet.dll", SetLastError=true)] static extern bool Compress(IntPtr compressor, byte[] input, UIntPtr inputSize, byte[] output, UIntPtr capacity, out UIntPtr size);
    [DllImport("cabinet.dll")] static extern bool CloseCompressor(IntPtr compressor);
    public static void Create(string directory, string destination)
    {
        var files = Directory.GetFiles(directory,"*",SearchOption.AllDirectories).OrderBy(p=>p,StringComparer.Ordinal).ToArray();
        if(!CreateCompressor(4,IntPtr.Zero,out var compressor)) throw new Win32Exception();
        try
        {
            using var writer = new BinaryWriter(File.Create(destination),Encoding.Unicode);
            writer.Write(files.Length); writer.Write(files.Sum(p=>new FileInfo(p).Length));
            var input = new byte[1048576]; var packed = new byte[2097152];
            foreach(var file in files)
            {
                string name = Path.GetRelativePath(directory,file);
                writer.Write(name.Length); writer.Write(Encoding.Unicode.GetBytes(name));
                long length = new FileInfo(file).Length; writer.Write(length); writer.Write((uint)((length+input.Length-1)/input.Length));
                using var source = File.OpenRead(file);
                int count;
                while((count=source.Read(input,0,input.Length))>0)
                {
                    if(!Compress(compressor,input,(UIntPtr)count,packed,(UIntPtr)packed.Length,out var bytes)) throw new Win32Exception();
                    writer.Write((uint)bytes); writer.Write(count); writer.Write(packed,0,(int)bytes);
                }
            }
        }
        finally { CloseCompressor(compressor); }
    }
}
