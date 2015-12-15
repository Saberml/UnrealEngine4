// Copyright 1998-2016 Epic Games, Inc. All Rights Reserved.

#pragma once

#include "MetalRHIPrivate.h"
#include "MetalCommandEncoder.h"
#include "MetalRenderPipelineDesc.h"

class FMetalStateCache
{
public:
	FMetalStateCache(FMetalCommandEncoder& CommandEncoder);
	~FMetalStateCache();

	void SetBlendFactor(FLinearColor const& InBlendFactor);
	void SetStencilRef(uint32 const InStencilRef);
	void SetBlendState(FMetalBlendState* InBlendState);
	void SetDepthStencilState(FMetalDepthStencilState* InDepthStencilState);
	void SetRasterizerState(FMetalRasterizerState* InRasterizerState);
	void SetBoundShaderState(FMetalBoundShaderState* BoundShaderState);
	void SetComputeShader(FMetalComputeShader* InComputeShader);
	void SetRenderTargetsInfo(FRHISetRenderTargetsInfo const& InRenderTargets, id<MTLBuffer> const QueryBuffer);
	void SetHasValidRenderTarget(bool const bIsValid);
	void SetViewport(const MTLViewport& InViewport);
	void SetVertexBuffer(uint32 const Index, id<MTLBuffer> Buffer, uint32 const Stride, uint32 const Offset);
#if PLATFORM_MAC
	void SetPrimitiveTopology(MTLPrimitiveTopologyClass PrimitiveType);
#endif
	void BindUniformBuffer(EShaderFrequency const Freq, uint32 const BufferIndex, FUniformBufferRHIParamRef BufferRHI);
	void SetDirtyUniformBuffers(EShaderFrequency const Freq, uint32 const Dirty);

	FMetalShaderParameterCache& GetShaderParameters(uint32 const Stage) { return ShaderParameters[Stage]; }
	
	/** Switch from blit/compute to render command encoding if needed. */
	void ConditionalSwitchToRender(void);
	
	/** Switch from blit/render to compute command encoding if needed. */
	void ConditionalSwitchToCompute(void);
	
	/** Switch from compute/render to blit command encoding if needed. */
	void ConditionalSwitchToBlit(void);
	
	FMetalRenderPipelineDesc const& GetRenderPipelineDesc() const { return PipelineDesc; }
	FLinearColor const& GetBlendFactor() const { return BlendFactor; }
	uint32 GetStencilRef() const { return StencilRef; }
	FMetalBlendState* GetBlendState() const { return BlendState; }
	FMetalDepthStencilState* GetDepthStencilState() const { return DepthStencilState; }
	FMetalRasterizerState* GetRasterizerState() const { return RasterizerState; }
	FMetalBoundShaderState* GetBoundShaderState() const { return BoundShaderState; }
	FMetalComputeShader* GetComputeShader() const { return ComputeShader; }
	CGSize GetFrameBufferSize() const { return FrameBufferSize; }
	FRHISetRenderTargetsInfo const& GetRenderTargetsInfo() const { return RenderTargetsInfo; }
	int32 GetNumRenderTargets() { return bHasValidRenderTarget ? RenderTargetsInfo.NumColorRenderTargets : -1; }
	bool GetHasValidRenderTarget() const { return bHasValidRenderTarget; }
	const MTLViewport& GetViewport() const { return Viewport; }
	id<MTLBuffer> GetVertexBuffer(uint32 const Index) { check(Index < MaxMetalStreams); return VertexBuffers[Index]; }
	uint32 GetVertexStride(uint32 const Index) { check(Index < MaxMetalStreams); return VertexStrides[Index]; }
	uint32 GetRenderTargetArraySize() const { return RenderTargetArraySize; }
	TArray<TRefCountPtr<FRHIUniformBuffer>> GetBoundUniformBuffers(EShaderFrequency const Freq) const { return BoundUniformBuffers[Freq]; }
	uint32 GetDirtyUniformBuffers(EShaderFrequency const Freq) const { return DirtyUniformBuffers[Freq]; }

private:
	void ConditionalUpdateBackBuffer(FMetalSurface& Surface);
	bool NeedsToSetRenderTarget(const FRHISetRenderTargetsInfo& RenderTargetsInfo) const;
	
private:
	FMetalCommandEncoder& CommandEncoder;
	FMetalShaderParameterCache ShaderParameters[CrossCompiler::NUM_SHADER_STAGES];

	FMetalRenderPipelineDesc PipelineDesc;

	TArray< TRefCountPtr<FRHIUniformBuffer> > BoundUniformBuffers[SF_NumFrequencies];
	
	/** Bitfield for which uniform buffers are dirty */
	uint64 DirtyUniformBuffers[SF_NumFrequencies];
	
	id<MTLBuffer> VertexBuffers[MaxMetalStreams];
	uint32 VertexStrides[MaxMetalStreams];

	id<MTLBuffer> VisibilityResults;
	
	TRefCountPtr<FMetalBlendState> BlendState;
	TRefCountPtr<FMetalDepthStencilState> DepthStencilState;
	TRefCountPtr<FMetalRasterizerState> RasterizerState;
	TRefCountPtr<FMetalBoundShaderState> BoundShaderState;
	TRefCountPtr<FMetalComputeShader> ComputeShader;
	uint32 StencilRef;
	
	FLinearColor BlendFactor;
	CGSize FrameBufferSize;
	
	uint32 RenderTargetArraySize;

	MTLViewport Viewport;
	
	FRHISetRenderTargetsInfo RenderTargetsInfo;
	bool bHasValidRenderTarget;
};
