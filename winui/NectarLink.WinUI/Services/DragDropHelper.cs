// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace NectarLink.WinUI.Services;

/// <summary>
/// Initiates native Win32 OLE drag-out (<c>DoDragDrop</c>) with <c>CF_HDROP</c> or
/// <c>CF_UNICODETEXT</c> so dragging a photo from Photos or an item from Shelf drops
/// cleanly into File Explorer, browsers, or chat apps.
/// </summary>
public static class DragDropHelper
{
    private const short CfUnicodeText = 13;
    private const short CfHDrop = 15;
    private const int DropEffectCopy = 1;
    private const int DragsourceSDrop = 0x00040100;
    private const int DragsourceSCancel = 0x00040101;
    private const int S_OK = 0;

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(
        IDataObject pDataObj,
        IDropSource pDropSource,
        uint dwOKEffects,
        out uint pdwEffect);

    [ComImport]
    [Guid("00000121-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropSource
    {
        [PreserveSig]
        int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool fEscapePressed, uint grfKeyState);
        [PreserveSig]
        int GiveFeedback(uint dwEffect);
    }

    public static void StartFileDrag(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        try
        {
            var data = new FileDropDataObject(paths);
            var src = new SimpleDropSource();
            _ = DoDragDrop(data, src, DropEffectCopy, out _);
        }
        catch
        {
            // Ignore drag failures
        }
    }

    public static void StartTextDrag(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var data = new TextDropDataObject(text);
            var src = new SimpleDropSource();
            _ = DoDragDrop(data, src, DropEffectCopy, out _);
        }
        catch { }
    }

    private sealed class SimpleDropSource : IDropSource
    {
        public int QueryContinueDrag(bool fEscapePressed, uint grfKeyState)
        {
            if (fEscapePressed) return DragsourceSCancel;
            if ((grfKeyState & 0x0001) == 0) return DragsourceSDrop; // MK_LBUTTON released
            return S_OK;
        }

        public int GiveFeedback(uint dwEffect) => 0x00040102; // DRAGDROP_S_USEDEFAULTCURSORS
    }

    private sealed class FileDropDataObject(IReadOnlyList<string> paths) : IDataObject
    {
        private readonly string[] _paths = [.. paths];

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = default;
            if (format.cfFormat != CfHDrop)
                Marshal.ThrowExceptionForHR(unchecked((int)0x80040064)); // DV_E_FORMATETC

            var joined = string.Join("\0", _paths) + "\0\0";
            byte[] strBytes = Encoding.Unicode.GetBytes(joined);
            const int dropFilesHeader = 20;
            IntPtr hGlobal = Marshal.AllocHGlobal(dropFilesHeader + strBytes.Length);
            Marshal.WriteInt32(hGlobal, 0, dropFilesHeader); // pFiles
            Marshal.WriteInt32(hGlobal, 4, 0); // pt.x
            Marshal.WriteInt32(hGlobal, 8, 0); // pt.y
            Marshal.WriteInt32(hGlobal, 12, 0); // fNC
            Marshal.WriteInt32(hGlobal, 16, 1); // fWide = TRUE
            Marshal.Copy(strBytes, 0, hGlobal + dropFilesHeader, strBytes.Length);

            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = hGlobal;
            medium.pUnkForRelease = null;
        }

        public int QueryGetData(ref FORMATETC format)
            => format.cfFormat == CfHDrop && (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? S_OK : unchecked((int)0x80040064);

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => new SingleFormatEnum(CfHDrop);
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { formatOut = formatIn; return 0x00040130; }
        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) { }
        public int EnumDAdvise(out IEnumSTATDATA? enumAdvise) { enumAdvise = null; return unchecked((int)0x80040003); }
    }

    private sealed class TextDropDataObject(string text) : IDataObject
    {
        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = default;
            if (format.cfFormat != CfUnicodeText)
                Marshal.ThrowExceptionForHR(unchecked((int)0x80040064));

            byte[] strBytes = Encoding.Unicode.GetBytes(text + "\0");
            IntPtr hGlobal = Marshal.AllocHGlobal(strBytes.Length);
            Marshal.Copy(strBytes, 0, hGlobal, strBytes.Length);

            medium.tymed = TYMED.TYMED_HGLOBAL;
            medium.unionmember = hGlobal;
            medium.pUnkForRelease = null;
        }

        public int QueryGetData(ref FORMATETC format)
            => format.cfFormat == CfUnicodeText && (format.tymed & TYMED.TYMED_HGLOBAL) != 0 ? S_OK : unchecked((int)0x80040064);

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => new SingleFormatEnum(CfUnicodeText);
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) { formatOut = formatIn; return 0x00040130; }
        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) { }
        public int EnumDAdvise(out IEnumSTATDATA? enumAdvise) { enumAdvise = null; return unchecked((int)0x80040003); }
    }

    private sealed class SingleFormatEnum(short cf) : IEnumFORMATETC
    {
        private int _pos;

        public int Next(int celt, FORMATETC[] rgelt, int[] pceltFetched)
        {
            if (_pos == 0 && celt > 0)
            {
                rgelt[0] = new FORMATETC
                {
                    cfFormat = cf,
                    dwAspect = DVASPECT.DVASPECT_CONTENT,
                    lindex = -1,
                    tymed = TYMED.TYMED_HGLOBAL
                };
                if (pceltFetched is { Length: > 0 }) pceltFetched[0] = 1;
                _pos = 1;
                return S_OK;
            }
            if (pceltFetched is { Length: > 0 }) pceltFetched[0] = 0;
            return 1;
        }

        public int Skip(int celt) { _pos += celt; return _pos <= 1 ? S_OK : 1; }
        public int Reset() { _pos = 0; return S_OK; }
        public void Clone(out IEnumFORMATETC newEnum) => newEnum = new SingleFormatEnum(cf);
    }
}
