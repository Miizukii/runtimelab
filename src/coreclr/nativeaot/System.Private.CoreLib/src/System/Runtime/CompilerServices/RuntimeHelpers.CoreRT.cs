// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Internal.Reflection.Augments;
using Internal.Reflection.Core.NonPortable;
using Internal.Runtime.Augments;
using System.Diagnostics.CodeAnalysis;
using System.Runtime;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using Debug = System.Diagnostics.Debug;

namespace System.Runtime.CompilerServices
{
    public static partial class RuntimeHelpers
    {
        [Intrinsic]
        public static void InitializeArray(Array array, RuntimeFieldHandle fldHandle)
        {
            // We only support this intrinsic when it occurs within a well-defined IL sequence.
            // If a call to this method occurs within the recognized sequence, codegen must expand the IL sequence completely.
            // For any other purpose, the API is currently unsupported.
            // https://github.com/dotnet/corert/issues/364
            throw new PlatformNotSupportedException();
        }

        public static void RunClassConstructor(RuntimeTypeHandle type)
        {
            if (type.IsNull)
                throw new ArgumentException(SR.InvalidOperation_HandleIsNotInitialized);

            IntPtr pStaticClassConstructionContext = RuntimeAugments.Callbacks.TryGetStaticClassConstructionContext(type);
            if (pStaticClassConstructionContext == IntPtr.Zero)
                return;

            unsafe
            {
                ClassConstructorRunner.EnsureClassConstructorRun((StaticClassConstructionContext*)pStaticClassConstructionContext);
            }
        }

        public static void RunModuleConstructor(ModuleHandle module)
        {
            if (module.AssociatedModule == null)
                throw new ArgumentException(SR.InvalidOperation_HandleIsNotInitialized);

            ReflectionAugments.ReflectionCoreCallbacks.RunModuleConstructor(module.AssociatedModule);
        }

        public static object GetObjectValue(object? obj)
        {
            if (obj == null)
                return null;

            EETypePtr eeType = obj.EETypePtr;
            if ((!eeType.IsValueType) || eeType.IsPrimitive)
                return obj;

            return RuntimeImports.RhMemberwiseClone(obj);
        }

        public static new bool Equals(object? o1, object? o2)
        {
            if (o1 == o2)
                return true;

            if ((o1 == null) || (o2 == null))
                return false;

            // If it's not a value class, don't compare by value
            if (!o1.EETypePtr.IsValueType)
                return false;

            // Make sure they are the same type.
            if (o1.EETypePtr != o2.EETypePtr)
                return false;

            return RuntimeImports.RhCompareObjectContentsAndPadding(o1, o2);
        }

#if !FEATURE_SYNCTABLE
        private const int HASHCODE_BITS = 26;
        private const int MASK_HASHCODE = (1 << HASHCODE_BITS) - 1;
#endif

        [ThreadStatic]
        private static int t_hashSeed;

        internal static int GetNewHashCode()
        {
            int multiplier = Environment.CurrentManagedThreadId * 4 + 5;
            // Every thread has its own generator for hash codes so that we won't get into a situation
            // where two threads consistently give out the same hash codes.
            // Choice of multiplier guarantees period of 2**32 - see Knuth Vol 2 p16 (3.2.1.2 Theorem A).
            t_hashSeed = t_hashSeed * multiplier + 1;
            return t_hashSeed;
        }

        public static unsafe int GetHashCode(object o)
        {
#if FEATURE_SYNCTABLE
            return ObjectHeader.GetHashCode(o);
#else
            if (o == null)
                return 0;

            fixed (IntPtr* pEEType = &o.m_pEEType)
            {
                int* pSyncBlockIndex = (int*)((byte*)pEEType - 4); // skipping exactly 4 bytes for the SyncTableEntry (exactly 4 bytes not a pointer size).
                int hash = *pSyncBlockIndex & MASK_HASHCODE;

                if (hash == 0)
                    return MakeHashCode(o, pSyncBlockIndex);
                else
                    return hash;
            }
#endif
        }

#if !FEATURE_SYNCTABLE
        private static unsafe int MakeHashCode(Object o, int* pSyncBlockIndex)
        {
            int hash = GetNewHashCode() & MASK_HASHCODE;

            if (hash == 0)
                hash = 1;

            while (true)
            {
                int oldIndex = Volatile.Read(ref *pSyncBlockIndex);

                int currentHash = oldIndex & MASK_HASHCODE;
                if (currentHash != 0)
                {
                    // Someone else set the hash code.
                    hash = currentHash;
                    break;
                }

                int newIndex = oldIndex | hash;

                if (Interlocked.CompareExchange(ref *pSyncBlockIndex, newIndex, oldIndex) == oldIndex)
                    break;
                // If we get here someone else modified the header.  They may have set the hash code, or maybe some
                // other bits.  Let's try again.
            }

            return hash;
        }
#endif

        public static int OffsetToStringData
        {
            // This offset is baked in by string indexer intrinsic, so there is no harm
            // in getting it baked in here as well.
            [System.Runtime.Versioning.NonVersionable]
            get =>
                // Number of bytes from the address pointed to by a reference to
                // a String to the first 16-bit character in the String.  Skip
                // over the MethodTable pointer, & String
                // length.  Of course, the String reference points to the memory
                // after the sync block, so don't count that.
                // This property allows C#'s fixed statement to work on Strings.
                // On 64 bit platforms, this should be 12 (8+4) and on 32 bit 8 (4+4).
#if TARGET_64BIT
                12;
#else // 32
                8;
#endif // TARGET_64BIT

        }

        [ThreadStatic]
        private static unsafe byte* t_sufficientStackLimit;

        public static unsafe void EnsureSufficientExecutionStack()
        {
            byte* limit = t_sufficientStackLimit;
            if (limit == null)
                limit = GetSufficientStackLimit();

            byte* currentStackPtr = (byte*)(&limit);
            if (currentStackPtr < limit)
                throw new InsufficientExecutionStackException();
        }

        public static unsafe bool TryEnsureSufficientExecutionStack()
        {
            byte* limit = t_sufficientStackLimit;
            if (limit == null)
                limit = GetSufficientStackLimit();

            byte* currentStackPtr = (byte*)(&limit);
            return (currentStackPtr >= limit);
        }

        [MethodImpl(MethodImplOptions.NoInlining)] // Only called once per thread, no point in inlining.
        private static unsafe byte* GetSufficientStackLimit()
        {
            IntPtr lower, upper;
            RuntimeImports.RhGetCurrentThreadStackBounds(out lower, out upper);

            // Compute the limit used by EnsureSufficientExecutionStack and cache it on the thread. This minimum
            // stack size should be sufficient to allow a typical non-recursive call chain to execute, including
            // potential exception handling and garbage collection.

#if TARGET_64BIT
            const int MinExecutionStackSize = 128 * 1024;
#else
            const int MinExecutionStackSize = 64 * 1024;
#endif

            byte* limit = (((byte*)upper - (byte*)lower > MinExecutionStackSize)) ?
                ((byte*)lower + MinExecutionStackSize) : ((byte*)upper);

            return (t_sufficientStackLimit = limit);
        }

        [Intrinsic]
        public static bool IsReferenceOrContainsReferences<T>()
        {
            var pEEType = EETypePtr.EETypePtrOf<T>();
            return !pEEType.IsValueType || pEEType.HasPointers;
        }

        [Intrinsic]
        internal static bool IsReference<T>()
        {
            var pEEType = EETypePtr.EETypePtrOf<T>();
            return !pEEType.IsValueType;
        }

        [Intrinsic]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsBitwiseEquatable<T>()
        {
            // Only reachable for universal shared code - the compiler replaces this otherwise.
            // Returning false is conservative.
            return false;
        }

        // Returns true iff the object has a component size;
        // i.e., is variable length like System.String or Array.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool ObjectHasComponentSize(object obj)
        {
            Debug.Assert(obj != null);
            return obj.EETypePtr.ComponentSize != 0;
        }

        public static void PrepareMethod(RuntimeMethodHandle method)
        {
            if (method.Value == IntPtr.Zero)
                throw new ArgumentException(SR.InvalidOperation_HandleIsNotInitialized, nameof(method));
        }

        public static void PrepareMethod(RuntimeMethodHandle method, RuntimeTypeHandle[] instantiation)
        {
            if (method.Value == IntPtr.Zero)
                throw new ArgumentException(SR.InvalidOperation_HandleIsNotInitialized, nameof(method));
        }

        /// <summary>
        /// Allocate memory that is associated with the <paramref name="type"/> and
        /// will be freed if and when the <see cref="System.Type"/> is unloaded.
        /// </summary>
        /// <param name="type">Type associated with the allocated memory.</param>
        /// <param name="size">Amount of memory in bytes to allocate.</param>
        /// <returns>The allocated memory</returns>
        public static IntPtr AllocateTypeAssociatedMemory(Type type, int size)
        {
            if (type is null || !type.IsRuntimeImplemented())
                throw new ArgumentException(SR.Arg_MustBeType, nameof(type));

            if (size < 0)
                throw new ArgumentOutOfRangeException(nameof(size));

            // We don't support unloading; the memory will never be freed.
            return Marshal.AllocHGlobal(size);
        }

        public static void PrepareDelegate(Delegate d)
        {
        }

        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2059:UnrecognizedReflectionPattern",
            Justification = "We keep class constructors of all types with an EEType")]
        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2072:UnrecognizedReflectionPattern",
            Justification = "Constructed EEType of a Nullable forces a constructed EEType of the element type")]
        public static object GetUninitializedObject(
            // This API doesn't call any constructors, but the type needs to be seen as constructed.
            // A type is seen as constructed if a constructor is kept.
            // This obviously won't cover a type with no constructor. Reference types with no
            // constructor are an academic problem. Valuetypes with no constructors are a problem,
            // but IL Linker currently treats them as always implicitly boxed.
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)]
            Type type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type), SR.ArgumentNull_Type);
            }

            if (!type.IsRuntimeImplemented())
            {
                throw new SerializationException(SR.Format(SR.Serialization_InvalidType, type));
            }

            if (type.HasElementType || type.IsGenericParameter)
            {
                throw new ArgumentException(SR.Argument_InvalidValue);
            }

            if (type.ContainsGenericParameters)
            {
                throw new MemberAccessException(SR.Acc_CreateGeneric);
            }

            if (type.IsCOMObject)
            {
                throw new NotSupportedException(SR.NotSupported_ManagedActivation);
            }

            EETypePtr eeTypePtr = type.TypeHandle.ToEETypePtr();

            if (eeTypePtr.ElementType == Internal.Runtime.EETypeElementType.Void)
            {
                throw new ArgumentException(SR.Argument_InvalidValue);
            }

            // Don't allow strings (we already checked for arrays above)
            if (eeTypePtr.ComponentSize != 0)
            {
                throw new ArgumentException(SR.Argument_NoUninitializedStrings);
            }

            if (RuntimeImports.AreTypesAssignable(eeTypePtr, EETypePtr.EETypePtrOf<Delegate>()))
            {
                throw new MemberAccessException();
            }

            if (eeTypePtr.IsAbstract)
            {
                throw new MemberAccessException(SR.Acc_CreateAbst);
            }

            if (eeTypePtr.IsByRefLike)
            {
                throw new NotSupportedException(SR.NotSupported_ByRefLike);
            }

            if (eeTypePtr.IsNullable)
            {
                return GetUninitializedObject(Type.GetTypeFromEETypePtr(eeTypePtr.NullableType));
            }

            // Triggering the .cctor here is slightly different than desktop/CoreCLR, which
            // decide based on BeforeFieldInit, but we don't want to include BeforeFieldInit
            // in EEType just for this API to behave slightly differently.
            RunClassConstructor(type.TypeHandle);

            return RuntimeImports.RhNewObject(eeTypePtr);
        }
    }

    // CLR arrays are laid out in memory as follows (multidimensional array bounds are optional):
    // [ sync block || pMethodTable || num components || MD array bounds || array data .. ]
    //                 ^               ^                 ^                  ^ returned reference
    //                 |               |                 \-- ref Unsafe.As<RawArrayData>(array).Data
    //                 \-- array       \-- ref Unsafe.As<RawData>(array).Data
    // The BaseSize of an array includes all the fields before the array data,
    // including the sync block and method table. The reference to RawData.Data
    // points at the number of components, skipping over these two pointer-sized fields.
    [StructLayout(LayoutKind.Sequential)]
    internal class RawArrayData
    {
        public uint Length; // Array._numComponents padded to IntPtr
#if TARGET_64BIT
        public uint Padding;
#endif
        public byte Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal class RawData
    {
        public byte Data;
    }
}
