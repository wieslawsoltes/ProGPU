# ProGPU memory capture

PID: 55932 (`dotnet`)
Samples: 4
Successful macOS VM-map samples: 4 of 4

| Metric | First | Last | Delta |
| --- | ---: | ---: | ---: |
| workingSetBytes | 189.58 MiB | 189.02 MiB | -0.56 MiB |
| physicalFootprintBytes | 322.20 MiB | 148.20 MiB | -174.00 MiB |
| region-resident:owned unmapped (graphics) | 137.10 MiB | 5.08 MiB | -132.02 MiB |
| region-dirty:owned unmapped (graphics) | 5.08 MiB | 5.08 MiB | +0.00 MiB |
| region-resident:IOAccelerator (graphics) | 6.94 MiB | 2.02 MiB | -4.92 MiB |
| region-dirty:IOAccelerator (graphics) | 6.39 MiB | 1.47 MiB | -4.92 MiB |
| region-resident:VM_ALLOCATE | 56.40 MiB | 55.50 MiB | -0.90 MiB |
| region-dirty:VM_ALLOCATE | 56.40 MiB | 55.50 MiB | -0.90 MiB |
| region-resident:IOSurface | 25.00 MiB | 25.00 MiB | +0.00 MiB |
| region-dirty:IOSurface | 25.00 MiB | 25.00 MiB | +0.00 MiB |
| region-resident:Dispatch continuations | 0.52 MiB | 0.52 MiB | +0.00 MiB |
| region-dirty:Dispatch continuations | 0.52 MiB | 0.52 MiB | +0.00 MiB |

## Native heap

Live allocator payload: 30.53 MiB

| Class | Count | Bytes |
| --- | ---: | ---: |
| non-object | 73891 | 26.04 MiB |
| MTLResourceList | 22 | 1.03 MiB |
| IOGPUMetalPooledResource | 187 | 0.07 MiB |
| AGX::G15::VertexProgramVariant | 6 | 0.03 MiB |
| AGX::G15::ComputeProgramVariant | 5 | 0.02 MiB |
| AGX::G15::FragmentProgramVariant | 5 | 0.02 MiB |
| AGXG15XFamilyRenderPipeline | 5 | 0.02 MiB |
| AGXG15SDevice._impl (malloc) | 1 | 0.02 MiB |
| AGX::G15::BackgroundObjectProgramVariant | 4 | 0.01 MiB |
| AGX::G15::BlitComputeProgramVariant | 4 | 0.01 MiB |
| AGXG15XFamilyCommandBuffer | 15 | 0.01 MiB |
| AGXG15XFamilyBuffer | 24 | 0.01 MiB |
| AGX::G15::BlitVertexFastClearProgramVariant | 3 | 0.01 MiB |
| AGXG15XFamilyCommandBuffer._impl (malloc) | 15 | 0.01 MiB |
| AGX::G15::Texture | 9 | 0.01 MiB |
| AGXG15XFamilyFragmentProgram | 4 | 0.01 MiB |
| IOGPUMetalDeviceShmem | 40 | 0.01 MiB |
| _MTLDeviceFeatureQueries | 1 | 0.01 MiB |
| AGXBuffer | 13 | 0.01 MiB |
| IOGPUMetalResourcePool | 44 | 0.00 MiB |
| IOGPUMetalResourcePool._resourceArgs (struct IOGPUNewResourceArgs) | 44 | 0.00 MiB |
| AGXG15XFamilyVertexProgram | 3 | 0.00 MiB |
| _MTLFunctionInternal | 9 | 0.00 MiB |
| AGXG15XFamilyTexture | 6 | 0.00 MiB |
| AGX::G15::ClearVisibilityVertexProgramVariant | 1 | 0.00 MiB |
| AGX::G15::DummyFeedbackFragmentProgramVariant | 1 | 0.00 MiB |
| AGX::G15::PassthroughVertexProgramVariant | 1 | 0.00 MiB |
| AGX::EndOfTileProgramKey | 3 | 0.00 MiB |
| AGXG15SDevice | 1 | 0.00 MiB |
| AGXG15SDevice._buffer_suballocator (struct IOGPUMetalSuballocator) | 1 | 0.00 MiB |
| AGXG15XFamilyComputeProgram | 2 | 0.00 MiB |
| std::__shared_ptr_emplace<MTLXPCCompilerConnection> | 10 | 0.00 MiB |
| IOGPUMetalResource | 6 | 0.00 MiB |
| AGXG15XFamilyComputePipeline | 2 | 0.00 MiB |
| AGX::BackgroundObjectProgramKey | 2 | 0.00 MiB |
| AGXG15XFamilyCommandQueue | 1 | 0.00 MiB |
| MTLVertexAttributeInternal | 24 | 0.00 MiB |
| std::__shared_ptr_emplace<AGX::FunctionTableSet<AGXG15XFamilyUserIntersectionFunctionTable>> | 13 | 0.00 MiB |
| std::__shared_ptr_emplace<AGX::FunctionTableSet<AGXG15XFamilyVisibleFunctionTable>> | 13 | 0.00 MiB |
| AGX::BlitComputeProgramKey | 1 | 0.00 MiB |
| AGX::BlitFragmentProgramKey | 1 | 0.00 MiB |
| _MTLLibrary._cacheEntry (struct MTLLibraryContainer) | 9 | 0.00 MiB |
| _MTLLibrary | 12 | 0.00 MiB |
| AGX::BlitFastClearProgramKey | 1 | 0.00 MiB |
| AGX::BlitSparseProgramKey | 1 | 0.00 MiB |
| AGX::BlitVertexFastClearProgramKey | 1 | 0.00 MiB |
| AGX::BlitVertexProgramKey | 1 | 0.00 MiB |
| AGX::ComputeControlFlowPredicateProgramKey | 1 | 0.00 MiB |
| AGX::PassthroughObjectProgramKey | 1 | 0.00 MiB |
| AGX::TessellationObjectProgramKey | 1 | 0.00 MiB |
| AGX::TileDispatchVertexProgramKey | 1 | 0.00 MiB |
| AGXG15XFamilySampler | 3 | 0.00 MiB |
| AGXG15XFamilyResidencySet._hashTable (struct) | 1 | 0.00 MiB |
| AGXG15XFamilyRayTracingAccelerationStructure | 1 | 0.00 MiB |
| IOGPUMetalDeviceShmemPool | 5 | 0.00 MiB |
| MTLCompilerFSCache | 2 | 0.00 MiB |
| MTLCompilerScheduler | 1 | 0.00 MiB |
| AGXG15XFamilyResidencySet | 1 | 0.00 MiB |
| AGXG15XFamilyComputeOrFragmentOrTileProgram | 6 | 0.00 MiB |
| AGXG15XFamilyCommandBuffer._scheduledCallbackBlockPtr (malloc) | 4 | 0.00 MiB |
| MTLPipelineDataCache | 1 | 0.00 MiB |
| std::__shared_ptr_emplace<AGX::G15::Sampler> | 1 | 0.00 MiB |
| MTLResourceListPool | 3 | 0.00 MiB |
| std::__shared_ptr_emplace<MTLCompilerProcess> | 2 | 0.00 MiB |
| AGXG15SDevice._supportedGPUFamilies (vector<MTLGPUFamily>) | 1 | 0.00 MiB |
| MTLCommandQueueDescriptorInternal | 1 | 0.00 MiB |
| AGXG15XFamilyCommandBuffer._completedCallbackBlockPtr (malloc) | 2 | 0.00 MiB |
| MTLCompiler | 1 | 0.00 MiB |
| AGXG15SDevice._libraryBuilder (malloc) | 1 | 0.00 MiB |
| MTLIOAccelService | 1 | 0.00 MiB |
| MTLIOAccelServiceGlobalContext | 1 | 0.00 MiB |
| MTLLoader | 1 | 0.00 MiB |
| MTLLoader._global (malloc) | 1 | 0.00 MiB |
| AGX::G15::EncoderComputeServiceCDMSubstreamProcessor | 1 | 0.00 MiB |
| AGXG15SDevice._commandBufferStoragePool (struct IOGPUMetalCommandBufferStoragePool) | 1 | 0.00 MiB |
| MTLIOAccelService._notifyPort (struct IONotificationPort) | 1 | 0.00 MiB |
| MTLIOAccelServiceGlobalContext._deviceNotifyPort (struct IONotificationPort) | 1 | 0.00 MiB |
| IOGPUMemoryInfo | 1 | 0.00 MiB |
| _MTLPipelineCache | 1 | 0.00 MiB |
| std::__shared_ptr_pointer<MTLCompilerCache*, std::shared_ptr<MTLCompilerCache>::__shared_ptr_default_delete<MTLCompilerCache, MTLCompilerCache>> | 1 | 0.00 MiB |
| AGXG15SDevice._pipelineLibraryBuilder (struct MTLPipelineLibraryBuilder) | 1 | 0.00 MiB |
| MTLPrivateDataTable | 1 | 0.00 MiB |
