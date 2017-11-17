// Copyright 1998-2017 Epic Games, Inc. All Rights Reserved.

#pragma once

#include "defines.hpp"
#include "ns.hpp"
#include "device.hpp"

MTLPP_CLASS(NSTask);

namespace mtlpp
{
	enum class Platform
	{
		iOS = 0,
		macOS = 1,
		tvOS = 2,
		watchOS = 3,
	};
	
	enum class AIROptions
	{
		none = 0,
		final = 1,
		verbose = 2
	};
	
	class CompilerOptions : public CompileOptions
	{
	public:
		CompilerOptions() : Platform(Platform::macOS), KeepDebugInfo(false), AirOptions(AIROptions::none), AirString(nullptr) { MinOS[0] = 0; MinOS[1] = 0; }
		CompilerOptions(MTLCompileOptions* handle) : CompileOptions(handle), Platform(Platform::macOS), KeepDebugInfo(false), AirOptions(AIROptions::none), AirString(nullptr) { MinOS[0] = 0; MinOS[1] = 0; }
		
		Platform Platform;
		int MinOS[2];
		bool KeepDebugInfo;
		AIROptions AirOptions;
		ns::String AirPath;
		ns::String* AirString;
	}
	MTLPP_AVAILABLE(10_11, 8_0);
	
	class Compiler
	{
	public:
		static bool Compile(const char* source, ns::String const& output, const CompilerOptions& options, ns::Error* error);
		
		static bool Compile(ns::String const& source, ns::String const& output, const CompilerOptions& options, ns::Error* error);
		
		static bool Link(ns::Array<ns::String> const& source, ns::String const& output, const CompilerOptions& options, ns::Error* error);
	}
	MTLPP_AVAILABLE_MAC(10_11);
}