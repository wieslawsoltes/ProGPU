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
        var layout = type.GetLayout();
        var layoutKind = GetLayoutKind(type.Attributes);
        var layoutFields = FormatLayoutFields(
            reader,
            formatter,
            typeHandle,
            type,
            kind,
            layoutKind);
        entries.Add(
            $"type|{typeName}|access={GetTypeAccess(type.Attributes)};" +
            $"kind={kind};abstract={type.Attributes.HasFlag(TypeAttributes.Abstract)};" +
            $"sealed={type.Attributes.HasFlag(TypeAttributes.Sealed)};" +
            $"base={baseType};arity={genericArity};" +
            $"layout={layoutKind};charset={GetStringFormat(type.Attributes)};" +
            $"pack={layout.PackingSize};size={layout.Size};" +
            $"layoutfields={layoutFields}");

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
            if (IsExternallyVisibleInterface(reader, implementation.Interface) &&
                ShouldIncludeApiInterface(kind, interfaceName))
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
                provider,
                typeName,
                @event,
                accessors,
                entries);
        }

        int fieldOrder = 0;
        foreach (var fieldHandle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(fieldHandle);
            bool isLayoutField = (kind is "struct" or "class") &&
                layoutKind is "sequential" or "explicit" &&
                !field.Attributes.HasFlag(FieldAttributes.Static);
            int currentFieldOrder = isLayoutField ? fieldOrder++ : -1;
            if (!IsExternallyVisible(field.Attributes))
                continue;

            var fieldType = field.DecodeSignature(
                provider,
                GenericContext.Empty);
            var fieldName = reader.GetString(field.Name);
            var constant = FormatConstant(reader, field.GetDefaultValue());
            string layoutMetadata = isLayoutField
                ? $"order={currentFieldOrder};offset={field.GetOffset()}"
                : "order=-;offset=-";
            entries.Add(
                $"field|{typeName}|access={GetMemberAccess(field.Attributes)};" +
                $"static={field.Attributes.HasFlag(FieldAttributes.Static)};" +
                $"readonly={field.Attributes.HasFlag(FieldAttributes.InitOnly)};" +
                $"literal={field.Attributes.HasFlag(FieldAttributes.Literal)};" +
                $"type={fieldType};name={fieldName};constant={constant};" +
                $"{layoutMetadata};" +
                $"marshal={FormatMarshallingDescriptor(reader, field)}");
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
        var methodName = reader.GetString(method.Name);
        var owner = FormatMethodOwner(
            typeName,
            methodName,
            method.GetGenericParameters().Count,
            signature);
        var parameterDefinitions = method.GetParameters()
            .Select(reader.GetParameter)
            .OrderBy(parameter => parameter.SequenceNumber)
            .ToArray();
        var parameterText = new string[signature.ParameterTypes.Length];
        Parameter? returnDefinition = parameterDefinitions
            .Where(parameter => parameter.SequenceNumber == 0)
            .Select(static parameter => (Parameter?)parameter)
            .FirstOrDefault();
        int parameterCursor = 0;
        while (parameterCursor < parameterDefinitions.Length &&
               parameterDefinitions[parameterCursor].SequenceNumber == 0)
        {
            parameterCursor++;
        }
        for (var index = 0; index < parameterText.Length; index++)
        {
            while (parameterCursor < parameterDefinitions.Length &&
                   parameterDefinitions[parameterCursor].SequenceNumber < index + 1)
            {
                parameterCursor++;
            }
            var hasDefinition = parameterCursor < parameterDefinitions.Length &&
                parameterDefinitions[parameterCursor].SequenceNumber == index + 1;
            Parameter? parameter = hasDefinition
                ? parameterDefinitions[parameterCursor++]
                : null;
            parameterText[index] = FormatAccessorParameterMetadata(
                reader,
                parameter,
                signature.ParameterTypes[index],
                $"arg{index}");
        }

        foreach (var parameter in parameterDefinitions)
        {
            int sequence = parameter.SequenceNumber;
            string parameterOwner;
            if (sequence == 0)
            {
                parameterOwner = $"{owner}.return({signature.ReturnType})";
            }
            else
            {
                int index = sequence - 1;
                string parameterType = index < signature.ParameterTypes.Length
                    ? signature.ParameterTypes[index]
                    : "-";
                parameterOwner =
                    $"{owner}.parameter[{index}]({parameterType}:{reader.GetString(parameter.Name)})";
            }
            AddAttributes(
                reader,
                formatter,
                parameterOwner,
                parameter.GetCustomAttributes(),
                entries);
        }

        entries.Add(
            $"method|{typeName}|access={GetMemberAccess(method.Attributes)};" +
            $"static={method.Attributes.HasFlag(MethodAttributes.Static)};" +
            $"abstract={method.Attributes.HasFlag(MethodAttributes.Abstract)};" +
            $"virtual={method.Attributes.HasFlag(MethodAttributes.Virtual)};" +
            $"final={method.Attributes.HasFlag(MethodAttributes.Final)};" +
            $"newslot={method.Attributes.HasFlag(MethodAttributes.NewSlot)};" +
            $"return={signature.ReturnType};" +
            $"returnmetadata={FormatReturnParameterMetadata(reader, returnDefinition)};" +
            $"name={methodName};" +
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

    private static string FormatMethodOwner(
        string typeName,
        string methodName,
        int genericArity,
        MethodSignature<string> signature) =>
        $"{typeName}.{methodName}`{genericArity}" +
        $"({string.Join(",", signature.ParameterTypes)})->" +
        signature.ReturnType;

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
        var propertyOwner =
            $"{typeName}.{propertyName}" +
            $"({string.Join(",", signature.ParameterTypes)})->" +
            signature.ReturnType;
        entries.Add(
            $"property|{typeName}|access={access};type={signature.ReturnType};" +
            $"name={propertyName};index=({string.Join(",", signature.ParameterTypes)});" +
            $"get={GetAccessorAccess(reader, accessors.Getter)};" +
            $"set={GetAccessorAccess(reader, accessors.Setter)};" +
            FormatAccessorFlags(reader, "get", accessors.Getter) + ";" +
            FormatAccessorFlags(reader, "set", accessors.Setter) + ";" +
            $"getmetadata={FormatAccessorMetadata(reader, provider, accessors.Getter)};" +
            $"setmetadata={FormatAccessorMetadata(reader, provider, accessors.Setter)}");
        AddAttributes(
            reader,
            formatter,
            propertyOwner,
            property.GetCustomAttributes(),
            entries);
        AddAccessorAttributes(
            reader,
            formatter,
            provider,
            propertyOwner,
            "get",
            accessors.Getter,
            entries);
        AddAccessorAttributes(
            reader,
            formatter,
            provider,
            propertyOwner,
            "set",
            accessors.Setter,
            entries);
    }

    private static string FormatAccessorMetadata(
        MetadataReader reader,
        CanonicalSignatureProvider provider,
        MethodDefinitionHandle handle)
    {
        if (handle.IsNil)
            return "-";

        MethodDefinition method = reader.GetMethodDefinition(handle);
        if (!IsExternallyVisible(method.Attributes))
            return "nonpublic";
        MethodSignature<string> signature = method.DecodeSignature(
            provider,
            GenericContext.Empty);
        Parameter[] definitions = method.GetParameters()
            .Select(reader.GetParameter)
            .OrderBy(parameter => parameter.SequenceNumber)
            .ToArray();
        Parameter? returnDefinition = definitions
            .Where(parameter => parameter.SequenceNumber == 0)
            .Select(static parameter => (Parameter?)parameter)
            .FirstOrDefault();
        string returnMetadata = FormatAccessorParameterMetadata(
            reader,
            returnDefinition,
            signature.ReturnType,
            "return");
        var parameters = new string[signature.ParameterTypes.Length];
        for (int index = 0; index < parameters.Length; index++)
        {
            int sequence = index + 1;
            Parameter? definition = definitions
                .Where(parameter =>
                    parameter.SequenceNumber == sequence)
                .Select(static parameter => (Parameter?)parameter)
                .FirstOrDefault();
            parameters[index] = FormatAccessorParameterMetadata(
                reader,
                definition,
                signature.ParameterTypes[index],
                $"arg{index}");
        }
        return $"return=({returnMetadata}),params=({string.Join(",", parameters)})";
    }

    private static string FormatAccessorParameterMetadata(
        MetadataReader reader,
        Parameter? definition,
        string type,
        string fallbackName)
    {
        if (definition is not Parameter parameter)
            return $"-:{type}:{fallbackName}:-:marshal=-";

        string name = reader.GetString(parameter.Name);
        return $"{FormatParameterAttributes(parameter.Attributes)}:" +
            $"{type}:{name}:{FormatConstant(reader, parameter.GetDefaultValue())}:" +
            $"marshal={FormatMarshallingDescriptor(reader, parameter)}";
    }

    private static void AddAccessorAttributes(
        MetadataReader reader,
        MetadataNameFormatter formatter,
        CanonicalSignatureProvider provider,
        string propertyOwner,
        string accessorKind,
        MethodDefinitionHandle handle,
        ISet<string> entries)
    {
        if (handle.IsNil)
            return;

        MethodDefinition method = reader.GetMethodDefinition(handle);
        if (!IsExternallyVisible(method.Attributes))
            return;
        MethodSignature<string> signature = method.DecodeSignature(
            provider,
            GenericContext.Empty);
        string accessorOwner = $"{propertyOwner}.{accessorKind}";
        AddAttributes(
            reader,
            formatter,
            accessorOwner,
            method.GetCustomAttributes(),
            entries);
        foreach (Parameter parameter in method.GetParameters()
                     .Select(reader.GetParameter)
                     .OrderBy(parameter => parameter.SequenceNumber))
        {
            int sequence = parameter.SequenceNumber;
            string name = reader.GetString(parameter.Name);
            string parameterOwner;
            if (sequence == 0)
            {
                parameterOwner =
                    $"{accessorOwner}.return({signature.ReturnType}:{name})";
            }
            else
            {
                int index = sequence - 1;
                string parameterType = index < signature.ParameterTypes.Length
                    ? signature.ParameterTypes[index]
                    : "-";
                parameterOwner =
                    $"{accessorOwner}.parameter[{index}]({parameterType}:{name})";
            }
            AddAttributes(
                reader,
                formatter,
                parameterOwner,
                parameter.GetCustomAttributes(),
                entries);
        }
    }

    private static string FormatAccessorFlags(
        MetadataReader reader,
        string prefix,
        MethodDefinitionHandle handle)
    {
        if (handle.IsNil)
        {
            return $"{prefix}static=-;{prefix}abstract=-;" +
                $"{prefix}virtual=-;{prefix}final=-;{prefix}newslot=-";
        }

        MethodAttributes attributes =
            reader.GetMethodDefinition(handle).Attributes;
        return $"{prefix}static={attributes.HasFlag(MethodAttributes.Static)};" +
            $"{prefix}abstract={attributes.HasFlag(MethodAttributes.Abstract)};" +
            $"{prefix}virtual={attributes.HasFlag(MethodAttributes.Virtual)};" +
            $"{prefix}final={attributes.HasFlag(MethodAttributes.Final)};" +
            $"{prefix}newslot={attributes.HasFlag(MethodAttributes.NewSlot)}";
    }

    private static void AddEvent(
        MetadataReader reader,
        MetadataNameFormatter formatter,
        CanonicalSignatureProvider provider,
        string typeName,
        EventDefinition @event,
        EventAccessors accessors,
        ISet<string> entries)
    {
        if (!TryGetAccessorAccess(reader, accessors.Adder, accessors.Remover, out var access))
            return;

        var eventName = reader.GetString(@event.Name);
        var eventType = formatter.GetTypeName(@event.Type);
        var eventOwner = $"{typeName}.{eventName}()->{eventType}";
        entries.Add(
            $"event|{typeName}|access={access};" +
            $"type={eventType};name={eventName};" +
            $"add={GetAccessorAccess(reader, accessors.Adder)};" +
            $"remove={GetAccessorAccess(reader, accessors.Remover)};" +
            FormatAccessorFlags(reader, "add", accessors.Adder) + ";" +
            FormatAccessorFlags(reader, "remove", accessors.Remover) + ";" +
            $"addmetadata={FormatAccessorMetadata(reader, provider, accessors.Adder)};" +
            $"removemetadata={FormatAccessorMetadata(reader, provider, accessors.Remover)}");
        AddAttributes(
            reader,
            formatter,
            eventOwner,
            @event.GetCustomAttributes(),
            entries);
        AddAccessorAttributes(
            reader,
            formatter,
            provider,
            eventOwner,
            "add",
            accessors.Adder,
            entries);
        AddAccessorAttributes(
            reader,
            formatter,
            provider,
            eventOwner,
            "remove",
            accessors.Remover,
            entries);
    }

    private static string GetLayoutKind(TypeAttributes attributes) =>
        (attributes & TypeAttributes.LayoutMask) switch
        {
            TypeAttributes.SequentialLayout => "sequential",
            TypeAttributes.ExplicitLayout => "explicit",
            _ => "auto"
        };

    private static string GetStringFormat(TypeAttributes attributes) =>
        (attributes & TypeAttributes.StringFormatMask) switch
        {
            TypeAttributes.UnicodeClass => "unicode",
            TypeAttributes.AutoClass => "auto",
            TypeAttributes.CustomFormatClass => "custom",
            _ => "ansi"
        };

    private static string FormatLayoutFields(
        MetadataReader reader,
        MetadataNameFormatter formatter,
        TypeDefinitionHandle typeHandle,
        TypeDefinition type,
        string kind,
        string layoutKind)
    {
        if (kind is not ("struct" or "class") ||
            layoutKind is not ("sequential" or "explicit"))
            return "-";

        var path = new HashSet<TypeDefinitionHandle> { typeHandle };
        var provider = new LayoutSignatureProvider(formatter);
        var fields = new List<string>();
        foreach (FieldDefinitionHandle handle in type.GetFields())
        {
            FieldDefinition field = reader.GetFieldDefinition(handle);
            if (field.Attributes.HasFlag(FieldAttributes.Static))
                continue;

            LayoutTypeInfo fieldType = field.DecodeSignature(
                provider,
                GenericContext.Empty);
            fields.Add(
                $"{{order={fields.Count};offset={field.GetOffset()};" +
                $"type={FormatLayoutType(reader, formatter, fieldType, path)};" +
                $"marshal={FormatMarshallingDescriptor(reader, field)}}}");
        }
        return $"({string.Join(",", fields)})";
    }

    private static string FormatLayoutType(
        MetadataReader reader,
        MetadataNameFormatter formatter,
        LayoutTypeInfo fieldType,
        ISet<TypeDefinitionHandle> path)
    {
        if (fieldType.EmbeddedValueType.IsNil)
            return fieldType.Display;

        TypeDefinitionHandle handle = fieldType.EmbeddedValueType;
        if (!path.Add(handle))
            return $"{fieldType.Display}{{recursive}}";

        try
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            string baseType = type.BaseType.IsNil
                ? "-"
                : formatter.GetTypeName(type.BaseType);
            string kind = GetTypeKind(type, baseType);
            string layoutKind = GetLayoutKind(type.Attributes);
            TypeLayout layout = type.GetLayout();
            var provider = new LayoutSignatureProvider(formatter);
            var fields = new List<string>();
            foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
            {
                FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
                if (field.Attributes.HasFlag(FieldAttributes.Static))
                    continue;

                LayoutTypeInfo nestedType = field.DecodeSignature(
                    provider,
                    GenericContext.Empty);
                fields.Add(
                    $"{{order={fields.Count};offset={field.GetOffset()};" +
                    $"type={FormatLayoutType(reader, formatter, nestedType, path)};" +
                    $"marshal={FormatMarshallingDescriptor(reader, field)}}}");
            }
            return $"{fieldType.Display}{{kind={kind};layout={layoutKind};" +
                $"charset={GetStringFormat(type.Attributes)};" +
                $"pack={layout.PackingSize};size={layout.Size};" +
                $"fields=({string.Join(",", fields)})}}";
        }
        finally
        {
            path.Remove(handle);
        }
    }

    private static string FormatMarshallingDescriptor(
        MetadataReader reader,
        FieldDefinition field)
    {
        BlobHandle descriptor = field.GetMarshallingDescriptor();
        return descriptor.IsNil
            ? "-"
            : Convert.ToHexString(reader.GetBlobBytes(descriptor));
    }

    private static string FormatMarshallingDescriptor(
        MetadataReader reader,
        Parameter parameter)
    {
        BlobHandle descriptor = parameter.GetMarshallingDescriptor();
        return descriptor.IsNil
            ? "-"
            : Convert.ToHexString(reader.GetBlobBytes(descriptor));
    }

    private static string FormatMarshallingDescriptor(
        MetadataReader reader,
        Parameter? parameter) =>
        parameter is Parameter definition
            ? FormatMarshallingDescriptor(reader, definition)
            : "-";

    private static string FormatReturnParameterMetadata(
        MetadataReader reader,
        Parameter? parameter)
    {
        if (parameter is not Parameter definition)
            return "flags=-;default=-;marshal=-";

        return $"flags={FormatParameterAttributes(definition.Attributes)};" +
            $"default={FormatConstant(reader, definition.GetDefaultValue())};" +
            $"marshal={FormatMarshallingDescriptor(reader, definition)}";
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
            !IsCompilerImplementationAttribute(attributeType) &&
            attributeType is not
                "System.CodeDom.Compiler.GeneratedCodeAttribute" and not
                "System.Diagnostics.DebuggerBrowsableAttribute" and not
                "System.Diagnostics.DebuggerDisplayAttribute" and not
                "System.Runtime.InteropServices.GuidAttribute";
    }

    private static bool IsCompilerImplementationAttribute(string attributeType) =>
        attributeType is
            "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute" or
            "System.Runtime.CompilerServices.AsyncStateMachineAttribute" or
            "System.Runtime.CompilerServices.CompilationRelaxationsAttribute" or
            "System.Runtime.CompilerServices.CompilerGeneratedAttribute" or
            "System.Runtime.CompilerServices.IteratorStateMachineAttribute" or
            "System.Runtime.CompilerServices.RuntimeCompatibilityAttribute" or
            "System.Runtime.CompilerServices.SkipLocalsInitAttribute" or
            "System.Runtime.CompilerServices.StateMachineAttribute";

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

    private static bool IsExternallyVisibleInterface(
        MetadataReader reader,
        EntityHandle interfaceHandle)
    {
        // An interface implemented through a TypeDefinition belongs to this
        // module, so its visibility is knowable. Projected WinRT assemblies
        // use private *Overrides interfaces as implementation plumbing on
        // otherwise public classes; those are not a consumable API contract.
        // Type references/specifications resolve outside this local metadata
        // surface and remain part of the public relationship. This is O(1)
        // work and storage per implemented-interface row.
        return interfaceHandle.Kind != HandleKind.TypeDefinition ||
            IsExternallyVisible(
                reader.GetTypeDefinition(
                    (TypeDefinitionHandle)interfaceHandle),
                reader);
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

    private readonly record struct LayoutTypeInfo(
        string Display,
        TypeDefinitionHandle EmbeddedValueType = default);

    private sealed class LayoutSignatureProvider :
        ISignatureTypeProvider<LayoutTypeInfo, GenericContext>
    {
        private readonly MetadataNameFormatter _formatter;

        public LayoutSignatureProvider(MetadataNameFormatter formatter)
        {
            _formatter = formatter;
        }

        public LayoutTypeInfo GetArrayType(LayoutTypeInfo elementType, ArrayShape shape)
        {
            string bounds = shape.Rank == 1
                ? "*"
                : new string(',', shape.Rank - 1);
            return new($"{elementType.Display}[{bounds}]");
        }

        public LayoutTypeInfo GetByReferenceType(LayoutTypeInfo elementType) =>
            new($"{elementType.Display}&");

        public LayoutTypeInfo GetFunctionPointerType(
            MethodSignature<LayoutTypeInfo> signature) =>
            new(
                $"fnptr({string.Join(",", signature.ParameterTypes.Select(static type => type.Display))})->" +
                signature.ReturnType.Display);

        public LayoutTypeInfo GetGenericInstantiation(
            LayoutTypeInfo genericType,
            ImmutableArray<LayoutTypeInfo> typeArguments) =>
            new(
                $"{genericType.Display}<" +
                $"{string.Join(",", typeArguments.Select(static type => type.Display))}>",
                genericType.EmbeddedValueType);

        public LayoutTypeInfo GetGenericMethodParameter(
            GenericContext context,
            int index) => new($"!!{index}");

        public LayoutTypeInfo GetGenericTypeParameter(
            GenericContext context,
            int index) => new($"!{index}");

        public LayoutTypeInfo GetModifiedType(
            LayoutTypeInfo modifier,
            LayoutTypeInfo unmodifiedType,
            bool isRequired) =>
            new(
                $"{unmodifiedType.Display} " +
                $"{(isRequired ? "modreq" : "modopt")}({modifier.Display})",
                unmodifiedType.EmbeddedValueType);

        public LayoutTypeInfo GetPinnedType(LayoutTypeInfo elementType) =>
            new($"{elementType.Display} pinned");

        public LayoutTypeInfo GetPointerType(LayoutTypeInfo elementType) =>
            new($"{elementType.Display}*");

        public LayoutTypeInfo GetPrimitiveType(PrimitiveTypeCode typeCode) =>
            new(typeCode switch
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
            });

        public LayoutTypeInfo GetSZArrayType(LayoutTypeInfo elementType) =>
            new($"{elementType.Display}[]");

        public LayoutTypeInfo GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            string baseType = type.BaseType.IsNil
                ? "-"
                : _formatter.GetTypeName(type.BaseType);
            bool isValueType = baseType is "System.ValueType" or "System.Enum";
            return new(
                _formatter.GetTypeName(handle),
                isValueType ? handle : default);
        }

        public LayoutTypeInfo GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind) => new(_formatter.GetTypeName(handle));

        public LayoutTypeInfo GetTypeFromSpecification(
            MetadataReader reader,
            GenericContext genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) =>
            reader.GetTypeSpecification(handle)
                .DecodeSignature(this, genericContext);
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
