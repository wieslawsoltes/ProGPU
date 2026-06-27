using System.Text;
using System.Text.RegularExpressions;

namespace ProGPU.DirectX;

internal static class ProGpuDirectXHlslTranslator
{
    private static readonly Regex s_structRegex = new(
        @"\bstruct\s+(?<name>[A-Za-z_]\w*)\s*\{(?<body>.*?)\}\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex s_fieldRegex = new(
        @"\b(?<type>[A-Za-z_]\w*)\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*(?<semantic>[A-Za-z_]\w*))?\s*;",
        RegexOptions.Compiled);

    private static readonly Regex s_cbufferRegex = new(
        @"\bcbuffer\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*b(?<slot>\d+)\s*\))?\s*\{(?<body>.*?)\}\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex s_cbufferFieldRegex = new(
        @"\b(?<type>[A-Za-z_]\w*)\s+(?<name>[A-Za-z_]\w*)\s*;",
        RegexOptions.Compiled);

    private static readonly Regex s_texture2DResourceRegex = new(
        @"\bTexture2D(?:\s*<\s*(?<type>[A-Za-z_]\w*)\s*>)?\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*t(?<slot>\d+)\s*\))?\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_texture2DArrayResourceRegex = new(
        @"\bTexture2DArray(?:\s*<\s*(?<type>[A-Za-z_]\w*)\s*>)?\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*t(?<slot>\d+)\s*\))?\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_texture3DResourceRegex = new(
        @"\bTexture3D(?:\s*<\s*(?<type>[A-Za-z_]\w*)\s*>)?\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*t(?<slot>\d+)\s*\))?\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_structuredBufferResourceRegex = new(
        @"\bStructuredBuffer\s*<\s*(?<type>[A-Za-z_]\w*)\s*>\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*t(?<slot>\d+)\s*\))?\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_typedBufferResourceRegex = new(
        @"\bBuffer\s*<\s*(?<type>[A-Za-z_]\w*)\s*>\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*t(?<slot>\d+)\s*\))?\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_rwStructuredBufferResourceRegex = new(
        @"\bRWStructuredBuffer\s*<\s*(?<type>[A-Za-z_]\w*)\s*>\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*u(?<slot>\d+)\s*\))?\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_rwTypedBufferResourceRegex = new(
        @"\bRWBuffer\s*<\s*(?<type>[A-Za-z_]\w*)\s*>\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*u(?<slot>\d+)\s*\))?\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_rwTexture2DResourceRegex = new(
        @"\bRWTexture2D(?:\s*<\s*(?<type>[A-Za-z_]\w*)\s*>)?\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*u(?<slot>\d+)\s*\))?\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_byteAddressBufferResourceRegex = new(
        @"\bByteAddressBuffer\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*t(?<slot>\d+)\s*\))?\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_rwByteAddressBufferResourceRegex = new(
        @"\bRWByteAddressBuffer\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*u(?<slot>\d+)\s*\))?\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_samplerStateResourceRegex = new(
        @"\bSamplerState\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*s(?<slot>\d+)\s*\))?\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_samplerComparisonStateResourceRegex = new(
        @"\bSamplerComparisonState\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*s(?<slot>\d+)\s*\))?\s*;",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_textureMethodCallStartRegex = new(
        @"(?<texture>[A-Za-z_]\w*)\.(?<method>SampleCmpLevelZero|SampleCmp|SampleLevel|SampleBias|SampleGrad|Sample|Load)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex s_byteAddressBufferMethodCallStartRegex = new(
        @"(?<buffer>[A-Za-z_]\w*)\.(?<method>Load4|Load3|Load2|Load)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex s_byteAddressBufferStoreStatementRegex = new(
        @"^(?<buffer>[A-Za-z_]\w*)\.(?<method>Store4|Store3|Store2|Store)\s*\((?<arguments>.*)\)$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex s_byteAddressBufferInterlockedStatementRegex = new(
        @"^(?<buffer>[A-Za-z_]\w*)\.(?<method>Interlocked(?:Add|And|Or|Xor|Min|Max|Exchange|CompareExchange))\s*\((?<arguments>.*)\)$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex s_hlslIntrinsicCallStartRegex = new(
        @"(?<!\.)\b(?<name>abs|acos|asin|atan|atan2|ceil|clamp|cos|cross|ddx|ddy|distance|dot|exp|exp2|floor|frac|length|lerp|log|log2|mad|max|min|mul|normalize|pow|rcp|reflect|refract|round|rsqrt|saturate|sign|sin|smoothstep|sqrt|tan)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex s_unsupportedRegex = new(
        @"\b(tbuffer|Texture(?!(?:2D|2DArray|3D)\b)\w*|Sampler(?!(?:State|ComparisonState)\b)\w*|RWTexture(?!(?:2D)\b)\w*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool TryTranslate(DxShaderDescriptor descriptor, out string wgsl)
    {
        wgsl = string.Empty;
        if (descriptor.SourceKind != DxShaderSourceKind.HlslText ||
            string.IsNullOrWhiteSpace(descriptor.Source) ||
            string.IsNullOrWhiteSpace(descriptor.EntryPoint))
        {
            return false;
        }

        var source = StripComments(descriptor.Source);
        if (s_unsupportedRegex.IsMatch(source))
        {
            return false;
        }

        try
        {
            var constantBuffers = ParseConstantBuffers(source);
            var structs = ParseStructs(source);
            var shaderResources = ParseShaderResources(source, structs);
            if (!TryParseFunction(source, descriptor.EntryPoint!, out var function))
            {
                return false;
            }

            wgsl = descriptor.Stage switch
            {
                DxShaderStage.Vertex => TranslateVertexShader(descriptor.Stage, constantBuffers, shaderResources, structs, function),
                DxShaderStage.Pixel => TranslatePixelShader(descriptor.Stage, constantBuffers, shaderResources, structs, function),
                DxShaderStage.Compute => TranslateComputeShader(descriptor.Stage, constantBuffers, shaderResources, structs, function),
                _ => string.Empty
            };

            return !string.IsNullOrWhiteSpace(wgsl);
        }
        catch (NotSupportedException)
        {
            wgsl = string.Empty;
            return false;
        }
    }

    private static string TranslateVertexShader(
        DxShaderStage stage,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyList<HlslShaderResource> shaderResources,
        IReadOnlyDictionary<string, HlslStruct> structs,
        HlslFunction function)
    {
        var builder = new StringBuilder();
        AppendConstantBuffers(builder, stage, constantBuffers);
        AppendStructs(builder, structs);
        AppendShaderResources(builder, stage, shaderResources, structs);
        builder.Append("@vertex\n");
        builder
            .Append("fn ")
            .Append(function.Name)
            .Append('(')
            .Append(TranslateParameters(function.Parameters, structs))
            .Append(") -> ");

        if (structs.ContainsKey(function.ReturnType))
        {
            builder.Append(function.ReturnType);
        }
        else if (IsSystemSemantic(function.ReturnSemantic, "SV_Position"))
        {
            builder.Append("@builtin(position) ").Append(MapType(function.ReturnType));
        }
        else
        {
            throw new NotSupportedException("Vertex HLSL translation requires a struct return or SV_Position return semantic.");
        }

        AppendTranslatedBody(
            builder,
            function,
            constantBuffers,
            shaderResources,
            allowReturnValue: true,
            allowDiscard: false);
        return builder.ToString();
    }

    private static string TranslatePixelShader(
        DxShaderStage stage,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyList<HlslShaderResource> shaderResources,
        IReadOnlyDictionary<string, HlslStruct> structs,
        HlslFunction function)
    {
        var builder = new StringBuilder();
        AppendConstantBuffers(builder, stage, constantBuffers);
        AppendStructs(builder, structs);
        AppendShaderResources(builder, stage, shaderResources, structs);
        builder.Append("@fragment\n");
        builder
            .Append("fn ")
            .Append(function.Name)
            .Append('(')
            .Append(TranslateParameters(function.Parameters, structs))
            .Append(") -> ");

        if (structs.ContainsKey(function.ReturnType))
        {
            builder.Append(function.ReturnType);
        }
        else if (IsSystemSemantic(function.ReturnSemantic, "SV_Target"))
        {
            builder.Append("@location(").Append(GetSemanticIndex(function.ReturnSemantic)).Append(") ").Append(MapType(function.ReturnType));
        }
        else
        {
            throw new NotSupportedException("Pixel HLSL translation requires a struct return or SV_Target return semantic.");
        }

        AppendTranslatedBody(
            builder,
            function,
            constantBuffers,
            shaderResources,
            allowReturnValue: true,
            allowDiscard: true);
        return builder.ToString();
    }

    private static string TranslateComputeShader(
        DxShaderStage stage,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyList<HlslShaderResource> shaderResources,
        IReadOnlyDictionary<string, HlslStruct> structs,
        HlslFunction function)
    {
        if (!string.Equals(function.ReturnType, "void", StringComparison.Ordinal))
        {
            throw new NotSupportedException("Compute HLSL translation requires a void return.");
        }

        var (x, y, z) = function.NumThreads ?? (1u, 1u, 1u);
        var builder = new StringBuilder();
        AppendConstantBuffers(builder, stage, constantBuffers);
        AppendStructs(builder, structs);
        AppendShaderResources(builder, stage, shaderResources, structs);
        builder
            .Append("@compute @workgroup_size(")
            .Append(x)
            .Append(", ")
            .Append(y)
            .Append(", ")
            .Append(z)
            .Append(")\nfn ")
            .Append(function.Name)
            .Append('(')
            .Append(TranslateParameters(function.Parameters, new Dictionary<string, HlslStruct>()))
            .Append(')');

        AppendTranslatedBody(
            builder,
            function,
            constantBuffers,
            shaderResources,
            allowReturnValue: false,
            allowDiscard: false);
        return builder.ToString();
    }

    private static void AppendConstantBuffers(
        StringBuilder builder,
        DxShaderStage stage,
        IReadOnlyList<HlslConstantBuffer> constantBuffers)
    {
        foreach (var constantBuffer in constantBuffers)
        {
            builder.Append("struct ").Append(constantBuffer.Name).Append(" {\n");
            if (constantBuffer.UsesPackedRegisterLayout)
            {
                for (var registerIndex = 0u; registerIndex < constantBuffer.RegisterCount; registerIndex++)
                {
                    builder
                        .Append("    _r")
                        .Append(registerIndex)
                        .Append(": vec4<u32>,\n");
                }
            }
            else
            {
                foreach (var field in constantBuffer.Fields)
                {
                    builder
                        .Append("    ")
                        .Append(field.Name)
                        .Append(": ")
                        .Append(MapType(field.Type))
                        .Append(",\n");
                }
            }

            builder
                .Append("}\n")
                .Append("@group(0) @binding(")
                .Append(ProGpuDirectXNativeBindingMap.GetConstantBufferBinding(stage, constantBuffer.Register))
                .Append(") var<uniform> ")
                .Append(constantBuffer.VariableName)
                .Append(": ")
                .Append(constantBuffer.Name)
                .Append(";\n\n");
        }
    }

    private static void AppendShaderResources(
        StringBuilder builder,
        DxShaderStage stage,
        IReadOnlyList<HlslShaderResource> shaderResources,
        IReadOnlyDictionary<string, HlslStruct> structs)
    {
        foreach (var resource in shaderResources)
        {
            switch (resource.Kind)
            {
                case HlslShaderResourceKind.Texture2D:
                    builder
                        .Append("@group(0) @binding(")
                        .Append(ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(stage, resource.Register))
                        .Append(") var ")
                        .Append(resource.Name)
                        .Append(resource.UsesComparisonSampling ? ": texture_depth_2d;\n" : ": texture_2d<f32>;\n");
                    break;
                case HlslShaderResourceKind.Texture2DArray:
                    builder
                        .Append("@group(0) @binding(")
                        .Append(ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(stage, resource.Register))
                        .Append(") var ")
                        .Append(resource.Name)
                        .Append(resource.UsesComparisonSampling ? ": texture_depth_2d_array;\n" : ": texture_2d_array<f32>;\n");
                    break;
                case HlslShaderResourceKind.Texture3D:
                    builder
                        .Append("@group(0) @binding(")
                        .Append(ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(stage, resource.Register))
                        .Append(") var ")
                        .Append(resource.Name)
                        .Append(": texture_3d<f32>;\n");
                    break;
                case HlslShaderResourceKind.StructuredBuffer:
                case HlslShaderResourceKind.Buffer:
                    builder
                        .Append("@group(0) @binding(")
                        .Append(ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(stage, resource.Register))
                        .Append(") var<storage, read> ")
                        .Append(resource.Name)
                        .Append(": array<")
                        .Append(MapResourceElementType(resource.ElementType!, structs))
                        .Append(">;\n");
                    break;
                case HlslShaderResourceKind.ByteAddressBuffer:
                    builder
                        .Append("@group(0) @binding(")
                        .Append(ProGpuDirectXNativeBindingMap.GetShaderResourceBinding(stage, resource.Register))
                        .Append(") var<storage, read> ")
                        .Append(resource.Name)
                        .Append(": array<u32>;\n");
                    break;
                case HlslShaderResourceKind.RWStructuredBuffer:
                case HlslShaderResourceKind.RWBuffer:
                    if (stage != DxShaderStage.Compute)
                    {
                        throw new NotSupportedException($"HLSL {resource.Kind} resources are currently supported only for compute shaders.");
                    }

                    builder
                        .Append("@group(0) @binding(")
                        .Append(ProGpuDirectXNativeBindingMap.GetNativeBinding(
                            stage,
                            ProGpuDirectXBindingKind.UnorderedAccessView,
                            resource.Register))
                        .Append(") var<storage, read_write> ")
                        .Append(resource.Name)
                        .Append(": array<")
                        .Append(MapResourceElementType(resource.ElementType!, structs))
                        .Append(">;\n");
                    break;
                case HlslShaderResourceKind.RWTexture2D:
                    if (stage != DxShaderStage.Compute)
                    {
                        throw new NotSupportedException($"HLSL {resource.Kind} resources are currently supported only for compute shaders.");
                    }

                    builder
                        .Append("@group(0) @binding(")
                        .Append(ProGpuDirectXNativeBindingMap.GetNativeBinding(
                            stage,
                            ProGpuDirectXBindingKind.UnorderedAccessView,
                            resource.Register))
                        .Append(") var ")
                        .Append(resource.Name)
                        .Append(": texture_storage_2d<")
                        .Append(MapStorageTextureFormat(resource.ElementType!))
                        .Append(", write>;\n");
                    break;
                case HlslShaderResourceKind.RWByteAddressBuffer:
                    if (stage != DxShaderStage.Compute)
                    {
                        throw new NotSupportedException($"HLSL {resource.Kind} resources are currently supported only for compute shaders.");
                    }

                    builder
                        .Append("@group(0) @binding(")
                        .Append(ProGpuDirectXNativeBindingMap.GetNativeBinding(
                            stage,
                            ProGpuDirectXBindingKind.UnorderedAccessView,
                            resource.Register))
                        .Append(") var<storage, read_write> ")
                        .Append(resource.Name)
                        .Append(": array<atomic<u32>>;\n");
                    break;
                case HlslShaderResourceKind.SamplerState:
                case HlslShaderResourceKind.SamplerComparisonState:
                    builder
                        .Append("@group(0) @binding(")
                        .Append(ProGpuDirectXNativeBindingMap.GetSamplerBinding(stage, resource.Register))
                        .Append(") var ")
                        .Append(resource.Name)
                        .Append(resource.Kind == HlslShaderResourceKind.SamplerComparisonState
                            ? ": sampler_comparison;\n"
                            : ": sampler;\n");
                    break;
                default:
                    throw new NotSupportedException($"Unsupported HLSL shader resource '{resource.Kind}'.");
            }
        }

        if (shaderResources.Count > 0)
        {
            builder.Append('\n');
        }
    }

    private static void AppendStructs(StringBuilder builder, IReadOnlyDictionary<string, HlslStruct> structs)
    {
        foreach (var hlslStruct in structs.Values)
        {
            var semanticLocations = CreateUserSemanticLocationMap([hlslStruct]);
            builder.Append("struct ").Append(hlslStruct.Name).Append(" {\n");
            foreach (var field in hlslStruct.Fields)
            {
                builder.Append("    ");
                if (!string.IsNullOrWhiteSpace(field.Semantic))
                {
                    builder
                        .Append(GetFieldAttribute(field.Semantic, semanticLocations))
                        .Append(' ');
                }

                builder
                    .Append(field.Name)
                    .Append(": ")
                    .Append(MapTypeOrIdentifier(field.Type))
                    .Append(",\n");
            }

            builder.Append("}\n\n");
        }
    }

    private static string TranslateParameters(
        IReadOnlyList<HlslParameter> parameters,
        IReadOnlyDictionary<string, HlslStruct> structs)
    {
        var semanticLocations = CreateUserSemanticLocationMap(parameters.Select(parameter => parameter.Semantic));
        return string.Join(
            ", ",
            parameters.Select(parameter =>
            {
                if (structs.ContainsKey(parameter.Type))
                {
                    return $"{parameter.Name}: {parameter.Type}";
                }

                if (string.IsNullOrWhiteSpace(parameter.Semantic))
                {
                    return $"{parameter.Name}: {MapType(parameter.Type)}";
                }

                return $"{GetParameterAttribute(parameter.Semantic, semanticLocations)} {parameter.Name}: {MapType(parameter.Type)}";
            }));
    }

    private static void AppendTranslatedBody(
        StringBuilder builder,
        HlslFunction function,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyList<HlslShaderResource> shaderResources,
        bool allowReturnValue,
        bool allowDiscard)
    {
        var localTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in function.Parameters)
        {
            localTypes[parameter.Name] = parameter.Type;
        }

        builder.Append(" {\n");
        foreach (var rawStatement in function.Body.Split(';'))
        {
            var statement = rawStatement.Trim();
            if (statement.Length == 0)
            {
                continue;
            }

            if (statement.StartsWith("return ", StringComparison.Ordinal))
            {
                if (!allowReturnValue)
                {
                    throw new NotSupportedException("Compute shader returns cannot return values.");
                }

                builder
                    .Append("    return ")
                    .Append(TranslateExpression(statement["return ".Length..].Trim(), constantBuffers, shaderResources, localTypes))
                    .Append(";\n");
                continue;
            }

            if (TryTranslateByteAddressBufferStoreStatement(builder, statement, constantBuffers, shaderResources, localTypes) ||
                TryTranslateRwTexture2DWriteStatement(builder, statement, constantBuffers, shaderResources, localTypes) ||
                TryTranslateClipStatement(builder, statement, constantBuffers, shaderResources, localTypes, allowDiscard) ||
                TryTranslateByteAddressBufferInterlockedStatement(builder, statement, constantBuffers, shaderResources, localTypes))
            {
                continue;
            }

            var declaration = Regex.Match(
                statement,
                @"^(?<type>[A-Za-z_]\w*)\s+(?<name>[A-Za-z_]\w*)$");
            if (declaration.Success)
            {
                builder
                    .Append("    var ")
                    .Append(declaration.Groups["name"].Value)
                    .Append(": ")
                    .Append(MapTypeOrIdentifier(declaration.Groups["type"].Value))
                    .Append(";\n");
                localTypes[declaration.Groups["name"].Value] = declaration.Groups["type"].Value;
                continue;
            }

            var initializedDeclaration = Regex.Match(
                statement,
                @"^(?<type>[A-Za-z_]\w*)\s+(?<name>[A-Za-z_]\w*)\s*=\s*(?<right>.+)$",
                RegexOptions.Singleline);
            if (initializedDeclaration.Success)
            {
                builder
                    .Append("    var ")
                    .Append(initializedDeclaration.Groups["name"].Value)
                    .Append(": ")
                    .Append(MapTypeOrIdentifier(initializedDeclaration.Groups["type"].Value))
                    .Append(" = ")
                    .Append(TranslateExpression(initializedDeclaration.Groups["right"].Value.Trim(), constantBuffers, shaderResources, localTypes))
                    .Append(";\n");
                localTypes[initializedDeclaration.Groups["name"].Value] = initializedDeclaration.Groups["type"].Value;
                continue;
            }

            var assignment = Regex.Match(
                statement,
                @"^(?<left>[A-Za-z_]\w*(?:(?:\[[^\]]+\])|(?:\.[A-Za-z_]\w*))*)\s*=\s*(?<right>.+)$",
                RegexOptions.Singleline);
            if (assignment.Success)
            {
                builder
                    .Append("    ")
                    .Append(assignment.Groups["left"].Value)
                    .Append(" = ")
                    .Append(TranslateExpression(assignment.Groups["right"].Value.Trim(), constantBuffers, shaderResources, localTypes))
                    .Append(";\n");
                continue;
            }

            throw new NotSupportedException($"Unsupported HLSL statement '{statement}'.");
        }

        builder.Append("}\n");
    }

    private static List<HlslConstantBuffer> ParseConstantBuffers(string source)
    {
        var constantBuffers = new List<HlslConstantBuffer>();
        var matches = s_cbufferRegex.Matches(source).Cast<Match>().ToArray();
        var usedRegisters = CreateExplicitRegisterSet(matches);
        uint nextRegister = 0;
        foreach (var match in matches)
        {
            var name = match.Groups["name"].Value;
            var register = ResolveRegister(match, usedRegisters, ref nextRegister);
            var parsedFields = new List<(string Type, string Name)>();

            foreach (Match fieldMatch in s_cbufferFieldRegex.Matches(match.Groups["body"].Value))
            {
                parsedFields.Add((
                    fieldMatch.Groups["type"].Value,
                    fieldMatch.Groups["name"].Value));
            }

            if (parsedFields.Count == 0)
            {
                throw new NotSupportedException($"HLSL cbuffer '{name}' has no translatable fields.");
            }

            var fields = CreateConstantBufferFields(parsedFields);
            constantBuffers.Add(new HlslConstantBuffer(
                name,
                ToVariableName(name),
                register,
                fields,
                RequiresPackedConstantBufferLayout(fields),
                GetConstantBufferRegisterCount(fields)));
        }

        return constantBuffers;
    }

    private static IReadOnlyList<HlslConstantBufferField> CreateConstantBufferFields(IReadOnlyList<(string Type, string Name)> fields)
    {
        var result = new List<HlslConstantBufferField>(fields.Count);
        uint registerIndex = 0;
        uint componentIndex = 0;
        foreach (var (type, name) in fields)
        {
            var registerSpan = GetCBufferRegisterSpan(type);
            if (registerSpan > 1)
            {
                if (componentIndex != 0)
                {
                    registerIndex++;
                    componentIndex = 0;
                }

                result.Add(new HlslConstantBufferField(type, name, registerIndex, componentIndex));
                registerIndex += registerSpan;
                componentIndex = 0;
                continue;
            }

            var componentCount = GetCBufferComponentCount(type);
            if (componentIndex + componentCount > 4)
            {
                registerIndex++;
                componentIndex = 0;
            }

            result.Add(new HlslConstantBufferField(type, name, registerIndex, componentIndex));
            componentIndex += componentCount;
            if (componentIndex == 4)
            {
                registerIndex++;
                componentIndex = 0;
            }
        }

        return result;
    }

    private static bool RequiresPackedConstantBufferLayout(IReadOnlyList<HlslConstantBufferField> fields)
    {
        uint wgslOffset = 0;
        foreach (var field in fields)
        {
            wgslOffset = AlignTo(wgslOffset, GetWgslUniformAlignment(field.Type));
            var hlslOffset = field.RegisterIndex * 16 + field.ComponentIndex * 4;
            if (wgslOffset != hlslOffset)
            {
                return true;
            }

            wgslOffset += GetWgslUniformSize(field.Type);
        }

        return false;
    }

    private static uint GetConstantBufferRegisterCount(IReadOnlyList<HlslConstantBufferField> fields)
    {
        if (fields.Count == 0)
        {
            return 0;
        }

        return fields.Max(field => field.RegisterIndex + GetCBufferRegisterSpan(field.Type));
    }

    private static List<HlslShaderResource> ParseShaderResources(
        string source,
        IReadOnlyDictionary<string, HlslStruct> structs)
    {
        var resources = new List<HlslShaderResource>();
        var pendingResources = CreatePendingShaderResources(source);
        var usedSrvRegisters = CreateExplicitRegisterSet(pendingResources
            .Where(resource => IsShaderResourceSlotKind(resource.Kind))
            .Select(resource => resource.Match));
        var usedUavRegisters = CreateExplicitRegisterSet(pendingResources
            .Where(resource => IsUnorderedAccessSlotKind(resource.Kind))
            .Select(resource => resource.Match));
        var usedSamplerRegisters = CreateExplicitRegisterSet(pendingResources
            .Where(resource => IsSamplerSlotKind(resource.Kind))
            .Select(resource => resource.Match));
        uint nextSrvRegister = 0;
        uint nextUavRegister = 0;
        uint nextSamplerRegister = 0;

        foreach (var pendingResource in pendingResources.OrderBy(resource => resource.Match.Index))
        {
            var match = pendingResource.Match;
            var register = pendingResource.Kind switch
            {
                HlslShaderResourceKind.SamplerState or
                HlslShaderResourceKind.SamplerComparisonState => ResolveRegister(match, usedSamplerRegisters, ref nextSamplerRegister),
                HlslShaderResourceKind.RWStructuredBuffer or
                HlslShaderResourceKind.RWBuffer or
                HlslShaderResourceKind.RWTexture2D or
                HlslShaderResourceKind.RWByteAddressBuffer => ResolveRegister(match, usedUavRegisters, ref nextUavRegister),
                _ => ResolveRegister(match, usedSrvRegisters, ref nextSrvRegister)
            };

            switch (pendingResource.Kind)
            {
                case HlslShaderResourceKind.Texture2D:
                case HlslShaderResourceKind.Texture2DArray:
                case HlslShaderResourceKind.Texture3D:
                    var resourceType = match.Groups["type"].Success ? match.Groups["type"].Value : "float4";
                    if (!string.Equals(resourceType, "float4", StringComparison.Ordinal))
                    {
                        throw new NotSupportedException($"HLSL {pendingResource.Kind} resource type '{resourceType}' is not supported.");
                    }

                    resources.Add(new HlslShaderResource(
                        pendingResource.Kind,
                        match.Groups["name"].Value,
                        register,
                        UsesComparisonSampling: UsesComparisonSampling(source, match.Groups["name"].Value)));
                    break;

                case HlslShaderResourceKind.StructuredBuffer:
                case HlslShaderResourceKind.Buffer:
                case HlslShaderResourceKind.RWStructuredBuffer:
                case HlslShaderResourceKind.RWBuffer:
                case HlslShaderResourceKind.RWTexture2D:
                    var elementType = match.Groups["type"].Value;
                    if (pendingResource.Kind == HlslShaderResourceKind.RWTexture2D &&
                        string.IsNullOrWhiteSpace(elementType))
                    {
                        elementType = "float4";
                    }

                    _ = MapResourceElementType(elementType, structs);
                    if (pendingResource.Kind == HlslShaderResourceKind.RWTexture2D)
                    {
                        _ = MapStorageTextureFormat(elementType);
                    }

                    resources.Add(new HlslShaderResource(
                        pendingResource.Kind,
                        match.Groups["name"].Value,
                        register,
                        elementType));
                    break;

                case HlslShaderResourceKind.ByteAddressBuffer:
                case HlslShaderResourceKind.RWByteAddressBuffer:
                case HlslShaderResourceKind.SamplerState:
                case HlslShaderResourceKind.SamplerComparisonState:
                    resources.Add(new HlslShaderResource(
                        pendingResource.Kind,
                        match.Groups["name"].Value,
                        register,
                        UsesComparisonSampling: pendingResource.Kind == HlslShaderResourceKind.SamplerComparisonState ||
                            UsesComparisonSampling(source, match.Groups["name"].Value)));
                    break;

                default:
                    throw new NotSupportedException($"Unsupported HLSL resource kind '{pendingResource.Kind}'.");
            }
        }

        return resources
            .OrderBy(resource => resource.Kind)
            .ThenBy(resource => resource.Register)
            .ToList();
    }

    private static List<PendingHlslShaderResource> CreatePendingShaderResources(string source)
    {
        var resources = new List<PendingHlslShaderResource>();
        AddPendingResources(resources, HlslShaderResourceKind.Texture2D, s_texture2DResourceRegex.Matches(source));
        AddPendingResources(resources, HlslShaderResourceKind.Texture2DArray, s_texture2DArrayResourceRegex.Matches(source));
        AddPendingResources(resources, HlslShaderResourceKind.Texture3D, s_texture3DResourceRegex.Matches(source));
        AddPendingResources(resources, HlslShaderResourceKind.StructuredBuffer, s_structuredBufferResourceRegex.Matches(source));
        AddPendingResources(resources, HlslShaderResourceKind.Buffer, s_typedBufferResourceRegex.Matches(source));
        AddPendingResources(resources, HlslShaderResourceKind.RWStructuredBuffer, s_rwStructuredBufferResourceRegex.Matches(source));
        AddPendingResources(resources, HlslShaderResourceKind.RWBuffer, s_rwTypedBufferResourceRegex.Matches(source));
        AddPendingResources(resources, HlslShaderResourceKind.RWTexture2D, s_rwTexture2DResourceRegex.Matches(source));
        AddPendingResources(resources, HlslShaderResourceKind.ByteAddressBuffer, s_byteAddressBufferResourceRegex.Matches(source));
        AddPendingResources(resources, HlslShaderResourceKind.RWByteAddressBuffer, s_rwByteAddressBufferResourceRegex.Matches(source));
        AddPendingResources(resources, HlslShaderResourceKind.SamplerState, s_samplerStateResourceRegex.Matches(source));
        AddPendingResources(resources, HlslShaderResourceKind.SamplerComparisonState, s_samplerComparisonStateResourceRegex.Matches(source));
        return resources;
    }

    private static void AddPendingResources(
        List<PendingHlslShaderResource> resources,
        HlslShaderResourceKind kind,
        MatchCollection matches)
    {
        foreach (Match match in matches)
        {
            resources.Add(new PendingHlslShaderResource(kind, match));
        }
    }

    private static HashSet<uint> CreateExplicitRegisterSet(IEnumerable<Match> matches)
    {
        var registers = new HashSet<uint>();
        foreach (var match in matches)
        {
            if (match.Groups["slot"].Success)
            {
                registers.Add(uint.Parse(match.Groups["slot"].Value));
            }
        }

        return registers;
    }

    private static uint ResolveRegister(Match match, HashSet<uint> usedRegisters, ref uint nextRegister)
    {
        if (match.Groups["slot"].Success)
        {
            return uint.Parse(match.Groups["slot"].Value);
        }

        while (usedRegisters.Contains(nextRegister))
        {
            nextRegister++;
        }

        var register = nextRegister++;
        usedRegisters.Add(register);
        return register;
    }

    private static bool IsShaderResourceSlotKind(HlslShaderResourceKind kind)
    {
        return kind is HlslShaderResourceKind.Texture2D or
            HlslShaderResourceKind.Texture2DArray or
            HlslShaderResourceKind.Texture3D or
            HlslShaderResourceKind.StructuredBuffer or
            HlslShaderResourceKind.Buffer or
            HlslShaderResourceKind.ByteAddressBuffer;
    }

    private static bool IsUnorderedAccessSlotKind(HlslShaderResourceKind kind)
    {
        return kind is HlslShaderResourceKind.RWStructuredBuffer or
            HlslShaderResourceKind.RWBuffer or
            HlslShaderResourceKind.RWTexture2D or
            HlslShaderResourceKind.RWByteAddressBuffer;
    }

    private static bool IsSamplerSlotKind(HlslShaderResourceKind kind)
    {
        return kind is HlslShaderResourceKind.SamplerState or
            HlslShaderResourceKind.SamplerComparisonState;
    }

    private static bool UsesComparisonSampling(string source, string resourceName)
    {
        return Regex.IsMatch(
            source,
            $@"\b{Regex.Escape(resourceName)}\s*\.\s*SampleCmp(?:LevelZero)?\s*\(",
            RegexOptions.IgnoreCase);
    }

    private static Dictionary<string, HlslStruct> ParseStructs(string source)
    {
        var structs = new Dictionary<string, HlslStruct>(StringComparer.Ordinal);
        foreach (Match match in s_structRegex.Matches(source))
        {
            var name = match.Groups["name"].Value;
            var fields = new List<HlslField>();
            foreach (Match fieldMatch in s_fieldRegex.Matches(match.Groups["body"].Value))
            {
                fields.Add(new HlslField(
                    fieldMatch.Groups["type"].Value,
                    fieldMatch.Groups["name"].Value,
                    fieldMatch.Groups["semantic"].Success ? fieldMatch.Groups["semantic"].Value : null));
            }

            if (fields.Count == 0)
            {
                throw new NotSupportedException($"HLSL struct '{name}' has no translatable fields.");
            }

            structs[name] = new HlslStruct(name, fields);
        }

        return structs;
    }

    private static bool TryParseFunction(string source, string entryPoint, out HlslFunction function)
    {
        function = default;
        var match = Regex.Match(
            source,
            $@"(?<attributes>(?:\[[^\]]+\]\s*)*)\b(?<return>[A-Za-z_]\w*)\s+{Regex.Escape(entryPoint)}\s*\((?<parameters>[^)]*)\)\s*(?::\s*(?<semantic>[A-Za-z_]\w*))?\s*\{{",
            RegexOptions.Singleline);
        if (!match.Success)
        {
            return false;
        }

        var bodyStart = match.Index + match.Length - 1;
        if (!TryExtractBody(source, bodyStart, out var body))
        {
            return false;
        }

        function = new HlslFunction(
            entryPoint,
            match.Groups["return"].Value,
            match.Groups["semantic"].Success ? match.Groups["semantic"].Value : null,
            ParseParameters(match.Groups["parameters"].Value),
            body,
            ParseNumThreads(match.Groups["attributes"].Value));
        return true;
    }

    private static IReadOnlyList<HlslParameter> ParseParameters(string parameters)
    {
        var result = new List<HlslParameter>();
        foreach (var rawParameter in parameters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = Regex.Match(
                rawParameter,
                @"^(?<type>[A-Za-z_]\w*)\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*(?<semantic>[A-Za-z_]\w*))?$");
            if (!match.Success)
            {
                throw new NotSupportedException($"Unsupported HLSL parameter '{rawParameter}'.");
            }

            result.Add(new HlslParameter(
                match.Groups["type"].Value,
                match.Groups["name"].Value,
                match.Groups["semantic"].Success ? match.Groups["semantic"].Value : null));
        }

        return result;
    }

    private static bool TryExtractBody(string source, int openBraceIndex, out string body)
    {
        body = string.Empty;
        var depth = 0;
        for (var index = openBraceIndex; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    body = source[(openBraceIndex + 1)..index];
                    return true;
                }
            }
        }

        return false;
    }

    private static (uint X, uint Y, uint Z)? ParseNumThreads(string attributes)
    {
        var match = Regex.Match(
            attributes,
            @"\[numthreads\s*\(\s*(?<x>\d+)\s*,\s*(?<y>\d+)\s*,\s*(?<z>\d+)\s*\)\s*\]",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        return (
            uint.Parse(match.Groups["x"].Value),
            uint.Parse(match.Groups["y"].Value),
            uint.Parse(match.Groups["z"].Value));
    }

    private static string StripComments(string source)
    {
        var noBlockComments = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlockComments, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    private static string TranslateExpression(
        string expression,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyList<HlslShaderResource> shaderResources,
        IReadOnlyDictionary<string, string>? localTypes = null)
    {
        var trimmed = expression.Trim();
        if (TryTranslateConditionalExpression(trimmed, constantBuffers, shaderResources, localTypes, out var conditional))
        {
            return conditional;
        }

        var translated = TranslateByteAddressBufferReadMethodCalls(trimmed, constantBuffers, shaderResources, localTypes);
        translated = TranslateTextureMethodCalls(translated, constantBuffers, shaderResources, localTypes);
        translated = TranslateHlslIntrinsicCalls(translated, constantBuffers, shaderResources, localTypes);
        translated = Regex.Replace(
            translated,
            @"\b(?<type>float|float2|float3|float4|float2x2|float2x3|float2x4|float3x2|float3x3|float3x4|float4x2|float4x3|float4x4|uint|uint2|uint3|uint4|int|int2|int3|int4)\s*\(",
            match => $"{MapType(match.Groups["type"].Value)}(");

        foreach (var constantBuffer in constantBuffers)
        {
            foreach (var field in constantBuffer.Fields)
            {
                if (localTypes?.ContainsKey(field.Name) == true)
                {
                    continue;
                }

                var fieldAccess = GetConstantBufferFieldAccess(constantBuffer, field);
                translated = Regex.Replace(
                    translated,
                    $@"(?<!\.)\b{Regex.Escape(field.Name)}\b",
                    fieldAccess);
            }
        }

        return translated;
    }

    private static string GetConstantBufferFieldAccess(HlslConstantBuffer constantBuffer, HlslConstantBufferField field)
    {
        if (!constantBuffer.UsesPackedRegisterLayout)
        {
            return $"{constantBuffer.VariableName}.{field.Name}";
        }

        if (TryGetMatrixDimensions(field.Type, out var columns, out var rows))
        {
            var columnValues = new List<string>(columns);
            for (var column = 0; column < columns; column++)
            {
                var components = Enumerable.Range(0, rows)
                    .Select(row => GetPackedRegisterComponent(constantBuffer.VariableName, field.RegisterIndex + (uint)column, (uint)row, "float"))
                    .ToArray();
                columnValues.Add($"vec{rows}<f32>({string.Join(", ", components)})");
            }

            return $"{MapType(field.Type)}({string.Join(", ", columnValues)})";
        }

        var componentCount = GetCBufferComponentCount(field.Type);
        if (componentCount == 1)
        {
            return GetPackedRegisterComponent(constantBuffer.VariableName, field.RegisterIndex, field.ComponentIndex, field.Type);
        }

        var scalarType = GetScalarType(field.Type);
        var wgslScalarType = MapType(scalarType);
        var vectorComponents = Enumerable.Range(0, (int)componentCount)
            .Select(component => GetPackedRegisterComponent(
                constantBuffer.VariableName,
                field.RegisterIndex,
                field.ComponentIndex + (uint)component,
                scalarType))
            .ToArray();
        return $"vec{componentCount}<{wgslScalarType}>({string.Join(", ", vectorComponents)})";
    }

    private static string GetPackedRegisterComponent(string bufferVariableName, uint registerIndex, uint componentIndex, string scalarType)
    {
        var component = "xyzw"[(int)componentIndex];
        var source = $"{bufferVariableName}._r{registerIndex}.{component}";
        return scalarType switch
        {
            "float" => $"bitcast<f32>({source})",
            "int" => $"bitcast<i32>({source})",
            "uint" => source,
            "bool" => $"({source} != 0u)",
            _ => throw new NotSupportedException($"Packed cbuffer field type '{scalarType}' is not supported.")
        };
    }

    private static bool TryTranslateConditionalExpression(
        string expression,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyList<HlslShaderResource> shaderResources,
        IReadOnlyDictionary<string, string>? localTypes,
        out string translated)
    {
        translated = string.Empty;
        if (!TrySplitConditionalExpression(expression, out var condition, out var trueValue, out var falseValue))
        {
            return false;
        }

        var translatedTrueValue = TranslateExpression(trueValue, constantBuffers, shaderResources, localTypes);
        var translatedFalseValue = TranslateExpression(falseValue, constantBuffers, shaderResources, localTypes);
        var translatedCondition = TranslateExpression(condition, constantBuffers, shaderResources, localTypes);
        if (TryGetMatchingWgslVectorSelectSize(translatedTrueValue, translatedFalseValue, out var vectorSize))
        {
            translatedCondition = $"vec{vectorSize}<bool>({string.Join(", ", Enumerable.Repeat(translatedCondition, vectorSize))})";
        }

        translated = string.Concat(
            "select(",
            translatedFalseValue,
            ", ",
            translatedTrueValue,
            ", ",
            translatedCondition,
            ")");
        return true;
    }

    private static bool TryGetMatchingWgslVectorSelectSize(
        string trueValue,
        string falseValue,
        out int vectorSize)
    {
        vectorSize = 0;
        var trueSize = GetWgslVectorConstructorSize(trueValue);
        if (trueSize == 0 || trueSize != GetWgslVectorConstructorSize(falseValue))
        {
            return false;
        }

        vectorSize = trueSize;
        return true;
    }

    private static int GetWgslVectorConstructorSize(string expression)
    {
        var match = Regex.Match(expression.TrimStart(), @"^vec(?<size>[234])<");
        return match.Success ? int.Parse(match.Groups["size"].Value) : 0;
    }

    private static bool TrySplitConditionalExpression(
        string expression,
        out string condition,
        out string trueValue,
        out string falseValue)
    {
        condition = string.Empty;
        trueValue = string.Empty;
        falseValue = string.Empty;

        var questionIndex = FindTopLevelConditionalQuestion(expression);
        if (questionIndex < 0)
        {
            return false;
        }

        var colonIndex = FindMatchingConditionalColon(expression, questionIndex);
        if (colonIndex < 0)
        {
            throw new NotSupportedException("HLSL conditional expression is missing a matching ':' arm.");
        }

        condition = expression[..questionIndex].Trim();
        trueValue = expression[(questionIndex + 1)..colonIndex].Trim();
        falseValue = expression[(colonIndex + 1)..].Trim();
        if (condition.Length == 0 || trueValue.Length == 0 || falseValue.Length == 0)
        {
            throw new NotSupportedException("HLSL conditional expression requires condition, true arm, and false arm.");
        }

        return true;
    }

    private static int FindTopLevelConditionalQuestion(string expression)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        for (var i = 0; i < expression.Length; i++)
        {
            switch (expression[i])
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth--;
                    break;
                case '?' when parenDepth == 0 && bracketDepth == 0:
                    return i;
            }
        }

        return -1;
    }

    private static int FindMatchingConditionalColon(string expression, int questionIndex)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var nestedConditionalDepth = 0;
        for (var i = questionIndex + 1; i < expression.Length; i++)
        {
            switch (expression[i])
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    bracketDepth--;
                    break;
                case '?' when parenDepth == 0 && bracketDepth == 0:
                    nestedConditionalDepth++;
                    break;
                case ':' when parenDepth == 0 && bracketDepth == 0:
                    if (nestedConditionalDepth == 0)
                    {
                        return i;
                    }

                    nestedConditionalDepth--;
                    break;
            }
        }

        return -1;
    }

    private static string TranslateHlslIntrinsicCalls(
        string expression,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyList<HlslShaderResource> shaderResources,
        IReadOnlyDictionary<string, string>? localTypes)
    {
        var builder = new StringBuilder();
        var searchIndex = 0;
        while (searchIndex < expression.Length)
        {
            var match = s_hlslIntrinsicCallStartRegex.Match(expression, searchIndex);
            if (!match.Success)
            {
                builder.Append(expression, searchIndex, expression.Length - searchIndex);
                break;
            }

            builder.Append(expression, searchIndex, match.Index - searchIndex);
            var name = match.Groups["name"].Value;
            var openParen = match.Index + match.Length - 1;
            var closeParen = FindMatchingParen(expression, openParen);
            if (closeParen < 0)
            {
                throw new NotSupportedException($"HLSL intrinsic '{name}' call is missing a closing parenthesis.");
            }

            var rawArguments = SplitTopLevelArguments(expression[(openParen + 1)..closeParen]);
            var arguments = rawArguments
                .Select(argument => TranslateExpression(argument, constantBuffers, shaderResources, localTypes))
                .ToArray();
            builder.Append(TranslateIntrinsic(name, rawArguments, arguments, constantBuffers, localTypes));
            searchIndex = closeParen + 1;
        }

        return builder.ToString();
    }

    private static string TranslateIntrinsic(
        string name,
        IReadOnlyList<string> rawArguments,
        IReadOnlyList<string> arguments,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyDictionary<string, string>? localTypes)
    {
        switch (name.ToLowerInvariant())
        {
            case "saturate":
                ValidateIntrinsicArgumentCount(name, arguments, 1);
                if (TryGetExpressionVectorWidth(rawArguments[0], arguments[0], localTypes, constantBuffers, out var vectorWidth))
                {
                    return $"clamp({arguments[0]}, vec{vectorWidth}<f32>(0.0), vec{vectorWidth}<f32>(1.0))";
                }

                return $"clamp({arguments[0]}, 0.0, 1.0)";

            case "lerp":
                ValidateIntrinsicArgumentCount(name, arguments, 3);
                return $"mix({arguments[0]}, {arguments[1]}, {arguments[2]})";

            case "frac":
                ValidateIntrinsicArgumentCount(name, arguments, 1);
                return $"fract({arguments[0]})";

            case "rsqrt":
                ValidateIntrinsicArgumentCount(name, arguments, 1);
                return $"inverseSqrt({arguments[0]})";

            case "ddx":
                ValidateIntrinsicArgumentCount(name, arguments, 1);
                return $"dpdx({arguments[0]})";

            case "ddy":
                ValidateIntrinsicArgumentCount(name, arguments, 1);
                return $"dpdy({arguments[0]})";

            case "mad":
                ValidateIntrinsicArgumentCount(name, arguments, 3);
                return $"(({arguments[0]}) * ({arguments[1]}) + ({arguments[2]}))";

            case "mul":
                ValidateIntrinsicArgumentCount(name, arguments, 2);
                return $"({arguments[0]} * {arguments[1]})";

            case "rcp":
                ValidateIntrinsicArgumentCount(name, arguments, 1);
                return $"(1.0 / ({arguments[0]}))";

            case "abs":
            case "acos":
            case "asin":
            case "atan":
            case "ceil":
            case "cos":
            case "exp":
            case "exp2":
            case "floor":
            case "length":
            case "log":
            case "log2":
            case "normalize":
            case "round":
            case "sign":
            case "sin":
            case "sqrt":
            case "tan":
                return TranslateSameNameIntrinsic(name, arguments, 1);

            case "atan2":
            case "cross":
            case "distance":
            case "dot":
            case "max":
            case "min":
            case "pow":
            case "reflect":
                return TranslateSameNameIntrinsic(name, arguments, 2);

            case "clamp":
            case "refract":
            case "smoothstep":
                return TranslateSameNameIntrinsic(name, arguments, 3);

            default:
                throw new NotSupportedException($"HLSL intrinsic '{name}' is not supported.");
        }
    }

    private static string TranslateSameNameIntrinsic(string name, IReadOnlyList<string> arguments, int expectedCount)
    {
        ValidateIntrinsicArgumentCount(name, arguments, expectedCount);
        return $"{name.ToLowerInvariant()}({string.Join(", ", arguments)})";
    }

    private static void ValidateIntrinsicArgumentCount(
        string name,
        IReadOnlyCollection<string> arguments,
        int expectedCount)
    {
        if (arguments.Count != expectedCount)
        {
            throw new NotSupportedException($"HLSL intrinsic '{name}' requires {expectedCount} argument(s).");
        }
    }

    private static string TranslateTextureMethodCalls(
        string expression,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyList<HlslShaderResource> shaderResources,
        IReadOnlyDictionary<string, string>? localTypes)
    {
        var builder = new StringBuilder();
        var searchIndex = 0;
        while (searchIndex < expression.Length)
        {
            var match = s_textureMethodCallStartRegex.Match(expression, searchIndex);
            if (!match.Success)
            {
                builder.Append(expression, searchIndex, expression.Length - searchIndex);
                break;
            }

            builder.Append(expression, searchIndex, match.Index - searchIndex);
            var texture = match.Groups["texture"].Value;
            var method = match.Groups["method"].Value;
            var openParen = match.Index + match.Length - 1;
            var closeParen = FindMatchingParen(expression, openParen);
            if (closeParen < 0)
            {
                throw new NotSupportedException($"HLSL Texture2D.{method} call is missing a closing parenthesis.");
            }

            var arguments = SplitTopLevelArguments(expression[(openParen + 1)..closeParen]);
            if (string.Equals(method, "Sample", StringComparison.Ordinal))
            {
                if (arguments.Count != 2)
                {
                    throw new NotSupportedException("HLSL Texture2D.Sample requires sampler and coordinate arguments.");
                }

                var sampler = arguments[0].Trim();
                var textureResource = ValidateTextureSampleResources(texture, sampler, shaderResources);
                var coordinates = TranslateExpression(arguments[1], constantBuffers, shaderResources, localTypes);
                AppendTextureSampleCall(builder, textureResource, "textureSample", texture, sampler, coordinates);
            }
            else if (string.Equals(method, "SampleLevel", StringComparison.Ordinal))
            {
                if (arguments.Count != 3)
                {
                    throw new NotSupportedException("HLSL Texture2D.SampleLevel requires sampler, coordinate, and LOD arguments.");
                }

                var sampler = arguments[0].Trim();
                var textureResource = ValidateTextureSampleResources(texture, sampler, shaderResources);
                var coordinates = TranslateExpression(arguments[1], constantBuffers, shaderResources, localTypes);
                AppendTextureSampleCall(
                    builder,
                    textureResource,
                    "textureSampleLevel",
                    texture,
                    sampler,
                    coordinates,
                    TranslateExpression(arguments[2], constantBuffers, shaderResources, localTypes));
            }
            else if (string.Equals(method, "SampleBias", StringComparison.Ordinal))
            {
                if (arguments.Count != 3)
                {
                    throw new NotSupportedException("HLSL Texture2D.SampleBias requires sampler, coordinate, and bias arguments.");
                }

                var sampler = arguments[0].Trim();
                var textureResource = ValidateTextureSampleResources(texture, sampler, shaderResources);
                var coordinates = TranslateExpression(arguments[1], constantBuffers, shaderResources, localTypes);
                AppendTextureSampleCall(
                    builder,
                    textureResource,
                    "textureSampleBias",
                    texture,
                    sampler,
                    coordinates,
                    TranslateExpression(arguments[2], constantBuffers, shaderResources, localTypes));
            }
            else if (string.Equals(method, "SampleGrad", StringComparison.Ordinal))
            {
                if (arguments.Count != 4)
                {
                    throw new NotSupportedException("HLSL Texture2D.SampleGrad requires sampler, coordinate, ddx, and ddy arguments.");
                }

                var sampler = arguments[0].Trim();
                var textureResource = ValidateTextureSampleResources(texture, sampler, shaderResources);
                var coordinates = TranslateExpression(arguments[1], constantBuffers, shaderResources, localTypes);
                var ddx = TranslateExpression(arguments[2], constantBuffers, shaderResources, localTypes);
                var ddy = TranslateExpression(arguments[3], constantBuffers, shaderResources, localTypes);
                if (textureResource.Kind == HlslShaderResourceKind.Texture2DArray)
                {
                    ddx = AppendVectorMemberAccess(ddx, "xy");
                    ddy = AppendVectorMemberAccess(ddy, "xy");
                }

                AppendTextureSampleCall(
                    builder,
                    textureResource,
                    "textureSampleGrad",
                    texture,
                    sampler,
                    coordinates,
                    ddx,
                    ddy);
            }
            else if (string.Equals(method, "SampleCmp", StringComparison.Ordinal) ||
                string.Equals(method, "SampleCmpLevelZero", StringComparison.Ordinal))
            {
                if (arguments.Count != 3)
                {
                    throw new NotSupportedException($"HLSL Texture2D.{method} requires sampler, coordinate, and compare-value arguments.");
                }

                var sampler = arguments[0].Trim();
                var textureResource = ValidateTextureComparisonSampleResources(texture, sampler, shaderResources);
                var coordinates = TranslateExpression(arguments[1], constantBuffers, shaderResources, localTypes);
                AppendTextureSampleCall(
                    builder,
                    textureResource,
                    "textureSampleCompare",
                    texture,
                    sampler,
                    coordinates,
                    TranslateExpression(arguments[2], constantBuffers, shaderResources, localTypes));
            }
            else
            {
                if (arguments.Count is not (1 or 2))
                {
                    throw new NotSupportedException("HLSL Texture2D.Load requires a location argument and optional texel offset.");
                }

                var textureResource = ValidateTextureResource(texture, shaderResources);
                var location = TranslateExpression(arguments[0], constantBuffers, shaderResources, localTypes);
                var coordinates = textureResource.Kind == HlslShaderResourceKind.Texture3D
                    ? AppendVectorMemberAccess(location, "xyz")
                    : AppendVectorMemberAccess(location, "xy");
                if (arguments.Count == 2)
                {
                    coordinates = $"({coordinates} + {TranslateExpression(arguments[1], constantBuffers, shaderResources, localTypes)})";
                }

                builder
                    .Append("textureLoad(")
                    .Append(texture)
                    .Append(", ")
                    .Append(coordinates)
                    .Append(", ");
                if (textureResource.Kind == HlslShaderResourceKind.Texture2DArray)
                {
                    builder
                        .Append("i32(")
                        .Append(AppendVectorMemberAccess(location, "z"))
                        .Append("), ")
                        .Append(AppendVectorMemberAccess(location, "w"));
                }
                else if (textureResource.Kind == HlslShaderResourceKind.Texture3D)
                {
                    builder.Append(AppendVectorMemberAccess(location, "w"));
                }
                else
                {
                    builder.Append(AppendVectorMemberAccess(location, "z"));
                }

                builder.Append(')');
            }

            searchIndex = closeParen + 1;
        }

        return builder.ToString();
    }

    private static void AppendTextureSampleCall(
        StringBuilder builder,
        HlslShaderResource textureResource,
        string wgslFunction,
        string texture,
        string sampler,
        string coordinates,
        params string[] additionalArguments)
    {
        builder
            .Append(wgslFunction)
            .Append('(')
            .Append(texture)
            .Append(", ")
            .Append(sampler)
            .Append(", ");

        if (textureResource.Kind == HlslShaderResourceKind.Texture2DArray)
        {
            builder
                .Append(AppendVectorMemberAccess(coordinates, "xy"))
                .Append(", i32(")
                .Append(AppendVectorMemberAccess(coordinates, "z"))
                .Append(')');
        }
        else
        {
            builder.Append(coordinates);
        }

        foreach (var argument in additionalArguments)
        {
            builder
                .Append(", ")
                .Append(argument);
        }

        builder.Append(')');
    }

    private static string TranslateByteAddressBufferReadMethodCalls(
        string expression,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyList<HlslShaderResource> shaderResources,
        IReadOnlyDictionary<string, string>? localTypes)
    {
        var builder = new StringBuilder();
        var searchIndex = 0;
        while (searchIndex < expression.Length)
        {
            var match = s_byteAddressBufferMethodCallStartRegex.Match(expression, searchIndex);
            if (!match.Success)
            {
                builder.Append(expression, searchIndex, expression.Length - searchIndex);
                break;
            }

            builder.Append(expression, searchIndex, match.Index - searchIndex);
            var buffer = match.Groups["buffer"].Value;
            var method = match.Groups["method"].Value;
            var openParen = match.Index + match.Length - 1;
            var closeParen = FindMatchingParen(expression, openParen);
            if (closeParen < 0)
            {
                throw new NotSupportedException($"HLSL ByteAddressBuffer.{method} call is missing a closing parenthesis.");
            }

            var resource = FindByteAddressBufferResource(buffer, shaderResources);
            if (resource is null)
            {
                if (string.Equals(method, "Load", StringComparison.Ordinal))
                {
                    builder.Append(expression, match.Index, closeParen + 1 - match.Index);
                    searchIndex = closeParen + 1;
                    continue;
                }

                throw new NotSupportedException("HLSL ByteAddressBuffer load methods require a declared ByteAddressBuffer or RWByteAddressBuffer resource.");
            }

            var arguments = SplitTopLevelArguments(expression[(openParen + 1)..closeParen]);
            if (arguments.Count != 1)
            {
                throw new NotSupportedException($"HLSL ByteAddressBuffer.{method} requires one byte-offset argument.");
            }

            var baseIndex = TranslateByteAddressBufferIndex(arguments[0], constantBuffers, shaderResources, localTypes);
            var componentCount = GetByteAddressBufferComponentCount(method);
            if (componentCount == 1)
            {
                builder.Append(TranslateByteAddressBufferRead(buffer, baseIndex, resource.Kind));
            }
            else
            {
                builder
                    .Append("vec")
                    .Append(componentCount)
                    .Append("<u32>(");

                for (var component = 0; component < componentCount; component++)
                {
                    if (component > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(TranslateByteAddressBufferRead(
                        buffer,
                        AddByteAddressBufferComponentOffset(baseIndex, component),
                        resource.Kind));
                }

                builder.Append(')');
            }

            searchIndex = closeParen + 1;
        }

        return builder.ToString();
    }

    private static bool TryTranslateByteAddressBufferStoreStatement(
        StringBuilder builder,
        string statement,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyList<HlslShaderResource> shaderResources,
        IReadOnlyDictionary<string, string>? localTypes)
    {
        var match = s_byteAddressBufferStoreStatementRegex.Match(statement);
        if (!match.Success)
        {
            return false;
        }

        var buffer = match.Groups["buffer"].Value;
        var method = match.Groups["method"].Value;
        ValidateByteAddressBufferResource(buffer, shaderResources, requireWritable: true);

        var arguments = SplitTopLevelArguments(match.Groups["arguments"].Value);
        if (arguments.Count != 2)
        {
            throw new NotSupportedException($"HLSL RWByteAddressBuffer.{method} requires byte-offset and value arguments.");
        }

        var baseIndex = TranslateByteAddressBufferIndex(arguments[0], constantBuffers, shaderResources, localTypes);
        var value = TranslateExpression(arguments[1], constantBuffers, shaderResources, localTypes);
        var componentCount = GetByteAddressBufferComponentCount(method);
        for (var component = 0; component < componentCount; component++)
        {
            builder
                .Append("    ")
                .Append("atomicStore(&")
                .Append(buffer)
                .Append('[')
                .Append(AddByteAddressBufferComponentOffset(baseIndex, component))
                .Append("], ");

            if (componentCount == 1)
            {
                builder.Append(value);
            }
            else
            {
                builder.Append(AppendVectorMemberAccess(value, GetVectorComponentName(component)));
            }

            builder.Append(");\n");
        }

        return true;
    }

    private static bool TryTranslateRwTexture2DWriteStatement(
        StringBuilder builder,
        string statement,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyList<HlslShaderResource> shaderResources,
        IReadOnlyDictionary<string, string>? localTypes)
    {
        var match = Regex.Match(
            statement,
            @"^(?<texture>[A-Za-z_]\w*)\s*\[\s*(?<coordinates>.+)\s*\]\s*=\s*(?<value>.+)$",
            RegexOptions.Singleline);
        if (!match.Success)
        {
            return false;
        }

        var texture = match.Groups["texture"].Value;
        if (FindRwTexture2DResource(texture, shaderResources) is null)
        {
            return false;
        }

        var coordinates = TranslateExpression(match.Groups["coordinates"].Value, constantBuffers, shaderResources, localTypes);
        var value = TranslateExpression(match.Groups["value"].Value, constantBuffers, shaderResources, localTypes);
        builder
            .Append("    textureStore(")
            .Append(texture)
            .Append(", ")
            .Append(coordinates)
            .Append(", ")
            .Append(value)
            .Append(");\n");
        return true;
    }

    private static bool TryTranslateClipStatement(
        StringBuilder builder,
        string statement,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyList<HlslShaderResource> shaderResources,
        IReadOnlyDictionary<string, string>? localTypes,
        bool allowDiscard)
    {
        var match = Regex.Match(
            statement,
            @"^clip\s*\((?<argument>.*)\)$",
            RegexOptions.Singleline);
        if (!match.Success)
        {
            return false;
        }

        if (!allowDiscard)
        {
            throw new NotSupportedException("HLSL clip(...) is currently supported only in pixel shaders.");
        }

        var argument = match.Groups["argument"].Value.Trim();
        if (argument.Length == 0)
        {
            throw new NotSupportedException("HLSL clip(...) requires one argument.");
        }

        var value = TranslateExpression(argument, constantBuffers, shaderResources, localTypes);
        var hasVectorWidth = TryGetExpressionVectorWidth(argument, value, localTypes, constantBuffers, out var vectorWidth);
        builder.Append("    if (");
        if (!hasVectorWidth)
        {
            builder
                .Append('(')
                .Append(value)
                .Append(") < 0.0");
        }
        else
        {
            builder
                .Append("any((")
                .Append(value)
                .Append(") < vec")
                .Append(vectorWidth)
                .Append("<f32>(0.0))");
        }

        builder.Append(") {\n        discard;\n    }\n");
        return true;
    }

    private static bool TryGetExpressionVectorWidth(
        string hlslArgument,
        string wgslValue,
        IReadOnlyDictionary<string, string>? localTypes,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        out int vectorWidth)
    {
        vectorWidth = 0;
        var vectorConstructor = Regex.Match(
            wgslValue,
            @"^\s*vec(?<width>[234])<",
            RegexOptions.Singleline);
        if (vectorConstructor.Success)
        {
            vectorWidth = int.Parse(vectorConstructor.Groups["width"].Value);
            return true;
        }

        var hlslConstructor = Regex.Match(
            hlslArgument,
            @"^\s*(?:float|half|double|int|uint|bool)(?<width>[234])\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (hlslConstructor.Success)
        {
            vectorWidth = int.Parse(hlslConstructor.Groups["width"].Value);
            return true;
        }

        var swizzle = Regex.Match(
            hlslArgument,
            @"\.(?<swizzle>[xyzwrgba]{2,4})\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (swizzle.Success)
        {
            vectorWidth = swizzle.Groups["swizzle"].Value.Length;
            return true;
        }

        if (TryGetExpressionType(hlslArgument, wgslValue, localTypes, constantBuffers, out var type) &&
            TryGetHlslVectorWidth(type, out vectorWidth))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetExpressionType(
        string hlslArgument,
        string wgslValue,
        IReadOnlyDictionary<string, string>? localTypes,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        out string type)
    {
        type = string.Empty;
        var hlslIdentifier = Regex.Match(
            hlslArgument.Trim(),
            @"^(?<name>[A-Za-z_]\w*)$",
            RegexOptions.Singleline);
        if (hlslIdentifier.Success &&
            localTypes is not null &&
            localTypes.TryGetValue(hlslIdentifier.Groups["name"].Value, out var localType))
        {
            type = localType;
            return true;
        }

        if (hlslIdentifier.Success)
        {
            var fieldName = hlslIdentifier.Groups["name"].Value;
            foreach (var constantBuffer in constantBuffers)
            {
                var matchingField = constantBuffer.Fields.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, fieldName, StringComparison.Ordinal));
                if (matchingField is not null)
                {
                    type = matchingField.Type;
                    return true;
                }
            }
        }

        var cbufferField = Regex.Match(
            wgslValue.Trim(),
            @"^(?<buffer>[A-Za-z_]\w*)\.(?<field>[A-Za-z_]\w*)$",
            RegexOptions.Singleline);
        if (cbufferField.Success)
        {
            var buffer = cbufferField.Groups["buffer"].Value;
            var field = cbufferField.Groups["field"].Value;
            foreach (var constantBuffer in constantBuffers)
            {
                if (!string.Equals(constantBuffer.VariableName, buffer, StringComparison.Ordinal))
                {
                    continue;
                }

                var matchingField = constantBuffer.Fields.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, field, StringComparison.Ordinal));
                if (matchingField is not null)
                {
                    type = matchingField.Type;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetHlslVectorWidth(string type, out int vectorWidth)
    {
        vectorWidth = 0;
        var match = Regex.Match(type, @"^(?:float|half|double|int|uint|bool)(?<width>[234])$");
        if (!match.Success)
        {
            return false;
        }

        vectorWidth = int.Parse(match.Groups["width"].Value);
        return true;
    }

    private static bool TryTranslateByteAddressBufferInterlockedStatement(
        StringBuilder builder,
        string statement,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyList<HlslShaderResource> shaderResources,
        IReadOnlyDictionary<string, string>? localTypes)
    {
        var match = s_byteAddressBufferInterlockedStatementRegex.Match(statement);
        if (!match.Success)
        {
            return false;
        }

        var buffer = match.Groups["buffer"].Value;
        var method = match.Groups["method"].Value;
        ValidateByteAddressBufferResource(buffer, shaderResources, requireWritable: true);

        var arguments = SplitTopLevelArguments(match.Groups["arguments"].Value);
        if (string.Equals(method, "InterlockedCompareExchange", StringComparison.Ordinal))
        {
            return TryTranslateByteAddressBufferCompareExchangeStatement(
                builder,
                buffer,
                arguments,
                constantBuffers,
                shaderResources,
                localTypes);
        }

        if (arguments.Count is not (2 or 3))
        {
            throw new NotSupportedException($"HLSL RWByteAddressBuffer.{method} requires byte-offset, value, and optional original-value arguments.");
        }

        var baseIndex = TranslateByteAddressBufferIndex(arguments[0], constantBuffers, shaderResources, localTypes);
        var value = TranslateExpression(arguments[1], constantBuffers, shaderResources, localTypes);
        var operation = TranslateByteAddressBufferInterlockedOperation(method);

        builder.Append("    ");
        if (arguments.Count == 3)
        {
            var original = arguments[2].Trim();
            if (!Regex.IsMatch(original, @"^[A-Za-z_]\w*(?:(?:\[[^\]]+\])|(?:\.[A-Za-z_]\w*))*$"))
            {
                throw new NotSupportedException($"HLSL RWByteAddressBuffer.{method} original-value argument must be assignable.");
            }

            builder
                .Append(original)
                .Append(" = ");
        }

        builder
            .Append(operation)
            .Append("(&")
            .Append(buffer)
            .Append('[')
            .Append(baseIndex)
            .Append("], ")
            .Append(value)
            .Append(");\n");

        return true;
    }

    private static bool TryTranslateByteAddressBufferCompareExchangeStatement(
        StringBuilder builder,
        string buffer,
        IReadOnlyList<string> arguments,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyList<HlslShaderResource> shaderResources,
        IReadOnlyDictionary<string, string>? localTypes)
    {
        if (arguments.Count != 4)
        {
            throw new NotSupportedException("HLSL RWByteAddressBuffer.InterlockedCompareExchange requires byte-offset, compare-value, value, and original-value arguments.");
        }

        var original = ValidateAssignableByteAddressBufferInterlockedOriginal("InterlockedCompareExchange", arguments[3]);
        var baseIndex = TranslateByteAddressBufferIndex(arguments[0], constantBuffers, shaderResources, localTypes);
        var compare = TranslateExpression(arguments[1], constantBuffers, shaderResources, localTypes);
        var value = TranslateExpression(arguments[2], constantBuffers, shaderResources, localTypes);
        builder
            .Append("    ")
            .Append(original)
            .Append(" = atomicCompareExchangeWeak(&")
            .Append(buffer)
            .Append('[')
            .Append(baseIndex)
            .Append("], ")
            .Append(compare)
            .Append(", ")
            .Append(value)
            .Append(").old_value;\n");

        return true;
    }

    private static string TranslateByteAddressBufferRead(
        string buffer,
        string index,
        HlslShaderResourceKind resourceKind)
    {
        return resourceKind == HlslShaderResourceKind.RWByteAddressBuffer
            ? $"atomicLoad(&{buffer}[{index}])"
            : $"{buffer}[{index}]";
    }

    private static string TranslateByteAddressBufferInterlockedOperation(string method)
    {
        return method switch
        {
            "InterlockedAdd" => "atomicAdd",
            "InterlockedAnd" => "atomicAnd",
            "InterlockedOr" => "atomicOr",
            "InterlockedXor" => "atomicXor",
            "InterlockedMin" => "atomicMin",
            "InterlockedMax" => "atomicMax",
            "InterlockedExchange" => "atomicExchange",
            _ => throw new NotSupportedException($"HLSL RWByteAddressBuffer method '{method}' is not supported.")
        };
    }

    private static string ValidateAssignableByteAddressBufferInterlockedOriginal(string method, string argument)
    {
        var original = argument.Trim();
        if (!Regex.IsMatch(original, @"^[A-Za-z_]\w*(?:(?:\[[^\]]+\])|(?:\.[A-Za-z_]\w*))*$"))
        {
            throw new NotSupportedException($"HLSL RWByteAddressBuffer.{method} original-value argument must be assignable.");
        }

        return original;
    }

    private static string TranslateByteAddressBufferIndex(
        string byteOffset,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyList<HlslShaderResource> shaderResources,
        IReadOnlyDictionary<string, string>? localTypes)
    {
        return $"(({TranslateExpression(byteOffset, constantBuffers, shaderResources, localTypes)}) / 4u)";
    }

    private static string AddByteAddressBufferComponentOffset(string baseIndex, int component)
    {
        return component == 0 ? baseIndex : $"({baseIndex} + {component}u)";
    }

    private static int GetByteAddressBufferComponentCount(string method)
    {
        return method switch
        {
            "Load" or "Store" => 1,
            "Load2" or "Store2" => 2,
            "Load3" or "Store3" => 3,
            "Load4" or "Store4" => 4,
            _ => throw new NotSupportedException($"HLSL ByteAddressBuffer method '{method}' is not supported.")
        };
    }

    private static string GetVectorComponentName(int component)
    {
        return component switch
        {
            0 => "x",
            1 => "y",
            2 => "z",
            3 => "w",
            _ => throw new ArgumentOutOfRangeException(nameof(component), component, null)
        };
    }

    private static int FindMatchingParen(string expression, int openParen)
    {
        var depth = 0;
        for (var i = openParen; i < expression.Length; i++)
        {
            if (expression[i] == '(')
            {
                depth++;
            }
            else if (expression[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static List<string> SplitTopLevelArguments(string argumentText)
    {
        var arguments = new List<string>();
        var start = 0;
        var depth = 0;
        for (var i = 0; i < argumentText.Length; i++)
        {
            var current = argumentText[i];
            if (current == '(')
            {
                depth++;
            }
            else if (current == ')')
            {
                depth--;
            }
            else if (current == ',' && depth == 0)
            {
                arguments.Add(argumentText[start..i]);
                start = i + 1;
            }
        }

        arguments.Add(argumentText[start..]);
        return arguments;
    }

    private static HlslShaderResource ValidateTextureSampleResources(
        string texture,
        string sampler,
        IReadOnlyList<HlslShaderResource> shaderResources)
    {
        var textureResource = FindTextureResource(texture, shaderResources);
        if (textureResource is null ||
            !shaderResources.Any(resource =>
                resource.Kind == HlslShaderResourceKind.SamplerState &&
                string.Equals(resource.Name, sampler, StringComparison.Ordinal)))
        {
            throw new NotSupportedException("HLSL texture sampling requires declared Texture2D, Texture2DArray, or Texture3D and SamplerState resources.");
        }

        return textureResource;
    }

    private static HlslShaderResource ValidateTextureComparisonSampleResources(
        string texture,
        string sampler,
        IReadOnlyList<HlslShaderResource> shaderResources)
    {
        var textureResource = FindTextureResource(texture, shaderResources);
        if (textureResource is null ||
            textureResource.Kind == HlslShaderResourceKind.Texture3D ||
            !textureResource.UsesComparisonSampling ||
            !shaderResources.Any(resource =>
                resource.Kind == HlslShaderResourceKind.SamplerComparisonState &&
                string.Equals(resource.Name, sampler, StringComparison.Ordinal)))
        {
            throw new NotSupportedException("HLSL texture comparison sampling requires declared Texture2D or Texture2DArray and SamplerComparisonState resources.");
        }

        return textureResource;
    }

    private static HlslShaderResource ValidateTextureResource(
        string texture,
        IReadOnlyList<HlslShaderResource> shaderResources)
    {
        var textureResource = FindTextureResource(texture, shaderResources);
        if (textureResource is null)
        {
            throw new NotSupportedException("HLSL texture Load requires a declared Texture2D, Texture2DArray, or Texture3D resource.");
        }

        return textureResource;
    }

    private static void ValidateByteAddressBufferResource(
        string buffer,
        IReadOnlyList<HlslShaderResource> shaderResources,
        bool requireWritable)
    {
        var resource = FindByteAddressBufferResource(buffer, shaderResources);
        if (resource is null)
        {
            throw new NotSupportedException("HLSL ByteAddressBuffer methods require a declared ByteAddressBuffer or RWByteAddressBuffer resource.");
        }

        if (requireWritable && resource.Kind != HlslShaderResourceKind.RWByteAddressBuffer)
        {
            throw new NotSupportedException("HLSL RWByteAddressBuffer.Store methods require a declared RWByteAddressBuffer resource.");
        }
    }

    private static HlslShaderResource? FindByteAddressBufferResource(
        string buffer,
        IReadOnlyList<HlslShaderResource> shaderResources)
    {
        return shaderResources.FirstOrDefault(resource =>
            string.Equals(resource.Name, buffer, StringComparison.Ordinal) &&
            resource.Kind is HlslShaderResourceKind.ByteAddressBuffer or HlslShaderResourceKind.RWByteAddressBuffer);
    }

    private static HlslShaderResource? FindRwTexture2DResource(
        string texture,
        IReadOnlyList<HlslShaderResource> shaderResources)
    {
        return shaderResources.FirstOrDefault(resource =>
            resource.Kind == HlslShaderResourceKind.RWTexture2D &&
            string.Equals(resource.Name, texture, StringComparison.Ordinal));
    }

    private static HlslShaderResource? FindTextureResource(
        string texture,
        IReadOnlyList<HlslShaderResource> shaderResources)
    {
        return shaderResources.FirstOrDefault(resource =>
            (resource.Kind is HlslShaderResourceKind.Texture2D or HlslShaderResourceKind.Texture2DArray or HlslShaderResourceKind.Texture3D) &&
            string.Equals(resource.Name, texture, StringComparison.Ordinal));
    }

    private static string AppendVectorMemberAccess(string expression, string member)
    {
        return Regex.IsMatch(expression, @"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*$")
            ? $"{expression}.{member}"
            : $"({expression}).{member}";
    }

    private static Dictionary<string, uint> CreateUserSemanticLocationMap(IEnumerable<HlslStruct> structs)
    {
        return CreateUserSemanticLocationMap(
            structs
                .SelectMany(hlslStruct => hlslStruct.Fields)
                .Select(field => field.Semantic));
    }

    private static Dictionary<string, uint> CreateUserSemanticLocationMap(IEnumerable<string?> semantics)
    {
        var locations = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var baseOffsets = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var nextLocation = 0u;
        foreach (var semantic in semantics
            .Where(semantic => !string.IsNullOrWhiteSpace(semantic) && !IsBuiltinSemantic(semantic!)))
        {
            var baseName = GetUserSemanticBaseName(semantic!);
            if (!baseOffsets.TryGetValue(baseName, out var baseOffset))
            {
                baseOffset = nextLocation;
                baseOffsets[baseName] = baseOffset;
            }

            var location = baseOffset + GetSemanticIndex(semantic);
            locations[GetUserSemanticKey(semantic!)] = location;
            nextLocation = Math.Max(nextLocation, location + 1);
        }

        return locations;
    }

    private static string GetFieldAttribute(string semantic, IReadOnlyDictionary<string, uint> semanticLocations)
    {
        if (IsSystemSemantic(semantic, "SV_Position"))
        {
            return "@builtin(position)";
        }

        if (IsSystemSemantic(semantic, "SV_VertexID"))
        {
            return "@builtin(vertex_index)";
        }

        if (IsSystemSemantic(semantic, "SV_InstanceID"))
        {
            return "@builtin(instance_index)";
        }

        if (IsSystemSemantic(semantic, "SV_IsFrontFace"))
        {
            return "@builtin(front_facing)";
        }

        if (IsSystemSemantic(semantic, "SV_DispatchThreadID"))
        {
            return "@builtin(global_invocation_id)";
        }

        if (IsSystemSemantic(semantic, "SV_GroupID"))
        {
            return "@builtin(workgroup_id)";
        }

        if (IsSystemSemantic(semantic, "SV_GroupThreadID"))
        {
            return "@builtin(local_invocation_id)";
        }

        if (IsSystemSemantic(semantic, "SV_GroupIndex"))
        {
            return "@builtin(local_invocation_index)";
        }

        if (IsSystemSemantic(semantic, "SV_Target"))
        {
            return $"@location({GetSemanticIndex(semantic)})";
        }

        if (IsSystemSemantic(semantic, "SV_Depth"))
        {
            return "@builtin(frag_depth)";
        }

        if (IsBuiltinSemantic(semantic))
        {
            throw new NotSupportedException($"Unsupported HLSL system-value semantic '{semantic}'.");
        }

        if (!semanticLocations.TryGetValue(GetUserSemanticKey(semantic), out var location))
        {
            throw new NotSupportedException($"HLSL semantic '{semantic}' was not assigned a WGSL location.");
        }

        return $"@location({location})";
    }

    private static string GetParameterAttribute(string semantic, IReadOnlyDictionary<string, uint> semanticLocations)
    {
        if (IsSystemSemantic(semantic, "SV_VertexID"))
        {
            return "@builtin(vertex_index)";
        }

        if (IsSystemSemantic(semantic, "SV_InstanceID"))
        {
            return "@builtin(instance_index)";
        }

        if (IsSystemSemantic(semantic, "SV_IsFrontFace"))
        {
            return "@builtin(front_facing)";
        }

        if (IsSystemSemantic(semantic, "SV_DispatchThreadID"))
        {
            return "@builtin(global_invocation_id)";
        }

        if (IsSystemSemantic(semantic, "SV_GroupID"))
        {
            return "@builtin(workgroup_id)";
        }

        if (IsSystemSemantic(semantic, "SV_GroupThreadID"))
        {
            return "@builtin(local_invocation_id)";
        }

        if (IsSystemSemantic(semantic, "SV_GroupIndex"))
        {
            return "@builtin(local_invocation_index)";
        }

        if (!semanticLocations.TryGetValue(GetUserSemanticKey(semantic), out var location))
        {
            throw new NotSupportedException($"HLSL semantic '{semantic}' was not assigned a WGSL location.");
        }

        return $"@location({location})";
    }

    private static bool IsBuiltinSemantic(string semantic)
    {
        return semantic.StartsWith("SV_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemSemantic(string? semantic, string expected)
    {
        if (string.IsNullOrWhiteSpace(semantic))
        {
            return false;
        }

        return semantic.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static uint GetSemanticIndex(string? semantic)
    {
        if (string.IsNullOrWhiteSpace(semantic))
        {
            return 0;
        }

        var match = Regex.Match(semantic, @"(?<index>\d+)$");
        return match.Success ? uint.Parse(match.Groups["index"].Value) : 0u;
    }

    private static string GetUserSemanticKey(string semantic)
    {
        return $"{GetUserSemanticBaseName(semantic)}{GetSemanticIndex(semantic)}";
    }

    private static string GetUserSemanticBaseName(string semantic)
    {
        return Regex.Replace(semantic, @"\d+$", string.Empty).ToUpperInvariant();
    }

    private static string MapTypeOrIdentifier(string type)
    {
        return IsKnownScalarOrVectorType(type) ? MapType(type) : type;
    }

    private static string MapResourceElementType(
        string type,
        IReadOnlyDictionary<string, HlslStruct> structs)
    {
        return IsKnownScalarOrVectorType(type)
            ? MapType(type)
            : structs.ContainsKey(type)
                ? type
                : throw new NotSupportedException($"Unsupported HLSL buffer resource element type '{type}'.");
    }

    private static string MapStorageTextureFormat(string type)
    {
        return type switch
        {
            "float" => "r32float",
            "float4" => "rgba8unorm",
            "uint" => "r32uint",
            "uint4" => "rgba32uint",
            "int" => "r32sint",
            "int4" => "rgba32sint",
            _ => throw new NotSupportedException($"Unsupported HLSL RWTexture2D element type '{type}'.")
        };
    }

    private static string MapType(string type)
    {
        return type switch
        {
            "bool" => "bool",
            "float" => "f32",
            "float2" => "vec2<f32>",
            "float3" => "vec3<f32>",
            "float4" => "vec4<f32>",
            "float2x2" => "mat2x2<f32>",
            "float2x3" => "mat2x3<f32>",
            "float2x4" => "mat2x4<f32>",
            "float3x2" => "mat3x2<f32>",
            "float3x3" => "mat3x3<f32>",
            "float3x4" => "mat3x4<f32>",
            "float4x2" => "mat4x2<f32>",
            "float4x3" => "mat4x3<f32>",
            "float4x4" => "mat4x4<f32>",
            "uint" => "u32",
            "uint2" => "vec2<u32>",
            "uint3" => "vec3<u32>",
            "uint4" => "vec4<u32>",
            "int" => "i32",
            "int2" => "vec2<i32>",
            "int3" => "vec3<i32>",
            "int4" => "vec4<i32>",
            _ => throw new NotSupportedException($"Unsupported HLSL type '{type}'.")
        };
    }

    private static bool IsKnownScalarOrVectorType(string type)
    {
        return type is "bool" or "float" or "float2" or "float3" or "float4" or
            "float2x2" or "float2x3" or "float2x4" or
            "float3x2" or "float3x3" or "float3x4" or
            "float4x2" or "float4x3" or "float4x4" or
            "uint" or "uint2" or "uint3" or "uint4" or
            "int" or "int2" or "int3" or "int4";
    }

    private static uint GetCBufferComponentCount(string type)
    {
        if (TryGetMatrixDimensions(type, out _, out _))
        {
            return 4;
        }

        var match = Regex.Match(type, @"^(?:float|int|uint|bool)(?<width>[234])$");
        return match.Success ? uint.Parse(match.Groups["width"].Value) : 1u;
    }

    private static uint GetCBufferRegisterSpan(string type)
    {
        if (TryGetMatrixDimensions(type, out var columns, out _))
        {
            return (uint)columns;
        }

        return 1u;
    }

    private static uint GetWgslUniformAlignment(string type)
    {
        if (TryGetMatrixDimensions(type, out _, out var rows))
        {
            return GetVectorAlignment((uint)rows);
        }

        return GetVectorAlignment(GetCBufferComponentCount(type));
    }

    private static uint GetWgslUniformSize(string type)
    {
        if (TryGetMatrixDimensions(type, out var columns, out var rows))
        {
            var columnSize = GetVectorSize((uint)rows);
            var columnStride = AlignTo(columnSize, GetVectorAlignment((uint)rows));
            return columnStride * (uint)(columns - 1) + columnSize;
        }

        return GetVectorSize(GetCBufferComponentCount(type));
    }

    private static uint GetVectorAlignment(uint componentCount)
    {
        return componentCount switch
        {
            1 => 4,
            2 => 8,
            3 or 4 => 16,
            _ => throw new NotSupportedException($"Unsupported vector component count '{componentCount}'.")
        };
    }

    private static uint GetVectorSize(uint componentCount)
    {
        return componentCount switch
        {
            >= 1 and <= 4 => componentCount * 4,
            _ => throw new NotSupportedException($"Unsupported vector component count '{componentCount}'.")
        };
    }

    private static uint AlignTo(uint value, uint alignment)
    {
        return ((value + alignment - 1) / alignment) * alignment;
    }

    private static string GetScalarType(string type)
    {
        if (type.StartsWith("float", StringComparison.Ordinal))
        {
            return "float";
        }

        if (type.StartsWith("int", StringComparison.Ordinal))
        {
            return "int";
        }

        if (type.StartsWith("uint", StringComparison.Ordinal))
        {
            return "uint";
        }

        if (type.StartsWith("bool", StringComparison.Ordinal))
        {
            return "bool";
        }

        throw new NotSupportedException($"Unsupported HLSL scalar type '{type}'.");
    }

    private static bool TryGetMatrixDimensions(string type, out int columns, out int rows)
    {
        var match = Regex.Match(type, @"^float(?<columns>[234])x(?<rows>[234])$");
        if (match.Success)
        {
            columns = int.Parse(match.Groups["columns"].Value);
            rows = int.Parse(match.Groups["rows"].Value);
            return true;
        }

        columns = 0;
        rows = 0;
        return false;
    }

    private static string ToVariableName(string name)
    {
        return name.Length == 0
            ? name
            : char.ToLowerInvariant(name[0]) + name[1..];
    }

    private sealed record HlslStruct(string Name, IReadOnlyList<HlslField> Fields);

    private sealed record HlslField(string Type, string Name, string? Semantic);

    private sealed record HlslConstantBuffer(
        string Name,
        string VariableName,
        uint Register,
        IReadOnlyList<HlslConstantBufferField> Fields,
        bool UsesPackedRegisterLayout,
        uint RegisterCount);

    private sealed record HlslConstantBufferField(
        string Type,
        string Name,
        uint RegisterIndex,
        uint ComponentIndex);

    private sealed record HlslShaderResource(
        HlslShaderResourceKind Kind,
        string Name,
        uint Register,
        string? ElementType = null,
        bool UsesComparisonSampling = false);

    private sealed record PendingHlslShaderResource(HlslShaderResourceKind Kind, Match Match);

    private enum HlslShaderResourceKind
    {
        Texture2D,
        Texture2DArray,
        Texture3D,
        StructuredBuffer,
        Buffer,
        ByteAddressBuffer,
        RWStructuredBuffer,
        RWBuffer,
        RWTexture2D,
        RWByteAddressBuffer,
        SamplerState,
        SamplerComparisonState
    }

    private readonly record struct HlslFunction(
        string Name,
        string ReturnType,
        string? ReturnSemantic,
        IReadOnlyList<HlslParameter> Parameters,
        string Body,
        (uint X, uint Y, uint Z)? NumThreads);

    private sealed record HlslParameter(string Type, string Name, string? Semantic);
}
