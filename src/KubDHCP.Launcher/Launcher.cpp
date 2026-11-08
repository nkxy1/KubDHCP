#define UNICODE
#define _UNICODE
#define WINVER 0x0A00
#undef _WIN32_WINNT
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>
#include <bcrypt.h>
#include <compressapi.h>
#include <dwmapi.h>
#include <string>
#include <vector>
#include <stdexcept>
#include <algorithm>
#include <memory>
#include "Version.h"

// The native entry point runs before any .NET or WinUI runtime is loaded.
namespace {
struct Handle {
    HANDLE h = INVALID_HANDLE_VALUE;
    explicit Handle(HANDLE value = INVALID_HANDLE_VALUE) : h(value) {}
    ~Handle() { if (h && h != INVALID_HANDLE_VALUE) CloseHandle(h); }
    Handle(const Handle&) = delete;
};
void Check(bool ok) { if (!ok) throw std::runtime_error("Windows error " + std::to_string(GetLastError())); }
std::wstring Env(const wchar_t* key) {
    DWORD n = GetEnvironmentVariableW(key, nullptr, 0); if (!n) return {};
    std::wstring s(n, 0); GetEnvironmentVariableW(key, s.data(), n); s.resize(n - 1); return s;
}
bool german, tui, silent;
Handle terminalOutput;
HWND splash;
DWORD mainThread;
volatile LONG cancelled, percent, displayed;
std::wstring cachePath, failure;
bool cacheHit = false;
unsigned fileCount = 0;
HFONT titleFont, bodyFont;
UINT splashDpi = 96;
const wchar_t* Text(const wchar_t* de, const wchar_t* en) { return german ? de : en; }
void ConsoleText(const std::wstring& s) {
    HANDLE output = terminalOutput.h != INVALID_HANDLE_VALUE ? terminalOutput.h : GetStdHandle(STD_OUTPUT_HANDLE); DWORD mode, written;
    if (GetConsoleMode(output, &mode)) WriteConsoleW(output, s.data(), (DWORD)s.size(), &written, nullptr);
    else {
        int n = WideCharToMultiByte(CP_UTF8, 0, s.data(), (int)s.size(), nullptr, 0, nullptr, nullptr);
        std::string utf8(n, 0); WideCharToMultiByte(CP_UTF8, 0, s.data(), (int)s.size(), utf8.data(), n, nullptr, nullptr);
        WriteFile(output, utf8.data(), n, &written, nullptr);
    }
}
void Progress(unsigned value) {
    if (InterlockedCompareExchange(&cancelled, 0, 0)) throw std::runtime_error("cancelled");
    LONG previous = InterlockedExchange(&percent, value);
    if (previous == (LONG)value && value != 0) return;
    if (tui) ConsoleText(L"\r" + std::wstring(Text(L"KubDHCP wird entpackt: ", L"Extracting KubDHCP: "))
        + L"[" + std::wstring(value/4,L'#') + std::wstring(25-value/4,L'-') + L"] " + std::to_wstring(value) + L"%   " + (value == 100 ? L"\r\n" : L""));
    else if (!silent) PostThreadMessageW(mainThread, WM_APP + 1, value, 0);
}
LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM w, LPARAM l) {
    if (message == WM_CLOSE) { InterlockedExchange(&cancelled, 1); return 0; }
    if (message == WM_ERASEBKGND) return 1;
    if (message == WM_PAINT) {
        PAINTSTRUCT ps; HDC dc = BeginPaint(window, &ps); RECT area; GetClientRect(window, &area);
        HBRUSH background = CreateSolidBrush(RGB(32,32,32)); FillRect(dc, &area, background); DeleteObject(background);
        SetGraphicsMode(dc, GM_ADVANCED);
        XFORM scale{(FLOAT)splashDpi/96,0,0,(FLOAT)splashDpi/96,0,0}; SetWorldTransform(dc,&scale);
        SetBkMode(dc, TRANSPARENT); SetTextColor(dc, RGB(245,245,245));
        SelectObject(dc, titleFont); RECT title{28,20,430,50};
        DrawTextW(dc, Text(L"KubDHCP wird vorbereitet", L"Preparing KubDHCP"), -1, &title, DT_SINGLELINE);
        SelectObject(dc, bodyFont); SetTextColor(dc, RGB(185,185,185)); RECT label{28,56,430,82};
        auto text = std::wstring(Text(L"Programmdateien entpacken", L"Extracting application files")) + L" · " + std::to_wstring(percent) + L"%";
        DrawTextW(dc, text.c_str(), -1, &label, DT_SINGLELINE);
        RECT track{28,98,432,106}; HBRUSH trackBrush = CreateSolidBrush(RGB(60,60,60)); FillRect(dc, &track, trackBrush); DeleteObject(trackBrush);
        track.right = track.left + (404 * percent / 100); HBRUSH fill = CreateSolidBrush(RGB(55,185,235)); FillRect(dc, &track, fill); DeleteObject(fill);
        EndPaint(window, &ps); return 0;
    }
    return DefWindowProcW(window, message, w, l);
}
void ShowProgress() {
    if (!displayed) { ShowWindow(splash, SW_SHOWNORMAL); UpdateWindow(splash); InterlockedExchange(&displayed, 1); }
    InvalidateRect(splash, nullptr, FALSE);
}
void Directory(const std::wstring& path) {
    int result = SHCreateDirectoryExW(nullptr, path.c_str(), nullptr);
    if (result != ERROR_SUCCESS && result != ERROR_ALREADY_EXISTS && result != ERROR_FILE_EXISTS) { SetLastError(result); Check(false); }
    DWORD attrs = GetFileAttributesW(path.c_str());
    Check(attrs != INVALID_FILE_ATTRIBUTES && (attrs & FILE_ATTRIBUTE_DIRECTORY) && !(attrs & FILE_ATTRIBUTE_REPARSE_POINT));
}
void Write(const std::wstring& path, const void* data, DWORD length) {
    Handle output(CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr)); Check(output.h != INVALID_HANDLE_VALUE);
    DWORD written; Check(WriteFile(output.h, data, length, &written, nullptr) && written == length); Check(FlushFileBuffers(output.h));
}
struct Reader {
    const BYTE* p; const BYTE* end;
    const BYTE* Take(size_t n) { Check(n <= (size_t)(end-p)); auto result=p; p+=n; return result; }
    template<class T> T Number() { T v; memcpy(&v, Take(sizeof(T)), sizeof(T)); return v; }
};
struct Chunk { const BYTE* data; DWORD packed, raw; };
struct Entry { std::wstring name; ULONGLONG size; std::vector<Chunk> chunks; };
DWORD WINAPI Extract(void*) {
    try {
        wchar_t self[32768]; Check(GetModuleFileNameW(nullptr, self, 32768) > 0);
        Handle input(CreateFileW(self, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr)); Check(input.h != INVALID_HANDLE_VALUE);
        LARGE_INTEGER size; Check(GetFileSizeEx(input.h, &size) && size.QuadPart > 48);
        Handle mapping(CreateFileMappingW(input.h, nullptr, PAGE_READONLY, 0, 0, nullptr)); Check(mapping.h != nullptr);
        auto memory = (const BYTE*)MapViewOfFile(mapping.h, FILE_MAP_READ, 0, 0, 0); Check(memory != nullptr);
        struct View { const void* p; ~View() { UnmapViewOfFile(p); } } view{memory};
        Reader footer{memory + size.QuadPart - 48, memory + size.QuadPart};
        auto offset = footer.Number<ULONGLONG>(); auto digest = footer.Take(32); Check(memcmp(footer.Take(8), "KUBPACK1", 8) == 0 && offset < (ULONGLONG)size.QuadPart - 48);
        wchar_t hex[65]; for (int i=0;i<32;i++) swprintf(hex+i*2, 3, L"%02x", digest[i]);
        auto base = Env(L"KUBDHCP_RUNTIME_CACHE");
        if (base.empty()) { PWSTR local; Check(SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local))); base=std::wstring(local)+L"\\KubDHCP.Runtime"; CoTaskMemFree(local); }
        Directory(base); cachePath=base+L"\\"+LAUNCHER_VERSION+L"-"+hex;
        // A file lock serializes starts and is released by Windows after a crash.
        Handle lock;
        while (lock.h == INVALID_HANDLE_VALUE) {
            lock.h=CreateFileW((cachePath+L".lock").c_str(), GENERIC_READ|GENERIC_WRITE, 0, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (lock.h != INVALID_HANDLE_VALUE) break;
            Check(GetLastError() == ERROR_SHARING_VIOLATION); Progress(0); Sleep(80);
        }
        Reader reader{memory+offset, memory+size.QuadPart-48};
        fileCount=reader.Number<DWORD>(); auto total=reader.Number<ULONGLONG>(); Check(fileCount > 0 && fileCount < 10000 && total > 0);
        std::vector<Entry> files; ULONGLONG sum=0;
        bool valid = GetFileAttributesW((cachePath+L"\\complete").c_str()) != INVALID_FILE_ATTRIBUTES;
        if (!valid) Progress(0);
        for (unsigned i=0;i<fileCount;i++) {
            DWORD chars=reader.Number<DWORD>(); Check(chars > 0 && chars < 240);
            Entry e; e.name.assign((const wchar_t*)reader.Take(chars*2), chars);
            Check(e.name.find(L"..") == std::wstring::npos && e.name.find_first_of(L":/\0",0,3) == std::wstring::npos && e.name[0] != L'\\');
            e.size=reader.Number<ULONGLONG>(); DWORD count=reader.Number<DWORD>(); Check(count <= 10000); ULONGLONG bytes=0;
            for (DWORD j=0;j<count;j++) { DWORD packed=reader.Number<DWORD>(), raw=reader.Number<DWORD>(); Check(raw > 0 && raw <= 1048576 && packed > 0); e.chunks.push_back({reader.Take(packed),packed,raw}); bytes+=raw; }
            Check(bytes == e.size); sum+=bytes;
            WIN32_FILE_ATTRIBUTE_DATA stat;
            valid = valid && GetFileAttributesExW((cachePath+L"\\"+e.name).c_str(), GetFileExInfoStandard, &stat)
                && !(stat.dwFileAttributes & (FILE_ATTRIBUTE_DIRECTORY|FILE_ATTRIBUTE_REPARSE_POINT)) && ((((ULONGLONG)stat.nFileSizeHigh<<32)|stat.nFileSizeLow) == e.size);
            files.push_back(std::move(e));
        }
        Check(reader.p == reader.end && sum == total);
        if (valid) { cacheHit=true; return 0; }
        Progress(0);
        BCRYPT_ALG_HANDLE algorithm; Check(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) == 0);
        Check((ULONGLONG)(size.QuadPart-48-offset) <= MAXDWORD);
        BCRYPT_HASH_HANDLE hash; Check(BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)==0);
        BYTE actual[32]; auto status=BCryptHashData(hash,(PUCHAR)(memory+offset),(ULONG)(size.QuadPart-48-offset),0);
        if(status==0) status=BCryptFinishHash(hash,actual,32,0);
        BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm,0);
        Check(status == 0 && memcmp(actual,digest,32)==0);
        Directory(cachePath);
        // An incomplete cache never has a completion marker.
        DeleteFileW((cachePath+L"\\complete").c_str());
        DECOMPRESSOR_HANDLE decompressor; Check(CreateDecompressor(COMPRESS_ALGORITHM_XPRESS_HUFF, nullptr, &decompressor));
        struct Decoder { DECOMPRESSOR_HANDLE h; ~Decoder() { CloseDecompressor(h); } } decoder{decompressor};
        std::vector<BYTE> buffer(1048576); ULONGLONG done=0;
        for (const auto& e:files) {
            std::wstring path=cachePath+L"\\"+e.name;
            // Check each parent independently; never follow a cache junction.
            for (size_t i=cachePath.size()+1;i<path.size();i++) if(path[i]==L'\\') Directory(path.substr(0,i));
            {
                Handle output(CreateFileW((path+L".partial").c_str(),GENERIC_WRITE,0,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL|FILE_FLAG_OPEN_REPARSE_POINT,nullptr)); Check(output.h != INVALID_HANDLE_VALUE);
                for (const auto& chunk:e.chunks) {
                    SIZE_T length; Check(Decompress(decompressor,(PVOID)chunk.data,chunk.packed,buffer.data(),chunk.raw,&length) && length==chunk.raw);
                    DWORD written; Check(WriteFile(output.h,buffer.data(),chunk.raw,&written,nullptr) && written==chunk.raw);
                    done+=chunk.raw; Progress((unsigned)(done*100/total));
                }
                Check(FlushFileBuffers(output.h));
            }
            Check(MoveFileExW((path+L".partial").c_str(),path.c_str(),MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH));
        }
        Write(cachePath+L"\\complete", digest, 32); Progress(100);
        return 0;
    } catch(const std::exception& error) {
        std::string message=error.what(); failure.assign(message.begin(),message.end()); return 1;
    }
}
BOOL WINAPI IgnoreControl(DWORD) { return TRUE; }
}

int wmain(int argc, wchar_t** argv) {
    mainThread=GetCurrentThreadId(); MSG message; PeekMessageW(&message,nullptr,0,0,PM_NOREMOVE);
    german=PRIMARYLANGID(GetUserDefaultUILanguage())==LANG_GERMAN;
    std::wstring testReport;
    for (int i=1;i<argc;i++) {
        if (!wcscmp(argv[i],L"--language=de")) german=true;
        if (!wcscmp(argv[i],L"--language=en")) german=false;
        if (!wcscmp(argv[i],L"--tui")) tui=true;
        if (!wcscmp(argv[i],L"--tui-test") || !wcscmp(argv[i],L"--transport-test")) silent=true;
        if (!wcscmp(argv[i],L"--launcher-test") && i+1<argc) testReport=argv[++i];
    }
    if (tui) {
        AttachConsole(ATTACH_PARENT_PROCESS);
        // In ConPTY, inherited standard handles can still point to the outer
        // process's redirected pipe. Open the actual calling console explicitly.
        terminalOutput.h=CreateFileW(L"CONOUT$",GENERIC_WRITE,FILE_SHARE_READ|FILE_SHARE_WRITE,nullptr,OPEN_EXISTING,0,nullptr);
        SetConsoleCtrlHandler(IgnoreControl,TRUE);
    }
    else FreeConsole();
    if (!tui && !silent) {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        splashDpi=GetDpiForSystem();
        WNDCLASSW wc{}; wc.lpfnWndProc=WindowProc; wc.hInstance=GetModuleHandleW(nullptr); wc.lpszClassName=L"KubDHCP.Preparing";
        wc.hCursor=LoadCursorW(nullptr,IDC_ARROW); wc.hIcon=LoadIconW(wc.hInstance,MAKEINTRESOURCEW(1)); RegisterClassW(&wc);
        titleFont=CreateFontW(-20,0,0,0,FW_SEMIBOLD,FALSE,FALSE,FALSE,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Segoe UI");
        bodyFont=CreateFontW(-14,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,0,0,CLEARTYPE_QUALITY,0,L"Segoe UI");
        RECT r{0,0,MulDiv(460,splashDpi,96),MulDiv(136,splashDpi,96)}; AdjustWindowRectExForDpi(&r,WS_CAPTION|WS_SYSMENU,FALSE,0,splashDpi);
        splash=CreateWindowExW(0,wc.lpszClassName,L"KubDHCP",WS_CAPTION|WS_SYSMENU,(GetSystemMetrics(SM_CXSCREEN)-(r.right-r.left))/2,(GetSystemMetrics(SM_CYSCREEN)-(r.bottom-r.top))/2,r.right-r.left,r.bottom-r.top,nullptr,nullptr,wc.hInstance,nullptr);
        BOOL dark=TRUE; DwmSetWindowAttribute(splash,20,&dark,sizeof(dark));
    }
    Handle worker(CreateThread(nullptr,0,Extract,nullptr,0,nullptr));
    if (!worker.h) return 1;
    while (MsgWaitForMultipleObjects(1,&worker.h,FALSE,INFINITE,QS_ALLINPUT) != WAIT_OBJECT_0) {
        while (PeekMessageW(&message,nullptr,0,0,PM_REMOVE)) {
            if (message.message==WM_APP+1) ShowProgress();
            else { TranslateMessage(&message); DispatchMessageW(&message); }
        }
    }
    // Drain the last progress update before reporting/closing the splash.
    while (PeekMessageW(&message,nullptr,0,0,PM_REMOVE)) { if(message.message==WM_APP+1) ShowProgress(); else {TranslateMessage(&message);DispatchMessageW(&message);} }
    DWORD result; GetExitCodeThread(worker.h,&result);
    if(splash) DestroyWindow(splash); if(titleFont) DeleteObject(titleFont); if(bodyFont) DeleteObject(bodyFont);
    if (result) {
        if (!cancelled) {
            auto text=std::wstring(Text(L"KubDHCP konnte nicht vorbereitet werden.\n",L"Could not prepare KubDHCP.\n"))+failure;
            if(tui||silent) ConsoleText(text+L"\r\n"); else MessageBoxW(nullptr,text.c_str(),L"KubDHCP",MB_OK|MB_ICONERROR);
        }
        return 1;
    }
    if (!testReport.empty()) {
        std::wstring report=L"PASS: cache="+std::wstring(cacheHit?L"hit":L"extracted")+L"; progress="+std::to_wstring(percent)+L"; window="+std::to_wstring(displayed)+L"; files="+std::to_wstring(fileCount)+L"; path="+cachePath;
        try { Write(testReport,report.data(),(DWORD)(report.size()*2)); return 0; } catch(...) { return 1; }
    }
    wchar_t self[32768]; GetModuleFileNameW(nullptr,self,32768);
    SetEnvironmentVariableW(L"KUBDHCP_LAUNCHER_PATH",self);
    auto exe=cachePath+L"\\KubDHCP.exe";
    // Preserve the caller's exact argument quoting and current directory.
    const wchar_t* tail=GetCommandLineW(); if(*tail==L'"') { tail++; while(*tail && *tail!=L'"') tail++; if(*tail) tail++; } else {while(*tail && *tail!=L' ' && *tail!=L'\t') tail++;}
    auto command=L"\""+exe+L"\""+tail;
    STARTUPINFOW start{}; start.cb=sizeof(start); PROCESS_INFORMATION child{};
    if(!CreateProcessW(exe.c_str(),command.data(),nullptr,nullptr,FALSE,tui?0:CREATE_NO_WINDOW,nullptr,nullptr,&start,&child)) {
        auto text=std::wstring(Text(L"KubDHCP konnte nicht gestartet werden. Windows-Fehler: ",L"Could not start KubDHCP. Windows error: "))+std::to_wstring(GetLastError());
        if(tui||silent) ConsoleText(text+L"\r\n"); else MessageBoxW(nullptr,text.c_str(),L"KubDHCP",MB_OK|MB_ICONERROR); return 1;
    }
    Handle process(child.hProcess), thread(child.hThread);
    WaitForSingleObject(process.h,INFINITE); GetExitCodeProcess(process.h,&result); return (int)result;
}
