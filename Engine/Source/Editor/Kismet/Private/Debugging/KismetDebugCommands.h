// Copyright 1998-2016 Epic Games, Inc. All Rights Reserved.

#pragma once

//////////////////////////////////////////////////////////////////////////
// FDebuggingActionCallbacks

class FDebuggingActionCallbacks
{
public:
	static void ClearWatches(class UBlueprint* Blueprint);
	static void ClearWatch(class UEdGraphPin* WatchedPin);
	static void ClearBreakpoints(class UBlueprint* OwnerBlueprint);
	static void ClearBreakpoint(class UBreakpoint* Breakpoint, class UBlueprint* OwnerBlueprint);
	static void SetBreakpointEnabled(class UBreakpoint* Breakpoint, bool bEnabled);
	static void SetEnabledOnAllBreakpoints(class UBlueprint* OwnerBlueprint, bool bShouldBeEnabled);
};

//////////////////////////////////////////////////////////////////////////
