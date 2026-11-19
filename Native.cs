using System;
using System.Runtime.InteropServices;

[assembly: System.Runtime.CompilerServices.DisableRuntimeMarshalling]

namespace BateriaTray;

// Declarações Win32 mínimas (P/Invoke gerado em tempo de compilação, sem marshalling em runtime).
internal static unsafe partial class N
{
    // ---------- user32 ----------
    [LibraryImport("user32.dll")] public static partial ushort RegisterClassExW(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll")] public static partial nint CreateWindowExW(uint ex, char* cls, char* name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [LibraryImport("user32.dll")] public static partial nint DefWindowProcW(nint h, uint m, nuint w, nint l);
    [LibraryImport("user32.dll")] public static partial int GetMessageW(MSG* m, nint h, uint min, uint max);
    [LibraryImport("user32.dll")] public static partial nint DispatchMessageW(MSG* m);
    [LibraryImport("user32.dll")] public static partial void PostQuitMessage(int code);
    [LibraryImport("user32.dll")] public static partial int DestroyWindow(nint h);
    [LibraryImport("user32.dll")] public static partial nuint SetTimer(nint h, nuint id, uint ms, nint fn);
    [LibraryImport("user32.dll")] public static partial int KillTimer(nint h, nuint id);
    [LibraryImport("user32.dll")] public static partial uint RegisterWindowMessageW(char* s);
    [LibraryImport("user32.dll")] public static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll")] public static partial int AppendMenuW(nint m, uint flags, nuint id, char* text);
    [LibraryImport("user32.dll")] public static partial int TrackPopupMenu(nint m, uint flags, int x, int y, int r, nint h, nint rc);
    [LibraryImport("user32.dll")] public static partial int DestroyMenu(nint m);
    [LibraryImport("user32.dll")] public static partial int SetForegroundWindow(nint h);
    [LibraryImport("user32.dll")] public static partial int GetCursorPos(POINT* p);
    [LibraryImport("user32.dll")] public static partial nint CreateIconIndirect(ICONINFO* i);
    [LibraryImport("user32.dll")] public static partial int DestroyIcon(nint h);
    [LibraryImport("user32.dll")] public static partial int GetSystemMetrics(int i);
    [LibraryImport("user32.dll")] public static partial int DrawTextW(nint dc, char* s, int n, RECT* r, uint f);

    // ---------- shell32 ----------
    [LibraryImport("shell32.dll")] public static partial int Shell_NotifyIconW(uint msg, NOTIFYICONDATAW* d);
    [LibraryImport("shell32.dll")] public static partial nint ShellExecuteW(nint hwnd, char* op, char* file, char* args, char* dir, int show);

    // ---------- kernel32 ----------
    [LibraryImport("kernel32.dll", SetLastError = true)] public static partial nint CreateFileW(char* name, uint access, uint share, nint sa, uint disp, uint flags, nint tmpl);
    [LibraryImport("kernel32.dll", SetLastError = true)] public static partial int WriteFile(nint h, byte* buf, uint n, uint* done, OVERLAPPED* ov);
    [LibraryImport("kernel32.dll", SetLastError = true)] public static partial int ReadFile(nint h, byte* buf, uint n, uint* done, OVERLAPPED* ov);
    [LibraryImport("kernel32.dll")] public static partial int GetOverlappedResult(nint h, OVERLAPPED* ov, uint* done, int wait);
    [LibraryImport("kernel32.dll")] public static partial int CancelIo(nint h);
    [LibraryImport("kernel32.dll")] public static partial int CloseHandle(nint h);
    [LibraryImport("kernel32.dll")] public static partial nint CreateEventW(nint sa, int manual, int initial, char* name);
    [LibraryImport("kernel32.dll")] public static partial int ResetEvent(nint h);
    [LibraryImport("kernel32.dll")] public static partial uint WaitForSingleObject(nint h, uint ms);
    [LibraryImport("kernel32.dll", SetLastError = true)] public static partial nint CreateMutexW(nint sa, int owner, char* name);
    [LibraryImport("kernel32.dll")] public static partial nint GetModuleHandleW(char* name);
    [LibraryImport("kernel32.dll")] public static partial nint GetCurrentProcess();
    [LibraryImport("kernel32.dll")] public static partial int SetProcessWorkingSetSize(nint p, nint min, nint max);
    [LibraryImport("kernel32.dll")] public static partial void GetLocalTime(SYSTEMTIME* t);

    // ---------- gdi32 ----------
    [LibraryImport("gdi32.dll")] public static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] public static partial nint CreateDIBSection(nint dc, BITMAPINFOHEADER* bi, uint usage, void** bits, nint sec, uint off);
    [LibraryImport("gdi32.dll")] public static partial nint CreateBitmap(int w, int h, uint planes, uint bpp, void* bits);
    [LibraryImport("gdi32.dll")] public static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] public static partial int DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] public static partial int DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] public static partial nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, char* face);
    [LibraryImport("gdi32.dll")] public static partial uint SetTextColor(nint dc, uint color);
    [LibraryImport("gdi32.dll")] public static partial int SetBkMode(nint dc, int mode);
    [LibraryImport("gdi32.dll")] public static partial int GdiFlush();

    // ---------- setupapi / hid ----------
    [LibraryImport("setupapi.dll")] public static partial nint SetupDiGetClassDevsW(Guid* g, char* enumerator, nint hwnd, uint flags);
    [LibraryImport("setupapi.dll")] public static partial int SetupDiEnumDeviceInterfaces(nint set, nint devInfo, Guid* g, uint index, SP_DEVICE_INTERFACE_DATA* d);
    [LibraryImport("setupapi.dll")] public static partial int SetupDiGetDeviceInterfaceDetailW(nint set, SP_DEVICE_INTERFACE_DATA* d, void* detail, uint size, uint* required, nint devInfo);
    [LibraryImport("setupapi.dll")] public static partial int SetupDiDestroyDeviceInfoList(nint set);
    [LibraryImport("hid.dll")] public static partial void HidD_GetHidGuid(Guid* g);
    [LibraryImport("hid.dll")] public static partial byte HidD_GetAttributes(nint h, HIDD_ATTRIBUTES* a);
    [LibraryImport("hid.dll")] public static partial byte HidD_GetPreparsedData(nint h, nint* pp);
    [LibraryImport("hid.dll")] public static partial byte HidD_FreePreparsedData(nint pp);
    [LibraryImport("hid.dll")] public static partial int HidP_GetCaps(nint pp, HIDP_CAPS* caps);
    [LibraryImport("hid.dll")] public static partial byte HidD_FlushQueue(nint h);
    [LibraryImport("hid.dll")] public static partial byte HidD_GetProductString(nint h, char* buf, uint bytes);
    [LibraryImport("hid.dll")] public static partial byte HidD_SetFeature(nint h, byte* buf, uint len);
    [LibraryImport("hid.dll")] public static partial byte HidD_GetFeature(nint h, byte* buf, uint len);
    [LibraryImport("hid.dll")] public static partial byte HidD_GetInputReport(nint h, byte* buf, uint len);
    [LibraryImport("hid.dll")] public static partial int HidP_GetValueCaps(int reportType, HIDP_VALUE_CAPS* caps, ushort* len, nint pp);
    [LibraryImport("hid.dll")] public static partial int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, uint* value, nint pp, byte* report, uint reportLen);

    // ---------- cfgmgr32 (propriedades de dispositivos: bateria Bluetooth) ----------
    [LibraryImport("cfgmgr32.dll")] public static partial uint CM_Get_Device_ID_List_SizeW(uint* len, char* filter, uint flags);
    [LibraryImport("cfgmgr32.dll")] public static partial uint CM_Get_Device_ID_ListW(char* filter, char* buf, uint len, uint flags);
    [LibraryImport("cfgmgr32.dll")] public static partial uint CM_Locate_DevNodeW(uint* devInst, char* id, uint flags);
    [LibraryImport("cfgmgr32.dll")] public static partial uint CM_Get_DevNode_PropertyW(uint devInst, DEVPROPKEY* key, uint* type, byte* buf, uint* size, uint flags);
    [LibraryImport("kernel32.dll")] public static partial void Sleep(uint ms);

    // ---------- advapi32 ----------
    [LibraryImport("advapi32.dll")] public static partial int RegOpenKeyExW(nint key, char* sub, uint opt, uint sam, nint* result);
    [LibraryImport("advapi32.dll")] public static partial int RegQueryValueExW(nint key, char* name, nint reserved, uint* type, byte* data, uint* size);
    [LibraryImport("advapi32.dll")] public static partial int RegSetValueExW(nint key, char* name, uint reserved, uint type, byte* data, uint size);
    [LibraryImport("advapi32.dll")] public static partial int RegDeleteValueW(nint key, char* name);
    [LibraryImport("advapi32.dll")] public static partial int RegCloseKey(nint key);
    [LibraryImport("advapi32.dll")] public static partial int RegCreateKeyExW(nint key, char* sub, uint reserved, char* cls, uint options, uint sam, nint sa, nint* result, uint* disposition);
    [LibraryImport("advapi32.dll")] public static partial int RegEnumKeyExW(nint key, uint index, char* name, uint* nameLen, nint reserved, char* cls, uint* clsLen, long* lastWrite);
    [LibraryImport("advapi32.dll")] public static partial int RegGetValueW(nint key, char* sub, char* value, uint flags, uint* type, void* data, uint* size);

    public static readonly nint HKEY_CURRENT_USER = unchecked((int)0x80000001);
    public const int ERROR_IO_PENDING = 997;
    public const int ERROR_ALREADY_EXISTS = 183;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WNDCLASSEXW
{
    public uint cbSize, style;
    public delegate* unmanaged<nint, uint, nuint, nint, nint> lpfnWndProc;
    public int cbClsExtra, cbWndExtra;
    public nint hInstance, hIcon, hCursor, hbrBackground;
    public char* lpszMenuName, lpszClassName;
    public nint hIconSm;
}

[StructLayout(LayoutKind.Sequential)] internal struct POINT { public int x, y; }
[StructLayout(LayoutKind.Sequential)] internal struct RECT { public int left, top, right, bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd; public uint message; public nuint wParam; public nint lParam;
    public uint time; public POINT pt; public uint lPrivate;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct NOTIFYICONDATAW
{
    public uint cbSize; public nint hWnd; public uint uID, uFlags, uCallbackMessage; public nint hIcon;
    public fixed char szTip[128];
    public uint dwState, dwStateMask;
    public fixed char szInfo[256];
    public uint uVersion;
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags; public Guid guidItem; public nint hBalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OVERLAPPED { public nuint Internal, InternalHigh; public uint Offset, OffsetHigh; public nint hEvent; }

[StructLayout(LayoutKind.Sequential)]
internal struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID, ProductID, VersionNumber; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HIDP_CAPS
{
    public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
    public fixed ushort Reserved[17];
    public fixed ushort Counts[10];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct HIDP_VALUE_CAPS // 72 bytes
{
    public ushort UsagePage; public byte ReportID, IsAlias;
    public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
    public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull, Reserved;
    public ushort BitSize, ReportCount;
    public fixed ushort Reserved2[5];
    public uint UnitsExp, Units;
    public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
    public ushort Usage, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public nuint Reserved; }

[StructLayout(LayoutKind.Sequential)]
internal struct ICONINFO { public int fIcon; public uint xHotspot, yHotspot; public nint hbmMask, hbmColor; }

[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
    public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROPKEY
{
    public Guid fmtid; public uint pid;
    public DEVPROPKEY(string g, uint p) { fmtid = new Guid(g); pid = p; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEMTIME { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Ms; }
