using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

internal sealed record MetadataApiSurface(
    string AssemblyName,
    string Sha256,
    IReadOnlyList<string> Entries)
{
    public static MetadataApiSurface Merge(
        IEnumerable<MetadataApiSurface> surfaces)
    {
        var surfaceArray = surfaces.ToArray();
        if (surfaceArray.Length == 0)
            throw new ArgumentException("No metadata surfaces were supplied.");
        return new MetadataApiSurface(
            string.Join(
                "+",
                surfaceArray.Select(surface => surface.AssemblyName)),
            string.Join(
                ";",
                surfaceArray.Select(
                    surface => $"{surface.AssemblyName}={surface.Sha256}")),
            surfaceArray.SelectMany(surface => surface.Entries)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    public static MetadataApiSurface Read(
        string assemblyPath,
        IReadOnlyList<string> namespacePrefixes)
    {
        using var stream = File.OpenRead(assemblyPath);
        var sha256 = Convert.ToHexString(SHA256.HashData(stream))
            .ToLowerInvariant();
        stream.Position = 0;
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
            throw new InvalidDataException(
                $"Managed metadata is missing: {assemblyPath}");

        var reader = peReader.GetMetadataReader();
        var entries = new SortedSet<string>(StringComparer.Ordinal);
        var formatter = new MetadataNameFormatter(reader);
        var provider = new CanonicalSignatureProvider(formatter);

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (!IsExternallyVisible(type, reader))
                continue;

            var typeNamespace = formatter.GetTypeNamespace(typeHandle);
            if (!namespacePrefixes.Any(
                    prefix => typeNamespace.Equals(
                        prefix,
                        StringComparison.Ordinal) ||
                    typeNamespace.StartsWith(
                        prefix + ".",
                        StringComparison.Ordinal)))
            {
                continue;
            }

            AddType(
                reader,
                formatter,
                provider,
                typeHandle,
                type,
                entries);
        }

        var assemblyName = reader.IsAssembly
            ? reader.GetString(reader.GetAssemblyDefinition().Name)
            : Path.GetFileNameWithoutExtension(assemblyPath);
        return new MetadataApiSurface(
            assemblyName,
            sha256,
            entries.ToArray());
    }

    private static void AddType(
        MetadataReader reader,
        MetadataNameFormatter formatter,
        CanonicalSignatureProvider provider,
        TypeDefinitionHandle typeHandle,
        TypeDefinition type,
        ISet<string> entries)
    {
        var typeName = formatter.GetTypeName(typeHandle);
        var baseType = type.BaseType.IsNil
            ? "-"
            : formatter.GetTypeName(type.BaseType);
        var kind = GetTypeKind(type, baseType);
        var genericArity = type.GetGenericParameters().Count;
        entries.Add(
            $"type|{typeName}|access={GetTypeAccess(type.Attributes)};" +
            $"kind={kind};abstract={type.Attributes.HasFlag(TypeAttributes.Abstract)};" +
            $"sealed={type.Attributes.HasFlag(TypeAttributes.Sealed)};" +
            $"base={baseType};arity={genericArity}");

        AddAttributes(reader, formatter, typeName, type.GetCustomAttributes(), entries);
        AddGenericParameters(
            reader,
            formatter,
            typeName,
            type.GetGenericParameters(),
            entries);

        foreach (var interfaceHandle in type.GetInterfaceImplementations())
        {
            var implementation = reader.GetInterfaceImplementation(interfaceHandle);
            var interfaceName = formatter.GetTypeName(implementation.Interface);
            if (ShouldIncludeApiInterface(kind, interfaceName))
                entries.Add($"interface|{typeName}|{interfaceName}");
        }

        var accessorHandles = new HashSet<MethodDefinitionHandle>();
        foreach (var propertyHandle in type.GetProperties())
        {
            var property = reader.GetPropertyDefinition(propertyHandle);
            var accessors = property.GetAccessors();
            AddAccessor(accessorHandles, accessors.Getter);
            AddAccessor(accessorHandles, accessors.Setter);
            foreach (var other in accessors.Others)
                AddAccessor(accessorHandles, other);
            AddProperty(
                reader,
                formatter,
                provider,
                typeName,
                property,
                accessors,
                entries);
        }

        foreach (var eventHandle in type.GetEvents())
        {
            var @event = reader.GetEventDefinition(eventHandle);
            var accessors = @event.GetAccessors();
            AddAccessor(accessorHandles, accessors.Adder);
            AddAccessor(accessorHandles, accessors.Remover);
            AddAccessor(accessorHandles, accessors.Raiser);
            foreach (var other in accessors.Others)
                AddAccessor(accessorHandles, other);
            AddEvent(
                reader,
                formatter,
                typeName,
                @event,
                accessors,
                entries);
        }

        foreach (var fieldHandle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            if (!IsExternallyVisible(field.Attributes))
                continue;

            var fieldType = field.DecodeSignature(
                provider,
                GenericContext.Empty);
            var fieldName = reader.GetString(field.Name);
            var constant = FormatConstant(reader, field.GetDefaultValue());
            entries.Add(
                $"field|{typeName}|access={GetMemberAccess(field.Attributes)};" +
                $"static={field.Attributes.HasFlag(FieldAttributes.Static)};" +
                $"readonly={field.Attributes.HasFlag(FieldAttributes.InitOnly)};" +
                $"literal={field.Attributes.HasFlag(FieldAttributes.Literal)};" +
                $"type={fieldType};name={fieldName};constant={constant}");
            AddAttributes(
                reader,
                formatter,
                $"{typeName}.{fieldName}",
                field.GetCustomAttributes(),
                entries);
        }

        foreach (var methodHandle in type.GetMethods())
        {
            if (accessorHandles.Contains(methodHandle))
                continue;

            var method = reader.GetMethodDefinition(methodHandle);
            if (!IsExternallyVisible(method.Attributes))
                continue;
            var methodName = reader.GetString(method.Name);
            if (!ShouldIncludeApiMethod(kind, methodName))
                continue;

            AddMethod(
                reader,
                formatter,
                provider,
                typeName,
                method,
                entries);
        }
    }

    private static void AddMethod(
        MetadataReader reader,
        MetadataNameFormatter formatter,
        CanonicalSignatureProvider provider,
        string typeName,
        MethodDefinition method,
        ISet<string> entries)
    {
        var signature = method.DecodeSignature(
            provider,
            GenericContext.Empty);
        var parameters = method.GetParameters()
            .Select(reader.GetParameter)
            .Where(parameter => parameter.SequenceNumber != 0)
            .OrderBy(parameter => parameter.SequenceNumber)
            .ToArray();
        var parameterText = new string[signature.ParameterTypes.Length];
        for (var index = 0; index < parameterText.Length; index++)
        {
            var parameter = index < parameters.Length ? parameters[index] : default;
            var attributes = index < parameters.Length
                ? FormatParameterAttributes(parameter.Attributes)
                : "-";
            var name = index < parameters.Length
                ? reader.GetString(parameter.Name)
                : $"arg{index}";
            var defaultValue = index < parameters.Length
                ? FormatConstant(reader, parameter.GetDefaultValue())
                : "-";
            parameterText[index] =
                $"{attributes}:{signature.ParameterTypes[index]}:{name}:{defaultValue}";
        }

        var methodName = reader.GetString(method.Name);
        var owner = $"{typeName}.{methodName}";
        entries.Add(
            $"method|{typeName}|access={GetMemberAccess(method.Attributes)};" +
            $"static={method.Attributes.HasFlag(MethodAttributes.Static)};" +
            $"abstract={method.Attributes.HasFlag(MethodAttributes.Abstract)};" +
            $"virtual={method.Attributes.HasFlag(MethodAttributes.Virtual)};" +
            $"final={method.Attributes.HasFlag(MethodAttributes.Final)};" +
            $"return={signature.ReturnType};name={methodName};" +
            $"arity={method.GetGenericParameters().Count};" +
            $"params=({string.Join(",", parameterText)})");
        AddAttributes(
            reader,
            formatter,
            owner,
            method.GetCustomAttributes(),
            entries);
        AddGenericParameters(
            reader,
            formatter,
            owner,
            method.GetGenericParameters(),
            entries);
    }

    private static void AddProperty(
        MetadataReader reader,
        MetadataNameFormatter formatter,
        CanonicalSignatureProvider provider,
        string typeName,
        PropertyDefinition property,
        PropertyAccessors accessors,
        ISet<string> entries)
    {
        if (!TryGetAccessorAccess(reader, accessors.Getter, accessors.Setter, out var access))
            return;

        var signature = property.DecodeSignature(
            provider,
            GenericContext.Empty);
        var propertyName = reader.GetString(property.Name);
        entries.Add(
            $"property|{typeName}|access={access};type={signature.ReturnType};" +
            $"name={propertyName};index=({string.Join(",", signature.ParameterTypes)});" +
            $"get={GetAccessorAccess(reader, accessors.Getter)};" +
            $"set={GetAccessorAccess(reader, accessors.Setter)}");
        AddAttributes(
            reader,
            formatter,
            $"{typeName}.{propertyName}",
            property.GetCustomAttributes(),
            entries);
    }

    private static void AddEvent(
        MetadataReader reader,
        MetadataNameFormatter formatter,
        string typeName,
        EventDefinition @event,
        EventAccessors accessors,
        ISet<string> entries)
    {
        if (!TryGetAccessorAccess(reader, accessors.Adder, accessors.Remover, out var access))
            return;

        var eventName = reader.GetString(@event.Name);
        entries.Add(
            $"event|{typeName}|access={access};" +
            $"type={formatter.GetTypeName(@event.Type)};name={eventName};" +
            $"add={GetAccessorAccess(reader, accessors.Adder)};" +
            $"remove={GetAccessorAccess(reader, accessors.Remover)}");
        AddAttributes(
            reader,
            formatter,
            $"{typeName}.{eventName}",
            @event.GetCustomAttributes(),
            entries);
    }

    private static void AddAttributes(
        MetadataReader reader,
        MetadataNameFormatter formatter,
        string owner,
        CustomAttributeHandleCollection handles,
        ISet<string> entries)
    {
        foreach (var handle in handles)
        {
            var attribute = reader.GetCustomAttribute(handle);
            var attributeType = formatter.GetAttributeTypeName(attribute.Constructor);
            if (attributeType is not null &&
                ShouldIncludeApiAttribute(attributeType))
            {
                entries.Add(
                    $"attribute|{owner}|{attributeType};" +
                    $"value={Convert.ToHexString(reader.GetBlobBytes(attribute.Value))}");
            }
        }
    }

    private static bool ShouldIncludeApiAttribute(string attributeType)
    {
        // C#/WinRT projection plumbing and compiler diagnostics describe the
        // producing toolchain, not the WinUI consumer contract. Availability,
        // XAML, deprecation, flags, and other semantic attributes remain.
        return !attributeType.StartsWith("ABI.", StringComparison.Ordinal) &&
            !attributeType.StartsWith("WinRT.", StringComparison.Ordinal) &&
            !attributeType.StartsWith(
                "System.Runtime.CompilerServices.",
                StringComparison.Ordinal) &&
            attributeType is not
                "System.CodeDom.Compiler.GeneratedCodeAttribute" and not
                "System.Diagnostics.DebuggerBrowsableAttribute" and not
                "System.Diagnostics.DebuggerDisplayAttribute" and not
                "System.Runtime.InteropServices.GuidAttribute";
    }

    private static bool ShouldIncludeApiInterface(
        string typeKind,
        string interfaceName)
    {
        if (interfaceName.StartsWith("WinRT.", StringComparison.Ordinal) ||
            interfaceName is
                "System.Runtime.InteropServices.ICustomQueryInterface" or
                "System.Runtime.InteropServices.IDynamicInterfaceCastable")
        {
            return false;
        }

        return typeKind != "class" ||
            !interfaceName.StartsWith(
                "System.IEquatable`1<",
                StringComparison.Ordinal);
    }

    private static bool ShouldIncludeApiMethod(
        string typeKind,
        string methodName)
    {
        if (typeKind == "class" &&
            methodName is
                "As" or
                "Equals" or
                "FromAbi" or
                "GetHashCode" or
                "IsOverridableInterface" or
                "op_Equality" or
                "op_Inequality")
        {
            return false;
        }

        return typeKind != "delegate" ||
            methodName is not ".ctor" and not "BeginInvoke" and not "EndInvoke";
    }

    private static void AddGenericParameters(
        MetadataReader reader,
        MetadataNameFormatter formatter,
        string owner,
        GenericParameterHandleCollection handles,
        ISet<string> entries)
    {
        foreach (var handle in handles)
        {
            var parameter = reader.GetGenericParameter(handle);
            var constraints = parameter.GetConstraints()
                .Select(reader.GetGenericParameterConstraint)
                .Select(constraint => formatter.GetTypeName(constraint.Type))
                .OrderBy(name => name, StringComparer.Ordinal);
            entries.Add(
                $"generic|{owner}|index={parameter.Index};" +
                $"name={reader.GetString(parameter.Name)};" +
                $"attributes={(int)parameter.Attributes};" +
                $"constraints=({string.Join(",", constraints)})");
        }
    }

    private static void AddAccessor(
        ISet<MethodDefinitionHandle> handles,
        MethodDefinitionHandle handle)
    {
        if (!handle.IsNil)
            handles.Add(handle);
    }

    private static bool TryGetAccessorAccess(
        MetadataReader reader,
        MethodDefinitionHandle first,
        MethodDefinitionHandle second,
        out string access)
    {
        var candidates = new[] { first, second }
            .Where(handle => !handle.IsNil)
            .Select(handle => reader.GetMethodDefinition(handle).Attributes)
            .Where(IsExternallyVisible)
            .Select(GetMemberAccess)
            .ToArray();
        if (candidates.Length == 0)
        {
            access = "-";
            return false;
        }

        access = candidates.OrderBy(AccessRank).First();
        return true;
    }

    private static string GetAccessorAccess(
        MetadataReader reader,
        MethodDefinitionHandle handle)
    {
        if (handle.IsNil)
            return "-";
        var attributes = reader.GetMethodDefinition(handle).Attributes;
        return IsExternallyVisible(attributes)
            ? GetMemberAccess(attributes)
            : "nonpublic";
    }

    private static int AccessRank(string access) => access switch
    {
        "public" => 0,
        "protected-public" => 1,
        "protected" => 2,
        _ => 3
    };

    private static bool IsExternallyVisible(
        TypeDefinition type,
        MetadataReader reader)
    {
        var visibility = type.Attributes & TypeAttributes.VisibilityMask;
        if (visibility == TypeAttributes.Public)
            return true;
        if (visibility is not (
            TypeAttributes.NestedPublic or
            TypeAttributes.NestedFamily or
            TypeAttributes.NestedFamORAssem))
        {
            return false;
        }

        var declaringType = type.GetDeclaringType();
        return !declaringType.IsNil &&
            IsExternallyVisible(reader.GetTypeDefinition(declaringType), reader);
    }

    private static bool IsExternallyVisible(FieldAttributes attributes)
    {
        return (attributes & FieldAttributes.FieldAccessMask) is
            FieldAttributes.Public or
            FieldAttributes.Family or
            FieldAttributes.FamORAssem;
    }

    private static bool IsExternallyVisible(MethodAttributes attributes)
    {
        return (attributes & MethodAttributes.MemberAccessMask) is
            MethodAttributes.Public or
            MethodAttributes.Family or
            MethodAttributes.FamORAssem;
    }

    private static string GetTypeAccess(TypeAttributes attributes)
    {
        return (attributes & TypeAttributes.VisibilityMask) switch
        {
            TypeAttributes.Public or TypeAttributes.NestedPublic => "public",
            TypeAttributes.NestedFamily => "protected",
            TypeAttributes.NestedFamORAssem => "protected-public",
            _ => "nonpublic"
        };
    }

    private static string GetMemberAccess(FieldAttributes attributes)
    {
        return (attributes & FieldAttributes.FieldAccessMask) switch
        {
            FieldAttributes.Public => "public",
            FieldAttributes.Family => "protected",
            FieldAttributes.FamORAssem => "protected-public",
            _ => "nonpublic"
        };
    }

    private static string GetMemberAccess(MethodAttributes attributes)
    {
        return (attributes & MethodAttributes.MemberAccessMask) switch
        {
            MethodAttributes.Public => "public",
            MethodAttributes.Family => "protected",
            MethodAttributes.FamORAssem => "protected-public",
            _ => "nonpublic"
        };
    }

    private static string GetTypeKind(TypeDefinition type, string baseType)
    {
        if (type.Attributes.HasFlag(TypeAttributes.Interface))
            return "interface";
        return baseType switch
        {
            "System.Enum" => "enum",
            "System.ValueType" => "struct",
            "System.MulticastDelegate" => "delegate",
            _ => "class"
        };
    }

    private static string FormatParameterAttributes(ParameterAttributes attributes)
    {
        var values = new List<string>(3);
        if (attributes.HasFlag(ParameterAttributes.In))
            values.Add("in");
        if (attributes.HasFlag(ParameterAttributes.Out))
            values.Add("out");
        if (attributes.HasFlag(ParameterAttributes.Optional))
            values.Add("optional");
        return values.Count == 0 ? "-" : string.Join("+", values);
    }

    private static string FormatConstant(
        MetadataReader reader,
        ConstantHandle handle)
    {
        if (handle.IsNil)
            return "-";
        var constant = reader.GetConstant(handle);
        var bytes = reader.GetBlobBytes(constant.Value);
        return $"{constant.TypeCode}:{Convert.ToHexString(bytes)}";
    }

    private readonly record struct GenericContext
    {
        public static GenericContext Empty => default;
    }

    private sealed class CanonicalSignatureProvider :
        ISignatureTypeProvider<string, GenericContext>
    {
        private readonly MetadataNameFormatter _formatter;

        public CanonicalSignatureProvider(MetadataNameFormatter formatter)
        {
            _formatter = formatter;
        }

        public string GetArrayType(string elementType, ArrayShape shape)
        {
            var bounds = shape.Rank == 1
                ? "*"
                : new string(',', shape.Rank - 1);
            return $"{elementType}[{bounds}]";
        }

        public string GetByReferenceType(string elementType) => $"{elementType}&";

        public string GetFunctionPointerType(MethodSignature<string> signature)
        {
            return $"fnptr({string.Join(",", signature.ParameterTypes)})->" +
                signature.ReturnType;
        }

        public string GetGenericInstantiation(
            string genericType,
            ImmutableArray<string> typeArguments)
        {
            return $"{genericType}<{string.Join(",", typeArguments)}>";
        }

        public string GetGenericMethodParameter(GenericContext context, int index)
            => $"!!{index}";

        public string GetGenericTypeParameter(GenericContext context, int index)
            => $"!{index}";

        public string GetModifiedType(
            string modifier,
            string unmodifiedType,
            bool isRequired)
        {
            return $"{unmodifiedType} {(isRequired ? "modreq" : "modopt")}({modifier})";
        }

        public string GetPinnedType(string elementType) => $"{elementType} pinned";

        public string GetPointerType(string elementType) => $"{elementType}*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Void => "System.Void",
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.TypedReference => "System.TypedReference",
            _ => typeCode.ToString()
        };

        public string GetSZArrayType(string elementType) => $"{elementType}[]";

        public string GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind) => _formatter.GetTypeName(handle);

        public string GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) => _formatter.GetTypeName(handle);

        public string GetTypeFromSpecification(
            MetadataReader reader,
            GenericContext genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind)
        {
            return reader.GetTypeSpecification(handle)
                .DecodeSignature(this, genericContext);
        }
    }

    private sealed class MetadataNameFormatter
    {
        private readonly MetadataReader _reader;

        public MetadataNameFormatter(MetadataReader reader)
        {
            _reader = reader;
        }

        public string GetTypeNamespace(TypeDefinitionHandle handle)
        {
            var type = _reader.GetTypeDefinition(handle);
            var declaringType = type.GetDeclaringType();
            return declaringType.IsNil
                ? _reader.GetString(type.Namespace)
                : GetTypeNamespace(declaringType);
        }

        public string GetTypeName(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition =>
                GetTypeName((TypeDefinitionHandle)handle),
            HandleKind.TypeReference =>
                GetTypeName((TypeReferenceHandle)handle),
            HandleKind.TypeSpecification =>
                _reader.GetTypeSpecification((TypeSpecificationHandle)handle)
                    .DecodeSignature(
                        new CanonicalSignatureProvider(this),
                        GenericContext.Empty),
            _ => $"<{handle.Kind}>"
        };

        public string GetTypeName(TypeDefinitionHandle handle)
        {
            var type = _reader.GetTypeDefinition(handle);
            var name = _reader.GetString(type.Name);
            var declaringType = type.GetDeclaringType();
            if (!declaringType.IsNil)
                return $"{GetTypeName(declaringType)}+{name}";
            var typeNamespace = _reader.GetString(type.Namespace);
            return string.IsNullOrEmpty(typeNamespace)
                ? name
                : $"{typeNamespace}.{name}";
        }

        public string GetTypeName(TypeReferenceHandle handle)
        {
            var type = _reader.GetTypeReference(handle);
            var name = _reader.GetString(type.Name);
            if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                return $"{GetTypeName((TypeReferenceHandle)type.ResolutionScope)}+{name}";
            }

            var typeNamespace = _reader.GetString(type.Namespace);
            return string.IsNullOrEmpty(typeNamespace)
                ? name
                : $"{typeNamespace}.{name}";
        }

        public string? GetAttributeTypeName(EntityHandle constructor)
        {
            return constructor.Kind switch
            {
                HandleKind.MethodDefinition => GetTypeName(
                    _reader.GetMethodDefinition(
                        (MethodDefinitionHandle)constructor).GetDeclaringType()),
                HandleKind.MemberReference => GetMemberReferenceParentType(
                    _reader.GetMemberReference(
                        (MemberReferenceHandle)constructor).Parent),
                _ => null
            };
        }

        private string? GetMemberReferenceParentType(EntityHandle parent)
        {
            return parent.Kind switch
            {
                HandleKind.TypeDefinition or
                HandleKind.TypeReference or
                HandleKind.TypeSpecification => GetTypeName(parent),
                _ => null
            };
        }
    }
}
