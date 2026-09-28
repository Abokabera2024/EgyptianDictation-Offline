using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EgyptianDictation.WordAddIn;

[Guid("289E9AF1-4973-11D1-AE81-00A0C90F26F4")]
public enum ExtConnectMode
{
    AfterStartup = 0,
    Startup = 1,
    External = 2,
    CommandLine = 3,
    Solution = 4,
    UISetup = 5
}

[Guid("289E9AF2-4973-11D1-AE81-00A0C90F26F4")]
public enum ExtDisconnectMode
{
    HostShutdown = 0,
    UserClosed = 1,
    UiSetupComplete = 2,
    SolutionClosed = 3
}

[ComImport]
[Guid("B65AD801-ABAF-11D0-BB8B-00A0C90F2744")]
[TypeLibType((TypeLibTypeFlags)4160)]
public interface IDTExtensibility2
{
    [DispId(1)]
    [MethodImpl(MethodImplOptions.InternalCall)]
    void OnConnection([MarshalAs(UnmanagedType.IDispatch), In] object application, [In] ExtConnectMode connectMode,
        [MarshalAs(UnmanagedType.IDispatch), In] object addInInst,
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT), In] ref Array custom);
    [DispId(2)]
    [MethodImpl(MethodImplOptions.InternalCall)]
    void OnDisconnection([In] ExtDisconnectMode removeMode,
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT), In] ref Array custom);
    [DispId(3)]
    [MethodImpl(MethodImplOptions.InternalCall)]
    void OnAddInsUpdate([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT), In] ref Array custom);
    [DispId(4)]
    [MethodImpl(MethodImplOptions.InternalCall)]
    void OnStartupComplete([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT), In] ref Array custom);
    [DispId(5)]
    [MethodImpl(MethodImplOptions.InternalCall)]
    void OnBeginShutdown([MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_VARIANT), In] ref Array custom);
}

[ComImport]
[Guid("000C0396-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
public interface IRibbonExtensibility
{
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetCustomUI([MarshalAs(UnmanagedType.BStr)] string ribbonId);
}

[ComVisible(true)]
[Guid("B1AE9906-0B24-4F8E-90A7-3B0BBD63A87E")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IEgyptianDictationBridge
{
    [DispId(1)]
    void StartFromWord([MarshalAs(UnmanagedType.IDispatch)] object application);

    [DispId(2)]
    void StopFromWord();

    [DispId(3)]
    void OnStartDictation([MarshalAs(UnmanagedType.IDispatch)] object control);

    [DispId(4)]
    void OnStopDictation([MarshalAs(UnmanagedType.IDispatch)] object control);

    [DispId(5)]
    void OnAbout([MarshalAs(UnmanagedType.IDispatch)] object control);
}
