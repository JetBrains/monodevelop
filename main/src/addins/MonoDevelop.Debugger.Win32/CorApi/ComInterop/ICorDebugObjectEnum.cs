﻿using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using JetBrains.Annotations;

namespace CorApi.ComInterop
{
    /// <summary>
    /// </summary>
    /// <example><code>
    /// [
    ///     object,
    ///     local,
    ///     uuid(CC7BCB02-8A68-11d2-983C-0000F808342D),
    ///     pointer_default(unique)
    /// ]
    /// interface ICorDebugObjectEnum : ICorDebugEnum
    /// {
    ///     /*
    ///      * Gets the next "celt" number of objects in the enumeration.
    ///      * The actual number of objects retrieved is returned in "pceltFetched".
    ///      * Returns S_FALSE if the actual number of objects retrieved is smaller
    ///      * than the number of objects requested.
    ///      */
    ///     HRESULT Next([in] ULONG celt,
    ///                  [out, size_is(celt),
    ///                   length_is(*pceltFetched)]  CORDB_ADDRESS objects[],
    ///                  [out] ULONG *pceltFetched);
    /// }; </code></example>
    [Guid("CC7BCB02-8A68-11D2-983C-0000F808342D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComImport]
    [SuppressMessage("ReSharper", "BuiltInTypeReferenceStyle")]
    public unsafe interface ICorDebugObjectEnum : ICorDebugEnum
    {
        /// <summary>
        /// Moves the current position forward the given number of elements.
        /// </summary>
        /// <param name="celt">the given number of elements</param>
        [MustUseReturnValue("HResult")]
        [PreserveSig]
        [MethodImpl(MethodImplOptions.InternalCall | MethodImplOptions.PreserveSig, MethodCodeType = MethodCodeType.Runtime)]
        new Int32 Skip([In] UInt32 celt);

        /// <summary>
        /// Sets the position of the enumerator to the beginning of the enumeration.
        /// </summary>
        [MustUseReturnValue("HResult")]
        [PreserveSig]
        [MethodImpl(MethodImplOptions.InternalCall | MethodImplOptions.PreserveSig, MethodCodeType = MethodCodeType.Runtime)]
        new Int32 Reset();

        /// <summary>
        /// Creates another enumerator with the same current position as this one.
        /// </summary>
        /// <param name="ppEnum">another enumerator</param>
        [MustUseReturnValue("HResult")]
        [PreserveSig]
        [MethodImpl(MethodImplOptions.InternalCall | MethodImplOptions.PreserveSig, MethodCodeType = MethodCodeType.Runtime)]
        new Int32 Clone([MarshalAs(UnmanagedType.Interface)] out ICorDebugEnum ppEnum);

        /// <summary>
        /// Gets the number of elements in the enumeration.
        /// </summary>
        [MustUseReturnValue("HResult")]
        [PreserveSig]
        [MethodImpl(MethodImplOptions.InternalCall | MethodImplOptions.PreserveSig, MethodCodeType = MethodCodeType.Runtime)]
        new Int32 GetCount([Out] UInt32* pcelt);

        /// <summary>
        ///  Gets the next "celt" number of objects in the enumeration.
        ///  The actual number of objects retrieved is returned in "pceltFetched".
        ///  Returns S_FALSE if the actual number of objects retrieved is smaller than the number of objects requested.
        /// </summary>
        /// <param name="celt"></param>
        /// <param name="objects"></param>
        /// <param name="pceltFetched"></param>
        [MustUseReturnValue("HResult")]
        [PreserveSig]
        [MethodImpl(MethodImplOptions.InternalCall | MethodImplOptions.PreserveSig, MethodCodeType = MethodCodeType.Runtime)]
        Int32 Next([In] UInt32 celt, [ComAliasName("CORDB_ADDRESS[]")] [Out] UInt64** objects, [Out] UInt32* pceltFetched);
    }
}