﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Debug = System.Diagnostics.Debug;

namespace Internal.Runtime.CompilerHelpers
{
    /// <summary>
    /// These methods are used to implement shared generic code.
    /// </summary>
    internal static class SharedCodeHelpers
    {
        public static unsafe EEType* GetOrdinalInterface(EEType* pType, ushort interfaceIndex)
        {
            Debug.Assert(interfaceIndex <= pType->NumInterfaces);
            return pType->InterfaceMap[interfaceIndex].InterfaceType;
        }
    }
}
