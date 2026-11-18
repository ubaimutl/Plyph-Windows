using System.Drawing;
using System.Runtime.InteropServices;

namespace Plyph.Windows.Interop;

public enum UiaSelectionStatus
{
    Unsupported,
    EmptySupported,
    Selected
}

/// <summary>
/// Provides zero-dependency COM interop with Windows UI Automation (UIAutomationCore.dll)
/// to read selected text and its screen bounding rectangle across Windows applications
/// (Word, Outlook, Edge, Chrome, Firefox, Notepad, Electron apps, etc.),
/// distinguishing between "supported control with empty selection" and "UIA unsupported".
/// </summary>
public static class UiAutomationInterop
{
    private const int UIA_TextPatternId = 10014;
    private const int UIA_TextPattern2Id = 10024;

    private static readonly Guid CLSID_CUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");

    [ComImport]
    [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements(IntPtr el1, IntPtr el2, out int areSame);
        void CompareRuntimeIds(IntPtr runtimeId1, IntPtr runtimeId2, out int areSame);
        void GetRootElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement root);
        void ElementFromHandle(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);
        void ElementFromPoint(NativeMethods.POINT pt, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);
        void GetFocusedElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement element);
    }

    [ComImport]
    [Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId(out IntPtr runtimeId);
        void FindFirst(int scope, IntPtr condition, out IntPtr found);
        void FindAll(int scope, IntPtr condition, out IntPtr found);
        void FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
        void FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
        void BuildUpdatedCache(IntPtr cacheRequest, out IntPtr updatedElement);
        void GetCurrentPropertyValue(int propertyId, out object value);
        void GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue, out object value);
        void GetCachedPropertyValue(int propertyId, out object value);
        void GetCachedPropertyValueEx(int propertyId, int ignoreDefaultValue, out object value);
        void GetCurrentPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        void GetCachedPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        void GetCurrentPattern(int patternId, [MarshalAs(UnmanagedType.IUnknown)] out object? patternObject);
    }

    [ComImport]
    [Guid("32EBA289-3583-42C9-9C59-3B6D9A1E9B6A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextPattern
    {
        void RangeFromPoint(NativeMethods.POINT pt, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange range);
        void RangeFromChild([MarshalAs(UnmanagedType.Interface)] IUIAutomationElement child, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange range);
        void GetSelection([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRangeArray? ranges);
        void GetVisibleRanges([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRangeArray? ranges);
        void get_DocumentRange([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange range);
        void get_SupportedTextSelection(out int supportedTextSelection);
    }

    [ComImport]
    [Guid("CE4AE76A-E717-4C98-81EA-47371D028EB6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRangeArray
    {
        void get_Length(out int length);
        void GetElement(int index, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange? element);
    }

    [ComImport]
    [Guid("A543CC6A-F4AE-494B-8239-C814481187A8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRange
    {
        void Clone([MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange clonedRange);
        void Compare([MarshalAs(UnmanagedType.Interface)] IUIAutomationTextRange range, out int areSame);
        void CompareEndpoints(int srcEndPoint, [MarshalAs(UnmanagedType.Interface)] IUIAutomationTextRange range, int targetEndPoint, out int compValue);
        void ExpandToEnclosingUnit(int textUnit);
        void FindAttribute(int attr, object val, int backward, [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange found);
        void FindText(
            [MarshalAs(UnmanagedType.BStr)] string text,
            int backward,
            int ignoreCase,
            [MarshalAs(UnmanagedType.Interface)] out IUIAutomationTextRange found);
        void GetAttributeValue(int attr, out object value);
        void GetBoundingRectangles([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_R8)] out double[]? boundingRectangles);
        void GetEnclosingElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement enclosingElement);
        void GetText(int maxLength, [MarshalAs(UnmanagedType.BStr)] out string? text);
    }

    public static bool TryGetSelectedText(out string selectedText, out Rectangle? selectionBounds) =>
        TryQuerySelectedText(out selectedText, out selectionBounds) == UiaSelectionStatus.Selected;

    public static UiaSelectionStatus TryQuerySelectedText(out string selectedText, out Rectangle? selectionBounds)
    {
        selectedText = "";
        selectionBounds = null;

        if (!OperatingSystem.IsWindows())
            return UiaSelectionStatus.Unsupported;

        var sawEmptySupported = false;

        try
        {
            var uiaType = Type.GetTypeFromCLSID(CLSID_CUIAutomation, throwOnError: false);
            if (uiaType == null)
                return UiaSelectionStatus.Unsupported;

            var uiaObj = Activator.CreateInstance(uiaType);
            if (uiaObj is not IUIAutomation automation)
                return UiaSelectionStatus.Unsupported;

            try
            {
                automation.GetFocusedElement(out var focused);
                if (focused != null)
                {
                    try
                    {
                        var st = TryExtractFromElement(focused, out selectedText, out selectionBounds);
                        if (st == UiaSelectionStatus.Selected)
                            return UiaSelectionStatus.Selected;
                        if (st == UiaSelectionStatus.EmptySupported)
                            sawEmptySupported = true;
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(focused);
                    }
                }

                if (NativeMethods.GetCursorPos(out var pt))
                {
                    automation.ElementFromPoint(pt, out var underCursor);
                    if (underCursor != null)
                    {
                        try
                        {
                            var st = TryExtractFromElement(underCursor, out selectedText, out selectionBounds);
                            if (st == UiaSelectionStatus.Selected)
                                return UiaSelectionStatus.Selected;
                            if (st == UiaSelectionStatus.EmptySupported)
                                sawEmptySupported = true;
                        }
                        finally
                        {
                            Marshal.FinalReleaseComObject(underCursor);
                        }
                    }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(automation);
            }
        }
        catch
        {
            // Applications that do not implement UIA TextPattern or run elevated may throw COMException
        }

        return sawEmptySupported ? UiaSelectionStatus.EmptySupported : UiaSelectionStatus.Unsupported;
    }

    private static UiaSelectionStatus TryExtractFromElement(
        IUIAutomationElement element,
        out string selectedText,
        out Rectangle? selectionBounds)
    {
        selectedText = "";
        selectionBounds = null;

        object? patternObj = null;
        try
        {
            element.GetCurrentPattern(UIA_TextPattern2Id, out patternObj);
        }
        catch
        {
            patternObj = null;
        }

        if (patternObj == null)
        {
            try
            {
                element.GetCurrentPattern(UIA_TextPatternId, out patternObj);
            }
            catch
            {
                patternObj = null;
            }
        }

        if (patternObj is not IUIAutomationTextPattern textPattern)
        {
            if (patternObj != null && Marshal.IsComObject(patternObj))
                Marshal.FinalReleaseComObject(patternObj);
            return UiaSelectionStatus.Unsupported;
        }

        try
        {
            textPattern.GetSelection(out var ranges);
            if (ranges == null)
                return UiaSelectionStatus.EmptySupported;

            try
            {
                ranges.get_Length(out var length);
                if (length <= 0)
                    return UiaSelectionStatus.EmptySupported;

                var sb = new System.Text.StringBuilder();
                Rectangle? unionBounds = null;
                Rectangle? lastBounds = null;

                for (var i = 0; i < length; i++)
                {
                    ranges.GetElement(i, out var range);
                    if (range == null)
                        continue;

                    try
                    {
                        range.GetText(262144, out var chunk);
                        if (!string.IsNullOrEmpty(chunk))
                            sb.Append(chunk);

                        try
                        {
                            range.GetBoundingRectangles(out var rects);
                            if (rects != null && rects.Length >= 4)
                            {
                                for (var r = 0; r + 3 < rects.Length; r += 4)
                                {
                                    var x = (int)Math.Round(rects[r]);
                                    var y = (int)Math.Round(rects[r + 1]);
                                    var w = (int)Math.Round(rects[r + 2]);
                                    var h = (int)Math.Round(rects[r + 3]);
                                    if (w > 0 && h > 0)
                                    {
                                        var rect = new Rectangle(x, y, w, h);
                                        unionBounds = unionBounds.HasValue
                                            ? Rectangle.Union(unionBounds.Value, rect)
                                            : rect;
                                        lastBounds = rect;
                                    }
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(range);
                    }
                }

                selectedText = sb.ToString();
                selectionBounds = lastBounds ?? unionBounds;
                return !string.IsNullOrWhiteSpace(selectedText)
                    ? UiaSelectionStatus.Selected
                    : UiaSelectionStatus.EmptySupported;
            }
            finally
            {
                Marshal.FinalReleaseComObject(ranges);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(textPattern);
        }
    }
}
