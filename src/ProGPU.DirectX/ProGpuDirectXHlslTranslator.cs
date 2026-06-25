using System.Text;
using System.Text.RegularExpressions;

namespace ProGPU.DirectX;

internal static class ProGpuDirectXHlslTranslator
{
    private static readonly Regex s_structRegex = new(
        @"\bstruct\s+(?<name>[A-Za-z_]\w*)\s*\{(?<body>.*?)\}\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex s_fieldRegex = new(
        @"\b(?<type>[A-Za-z_]\w*)\s+(?<name>[A-Za-z_]\w*)\s*:\s*(?<semantic>[A-Za-z_]\w*)\s*;",
        RegexOptions.Compiled);

    private static readonly Regex s_cbufferRegex = new(
        @"\bcbuffer\s+(?<name>[A-Za-z_]\w*)\s*(?::\s*register\s*\(\s*b(?<slot>\d+)\s*\))?\s*\{(?<body>.*?)\}\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex s_cbufferFieldRegex = new(
        @"\b(?<type>[A-Za-z_]\w*)\s+(?<name>[A-Za-z_]\w*)\s*;",
        RegexOptions.Compiled);

    private static readonly Regex s_unsupportedRegex = new(
        @"\b(tbuffer|Texture\w*|Sampler\w*|RWTexture\w*|StructuredBuffer|RWStructuredBuffer|ByteAddressBuffer|RWByteAddressBuffer)\b",
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
            if (!TryParseFunction(source, descriptor.EntryPoint!, out var function))
            {
                return false;
            }

            wgsl = descriptor.Stage switch
            {
                DxShaderStage.Vertex => TranslateVertexShader(constantBuffers, structs, function),
                DxShaderStage.Pixel => TranslatePixelShader(constantBuffers, structs, function),
                DxShaderStage.Compute => TranslateComputeShader(constantBuffers, function),
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
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyDictionary<string, HlslStruct> structs,
        HlslFunction function)
    {
        var builder = new StringBuilder();
        AppendConstantBuffers(builder, constantBuffers);
        AppendStructs(builder, structs);
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

        AppendTranslatedBody(builder, function.Body, constantBuffers, allowReturnValue: true);
        return builder.ToString();
    }

    private static string TranslatePixelShader(
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        IReadOnlyDictionary<string, HlslStruct> structs,
        HlslFunction function)
    {
        var builder = new StringBuilder();
        AppendConstantBuffers(builder, constantBuffers);
        AppendStructs(builder, structs);
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

        AppendTranslatedBody(builder, function.Body, constantBuffers, allowReturnValue: true);
        return builder.ToString();
    }

    private static string TranslateComputeShader(
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        HlslFunction function)
    {
        if (!string.Equals(function.ReturnType, "void", StringComparison.Ordinal))
        {
            throw new NotSupportedException("Compute HLSL translation requires a void return.");
        }

        var (x, y, z) = function.NumThreads ?? (1u, 1u, 1u);
        var builder = new StringBuilder();
        AppendConstantBuffers(builder, constantBuffers);
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

        AppendTranslatedBody(builder, function.Body, constantBuffers, allowReturnValue: false);
        return builder.ToString();
    }

    private static void AppendConstantBuffers(StringBuilder builder, IReadOnlyList<HlslConstantBuffer> constantBuffers)
    {
        foreach (var constantBuffer in constantBuffers)
        {
            builder.Append("struct ").Append(constantBuffer.Name).Append(" {\n");
            foreach (var field in constantBuffer.Fields)
            {
                builder
                    .Append("    ")
                    .Append(field.Name)
                    .Append(": ")
                    .Append(MapType(field.Type))
                    .Append(",\n");
            }

            builder
                .Append("}\n")
                .Append("@group(0) @binding(")
                .Append(constantBuffer.Register)
                .Append(") var<uniform> ")
                .Append(constantBuffer.VariableName)
                .Append(": ")
                .Append(constantBuffer.Name)
                .Append(";\n\n");
        }
    }

    private static void AppendStructs(StringBuilder builder, IReadOnlyDictionary<string, HlslStruct> structs)
    {
        foreach (var hlslStruct in structs.Values)
        {
            builder.Append("struct ").Append(hlslStruct.Name).Append(" {\n");
            var location = 0u;
            foreach (var field in hlslStruct.Fields)
            {
                builder
                    .Append("    ")
                    .Append(GetFieldAttribute(field.Semantic, location))
                    .Append(' ')
                    .Append(field.Name)
                    .Append(": ")
                    .Append(MapType(field.Type))
                    .Append(",\n");

                if (!IsBuiltinSemantic(field.Semantic))
                {
                    location++;
                }
            }

            builder.Append("}\n\n");
        }
    }

    private static string TranslateParameters(
        IReadOnlyList<HlslParameter> parameters,
        IReadOnlyDictionary<string, HlslStruct> structs)
    {
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

                return $"{GetParameterAttribute(parameter.Semantic)} {parameter.Name}: {MapType(parameter.Type)}";
            }));
    }

    private static void AppendTranslatedBody(
        StringBuilder builder,
        string body,
        IReadOnlyList<HlslConstantBuffer> constantBuffers,
        bool allowReturnValue)
    {
        builder.Append(" {\n");
        foreach (var rawStatement in body.Split(';'))
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
                    .Append(TranslateExpression(statement["return ".Length..].Trim(), constantBuffers))
                    .Append(";\n");
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
                continue;
            }

            var assignment = Regex.Match(
                statement,
                @"^(?<left>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*=\s*(?<right>.+)$",
                RegexOptions.Singleline);
            if (assignment.Success)
            {
                builder
                    .Append("    ")
                    .Append(assignment.Groups["left"].Value)
                    .Append(" = ")
                    .Append(TranslateExpression(assignment.Groups["right"].Value.Trim(), constantBuffers))
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
        foreach (Match match in s_cbufferRegex.Matches(source))
        {
            var name = match.Groups["name"].Value;
            var register = match.Groups["slot"].Success
                ? uint.Parse(match.Groups["slot"].Value)
                : 0u;
            var fields = new List<HlslConstantBufferField>();

            foreach (Match fieldMatch in s_cbufferFieldRegex.Matches(match.Groups["body"].Value))
            {
                fields.Add(new HlslConstantBufferField(
                    fieldMatch.Groups["type"].Value,
                    fieldMatch.Groups["name"].Value));
            }

            if (fields.Count == 0)
            {
                throw new NotSupportedException($"HLSL cbuffer '{name}' has no translatable fields.");
            }

            constantBuffers.Add(new HlslConstantBuffer(
                name,
                ToVariableName(name),
                register,
                fields));
        }

        return constantBuffers;
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
                    fieldMatch.Groups["semantic"].Value));
            }

            if (fields.Count == 0)
            {
                throw new NotSupportedException($"HLSL struct '{name}' has no translatable semantic fields.");
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

    private static string TranslateExpression(string expression, IReadOnlyList<HlslConstantBuffer> constantBuffers)
    {
        var trimmed = expression.Trim();
        var mul = Regex.Match(
            trimmed,
            @"^mul\s*\(\s*(?<left>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)?)\s*,\s*(?<right>.+)\s*\)$",
            RegexOptions.Singleline);
        if (mul.Success)
        {
            return $"{TranslateExpression(mul.Groups["left"].Value, constantBuffers)} * {TranslateExpression(mul.Groups["right"].Value, constantBuffers)}";
        }

        var translated = Regex.Replace(
            trimmed,
            @"\b(?<type>float|float2|float3|float4|uint|uint2|uint3|uint4|int|int2|int3|int4)\s*\(",
            match => $"{MapType(match.Groups["type"].Value)}(");

        foreach (var constantBuffer in constantBuffers)
        {
            foreach (var field in constantBuffer.Fields)
            {
                translated = Regex.Replace(
                    translated,
                    $@"(?<!\.)\b{Regex.Escape(field.Name)}\b",
                    $"{constantBuffer.VariableName}.{field.Name}");
            }
        }

        return translated;
    }

    private static string GetFieldAttribute(string semantic, uint location)
    {
        if (IsSystemSemantic(semantic, "SV_Position"))
        {
            return "@builtin(position)";
        }

        if (IsSystemSemantic(semantic, "SV_Target"))
        {
            return $"@location({GetSemanticIndex(semantic)})";
        }

        return $"@location({location})";
    }

    private static string GetParameterAttribute(string semantic)
    {
        if (IsSystemSemantic(semantic, "SV_VertexID"))
        {
            return "@builtin(vertex_index)";
        }

        if (IsSystemSemantic(semantic, "SV_InstanceID"))
        {
            return "@builtin(instance_index)";
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

        return $"@location({GetSemanticIndex(semantic)})";
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

    private static string MapTypeOrIdentifier(string type)
    {
        return IsKnownScalarOrVectorType(type) ? MapType(type) : type;
    }

    private static string MapType(string type)
    {
        return type switch
        {
            "float" => "f32",
            "float2" => "vec2<f32>",
            "float3" => "vec3<f32>",
            "float4" => "vec4<f32>",
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
        return type is "float" or "float2" or "float3" or "float4" or
            "float4x4" or
            "uint" or "uint2" or "uint3" or "uint4" or
            "int" or "int2" or "int3" or "int4";
    }

    private static string ToVariableName(string name)
    {
        return name.Length == 0
            ? name
            : char.ToLowerInvariant(name[0]) + name[1..];
    }

    private sealed record HlslStruct(string Name, IReadOnlyList<HlslField> Fields);

    private sealed record HlslField(string Type, string Name, string Semantic);

    private sealed record HlslConstantBuffer(
        string Name,
        string VariableName,
        uint Register,
        IReadOnlyList<HlslConstantBufferField> Fields);

    private sealed record HlslConstantBufferField(string Type, string Name);

    private readonly record struct HlslFunction(
        string Name,
        string ReturnType,
        string? ReturnSemantic,
        IReadOnlyList<HlslParameter> Parameters,
        string Body,
        (uint X, uint Y, uint Z)? NumThreads);

    private sealed record HlslParameter(string Type, string Name, string? Semantic);
}
