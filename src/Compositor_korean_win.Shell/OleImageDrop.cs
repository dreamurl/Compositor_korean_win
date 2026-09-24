using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using static Compositor_korean_win.Shell.Win32;

namespace Compositor_korean_win.Shell;

[GeneratedComInterface]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("00000122-0000-0000-C000-000000000046")]
internal partial interface IOleDropTarget
{
    [PreserveSig] int DragEnter(nint dataObject, uint keyState, PointL point, ref uint effect);
    [PreserveSig] int DragOver(uint keyState, PointL point, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop(nint dataObject, uint keyState, PointL point, ref uint effect);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PointL
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FormatEtc
{
    public short Format;
    public nint TargetDevice;
    public uint Aspect;
    public int Index;
    public uint Medium;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StorageMedium
{
    public uint Medium;
    public nint Value;
    public nint ReleaseOwner;
}

/// <summary>OLE images and file lists dropped by browsers, Office, Explorer, and other applications.</summary>
[GeneratedComClass]
[Guid("5D0D914B-6276-4D16-960F-3078FF304D57")]
internal sealed partial class OleImageDropTarget : IOleDropTarget
{
    private const short CfDib = 8;
    private const short CfHDrop = 15;
    private const uint Content = 1;
    private const uint HGlobal = 1;
    private const uint Copy = 1;
    private static readonly short Png = unchecked((short)RegisterClipboardFormatW("PNG"));

    private readonly Action<IReadOnlyList<string>, PointL> _files;
    private readonly Action<byte[], bool, PointL> _image;
    private bool _accepts;

    public OleImageDropTarget(Action<IReadOnlyList<string>, PointL> files,
                              Action<byte[], bool, PointL> image)
    {
        _files = files;
        _image = image;
    }

    public int DragEnter(nint dataObject, uint keyState, PointL point, ref uint effect)
    {
        _accepts = Supports(dataObject, CfHDrop) || Supports(dataObject, Png) || Supports(dataObject, CfDib);
        effect = _accepts ? effect & Copy : 0;
        return 0;
    }

    public int DragOver(uint keyState, PointL point, ref uint effect)
    {
        effect = _accepts ? effect & Copy : 0;
        return 0;
    }

    public int DragLeave()
    {
        _accepts = false;
        return 0;
    }

    public int Drop(nint dataObject, uint keyState, PointL point, ref uint effect)
    {
        effect = 0;
        try
        {
            if (Take(dataObject, CfHDrop) is StorageMedium files)
            {
                try { _files(Paths(files.Value), point); }
                finally { ReleaseStgMedium(in files); }
                effect = Copy;
            }
            else if (Take(dataObject, Png) is StorageMedium png)
            {
                try { _image(Bytes(png.Value), true, point); }
                finally { ReleaseStgMedium(in png); }
                effect = Copy;
            }
            else if (Take(dataObject, CfDib) is StorageMedium dib)
            {
                try { _image(Bytes(dib.Value), false, point); }
                finally { ReleaseStgMedium(in dib); }
                effect = Copy;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("OLE drop failed: " + exception);
        }
        _accepts = false;
        return 0;
    }

    private static unsafe bool Supports(nint dataObject, short format)
    {
        FormatEtc request = Request(format);
        nint* table = *(nint**)dataObject;
        var query = (delegate* unmanaged[Stdcall]<nint, FormatEtc*, int>)table[5];
        return query(dataObject, &request) >= 0;
    }

    private static unsafe StorageMedium? Take(nint dataObject, short format)
    {
        FormatEtc request = Request(format);
        StorageMedium medium = default;
        nint* table = *(nint**)dataObject;
        var get = (delegate* unmanaged[Stdcall]<nint, FormatEtc*, StorageMedium*, int>)table[3];
        return get(dataObject, &request, &medium) >= 0 ? medium : null;
    }

    private static FormatEtc Request(short format) => new()
    {
        Format = format,
        Aspect = Content,
        Index = -1,
        Medium = HGlobal,
    };

    private static byte[] Bytes(nint memory)
    {
        nint source = GlobalLock(memory);
        if (source == 0) return [];
        try
        {
            var bytes = new byte[checked((int)GlobalSize(memory))];
            Marshal.Copy(source, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { GlobalUnlock(memory); }
    }

    private static unsafe List<string> Paths(nint drop)
    {
        var paths = new List<string>();
        uint count = DragQueryFileW(drop, uint.MaxValue, null, 0);
        char* buffer = stackalloc char[4096];
        for (uint index = 0; index < count; index++)
        {
            uint length = DragQueryFileW(drop, index, buffer, 4096);
            if (length > 0) paths.Add(new string(buffer, 0, (int)length));
        }
        return paths;
    }
}

internal readonly record struct DropDestination(int? Tab, bool NewTab);
