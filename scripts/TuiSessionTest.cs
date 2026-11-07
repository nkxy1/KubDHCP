using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class KubTuiSessionTest
{
    [StructLayout(LayoutKind.Sequential)] struct Coord { public short X,Y; public Coord(short x,short y) {X=x;Y=y;} }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct Startup
    {
        public int Size; public string Reserved, Desktop, Title;
        public uint X,Y,XSize,YSize,XChars,YChars,Fill,Flags;
        public short Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct StartupEx { public Startup Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process,Thread; public uint Id,ThreadId; }
    static void Check(bool result) { if(!result) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    public static void Run(string powershell, string exe)
    {
        IntPtr inRead=IntPtr.Zero,inWrite=IntPtr.Zero,outRead=IntPtr.Zero,outWrite=IntPtr.Zero,pty=IntPtr.Zero,list=IntPtr.Zero;
        ProcessInfo process=new ProcessInfo(); var captured=new StringBuilder(); object sync=new object(); Thread reader=null;
        string marker=Path.Combine(Path.GetDirectoryName(exe),"tui-shell-"+Guid.NewGuid().ToString("N")+".txt");
        try
        {
            Check(CreatePipe(out inRead,out inWrite,IntPtr.Zero,0)); Check(CreatePipe(out outRead,out outWrite,IntPtr.Zero,0));
            int hr=CreatePseudoConsole(new Coord(140,50),inRead,outWrite,0,out pty); if(hr!=0) Marshal.ThrowExceptionForHR(hr);
            CloseHandle(inRead); inRead=IntPtr.Zero; CloseHandle(outWrite); outWrite=IntPtr.Zero;
            IntPtr bytes=IntPtr.Zero; InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref bytes);
            list=Marshal.AllocHGlobal(bytes); Check(InitializeProcThreadAttributeList(list,1,0,ref bytes));
            Check(UpdateProcThreadAttribute(list,0,new IntPtr(0x20016),pty,new IntPtr(IntPtr.Size),IntPtr.Zero,IntPtr.Zero));
            var start=new StartupEx(); start.Startup.Size=Marshal.SizeOf<StartupEx>(); start.Attributes=list;
            // EncodedCommand avoids shell quoting and emits a marker only after the
            // direct EXE invocation returns. A GUI-subsystem regression returns early.
            string script="& '"+exe.Replace("'","''")+"' --tui; [IO.File]::WriteAllText('"+marker.Replace("'","''")+"','RETURNED_TO_POWERSHELL')";
            var command=new StringBuilder("\""+powershell+"\" -NoLogo -NoProfile -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            Check(CreateProcess(null,command,IntPtr.Zero,IntPtr.Zero,false,0x80000,IntPtr.Zero,null,ref start,out process));
            reader=new Thread(()=>
            {
                var buffer=new byte[16384]; var chars=new char[16384]; var decoder=Encoding.UTF8.GetDecoder();
                while(ReadFile(outRead,buffer,(uint)buffer.Length,out uint count,IntPtr.Zero)&&count>0)
                { int n=decoder.GetChars(buffer,0,(int)count,chars,0); lock(sync) captured.Append(chars,0,n); }
            }); reader.IsBackground=true; reader.Start();
            string Output() {lock(sync) return captured.ToString();}
            void Until(Func<string,bool> condition,string error)
            {
                var deadline=Environment.TickCount64+30000;
                while(Environment.TickCount64<deadline) {if(condition(Output())) return; Thread.Sleep(40);}
                string snapshot=Output(); throw new InvalidOperationException(error+"\n"+snapshot.Substring(Math.Max(0,snapshot.Length-1500)));
            }
            void Send(string text) {var data=Encoding.UTF8.GetBytes(text); Check(WriteFile(inWrite,data,(uint)data.Length,out _,IntPtr.Zero));}
            Until(s=>s.Contains("DHCP-Konfiguration"),"TUI did not render inside PowerShell/ConPTY");
            Thread.Sleep(400);
            if(File.Exists(marker)) throw new InvalidOperationException("PowerShell returned before TUI exit");
            // Real SGR mouse click/release on the Monitor button at columns 133..137.
            Send("\x1b[<0;135;4M\x1b[<0;135;4m");
            Until(s=>s.Contains("Netzwerk-Monitor"),"Remote mouse click did not open monitor");
            Send("\t\x1b[Z\x1bOP"); Until(s=>s.Contains("Bedienung"),"Remote F1 key did not open help");
            Send("\x1b"); Thread.Sleep(300);
            hr=ResizePseudoConsole(pty,new Coord(100,35)); if(hr!=0) Marshal.ThrowExceptionForHR(hr);
            Thread.Sleep(300); Send("\x11");
            Until(s=>File.Exists(marker),"PowerShell did not resume after Ctrl+Q");
            if(WaitForSingleObject(process.Process,10000)!=0) throw new InvalidOperationException("PowerShell did not exit");
            Until(s=>s.Contains("1006l"),"Terminal mouse reporting was not disabled on exit");
            Console.WriteLine("PASS: direct --tui in PowerShell/ConPTY; shell waits; in-session Unicode output; remote mouse click; Tab/Shift+Tab; F1; resize; Ctrl+Q; restored terminal and returned shell.");
        }
        finally
        {
            if(process.Process!=IntPtr.Zero) { if(WaitForSingleObject(process.Process,0)!=0) TerminateProcess(process.Process,1); CloseHandle(process.Process); }
            if(process.Thread!=IntPtr.Zero) CloseHandle(process.Thread);
            if(inWrite!=IntPtr.Zero) CloseHandle(inWrite);
            if(pty!=IntPtr.Zero) ClosePseudoConsole(pty);
            if(reader!=null) reader.Join(2000);
            if(list!=IntPtr.Zero) {DeleteProcThreadAttributeList(list); Marshal.FreeHGlobal(list);}
            if(inRead!=IntPtr.Zero) CloseHandle(inRead); if(outRead!=IntPtr.Zero) CloseHandle(outRead); if(outWrite!=IntPtr.Zero) CloseHandle(outWrite);
            if(File.Exists(marker)) File.Delete(marker);
        }
    }
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool CreatePipe(out IntPtr read,out IntPtr write,IntPtr attrs,uint size);
    [DllImport("kernel32.dll")] static extern int CreatePseudoConsole(Coord size,IntPtr input,IntPtr output,uint flags,out IntPtr console);
    [DllImport("kernel32.dll")] static extern int ResizePseudoConsole(IntPtr console,Coord size);
    [DllImport("kernel32.dll")] static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,uint flags,ref IntPtr size);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateProcessW")] static extern bool CreateProcess(string app,StringBuilder command,IntPtr processAttrs,IntPtr threadAttrs,bool inherit,uint flags,IntPtr environment,string directory,ref StartupEx startup,out ProcessInfo process);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool ReadFile(IntPtr file,byte[] bytes,uint size,out uint read,IntPtr overlap);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool WriteFile(IntPtr file,byte[] bytes,uint size,out uint written,IntPtr overlap);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
