using System.Runtime.InteropServices;
using OFS.Sdk;

namespace OFS.Runtime.Entry;

internal sealed partial class UnsafeIl2CppApi(
    nint gameAssemblyModule,
    nint domain,
    IReadOnlyDictionary<string, nint> images) : IUnsafeIl2CppApi
{
    private readonly IReadOnlyDictionary<string, nint> _images = images;

    public nint GameAssemblyModule { get; } = gameAssemblyModule;
    public nint Domain { get; } = domain;

    public nint FindImage(string assemblyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        if (_images.TryGetValue(assemblyName, out var exact))
        {
            return exact;
        }
        var shortName = Path.GetFileNameWithoutExtension(assemblyName);
        return _images
            .Where(pair => string.Equals(
                Path.GetFileNameWithoutExtension(pair.Key),
                shortName,
                StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .FirstOrDefault();
    }

    public IReadOnlyList<Il2CppImageMetadata> GetImages() =>
        _images
            .Select(pair => new Il2CppImageMetadata(
                pair.Value,
                pair.Key,
                Native.image_get_class_count(pair.Value)))
            .OrderBy(image => image.Name, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<Il2CppClassMetadata> GetClasses(string assemblyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        var image = FindImage(assemblyName);
        if (image == 0)
        {
            throw new FileNotFoundException(
                $"Loaded IL2CPP image '{assemblyName}' was not found.",
                assemblyName);
        }
        var count = Native.image_get_class_count(image);
        if (count > int.MaxValue)
        {
            throw new InvalidDataException(
                $"IL2CPP image '{assemblyName}' exposes too many classes ({count}).");
        }
        var result = new List<Il2CppClassMetadata>(checked((int)count));
        for (nuint index = 0; index < count; ++index)
        {
            var klass = Native.image_get_class(image, index);
            if (klass != 0) result.Add(GetClassMetadata(klass));
        }
        return result;
    }

    public nint FindClass(string assemblyName, string namespaze, string name)
    {
        var image = FindImage(assemblyName);
        return image == 0 ? 0 : Native.class_from_name(image, namespaze, name);
    }

    public nint FindNestedClass(nint declaringClass, string name)
    {
        if (declaringClass == 0)
        {
            throw new ArgumentException("Declaring IL2CPP class pointer is null.", nameof(declaringClass));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        nint iterator = 0;
        while (true)
        {
            var nested = Native.class_get_nested_types(declaringClass, ref iterator);
            if (nested == 0)
            {
                return 0;
            }
            var nestedName = Marshal.PtrToStringUTF8(Native.class_get_name(nested));
            if (string.Equals(nestedName, name, StringComparison.Ordinal))
            {
                return nested;
            }
        }
    }

    public Il2CppClassMetadata GetClassMetadata(nint klass)
    {
        RequirePointer(klass, "IL2CPP class");
        var namespaze = ReadUtf8(Native.class_get_namespace(klass));
        var name = ReadUtf8(Native.class_get_name(klass));
        var interfaces = new List<nint>();
        nint iterator = 0;
        while (true)
        {
            var candidate = Native.class_get_interfaces(klass, ref iterator);
            if (candidate == 0) break;
            interfaces.Add(candidate);
        }
        return new Il2CppClassMetadata(
            klass,
            namespaze,
            name,
            namespaze.Length == 0 ? name : $"{namespaze}.{name}",
            Native.class_get_parent(klass),
            interfaces);
    }

    public IReadOnlyList<Il2CppMethodMetadata> GetMethods(nint klass)
    {
        RequirePointer(klass, "IL2CPP class");
        var result = new List<Il2CppMethodMetadata>();
        nint iterator = 0;
        while (true)
        {
            var method = Native.class_get_methods(klass, ref iterator);
            if (method == 0) break;
            var parameterCount = Native.method_get_param_count(method);
            var parameters = new List<Il2CppParameterMetadata>(checked((int)parameterCount));
            for (uint index = 0; index < parameterCount; index++)
            {
                parameters.Add(new Il2CppParameterMetadata(
                    checked((int)index),
                    ReadUtf8(Native.method_get_param_name(method, index)),
                    GetTypeName(Native.method_get_param(method, index))));
            }
            var flags = Native.method_get_flags(method, out var implementationFlags);
            result.Add(new Il2CppMethodMetadata(
                method,
                ReadUtf8(Native.method_get_name(method)),
                GetTypeName(Native.method_get_return_type(method)),
                flags,
                implementationFlags,
                parameters));
        }
        return result;
    }

    public IReadOnlyList<Il2CppFieldMetadata> GetFields(nint klass)
    {
        RequirePointer(klass, "IL2CPP class");
        var result = new List<Il2CppFieldMetadata>();
        nint iterator = 0;
        while (true)
        {
            var field = Native.class_get_fields(klass, ref iterator);
            if (field == 0) break;
            result.Add(new Il2CppFieldMetadata(
                field,
                ReadUtf8(Native.field_get_name(field)),
                GetTypeName(Native.field_get_type(field)),
                Native.field_get_offset(field),
                Native.field_get_flags(field)));
        }
        return result;
    }

    public void EnsureClassInitialized(nint klass)
    {
        if (klass == 0)
        {
            throw new ArgumentException("IL2CPP class pointer is null.", nameof(klass));
        }
        Native.runtime_class_init(klass);
    }

    public nint NewObject(nint klass)
    {
        if (klass == 0)
        {
            throw new ArgumentException("IL2CPP class pointer is null.", nameof(klass));
        }
        var instance = Native.object_new(klass);
        return instance != 0
            ? instance
            : throw new InvalidOperationException("il2cpp_object_new returned null.");
    }

    public unsafe nint ShallowCloneObject(nint instance)
    {
        if (instance == 0)
            throw new ArgumentException("IL2CPP object pointer is null.", nameof(instance));
        var klass = Native.object_get_class(instance);
        var size = checked((int)Native.class_instance_size(klass));
        var clone = Native.object_new(klass);
        if (clone == 0)
            throw new InvalidOperationException("IL2CPP clone allocation returned null.");
        var header = 2 * nint.Size;
        if (size > header)
            Buffer.MemoryCopy(
                (void*)(instance + header),
                (void*)(clone + header),
                size - header,
                size - header);
        return clone;
    }

    public nint FindMethod(nint klass, string name, int argumentCount) =>
        Native.class_get_method_from_name(klass, name, argumentCount);

    public nint FindMethodBySignature(
        nint klass,
        string name,
        IReadOnlyList<string> parameterTypeNames)
    {
        if (klass == 0)
        {
            throw new ArgumentException("IL2CPP class pointer is null.", nameof(klass));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(parameterTypeNames);
        if (parameterTypeNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Parameter type names must be namespace-qualified non-empty names.",
                nameof(parameterTypeNames));
        }

        nint iterator = 0;
        while (true)
        {
            var method = Native.class_get_methods(klass, ref iterator);
            if (method == 0)
            {
                return 0;
            }
            var methodName = Marshal.PtrToStringUTF8(Native.method_get_name(method));
            if (!string.Equals(methodName, name, StringComparison.Ordinal) ||
                Native.method_get_param_count(method) != (uint)parameterTypeNames.Count)
            {
                continue;
            }

            var matches = true;
            for (var index = 0; index < parameterTypeNames.Count; index++)
            {
                var parameterType = Native.method_get_param(method, (uint)index);
                var parameterClass = parameterType == 0 ? 0 : Native.class_from_type(parameterType);
                if (parameterClass == 0 || !string.Equals(
                    GetQualifiedClassName(parameterClass),
                    parameterTypeNames[index],
                    StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }
            if (matches)
            {
                return method;
            }
        }
    }

    private static string GetQualifiedClassName(nint klass)
    {
        var namespaze = Marshal.PtrToStringUTF8(Native.class_get_namespace(klass)) ?? string.Empty;
        var name = Marshal.PtrToStringUTF8(Native.class_get_name(klass)) ?? string.Empty;
        return namespaze.Length == 0 ? name : $"{namespaze}.{name}";
    }

    private static string GetTypeName(nint type)
    {
        if (type == 0) return string.Empty;
        var allocated = Native.type_get_name(type);
        if (allocated == 0) return string.Empty;
        try
        {
            return ReadUtf8(allocated);
        }
        finally
        {
            Native.free(allocated);
        }
    }

    private static string ReadUtf8(nint value) =>
        value == 0 ? string.Empty : Marshal.PtrToStringUTF8(value) ?? string.Empty;

    private static void RequirePointer(nint value, string description)
    {
        if (value == 0) throw new ArgumentException($"{description} pointer is null.");
    }

    public nint ResolveVirtualMethod(nint instance, nint methodInfo)
    {
        if (instance == 0)
            throw new ArgumentException("IL2CPP object pointer is null.", nameof(instance));
        if (methodInfo == 0)
            throw new ArgumentException("IL2CPP method pointer is null.", nameof(methodInfo));
        var resolved = Native.object_get_virtual_method(instance, methodInfo);
        return resolved != 0
            ? resolved
            : throw new MissingMethodException("IL2CPP virtual method resolution returned null.");
    }

    public nint GetObjectClass(nint instance) =>
        instance == 0 ? 0 : Native.object_get_class(instance);

    public bool IsAssignableFrom(nint baseClass, nint candidateClass) =>
        baseClass != 0 && candidateClass != 0 &&
        Native.class_is_assignable_from(baseClass, candidateClass);

    public nint GetTypeObject(nint klass)
    {
        if (klass == 0)
        {
            throw new ArgumentException("IL2CPP class pointer is null.", nameof(klass));
        }
        return Native.type_get_object(Native.class_get_type(klass));
    }

    public nint GetMethodPointer(nint methodInfo) =>
        methodInfo == 0 ? 0 : Marshal.ReadIntPtr(methodInfo);

    public nint FindField(nint klass, string name) =>
        Native.class_get_field_from_name(klass, name);

    public nint GetFieldTypeClass(nint fieldInfo)
    {
        if (fieldInfo == 0)
        {
            throw new ArgumentException("IL2CPP field pointer is null.", nameof(fieldInfo));
        }
        var klass = Native.class_from_type(Native.field_get_type(fieldInfo));
        return klass != 0
            ? klass
            : throw new InvalidOperationException("IL2CPP field type has no class.");
    }

    public int GetFieldOffset(nint fieldInfo) => Native.field_get_offset(fieldInfo);

    public nint ReadObjectReference(nint instance, nint fieldInfo) =>
        Marshal.ReadIntPtr(instance, GetFieldOffset(fieldInfo));

    public nint ReadStaticObjectReference(nint fieldInfo)
    {
        if (fieldInfo == 0)
        {
            throw new ArgumentException("IL2CPP field pointer is null.", nameof(fieldInfo));
        }
        Native.field_static_get_value(fieldInfo, out var value);
        return value;
    }

    public void WriteObjectReference(nint instance, nint fieldInfo, nint value)
    {
        var targetAddress = instance + GetFieldOffset(fieldInfo);
        Native.gc_wbarrier_set_field(instance, targetAddress, value);
    }

    public void SetStaticFieldValue(nint fieldInfo, nint source)
    {
        RequirePointer(fieldInfo, "IL2CPP field");
        RequirePointer(source, "Source");
        Native.field_static_set_value(fieldInfo, source);
    }

    public void WriteStaticObjectReference(nint fieldInfo, nint value)
    {
        RequirePointer(fieldInfo, "IL2CPP field");
        Native.field_static_set_value(fieldInfo, value);
    }

    public void GetFieldValue(nint instance, nint fieldInfo, nint destination)
    {
        if (instance == 0) throw new ArgumentException("IL2CPP object pointer is null.", nameof(instance));
        if (fieldInfo == 0) throw new ArgumentException("IL2CPP field pointer is null.", nameof(fieldInfo));
        if (destination == 0) throw new ArgumentException("Destination pointer is null.", nameof(destination));
        Native.field_get_value(instance, fieldInfo, destination);
    }

    public void SetFieldValue(nint instance, nint fieldInfo, nint source)
    {
        if (instance == 0) throw new ArgumentException("IL2CPP object pointer is null.", nameof(instance));
        if (fieldInfo == 0) throw new ArgumentException("IL2CPP field pointer is null.", nameof(fieldInfo));
        if (source == 0) throw new ArgumentException("Source pointer is null.", nameof(source));
        Native.field_set_value(instance, fieldInfo, source);
    }

    public int ReadInt32(nint instance, nint fieldInfo) =>
        Marshal.ReadInt32(instance, GetFieldOffset(fieldInfo));

    public void WriteInt32(nint instance, nint fieldInfo, int value) =>
        Marshal.WriteInt32(instance, GetFieldOffset(fieldInfo), value);

    public float ReadSingle(nint instance, nint fieldInfo) =>
        BitConverter.Int32BitsToSingle(ReadInt32(instance, fieldInfo));

    public void WriteSingle(nint instance, nint fieldInfo, float value) =>
        WriteInt32(instance, fieldInfo, BitConverter.SingleToInt32Bits(value));

    public bool ReadBoolean(nint instance, nint fieldInfo) =>
        Marshal.ReadByte(instance, GetFieldOffset(fieldInfo)) != 0;

    public void WriteBoolean(nint instance, nint fieldInfo, bool value) =>
        Marshal.WriteByte(instance, GetFieldOffset(fieldInfo), value ? (byte)1 : (byte)0);

    public nint Unbox(nint boxedValue) =>
        boxedValue == 0 ? 0 : Native.object_unbox(boxedValue);

    public nint BoxValue(nint valueClass, nint source)
    {
        RequirePointer(valueClass, "IL2CPP value class");
        RequirePointer(source, "Source");
        var boxed = Native.value_box(valueClass, source);
        return boxed != 0
            ? boxed
            : throw new InvalidOperationException("il2cpp_value_box returned null.");
    }

    public nint NewString(string value) => Native.string_new(value);

    public string ReadString(nint value)
    {
        if (value == 0)
        {
            throw new ArgumentException("IL2CPP string pointer is null.", nameof(value));
        }
        var length = Native.string_length(value);
        return Marshal.PtrToStringUni(Native.string_chars(value), length)
            ?? throw new InvalidDataException("IL2CPP string could not be decoded.");
    }

    public nint NewByteArray(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var byteClass = FindClass("mscorlib.dll", "System", "Byte");
        if (byteClass == 0)
        {
            throw new TypeLoadException("System.Byte was not found in mscorlib.dll.");
        }
        var array = Native.array_new(byteClass, (nuint)value.Length);
        if (array == 0)
        {
            throw new InvalidOperationException("il2cpp_array_new returned null for byte array.");
        }
        if (value.Length != 0)
        {
            Marshal.Copy(value, 0, GetArrayVector(array), value.Length);
        }
        return array;
    }

    public byte[] ReadByteArray(nint array)
    {
        var length = GetArrayLength(array);
        if (length > int.MaxValue)
        {
            throw new InvalidDataException("IL2CPP byte array exceeds CoreCLR array limits.");
        }
        var result = new byte[(int)length];
        if (result.Length != 0)
        {
            Marshal.Copy(GetArrayVector(array), result, 0, result.Length);
        }
        return result;
    }

    public nint NewSingleArray(float[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var singleClass = FindClass("mscorlib.dll", "System", "Single");
        if (singleClass == 0)
        {
            throw new TypeLoadException("System.Single was not found in mscorlib.dll.");
        }
        var array = Native.array_new(singleClass, (nuint)value.Length);
        if (array == 0)
        {
            throw new InvalidOperationException("il2cpp_array_new returned null for float array.");
        }
        if (value.Length != 0)
        {
            Marshal.Copy(value, 0, GetArrayVector(array), value.Length);
        }
        return array;
    }

    public float[] ReadSingleArray(nint array)
    {
        var length = GetArrayLength(array);
        if (length > int.MaxValue)
        {
            throw new InvalidDataException("IL2CPP float array exceeds CoreCLR array limits.");
        }
        var result = new float[(int)length];
        if (result.Length != 0)
        {
            Marshal.Copy(GetArrayVector(array), result, 0, result.Length);
        }
        return result;
    }

    public nint NewArray(nint elementClass, nuint length)
    {
        RequirePointer(elementClass, "IL2CPP array element class");
        var array = Native.array_new(elementClass, length);
        return array != 0
            ? array
            : throw new InvalidOperationException("il2cpp_array_new returned null.");
    }

    public nuint GetArrayLength(nint array)
    {
        if (array == 0) throw new ArgumentException("IL2CPP array pointer is null.", nameof(array));
        return Native.array_length(array);
    }

    public nint ReadArrayElementReference(nint array, nuint index)
    {
        var length = GetArrayLength(array);
        if (index >= length) throw new ArgumentOutOfRangeException(nameof(index));
        // Il2CppArray = Il2CppObject (klass + monitor), bounds, max_length,
        // followed by the aligned vector. array_addr_with_size is a header
        // helper and is not exported by this Unity 6 GameAssembly.
        var vectorOffset = 4 * nint.Size;
        var byteOffset = checked(vectorOffset + ((int)index * nint.Size));
        return Marshal.ReadIntPtr(array, byteOffset);
    }

    public void WriteArrayElementReference(nint array, nuint index, nint value)
    {
        var length = GetArrayLength(array);
        if (index >= length) throw new ArgumentOutOfRangeException(nameof(index));
        var vectorOffset = 4 * nint.Size;
        var byteOffset = checked(vectorOffset + ((int)index * nint.Size));
        Native.gc_wbarrier_set_field(array, array + byteOffset, value);
    }

    private static nint GetArrayVector(nint array) => array + (4 * nint.Size);

    public nint RuntimeInvoke(nint methodInfo, nint instance, nint parameters)
    {
        var result = Native.runtime_invoke(methodInfo, instance, parameters, out var exception);
        if (exception != 0)
        {
            throw new InvalidOperationException(
                $"IL2CPP invocation raised exception 0x{exception:X}: " +
                FormatException(exception));
        }
        return result;
    }

    public unsafe nint Invoke(
        nint methodInfo,
        nint instance,
        params Il2CppArgument[] arguments)
    {
        RequirePointer(methodInfo, "IL2CPP method");
        ArgumentNullException.ThrowIfNull(arguments);
        var expectedCount = Native.method_get_param_count(methodInfo);
        if (expectedCount != (uint)arguments.Length)
        {
            throw new ArgumentException(
                $"Method expects {expectedCount} argument(s), but {arguments.Length} were supplied.",
                nameof(arguments));
        }
        if (arguments.Length == 0)
        {
            return RuntimeInvoke(methodInfo, instance, 0);
        }
        if (arguments.Length > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(arguments),
                "Marshalled IL2CPP invocation supports at most 128 arguments.");
        }

        const int maximumValueBytes = 16 * 1024;
        var totalValueBytes = 0;
        foreach (var argument in arguments)
        {
            if (argument.Kind == Il2CppArgumentKind.Reference) continue;
            if (argument.Kind != Il2CppArgumentKind.Value || argument.ValueBytes.IsEmpty)
            {
                throw new ArgumentException("Invalid IL2CPP invocation argument.", nameof(arguments));
            }
            totalValueBytes = Align(checked(totalValueBytes + argument.ValueBytes.Length));
        }
        if (totalValueBytes > maximumValueBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(arguments),
                $"Value arguments exceed the {maximumValueBytes}-byte stack budget.");
        }

        nint* parameterVector = stackalloc nint[arguments.Length];
        byte* valueStorage = stackalloc byte[totalValueBytes == 0 ? 1 : totalValueBytes];
        var valueOffset = 0;
        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            if (argument.Kind == Il2CppArgumentKind.Reference)
            {
                parameterVector[index] = argument.Reference;
                continue;
            }

            var destination = new Span<byte>(valueStorage + valueOffset, argument.ValueBytes.Length);
            argument.ValueBytes.Span.CopyTo(destination);
            parameterVector[index] = (nint)(valueStorage + valueOffset);
            valueOffset = Align(checked(valueOffset + argument.ValueBytes.Length));
        }
        return RuntimeInvoke(methodInfo, instance, (nint)parameterVector);
    }

    private static int Align(int value) =>
        checked((value + (nint.Size - 1)) & ~(nint.Size - 1));

    private static unsafe string FormatException(nint exception)
    {
        const int capacity = 4096;
        byte* buffer = stackalloc byte[capacity];
        buffer[0] = 0;
        Native.format_exception(exception, (nint)buffer, capacity);
        return Marshal.PtrToStringUTF8((nint)buffer) ?? "<unavailable>";
    }

    private static partial class Native
    {
        private const string GameAssembly = "GameAssembly.dll";

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_from_name", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint class_from_name(nint image, string namespaze, string name);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_image_get_class_count")]
        internal static partial nuint image_get_class_count(nint image);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_image_get_class")]
        internal static partial nint image_get_class(nint image, nuint index);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_get_nested_types")]
        internal static partial nint class_get_nested_types(nint klass, ref nint iterator);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_get_parent")]
        internal static partial nint class_get_parent(nint klass);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_get_interfaces")]
        internal static partial nint class_get_interfaces(nint klass, ref nint iterator);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_runtime_class_init")]
        internal static partial void runtime_class_init(nint klass);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_get_name")]
        internal static partial nint class_get_name(nint klass);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_get_namespace")]
        internal static partial nint class_get_namespace(nint klass);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_instance_size")]
        internal static partial uint class_instance_size(nint klass);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_get_methods")]
        internal static partial nint class_get_methods(nint klass, ref nint iterator);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_get_fields")]
        internal static partial nint class_get_fields(nint klass, ref nint iterator);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_field_get_value")]
        internal static partial void field_get_value(nint instance, nint field, nint destination);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_field_set_value")]
        internal static partial void field_set_value(nint instance, nint field, nint source);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_method_get_name")]
        internal static partial nint method_get_name(nint method);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_method_get_param_count")]
        internal static partial uint method_get_param_count(nint method);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_method_get_param")]
        internal static partial nint method_get_param(nint method, uint index);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_method_get_param_name")]
        internal static partial nint method_get_param_name(nint method, uint index);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_method_get_return_type")]
        internal static partial nint method_get_return_type(nint method);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_method_get_flags")]
        internal static partial uint method_get_flags(nint method, out uint implementationFlags);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_object_new")]
        internal static partial nint object_new(nint klass);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_object_get_class")]
        internal static partial nint object_get_class(nint instance);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_object_get_virtual_method")]
        internal static partial nint object_get_virtual_method(nint instance, nint method);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_is_assignable_from")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static partial bool class_is_assignable_from(nint baseClass, nint candidateClass);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_get_method_from_name", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint class_get_method_from_name(nint klass, string name, int argumentCount);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_get_type")]
        internal static partial nint class_get_type(nint klass);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_type_get_object")]
        internal static partial nint type_get_object(nint type);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_get_field_from_name", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint class_get_field_from_name(nint klass, string name);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_field_get_type")]
        internal static partial nint field_get_type(nint field);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_field_get_name")]
        internal static partial nint field_get_name(nint field);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_field_get_flags")]
        internal static partial uint field_get_flags(nint field);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_class_from_type")]
        internal static partial nint class_from_type(nint type);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_field_get_offset")]
        internal static partial int field_get_offset(nint field);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_field_static_get_value")]
        internal static partial void field_static_get_value(nint field, out nint value);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_field_static_set_value")]
        internal static partial void field_static_set_value(nint field, nint value);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_gc_wbarrier_set_field")]
        internal static partial void gc_wbarrier_set_field(
            nint instance,
            nint targetAddress,
            nint value);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_string_new", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint string_new(string value);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_string_length")]
        internal static partial int string_length(nint value);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_string_chars")]
        internal static partial nint string_chars(nint value);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_object_unbox")]
        internal static partial nint object_unbox(nint value);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_value_box")]
        internal static partial nint value_box(nint klass, nint value);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_type_get_name")]
        internal static partial nint type_get_name(nint type);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_free")]
        internal static partial void free(nint value);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_runtime_invoke")]
        internal static partial nint runtime_invoke(
            nint method,
            nint instance,
            nint parameters,
            out nint exception);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_format_exception")]
        internal static partial void format_exception(nint exception, nint buffer, int bufferSize);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_array_length")]
        internal static partial nuint array_length(nint array);

        [LibraryImport(GameAssembly, EntryPoint = "il2cpp_array_new")]
        internal static partial nint array_new(nint elementClass, nuint length);

    }
}
