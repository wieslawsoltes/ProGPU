# ProGPU memory capture

PID: 56098 (`dotnet`)
Samples: 4
Successful macOS VM-map samples: 4 of 4

| Metric | First | Last | Delta |
| --- | ---: | ---: | ---: |
| workingSetBytes | 173.88 MiB | 128.80 MiB | -45.08 MiB |
| physicalFootprintBytes | 267.40 MiB | 254.00 MiB | -13.40 MiB |
| region-resident:owned unmapped (graphics) | 102.80 MiB | 102.80 MiB | +0.00 MiB |
| region-dirty:owned unmapped (graphics) | 102.80 MiB | 102.80 MiB | +0.00 MiB |
| region-resident:IOAccelerator (graphics) | 6.05 MiB | 6.05 MiB | +0.00 MiB |
| region-dirty:IOAccelerator (graphics) | 5.86 MiB | 5.92 MiB | +0.06 MiB |
| region-resident:VM_ALLOCATE | 42.20 MiB | 17.30 MiB | -24.90 MiB |
| region-dirty:VM_ALLOCATE | 42.20 MiB | 17.30 MiB | -24.90 MiB |
| region-resident:IOSurface | 37.50 MiB | 37.50 MiB | +0.00 MiB |
| region-dirty:IOSurface | 37.50 MiB | 37.50 MiB | +0.00 MiB |
| region-resident:Dispatch continuations | 0.44 MiB | 0.47 MiB | +0.03 MiB |
| region-dirty:Dispatch continuations | 0.42 MiB | 0.47 MiB | +0.05 MiB |

## Native heap

Live allocator payload: 25.55 MiB

| Class | Count | Bytes |
| --- | ---: | ---: |
| non-object | 63811 | 21.15 MiB |
| MTLResourceList | 8 | 0.38 MiB |
| AGX::G15::VertexProgramVariant | 14 | 0.07 MiB |
| AGX::G15::FragmentProgramVariant | 11 | 0.05 MiB |
| AGXG15XFamilyRenderPipeline | 11 | 0.04 MiB |
| AGXG15SDevice._impl (malloc) | 1 | 0.02 MiB |
| AGXG15XFamilyFragmentProgram | 11 | 0.02 MiB |
| AGX::G15::BlitComputeProgramVariant | 5 | 0.02 MiB |
| AGXG15XFamilyVertexProgram | 11 | 0.02 MiB |
| AGX::G15::ComputeProgramVariant | 3 | 0.01 MiB |
| IOGPUMetalPooledResource | 38 | 0.01 MiB |
| AGX::G15::BackgroundObjectProgramVariant | 3 | 0.01 MiB |
| AGX::G15::Texture | 9 | 0.01 MiB |
| AGXG15XFamilyBuffer | 16 | 0.01 MiB |
| _MTLDeviceFeatureQueries | 1 | 0.01 MiB |
| AGXBuffer | 13 | 0.01 MiB |
| IOGPUMetalResourcePool | 44 | 0.00 MiB |
| IOGPUMetalResourcePool._resourceArgs (struct IOGPUNewResourceArgs) | 44 | 0.00 MiB |
| AGXG15XFamilyTexture | 6 | 0.00 MiB |
| AGX::G15::ClearVisibilityVertexProgramVariant | 1 | 0.00 MiB |
| AGX::G15::DummyFeedbackFragmentProgramVariant | 1 | 0.00 MiB |
| AGX::G15::PassthroughVertexProgramVariant | 1 | 0.00 MiB |
| AGXG15SDevice | 1 | 0.00 MiB |
| AGXG15SDevice._buffer_suballocator (struct IOGPUMetalSuballocator) | 1 | 0.00 MiB |
| AGX::EndOfTileProgramKey | 2 | 0.00 MiB |
| IOGPUMetalResource | 6 | 0.00 MiB |
| AGXG15XFamilyCommandBuffer | 2 | 0.00 MiB |
| AGX::BlitComputeProgramKey | 2 | 0.00 MiB |
| IOGPUMetalDeviceShmem | 10 | 0.00 MiB |
| std::__shared_ptr_emplace<AGX::FunctionTableSet<AGXG15XFamilyUserIntersectionFunctionTable>> | 17 | 0.00 MiB |
| std::__shared_ptr_emplace<AGX::FunctionTableSet<AGXG15XFamilyVisibleFunctionTable>> | 17 | 0.00 MiB |
| AGXG15XFamilyCommandBuffer._impl (malloc) | 2 | 0.00 MiB |
| AGXG15XFamilyCommandQueue | 1 | 0.00 MiB |
| AGX::BackgroundObjectProgramKey | 1 | 0.00 MiB |
| AGX::BlitFragmentProgramKey | 1 | 0.00 MiB |
| AGX::BlitFastClearProgramKey | 1 | 0.00 MiB |
| AGX::BlitSparseProgramKey | 1 | 0.00 MiB |
| AGX::BlitVertexFastClearProgramKey | 1 | 0.00 MiB |
| AGX::BlitVertexProgramKey | 1 | 0.00 MiB |
| AGX::ComputeControlFlowPredicateProgramKey | 1 | 0.00 MiB |
| AGX::PassthroughObjectProgramKey | 1 | 0.00 MiB |
| AGX::TessellationObjectProgramKey | 1 | 0.00 MiB |
| AGX::TileDispatchVertexProgramKey | 1 | 0.00 MiB |
| AGXG15XFamilyResidencySet._hashTable (struct) | 1 | 0.00 MiB |
| AGXG15XFamilyRayTracingAccelerationStructure | 1 | 0.00 MiB |
| AGXG15XFamilyComputeOrFragmentOrTileProgram | 11 | 0.00 MiB |
| IOGPUMetalDeviceShmemPool | 5 | 0.00 MiB |
| MTLCompilerFSCache | 2 | 0.00 MiB |
| MTLCompilerScheduler | 1 | 0.00 MiB |
| AGXG15XFamilyResidencySet | 1 | 0.00 MiB |
| _MTLCommandBufferEncoderInfo | 5 | 0.00 MiB |
| AGXG15XFamilySampler | 1 | 0.00 MiB |
| MTLPipelineDataCache | 1 | 0.00 MiB |
| std::__shared_ptr_emplace<AGX::G15::Sampler> | 1 | 0.00 MiB |
| MTLResourceListPool | 3 | 0.00 MiB |
| AGXG15SDevice._supportedGPUFamilies (vector<MTLGPUFamily>) | 1 | 0.00 MiB |
| AGXG15XFamilyDepthStencilState | 1 | 0.00 MiB |
| MTLCompiler | 1 | 0.00 MiB |
| AGXG15SDevice._libraryBuilder (malloc) | 1 | 0.00 MiB |
| MTLIOAccelService | 1 | 0.00 MiB |
| MTLIOAccelServiceGlobalContext | 1 | 0.00 MiB |
| MTLLoader | 1 | 0.00 MiB |
| MTLLoader._global (malloc) | 1 | 0.00 MiB |
| AGX::G15::EncoderComputeServiceCDMSubstreamProcessor | 1 | 0.00 MiB |
| AGXG15SDevice._commandBufferStoragePool (struct IOGPUMetalCommandBufferStoragePool) | 1 | 0.00 MiB |
| AGXG15XFamilyCommandBuffer._completedCallbackBlockPtr (malloc) | 1 | 0.00 MiB |
| MTLIOAccelService._notifyPort (struct IONotificationPort) | 1 | 0.00 MiB |
| MTLIOAccelServiceGlobalContext._deviceNotifyPort (struct IONotificationPort) | 1 | 0.00 MiB |
| IOGPUMemoryInfo | 1 | 0.00 MiB |
| _MTLPipelineCache | 1 | 0.00 MiB |
| std::__shared_ptr_pointer<MTLCompilerCache*, std::shared_ptr<MTLCompilerCache>::__shared_ptr_default_delete<MTLCompilerCache, MTLCompilerCache>> | 1 | 0.00 MiB |
| AGXG15SDevice._pipelineLibraryBuilder (struct MTLPipelineLibraryBuilder) | 1 | 0.00 MiB |
| MTLPrivateDataTable | 1 | 0.00 MiB |
