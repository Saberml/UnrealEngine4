// Copyright 1998-2018 Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "EngineDefines.h"

#ifdef NV_CLOTH_API
#undef NV_CLOTH_API	//Our build system treats PhysX as a module and automatically defines this. PhysX has its own way of defining this.
#endif

#if WITH_NVCLOTH

// IMPROBABLE-BEGIN - Workaround a VS2015 compiler bug when run in FASTBuild (https://github.com/fastbuild/fastbuild/issues/256)
#ifndef __clang__
#pragma push_macro("check")
#else
PUSH_MACRO(check)
#endif
// IMPROBABLE-END
#undef check

#if PLATFORM_XBOXONE
	#pragma pack(push,16)
#elif PLATFORM_WINDOWS
	#if PLATFORM_64BITS
		#pragma pack(push,16)
	#elif PLATFORM_32BITS
		#pragma pack(push,8)
	#endif
#endif

#include "PxErrors.h"
#include "PxErrorCallback.h"

#include "NvCloth/PhaseConfig.h"
#include "NvCloth/Callbacks.h"
#include "NvCloth/Factory.h"
#include "NvCloth/Solver.h"
#include "NvCloth/Cloth.h"

#include "NvClothExt/ClothMeshDesc.h"
#include "NvClothExt/ClothFabricCooker.h"
#include "NvClothExt/ClothMeshQuadifier.h"

#if PLATFORM_XBOXONE
	#pragma pack(pop)
#elif PLATFORM_WINDOWS
	#if PLATFORM_64BITS
		#pragma pack(pop)
	#elif PLATFORM_32BITS
		#pragma pack(pop)
	#endif
#endif

// IMPROBABLE-BEGIN - Workaround a VS2015 compiler bug when run in FASTBuild (https://github.com/fastbuild/fastbuild/issues/256)
#ifndef __clang__
#pragma pop_macro("check")
#else
	POP_MACRO(check)
#endif
// IMPROBABLE-END

#endif
