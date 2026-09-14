using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using RoboMouse.Core.Logging;
using RoboMouse.Core.Network.Protocol;

namespace RoboMouse.Core.Input;

/// <summary>
/// A clipboard data object that presents remote files as "virtual files" (the CFSTR_FILEDESCRIPTORW
/// and CFSTR_FILECONTENTS formats Explorer and Office understand). Names and sizes are known up front;
/// each file's bytes are produced by a stream that pulls from the remote machine only when an
/// application actually reads it, so nothing transfers until you paste.
/// </summary>
public sealed class VirtualFileDataObject : System.Runtime.InteropServices.ComTypes.IDataObject
{
    /// <summary>Reads up to <c>length</c> bytes of entry <c>index</c> at <c>offset</c>; fewer bytes means end of file.</summary>
    public delegate byte[] ReadRange(int index, long offset, int length);

    private static readonly short CfFileDescriptor = (short)Native.RegisterClipboardFormat("FileGroupDescriptorW");
    private static readonly short CfFileContents = (short)Native.RegisterClipboardFormat("FileContents");
    private static readonly short CfPreferredDropEffect = (short)Native.RegisterClipboardFormat("Preferred DropEffect");

    private readonly IReadOnlyList<FileOfferEntry> _entries;
    private readonly ReadRange _read;

    public string OfferId { get; }

    public VirtualFileDataObject(string offerId, IReadOnlyList<FileOfferEntry> entries, ReadRange read)
    {
        OfferId = offerId;
        _entries = entries;
        _read = read;
    }

    #region IDataObject

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;

        if (format.cfFormat == CfFileDescriptor && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = BuildFileGroupDescriptor();
            return;
        }

        if (format.cfFormat == CfFileContents && (format.tymed & TYMED.TYMED_ISTREAM) != 0)
        {
            // lindex -1 means "no particular item"; some callers probe with it. Serve the first file.
            var index = format.lindex >= 0 ? format.lindex : FirstFileIndex();
            if (index >= 0 && index < _entries.Count && !_entries[index].IsDirectory)
            {
                var stream = new PullStream(_entries[index], index, _read);
                medium.tymed = TYMED.TYMED_ISTREAM;
                medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream));
                return;
            }
        }

        if (format.cfFormat == CfPreferredDropEffect && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
        {
            var handle = Native.GlobalAlloc(Native.GHND, 4);
            var ptr = Native.GlobalLock(handle);
            Marshal.WriteInt32(ptr, Native.DROPEFFECT_COPY);
            Native.GlobalUnlock(handle);
            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = handle;
            return;
        }

        SimpleLogger.Log("Files", $"GetData refused: {Describe(format)}");
        throw new COMException("Format not supported.", Native.DV_E_FORMATETC);
    }

    private int FirstFileIndex()
    {
        for (var i = 0; i < _entries.Count; i++)
            if (!_entries[i].IsDirectory)
                return i;
        return -1;
    }

    private static string Describe(FORMATETC format)
    {
        var name = new StringBuilder(256);
        var length = Native.GetClipboardFormatName((uint)(ushort)format.cfFormat, name, name.Capacity);
        var formatName = length > 0 ? name.ToString() : $"CF #{(ushort)format.cfFormat}";
        return $"{formatName} tymed={format.tymed} lindex={format.lindex} aspect={format.dwAspect}";
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium)
        => throw new COMException("Not implemented.", Native.E_NOTIMPL);

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.cfFormat == CfFileDescriptor && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
            return Native.S_OK;
        if (format.cfFormat == CfFileContents && (format.tymed & TYMED.TYMED_ISTREAM) != 0)
            return Native.S_OK;
        if (format.cfFormat == CfPreferredDropEffect && (format.tymed & TYMED.TYMED_HGLOBAL) != 0)
            return Native.S_OK;
        return Native.DV_E_FORMATETC;
    }

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return Native.DATA_S_SAMEFORMATETC;
    }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        // Explorer reports the drop effect it performed here. Accept and ignore.
        if (release)
            Native.ReleaseStgMedium(ref medium);
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET)
            throw new COMException("Not implemented.", Native.E_NOTIMPL);

        var formats = new[]
        {
            new FORMATETC { cfFormat = CfFileDescriptor, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL },
            new FORMATETC { cfFormat = CfFileContents, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_ISTREAM },
            new FORMATETC { cfFormat = CfPreferredDropEffect, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL }
        };
        Marshal.ThrowExceptionForHR(Native.SHCreateStdEnumFmtEtc((uint)formats.Length, formats, out var enumerator));
        return enumerator;
    }

    public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink sink, out int connection)
    {
        connection = 0;
        return Native.OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) => throw new COMException("Not supported.", Native.OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out IEnumSTATDATA? enumAdvise)
    {
        enumAdvise = null;
        return Native.OLE_E_ADVISENOTSUPPORTED;
    }

    #endregion

    /// <summary>Builds a FILEGROUPDESCRIPTORW in global memory: a count followed by one FILEDESCRIPTORW per entry.</summary>
    private IntPtr BuildFileGroupDescriptor()
    {
        var size = 4 + Native.FileDescriptorSize * _entries.Count;
        var handle = Native.GlobalAlloc(Native.GHND, (UIntPtr)size);
        var ptr = Native.GlobalLock(handle);
        try
        {
            Marshal.WriteInt32(ptr, _entries.Count);
            var p = ptr + 4;
            foreach (var entry in _entries)
            {
                var flags = Native.FD_ATTRIBUTES | Native.FD_WRITESTIME | Native.FD_PROGRESSUI | Native.FD_UNICODE;
                if (!entry.IsDirectory)
                    flags |= Native.FD_FILESIZE;

                Marshal.WriteInt32(p + 0, (int)flags);                                   // dwFlags
                // clsid (16), sizel (8), pointl (8) stay zero
                Marshal.WriteInt32(p + 36, entry.IsDirectory ? Native.FILE_ATTRIBUTE_DIRECTORY : Native.FILE_ATTRIBUTE_NORMAL);
                var fileTime = entry.LastWriteTimeUtc == default ? DateTime.UtcNow.ToFileTimeUtc() : entry.LastWriteTimeUtc.ToFileTimeUtc();
                Marshal.WriteInt64(p + 40, fileTime);                                     // ftCreationTime
                Marshal.WriteInt64(p + 48, fileTime);                                     // ftLastAccessTime
                Marshal.WriteInt64(p + 56, fileTime);                                     // ftLastWriteTime
                Marshal.WriteInt32(p + 64, (int)(entry.Size >> 32));                      // nFileSizeHigh
                Marshal.WriteInt32(p + 68, (int)(entry.Size & 0xFFFFFFFF));               // nFileSizeLow

                var name = entry.RelativePath;
                if (name.Length >= Native.MaxPath)
                    name = name[..(Native.MaxPath - 1)];
                var chars = name.ToCharArray();
                Marshal.Copy(chars, 0, p + 72, chars.Length);                             // cFileName (null-terminated by zeroed memory)

                p += Native.FileDescriptorSize;
            }
        }
        finally
        {
            Native.GlobalUnlock(handle);
        }
        return handle;
    }

    /// <summary>
    /// An IStream over one remote file. Explorer reads it sequentially in large blocks; each block is
    /// fetched from the remote on demand. Stat reports the size so Explorer can show progress.
    /// </summary>
    private sealed class PullStream : IStream
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

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            var total = 0;
            while (total < cb && _position < _entry.Size)
            {
                var want = (int)Math.Min(cb - total, _entry.Size - _position);
                var chunk = _read(_index, _position, want);
                if (chunk.Length == 0)
                    break;
                Buffer.BlockCopy(chunk, 0, pv, total, chunk.Length);
                total += chunk.Length;
                _position += chunk.Length;
            }

            if (pcbRead != IntPtr.Zero)
                Marshal.WriteInt32(pcbRead, total);
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            _position = dwOrigin switch
            {
                0 => dlibMove,                 // STREAM_SEEK_SET
                1 => _position + dlibMove,     // STREAM_SEEK_CUR
                2 => _entry.Size + dlibMove,   // STREAM_SEEK_END
                _ => throw new COMException("Bad origin.", Native.STG_E_INVALIDFUNCTION)
            };
            if (_position < 0)
                _position = 0;
            if (plibNewPosition != IntPtr.Zero)
                Marshal.WriteInt64(plibNewPosition, _position);
        }

        public void Stat(out STATSTG pstatstg, int grfStatFlag)
        {
            pstatstg = new STATSTG
            {
                type = 2, // STGTY_STREAM
                cbSize = _entry.Size,
                grfMode = 0, // STGM_READ
                pwcsName = (grfStatFlag & 1) != 0 ? string.Empty : _entry.RelativePath // STATFLAG_NONAME
            };
        }

        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("Read-only.", Native.STG_E_ACCESSDENIED);
        public void SetSize(long libNewSize) => throw new COMException("Read-only.", Native.STG_E_ACCESSDENIED);
        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new COMException("Not implemented.", Native.E_NOTIMPL);
        public void Commit(int grfCommitFlags) { }
        public void Revert() { }
        public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Not implemented.", Native.STG_E_INVALIDFUNCTION);
        public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("Not implemented.", Native.STG_E_INVALIDFUNCTION);
        public void Clone(out IStream ppstm) => throw new COMException("Not implemented.", Native.E_NOTIMPL);
    }

    #region Clipboard placement

    /// <summary>Places this object on the OLE clipboard. Must be called on an STA thread.</summary>
    public void SetOnClipboard()
    {
        Marshal.ThrowExceptionForHR(Native.OleSetClipboard(this));
    }

    /// <summary>True while this object is still what the clipboard holds.</summary>
    public bool IsOnClipboard() => Native.OleIsCurrentClipboard(this) == Native.S_OK;

    /// <summary>Empties the clipboard if this object is still on it. Must be called on an STA thread.</summary>
    public void RemoveFromClipboard()
    {
        if (IsOnClipboard())
            Native.OleSetClipboard(null);
    }

    #endregion

    private static class Native
    {
        public const int S_OK = 0;
        public const int E_NOTIMPL = unchecked((int)0x80004001);
        public const int DV_E_FORMATETC = unchecked((int)0x80040064);
        public const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
        public const int DATA_S_SAMEFORMATETC = 0x00040130;
        public const int STG_E_INVALIDFUNCTION = unchecked((int)0x80030001);
        public const int STG_E_ACCESSDENIED = unchecked((int)0x80030005);

        public const uint GHND = 0x0042; // GMEM_MOVEABLE | GMEM_ZEROINIT
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterClipboardFormat(string lpszFormat);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClipboardFormatName(uint format, StringBuilder lpszFormatName, int cchMaxCount);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("ole32.dll")]
        public static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);

        [DllImport("ole32.dll")]
        public static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject? pDataObj);

        [DllImport("ole32.dll")]
        public static extern int OleIsCurrentClipboard([MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject pDataObj);

        [DllImport("shell32.dll")]
        public static extern int SHCreateStdEnumFmtEtc(uint cfmt, [In] FORMATETC[] afmt, out IEnumFORMATETC ppenumFormatEtc);
    }
}
