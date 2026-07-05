using System.Buffers.Binary;
using System.Text;

namespace ProGPU.DirectX;

public enum DxShaderBytecodeContainerKind
{
    Unknown,
    Dxbc,
    RawDxil,
    RawDxilBitcode
}

public enum DxShaderProgramKind
{
    Unknown,
    Pixel,
    Vertex,
    Geometry,
    Hull,
    Domain,
    Compute
}

public enum DxReflectedShaderResourceType
{
    ConstantBuffer = 0,
    TextureBuffer = 1,
    Texture = 2,
    Sampler = 3,
    UnorderedAccessTyped = 4,
    StructuredBuffer = 5,
    UnorderedAccessStructured = 6,
    ByteAddressBuffer = 7,
    UnorderedAccessByteAddress = 8,
    UnorderedAccessAppendStructured = 9,
    UnorderedAccessConsumeStructured = 10,
    UnorderedAccessStructuredWithCounter = 11
}

public sealed record DxShaderBytecodeChunk(string FourCC, uint Offset, uint Size);

public sealed record DxShaderSignatureParameter(
    string SemanticName,
    uint SemanticIndex,
    uint Register,
    uint Mask,
    uint ReadWriteMask,
    uint ComponentType,
    uint SystemValueType);

public sealed record DxReflectedShaderResourceBinding(
    string Name,
    uint Type,
    uint ReturnType,
    uint Dimension,
    uint BindPoint,
    uint BindCount,
    uint Flags);

public sealed record DxReflectedShaderBindingRequirement(
    string Name,
    ProGpuDirectXBindingKind Kind,
    DxShaderStage Stage,
    uint Slot,
    uint Count,
    uint NativeBinding,
    uint RawType,
    uint RawReturnType,
    uint RawDimension,
    uint Flags);

public sealed record ProGpuDirectXShaderBytecodeInfo
{
    private const uint D3DRegisterComponentUInt32 = 1;
    private const uint D3DRegisterComponentSInt32 = 2;
    private const uint D3DRegisterComponentFloat32 = 3;

    public required DxShaderBytecodeContainerKind ContainerKind { get; init; }

    public required bool IsValid { get; init; }

    public string? FailureReason { get; init; }

    public uint TotalSizeInBytes { get; init; }

    public DxShaderProgramKind ProgramKind { get; init; } = DxShaderProgramKind.Unknown;

    public uint ShaderModelMajor { get; init; }

    public uint ShaderModelMinor { get; init; }

    public IReadOnlyList<DxShaderBytecodeChunk> Chunks { get; init; } = Array.Empty<DxShaderBytecodeChunk>();

    public IReadOnlyList<DxShaderSignatureParameter> InputSignature { get; init; } = Array.Empty<DxShaderSignatureParameter>();

    public IReadOnlyList<DxShaderSignatureParameter> OutputSignature { get; init; } = Array.Empty<DxShaderSignatureParameter>();

    public IReadOnlyList<DxShaderSignatureParameter> PatchConstantSignature { get; init; } = Array.Empty<DxShaderSignatureParameter>();

    public IReadOnlyList<DxReflectedShaderResourceBinding> ResourceBindings { get; init; } = Array.Empty<DxReflectedShaderResourceBinding>();

    public bool HasDxilProgram => ContainsChunk("DXIL");

    public bool HasTokenizedProgram => ContainsChunk("SHDR", "SHEX");

    public bool HasResourceDefinition => ContainsChunk("RDEF");

    public bool HasInputSignature => InputSignature.Count > 0 || ContainsChunk("ISGN", "ISG1");

    public bool HasOutputSignature => OutputSignature.Count > 0 || ContainsChunk("OSGN", "OSG5");

    public DxShaderBytecodeChunk? GetChunk(string fourCC)
    {
        ArgumentNullException.ThrowIfNull(fourCC);
        var chunkCount = Chunks.Count;
        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var chunk = Chunks[chunkIndex];
            if (string.Equals(chunk.FourCC, fourCC, StringComparison.Ordinal))
            {
                return chunk;
            }
        }

        return null;
    }

    public bool TryCreateBindingRequirements(
        DxShaderStage stage,
        out IReadOnlyList<DxReflectedShaderBindingRequirement> requirements)
    {
        var entries = new List<DxReflectedShaderBindingRequirement>();
        var resourceCount = ResourceBindings.Count;
        for (var resourceIndex = 0; resourceIndex < resourceCount; resourceIndex++)
        {
            var resource = ResourceBindings[resourceIndex];
            if (!TryMapBindingKind(resource.Type, out var kind))
            {
                requirements = Array.Empty<DxReflectedShaderBindingRequirement>();
                return false;
            }

            entries.Add(new DxReflectedShaderBindingRequirement(
                resource.Name,
                kind,
                stage,
                resource.BindPoint,
                resource.BindCount,
                ProGpuDirectXNativeBindingMap.GetNativeBinding(stage, kind, resource.BindPoint),
                resource.Type,
                resource.ReturnType,
                resource.Dimension,
                resource.Flags));
        }

        requirements = entries;
        return true;
    }

    public bool TryCreateInputLayoutDescriptor(
        out DxInputLayoutDescriptor? descriptor,
        uint inputSlot = 0,
        DxInputClassification inputSlotClass = DxInputClassification.PerVertexData,
        uint instanceDataStepRate = 0,
        string label = "Reflected DirectX Input Layout")
    {
        var elements = new List<DxInputElementDescriptor>();
        var alignedByteOffset = 0u;
        var inputSignatureCount = InputSignature.Count;
        for (var parameterIndex = 0; parameterIndex < inputSignatureCount; parameterIndex++)
        {
            var parameter = InputSignature[parameterIndex];
            if (IsSystemGeneratedInput(parameter))
            {
                continue;
            }

            if (!TryInferInputElementFormat(parameter, out var format))
            {
                descriptor = null;
                return false;
            }

            elements.Add(new DxInputElementDescriptor
            {
                SemanticName = parameter.SemanticName,
                SemanticIndex = parameter.SemanticIndex,
                Format = format,
                InputSlot = inputSlot,
                AlignedByteOffset = alignedByteOffset,
                InputSlotClass = inputSlotClass,
                InstanceDataStepRate = instanceDataStepRate,
                ShaderLocation = parameter.Register
            });
            alignedByteOffset += ProGpuDirectXFormatConverter.GetVertexFormatSizeInBytes(format);
        }

        if (elements.Count == 0)
        {
            descriptor = null;
            return false;
        }

        descriptor = new DxInputLayoutDescriptor
        {
            Elements = elements,
            Label = label
        };
        return true;
    }

    private bool ContainsChunk(string fourCC)
    {
        var chunkCount = Chunks.Count;
        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            if (string.Equals(Chunks[chunkIndex].FourCC, fourCC, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsChunk(string firstFourCC, string secondFourCC)
    {
        var chunkCount = Chunks.Count;
        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var fourCC = Chunks[chunkIndex].FourCC;
            if (string.Equals(fourCC, firstFourCC, StringComparison.Ordinal) ||
                string.Equals(fourCC, secondFourCC, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSystemGeneratedInput(DxShaderSignatureParameter parameter)
    {
        return parameter.SystemValueType != 0 ||
            parameter.SemanticName.StartsWith("SV_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMapBindingKind(uint resourceType, out ProGpuDirectXBindingKind kind)
    {
        kind = resourceType switch
        {
            (uint)DxReflectedShaderResourceType.ConstantBuffer => ProGpuDirectXBindingKind.ConstantBuffer,
            (uint)DxReflectedShaderResourceType.TextureBuffer => ProGpuDirectXBindingKind.ShaderResourceView,
            (uint)DxReflectedShaderResourceType.Texture => ProGpuDirectXBindingKind.ShaderResourceView,
            (uint)DxReflectedShaderResourceType.Sampler => ProGpuDirectXBindingKind.Sampler,
            (uint)DxReflectedShaderResourceType.StructuredBuffer => ProGpuDirectXBindingKind.ShaderResourceView,
            (uint)DxReflectedShaderResourceType.ByteAddressBuffer => ProGpuDirectXBindingKind.ShaderResourceView,
            (uint)DxReflectedShaderResourceType.UnorderedAccessTyped => ProGpuDirectXBindingKind.UnorderedAccessView,
            (uint)DxReflectedShaderResourceType.UnorderedAccessStructured => ProGpuDirectXBindingKind.UnorderedAccessView,
            (uint)DxReflectedShaderResourceType.UnorderedAccessByteAddress => ProGpuDirectXBindingKind.UnorderedAccessView,
            (uint)DxReflectedShaderResourceType.UnorderedAccessAppendStructured => ProGpuDirectXBindingKind.UnorderedAccessView,
            (uint)DxReflectedShaderResourceType.UnorderedAccessConsumeStructured => ProGpuDirectXBindingKind.UnorderedAccessView,
            (uint)DxReflectedShaderResourceType.UnorderedAccessStructuredWithCounter => ProGpuDirectXBindingKind.UnorderedAccessView,
            _ => default
        };

        return resourceType is >= (uint)DxReflectedShaderResourceType.ConstantBuffer and <= (uint)DxReflectedShaderResourceType.UnorderedAccessStructuredWithCounter;
    }

    private static bool TryInferInputElementFormat(DxShaderSignatureParameter parameter, out DxResourceFormat format)
    {
        var componentCount = parameter.Mask switch
        {
            0x1 => 1,
            0x3 => 2,
            0x7 => 3,
            0xF => 4,
            _ => 0
        };

        format = DxResourceFormat.Unknown;
        if (componentCount == 0)
        {
            return false;
        }

        format = parameter.ComponentType switch
        {
            D3DRegisterComponentFloat32 => componentCount switch
            {
                1 => DxResourceFormat.R32Float,
                2 => DxResourceFormat.R32G32Float,
                3 => DxResourceFormat.R32G32B32Float,
                _ => DxResourceFormat.R32G32B32A32Float
            },
            D3DRegisterComponentUInt32 => componentCount switch
            {
                1 => DxResourceFormat.R32UInt,
                2 => DxResourceFormat.R32G32UInt,
                3 => DxResourceFormat.R32G32B32UInt,
                _ => DxResourceFormat.R32G32B32A32UInt
            },
            D3DRegisterComponentSInt32 => componentCount switch
            {
                1 => DxResourceFormat.R32SInt,
                2 => DxResourceFormat.R32G32SInt,
                3 => DxResourceFormat.R32G32B32SInt,
                _ => DxResourceFormat.R32G32B32A32SInt
            },
            _ => DxResourceFormat.Unknown
        };

        return format != DxResourceFormat.Unknown;
    }
}

internal static class ProGpuDirectXShaderBytecodeParser
{
    private const uint DxbcHeaderSize = 32u;

    public static ProGpuDirectXShaderBytecodeInfo Parse(ReadOnlySpan<byte> bytecode)
    {
        if (bytecode.Length < 4)
        {
            return Invalid(DxShaderBytecodeContainerKind.Unknown, "Shader bytecode is shorter than a DirectX bytecode magic.");
        }

        var magic = ReadFourCC(bytecode, 0);
        return magic switch
        {
            "DXBC" => ParseDxbc(bytecode),
            "DXIL" => new ProGpuDirectXShaderBytecodeInfo
            {
                ContainerKind = DxShaderBytecodeContainerKind.RawDxil,
                IsValid = true,
                TotalSizeInBytes = checked((uint)bytecode.Length)
            },
            _ when bytecode.Length >= 4 && bytecode[0] == (byte)'B' && bytecode[1] == (byte)'C' && bytecode[2] == 0xC0 && bytecode[3] == 0xDE =>
                new ProGpuDirectXShaderBytecodeInfo
                {
                    ContainerKind = DxShaderBytecodeContainerKind.RawDxilBitcode,
                    IsValid = true,
                    TotalSizeInBytes = checked((uint)bytecode.Length)
                },
            _ => Invalid(DxShaderBytecodeContainerKind.Unknown, $"Unknown DirectX shader bytecode magic '{magic}'.")
        };
    }

    private static ProGpuDirectXShaderBytecodeInfo ParseDxbc(ReadOnlySpan<byte> bytecode)
    {
        if (bytecode.Length < DxbcHeaderSize)
        {
            return Invalid(DxShaderBytecodeContainerKind.Dxbc, "DXBC container header is incomplete.");
        }

        var totalSize = BinaryPrimitives.ReadUInt32LittleEndian(bytecode[24..28]);
        var chunkCount = BinaryPrimitives.ReadUInt32LittleEndian(bytecode[28..32]);
        var offsetsByteCount = checked(chunkCount * 4u);
        if (chunkCount > 4096 || DxbcHeaderSize + offsetsByteCount > bytecode.Length)
        {
            return Invalid(DxShaderBytecodeContainerKind.Dxbc, "DXBC chunk-offset table is outside the bytecode range.");
        }

        if (totalSize == 0 || totalSize > bytecode.Length)
        {
            return Invalid(DxShaderBytecodeContainerKind.Dxbc, "DXBC total size is outside the supplied bytecode range.");
        }

        var chunks = new List<DxShaderBytecodeChunk>(checked((int)chunkCount));
        var inputs = new List<DxShaderSignatureParameter>();
        var outputs = new List<DxShaderSignatureParameter>();
        var patchConstants = new List<DxShaderSignatureParameter>();
        var resources = new List<DxReflectedShaderResourceBinding>();
        var programKind = DxShaderProgramKind.Unknown;
        uint shaderModelMajor = 0;
        uint shaderModelMinor = 0;

        for (var i = 0; i < chunkCount; i++)
        {
            var offsetTableIndex = checked((int)(DxbcHeaderSize + (uint)i * 4u));
            var chunkOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytecode.Slice(offsetTableIndex, 4));
            if (chunkOffset > totalSize || chunkOffset + 8u > totalSize)
            {
                return Invalid(DxShaderBytecodeContainerKind.Dxbc, "DXBC chunk header is outside the declared container size.");
            }

            var chunkStart = checked((int)chunkOffset);
            var fourCC = ReadFourCC(bytecode, chunkStart);
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytecode.Slice(chunkStart + 4, 4));
            if (chunkSize > totalSize - chunkOffset - 8u)
            {
                return Invalid(DxShaderBytecodeContainerKind.Dxbc, $"DXBC chunk '{fourCC}' is outside the declared container size.");
            }

            chunks.Add(new DxShaderBytecodeChunk(fourCC, chunkOffset, chunkSize));
            var chunkData = bytecode.Slice(chunkStart + 8, checked((int)chunkSize));
            switch (fourCC)
            {
                case "ISGN":
                case "ISG1":
                    inputs.AddRange(ParseSignatureParameters(chunkData, hasStream: false));
                    break;
                case "OSGN":
                    outputs.AddRange(ParseSignatureParameters(chunkData, hasStream: false));
                    break;
                case "OSG5":
                    outputs.AddRange(ParseSignatureParameters(chunkData, hasStream: true));
                    break;
                case "PCSG":
                    patchConstants.AddRange(ParseSignatureParameters(chunkData, hasStream: false));
                    break;
                case "RDEF":
                    resources.AddRange(ParseResourceBindings(chunkData));
                    break;
                case "SHDR":
                case "SHEX":
                case "DXIL":
                    if (TryParseProgramVersion(chunkData, out var kind, out var major, out var minor))
                    {
                        programKind = kind;
                        shaderModelMajor = major;
                        shaderModelMinor = minor;
                    }

                    break;
            }
        }

        return new ProGpuDirectXShaderBytecodeInfo
        {
            ContainerKind = DxShaderBytecodeContainerKind.Dxbc,
            IsValid = true,
            TotalSizeInBytes = totalSize,
            Chunks = chunks,
            InputSignature = inputs,
            OutputSignature = outputs,
            PatchConstantSignature = patchConstants,
            ResourceBindings = resources,
            ProgramKind = programKind,
            ShaderModelMajor = shaderModelMajor,
            ShaderModelMinor = shaderModelMinor
        };
    }

    private static List<DxShaderSignatureParameter> ParseSignatureParameters(ReadOnlySpan<byte> chunkData, bool hasStream)
    {
        var parameters = new List<DxShaderSignatureParameter>();
        if (chunkData.Length < 8)
        {
            return parameters;
        }

        var parameterCount = BinaryPrimitives.ReadUInt32LittleEndian(chunkData[..4]);
        var parameterOffset = BinaryPrimitives.ReadUInt32LittleEndian(chunkData[4..8]);
        var entrySize = hasStream ? 28u : 24u;
        if (parameterCount > 4096 || parameterOffset > chunkData.Length)
        {
            return parameters;
        }

        for (var i = 0u; i < parameterCount; i++)
        {
            var entryOffset = parameterOffset + i * entrySize;
            if (entryOffset > chunkData.Length || entrySize > chunkData.Length - entryOffset)
            {
                return parameters;
            }

            var entry = chunkData.Slice(checked((int)entryOffset), checked((int)entrySize));
            var baseOffset = hasStream ? 4 : 0;
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(baseOffset, 4));
            var semanticIndex = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(baseOffset + 4, 4));
            var systemValueType = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(baseOffset + 8, 4));
            var componentType = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(baseOffset + 12, 4));
            var register = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(baseOffset + 16, 4));
            var mask = entry[baseOffset + 20];
            var readWriteMask = entry[baseOffset + 21];
            parameters.Add(new DxShaderSignatureParameter(
                ReadNullTerminatedAscii(chunkData, nameOffset),
                semanticIndex,
                register,
                mask,
                readWriteMask,
                componentType,
                systemValueType));
        }

        return parameters;
    }

    private static List<DxReflectedShaderResourceBinding> ParseResourceBindings(ReadOnlySpan<byte> chunkData)
    {
        var resources = new List<DxReflectedShaderResourceBinding>();
        if (chunkData.Length < 28)
        {
            return resources;
        }

        var resourceCount = BinaryPrimitives.ReadUInt32LittleEndian(chunkData[8..12]);
        var resourceOffset = BinaryPrimitives.ReadUInt32LittleEndian(chunkData[12..16]);
        const uint entrySize = 32u;
        if (resourceCount > 4096 || resourceOffset > chunkData.Length)
        {
            return resources;
        }

        for (var i = 0u; i < resourceCount; i++)
        {
            var entryOffset = resourceOffset + i * entrySize;
            if (entryOffset > chunkData.Length || entrySize > chunkData.Length - entryOffset)
            {
                return resources;
            }

            var entry = chunkData.Slice(checked((int)entryOffset), checked((int)entrySize));
            var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry[..4]);
            resources.Add(new DxReflectedShaderResourceBinding(
                ReadNullTerminatedAscii(chunkData, nameOffset),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[4..8]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[8..12]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[12..16]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[20..24]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[24..28]),
                BinaryPrimitives.ReadUInt32LittleEndian(entry[28..32])));
        }

        return resources;
    }

    private static bool TryParseProgramVersion(
        ReadOnlySpan<byte> chunkData,
        out DxShaderProgramKind programKind,
        out uint shaderModelMajor,
        out uint shaderModelMinor)
    {
        programKind = DxShaderProgramKind.Unknown;
        shaderModelMajor = 0;
        shaderModelMinor = 0;
        if (chunkData.Length < 4)
        {
            return false;
        }

        var versionToken = BinaryPrimitives.ReadUInt32LittleEndian(chunkData[..4]);
        programKind = ((versionToken >> 16) & 0xFFFFu) switch
        {
            0 => DxShaderProgramKind.Pixel,
            1 => DxShaderProgramKind.Vertex,
            2 => DxShaderProgramKind.Geometry,
            3 => DxShaderProgramKind.Hull,
            4 => DxShaderProgramKind.Domain,
            5 => DxShaderProgramKind.Compute,
            _ => DxShaderProgramKind.Unknown
        };
        shaderModelMajor = (versionToken >> 4) & 0xFu;
        shaderModelMinor = versionToken & 0xFu;
        return programKind != DxShaderProgramKind.Unknown;
    }

    private static ProGpuDirectXShaderBytecodeInfo Invalid(DxShaderBytecodeContainerKind kind, string reason)
    {
        return new ProGpuDirectXShaderBytecodeInfo
        {
            ContainerKind = kind,
            IsValid = false,
            FailureReason = reason
        };
    }

    private static string ReadFourCC(ReadOnlySpan<byte> bytes, int offset)
    {
        return Encoding.ASCII.GetString(bytes.Slice(offset, 4));
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> bytes, uint offset)
    {
        if (offset >= bytes.Length)
        {
            return string.Empty;
        }

        var start = checked((int)offset);
        var end = start;
        while (end < bytes.Length && bytes[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(bytes[start..end]);
    }
}
