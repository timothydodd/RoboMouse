using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core.Input;

/// <summary>
/// A clipboard data object that presents remote files as "virtual files" (the CFSTR_FILEDESCRIPTORW
/// and CFSTR_FILECONTENTS formats Explorer and Office understand). Names and sizes are known up front;
/// each file's bytes are produced by a stream that pulls from the remote machine only when an
/// application actually reads it, so nothing transfers until you paste.
/// </summary>
/// <remarks>
/// The COM interfaces are source-generated (<see cref="GeneratedComInterfaceAttribute"/>) so no runtime
/// COM interop is needed, which is what lets the app compile ahead of time. Every method returns an
/// HRESULT: Explorer probes many formats on every paste evaluation and a refusal must be an ordinary
/// return value, never an exception.
/// </remarks>
[GeneratedComClass]
public sealed unsafe partial class VirtualFileDataObject : VirtualFileDataObject.IDataObject
{
    /// <summary>Reads up to <c>length</c> bytes of entry <c>index</c> at <c>offset</c>; fewer bytes means end of file.</summary>
    public delegate byte[] ReadRange(int index, long offset, int length);

    private static readonly StrategyBasedComWrappers ComWrappers = new();

    private static readonly ushort CfFileDescriptor = (ushort)NativeMethods.RegisterClipboardFormatW("FileGroupDescriptorW");
    private static readonly ushort CfFileContents = (ushort)NativeMethods.RegisterClipboardFormatW("FileContents");
    private static readonly ushort CfPreferredDropEffect = (ushort)NativeMethods.RegisterClipboardFormatW("Preferred DropEffect");

    private readonly IReadOnlyList<FileOfferEntry> _entries;
    private readonly ReadRange _read;

    public string OfferId { get; }

    public VirtualFileDataObject(string offerId, IReadOnlyList<FileOfferEntry> entries, ReadRange read)
    {
        OfferId = offerId;
        _entries = entries;
        _read = read;
    }

    #region COM interfaces

    [StructLayout(LayoutKind.Sequential)]
    public struct FORMATETC
    {
        public ushort cfFormat;
        public nint ptd;
        public uint dwAspect;
        public int lindex;
        public uint tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STGMEDIUM
    {
        public uint tymed;
        public nint unionmember;
        public nint pUnkForRelease;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STATSTG
    {
        public nint pwcsName;
        public uint type;
        public ulong cbSize;
        public long mtime;
        public long ctime;
        public long atime;
        public uint grfMode;
        public uint grfLocksSupported;
        public Guid clsid;
        public uint grfStateBits;
        public uint reserved;
    }

    /// <summary>IDataObject with every interface pointer passed as a raw pointer; only the data paths are used.</summary>
    [GeneratedComInterface]
    [Guid("0000010E-0000-0000-C000-000000000046")]
    public partial interface IDataObject
    {
        [PreserveSig] int GetData(FORMATETC* format, STGMEDIUM* medium);
        [PreserveSig] int GetDataHere(FORMATETC* format, STGMEDIUM* medium);
        [PreserveSig] int QueryGetData(FORMATETC* format);
        [PreserveSig] int GetCanonicalFormatEtc(FORMATETC* formatIn, FORMATETC* formatOut);
        [PreserveSig] int SetData(FORMATETC* formatIn, STGMEDIUM* medium, int release);
        [PreserveSig] int EnumFormatEtc(uint direction, nint* enumerator);
        [PreserveSig] int DAdvise(FORMATETC* format, uint advf, nint sink, uint* connection);
        [PreserveSig] int DUnadvise(uint connection);
        [PreserveSig] int EnumDAdvise(nint* enumAdvise);
    }

    /// <summary>IStream (including the ISequentialStream methods it inherits), read side only.</summary>
    [GeneratedComInterface]
    [Guid("0000000C-0000-0000-C000-000000000046")]
    public partial interface IStream
    {
        [PreserveSig] int Read(byte* pv, uint cb, uint* pcbRead);
        [PreserveSig] int Write(byte* pv, uint cb, uint* pcbWritten);
        [PreserveSig] int Seek(long dlibMove, uint dwOrigin, ulong* plibNewPosition);
        [PreserveSig] int SetSize(ulong libNewSize);
        [PreserveSig] int CopyTo(nint pstm, ulong cb, ulong* pcbRead, ulong* pcbWritten);
        [PreserveSig] int Commit(uint grfCommitFlags);
        [PreserveSig] int Revert();
        [PreserveSig] int LockRegion(ulong libOffset, ulong cb, uint dwLockType);
        [PreserveSig] int UnlockRegion(ulong libOffset, ulong cb, uint dwLockType);
        [PreserveSig] int Stat(STATSTG* pstatstg, uint grfStatFlag);
        [PreserveSig] int Clone(nint* ppstm);
    }

    #endregion

    #region IDataObject

    public int GetData(FORMATETC* format, STGMEDIUM* medium)
    {
        if (format == null || medium == null)
            return Native.E_POINTER;
        *medium = default;

        if (format->cfFormat == CfFileDescriptor && (format->tymed & Native.TYMED_HGLOBAL) != 0)
        {
            var descriptor = BuildFileGroupDescriptor();
            if (descriptor == 0)
                return Native.E_OUTOFMEMORY;
            medium->tymed = Native.TYMED_HGLOBAL;
            medium->unionmember = descriptor;
            return Native.S_OK;
        }

        if (format->cfFormat == CfFileContents && (format->tymed & Native.TYMED_ISTREAM) != 0)
        {
            // lindex -1 means "no particular item"; some callers probe with it. Serve the first file.
            var index = format->lindex >= 0 ? format->lindex : FirstFileIndex();
            if (index >= 0 && index < _entries.Count && !_entries[index].IsDirectory)
            {
                var stream = new PullStream(_entries[index], index, _read);
                medium->tymed = Native.TYMED_ISTREAM;
                medium->unionmember = GetInterfacePointer(stream, in Native.IID_IStream);
                return Native.S_OK;
            }
        }

        if (format->cfFormat == CfPreferredDropEffect && (format->tymed & Native.TYMED_HGLOBAL) != 0)
        {
            var handle = NativeMethods.GlobalAlloc(NativeMethods.GHND, 4);
            var ptr = handle == 0 ? 0 : NativeMethods.GlobalLock(handle);
            if (ptr == 0)
            {
                if (handle != 0)
                    NativeMethods.GlobalFree(handle);
                return Native.E_OUTOFMEMORY;
            }
            *(int*)ptr = Native.DROPEFFECT_COPY;
            NativeMethods.GlobalUnlock(handle);
            medium->tymed = Native.TYMED_HGLOBAL;
            medium->unionmember = handle;
            return Native.S_OK;
        }

        // Explorer probes many formats (Shell IDList Array, Net Resource, ...); refusing is normal.
        return Native.DV_E_FORMATETC;
    }

    private int FirstFileIndex()
    {
        for (var i = 0; i < _entries.Count; i++)
            if (!_entries[i].IsDirectory)
                return i;
        return -1;
    }

    public int GetDataHere(FORMATETC* format, STGMEDIUM* medium) => Native.E_NOTIMPL;

    public int QueryGetData(FORMATETC* format)
    {
        if (format == null)
            return Native.E_POINTER;
        if (format->cfFormat == CfFileDescriptor && (format->tymed & Native.TYMED_HGLOBAL) != 0)
            return Native.S_OK;
        if (format->cfFormat == CfFileContents && (format->tymed & Native.TYMED_ISTREAM) != 0)
            return Native.S_OK;
        if (format->cfFormat == CfPreferredDropEffect && (format->tymed & Native.TYMED_HGLOBAL) != 0)
            return Native.S_OK;
        return Native.DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(FORMATETC* formatIn, FORMATETC* formatOut)
    {
        if (formatIn == null || formatOut == null)
            return Native.E_POINTER;
        *formatOut = *formatIn;
        formatOut->ptd = 0;
        return Native.DATA_S_SAMEFORMATETC;
    }

    public int SetData(FORMATETC* formatIn, STGMEDIUM* medium, int release)
    {
        // Explorer reports the drop effect it performed here. Accept and ignore.
        if (release != 0 && medium != null)
            Native.ReleaseStgMedium(medium);
        return Native.S_OK;
    }

    public int EnumFormatEtc(uint direction, nint* enumerator)
    {
        if (enumerator == null)
            return Native.E_POINTER;
        *enumerator = 0;
        if (direction != Native.DATADIR_GET)
            return Native.E_NOTIMPL;

        var formats = stackalloc FORMATETC[3];
        formats[0] = new FORMATETC { cfFormat = CfFileDescriptor, dwAspect = Native.DVASPECT_CONTENT, lindex = -1, tymed = Native.TYMED_HGLOBAL };
        formats[1] = new FORMATETC { cfFormat = CfFileContents, dwAspect = Native.DVASPECT_CONTENT, lindex = -1, tymed = Native.TYMED_ISTREAM };
        formats[2] = new FORMATETC { cfFormat = CfPreferredDropEffect, dwAspect = Native.DVASPECT_CONTENT, lindex = -1, tymed = Native.TYMED_HGLOBAL };
        return Native.SHCreateStdEnumFmtEtc(3, formats, enumerator);
    }

    public int DAdvise(FORMATETC* format, uint advf, nint sink, uint* connection)
    {
        if (connection != null)
            *connection = 0;
        return Native.OLE_E_ADVISENOTSUPPORTED;
    }

    public int DUnadvise(uint connection) => Native.OLE_E_ADVISENOTSUPPORTED;

    public int EnumDAdvise(nint* enumAdvise)
    {
        if (enumAdvise != null)
            *enumAdvise = 0;
        return Native.OLE_E_ADVISENOTSUPPORTED;
    }

    #endregion

    /// <summary>
    /// Builds a FILEGROUPDESCRIPTORW in global memory: a count followed by one FILEDESCRIPTORW per entry.
    /// Returns 0 when the memory could not be allocated.
    /// </summary>
    private nint BuildFileGroupDescriptor()
    {
        var size = 4 + (long)Native.FileDescriptorSize * _entries.Count;
        var handle = NativeMethods.GlobalAlloc(NativeMethods.GHND, (nuint)size);
        if (handle == 0)
            return 0;
        var ptr = (byte*)NativeMethods.GlobalLock(handle);
        if (ptr == null)
        {
            NativeMethods.GlobalFree(handle);
            return 0;
        }
        try
        {
            *(int*)ptr = _entries.Count;
            var p = ptr + 4;
            foreach (var entry in _entries)
            {
                var flags = Native.FD_ATTRIBUTES | Native.FD_WRITESTIME | Native.FD_PROGRESSUI | Native.FD_UNICODE;
                if (!entry.IsDirectory)
                    flags |= Native.FD_FILESIZE;

                *(uint*)(p + 0) = flags;                                                   // dwFlags
                // clsid (16), sizel (8), pointl (8) stay zero
                *(int*)(p + 36) = entry.IsDirectory ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL;
                var fileTime = entry.LastWriteTimeUtc == default ? DateTime.UtcNow.ToFileTimeUtc() : entry.LastWriteTimeUtc.ToFileTimeUtc();
                *(long*)(p + 40) = fileTime;                                               // ftCreationTime
                *(long*)(p + 48) = fileTime;                                               // ftLastAccessTime
                *(long*)(p + 56) = fileTime;                                               // ftLastWriteTime
                *(int*)(p + 64) = (int)(entry.Size >> 32);                                 // nFileSizeHigh
                *(int*)(p + 68) = (int)(entry.Size & 0xFFFFFFFF);                          // nFileSizeLow

                var name = entry.RelativePath;
                if (name.Length >= Native.MaxPath)
                    name = name[..(Native.MaxPath - 1)];
                name.AsSpan().CopyTo(new Span<char>(p + 72, Native.MaxPath));              // cFileName (null-terminated by zeroed memory)

                p += Native.FileDescriptorSize;
            }
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
        return handle;
    }

    /// <summary>
    /// An IStream over one remote file. Explorer reads it sequentially in large blocks; each block is
    /// fetched from the remote on demand. Stat reports the size so Explorer can show progress.
    /// </summary>
    [GeneratedComClass]
    private sealed partial class PullStream : IStream
    {
        private readonly FileOfferEntry _entry;
        private readonly int _index;
        private readonly ReadRange _read;
        private long _position;

        public PullStream(FileOfferEntry entry, int index, ReadRange read)
        {
            _entry = entry;
            _index = index;
            _read = read;
        }

        public int Read(byte* pv, uint cb, uint* pcbRead)
        {
            var total = 0;
            try
            {
                while (total < cb && _position < _entry.Size)
                {
                    var want = (int)Math.Min(cb - (uint)total, _entry.Size - _position);
                    var chunk = _read(_index, _position, want);
                    if (chunk.Length == 0)
                        break;
                    chunk.AsSpan().CopyTo(new Span<byte>(pv + total, chunk.Length));
                    total += chunk.Length;
                    _position += chunk.Length;
                }
            }
            catch (Exception ex)
            {
                Logging.SimpleLogger.Log("Files", $"Read of {_entry.RelativePath} at {_position} failed: {ex}");
                if (pcbRead != null)
                    *pcbRead = (uint)total;
                return Native.STG_E_READFAULT;
            }

            // Always S_OK: pcbRead carries the count, and a short read at end of file is normal. Explorer's
            // copy engine treats any other HRESULT (S_FALSE included) as a read error.
            if (pcbRead != null)
                *pcbRead = (uint)total;
            return Native.S_OK;
        }

        public int Seek(long dlibMove, uint dwOrigin, ulong* plibNewPosition)
        {
            switch (dwOrigin)
            {
                case 0: _position = dlibMove; break;               // STREAM_SEEK_SET
                case 1: _position += dlibMove; break;              // STREAM_SEEK_CUR
                case 2: _position = _entry.Size + dlibMove; break; // STREAM_SEEK_END
                default: return Native.STG_E_INVALIDFUNCTION;
            }
            if (_position < 0)
                _position = 0;
            if (plibNewPosition != null)
                *plibNewPosition = (ulong)_position;
            return Native.S_OK;
        }

        public int Stat(STATSTG* pstatstg, uint grfStatFlag)
        {
            if (pstatstg == null)
                return Native.E_POINTER;
            *pstatstg = new STATSTG
            {
                type = 2, // STGTY_STREAM
                cbSize = (ulong)_entry.Size,
                grfMode = 0, // STGM_READ
                pwcsName = (grfStatFlag & 1) != 0 ? 0 : Marshal.StringToCoTaskMemUni(_entry.RelativePath) // STATFLAG_NONAME
            };
            return Native.S_OK;
        }

        public int Write(byte* pv, uint cb, uint* pcbWritten) => Native.STG_E_ACCESSDENIED;
        public int SetSize(ulong libNewSize) => Native.STG_E_ACCESSDENIED;
        public int CopyTo(nint pstm, ulong cb, ulong* pcbRead, ulong* pcbWritten) => Native.E_NOTIMPL;
        public int Commit(uint grfCommitFlags) => Native.S_OK;
        public int Revert() => Native.S_OK;
        public int LockRegion(ulong libOffset, ulong cb, uint dwLockType) => Native.STG_E_INVALIDFUNCTION;
        public int UnlockRegion(ulong libOffset, ulong cb, uint dwLockType) => Native.STG_E_INVALIDFUNCTION;
        public int Clone(nint* ppstm)
        {
            if (ppstm != null)
                *ppstm = 0;
            return Native.E_NOTIMPL;
        }
    }

    #region Clipboard placement

    /// <summary>Places this object on the OLE clipboard. Must be called on an STA thread.</summary>
    public void SetOnClipboard()
    {
        var punk = GetInterfacePointer(this, in Native.IID_IDataObject);
        try
        {
            Marshal.ThrowExceptionForHR(Native.OleSetClipboard(punk));
        }
        finally
        {
            Marshal.Release(punk);
        }
    }

    /// <summary>True while this object is still what the clipboard holds.</summary>
    public bool IsOnClipboard()
    {
        var punk = GetInterfacePointer(this, in Native.IID_IDataObject);
        try
        {
            return Native.OleIsCurrentClipboard(punk) == Native.S_OK;
        }
        finally
        {
            Marshal.Release(punk);
        }
    }

    /// <summary>Empties the clipboard if this object is still on it. Must be called on an STA thread.</summary>
    public void RemoveFromClipboard()
    {
        if (IsOnClipboard())
            Native.OleSetClipboard(0);
    }

    /// <summary>A COM pointer for <paramref name="instance"/> of the given interface; the caller releases it.</summary>
    private static nint GetInterfacePointer(object instance, in Guid iid)
    {
        var unknown = ComWrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
        try
        {
            var hr = Marshal.QueryInterface(unknown, in iid, out var result);
            Marshal.ThrowExceptionForHR(hr);
            return result;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    #endregion

    private static partial class Native
    {
        public const int S_OK = 0;
        public const int S_FALSE = 1;
        public const int E_NOTIMPL = unchecked((int)0x80004001);
        public const int E_POINTER = unchecked((int)0x80004003);
        public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
        public const int DV_E_FORMATETC = unchecked((int)0x80040064);
        public const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
        public const int DATA_S_SAMEFORMATETC = 0x00040130;
        public const int STG_E_INVALIDFUNCTION = unchecked((int)0x80030001);
        public const int STG_E_ACCESSDENIED = unchecked((int)0x80030005);
        public const int STG_E_READFAULT = unchecked((int)0x8003001E);

        public const uint TYMED_HGLOBAL = 1;
        public const uint TYMED_ISTREAM = 4;
        public const uint DVASPECT_CONTENT = 1;
        public const uint DATADIR_GET = 1;

        public const int DROPEFFECT_COPY = 1;

        public const uint FD_ATTRIBUTES = 0x00000004;
        public const uint FD_WRITESTIME = 0x00000020;
        public const uint FD_FILESIZE = 0x00000040;
        public const uint FD_PROGRESSUI = 0x00004000;
        public const uint FD_UNICODE = 0x80000000;

        public const int FILE_ATTRIBUTE_DIRECTORY = 0x10;
        public const int FILE_ATTRIBUTE_NORMAL = 0x80;

        public const int MaxPath = 260;
        public const int FileDescriptorSize = 4 + 16 + 8 + 8 + 4 + 8 + 8 + 8 + 4 + 4 + MaxPath * 2; // 592

        public static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");
        public static readonly Guid IID_IStream = new("0000000C-0000-0000-C000-000000000046");

        [LibraryImport("ole32.dll")]
        public static partial void ReleaseStgMedium(STGMEDIUM* pmedium);

        [LibraryImport("ole32.dll")]
        public static partial int OleSetClipboard(nint pDataObj);

        [LibraryImport("ole32.dll")]
        public static partial int OleIsCurrentClipboard(nint pDataObj);

        [LibraryImport("shell32.dll")]
        public static partial int SHCreateStdEnumFmtEtc(uint cfmt, FORMATETC* afmt, nint* ppenumFormatEtc);
    }
}
