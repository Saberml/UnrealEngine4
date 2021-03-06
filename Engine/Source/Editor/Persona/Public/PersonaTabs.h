// Copyright 1998-2018 Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"

// Tab constants
struct PERSONA_API FPersonaTabs
{
	// Selection Details
	static const FName MorphTargetsID;
	static const FName AnimCurveViewID;
	static const FName SkeletonTreeViewID;
	// Skeleton Pose manager
	static const FName RetargetManagerID;
	static const FName RigManagerID;
	// Skeleton/Sockets
	// Anim Blueprint Params
	// Explorer
	// Class Defaults
	static const FName AnimBlueprintPreviewEditorID;
	static const FName AnimBlueprintParentPlayerEditorID;
	// Anim Document
	static const FName ScrubberID;
	// Toolbar
	static const FName PreviewViewportID;
	static const FName PreviewViewport1ID;
	static const FName PreviewViewport2ID;
	static const FName PreviewViewport3ID;
	static const FName AssetBrowserID;
	static const FName MirrorSetupID;
	static const FName AnimBlueprintDebugHistoryID;
	static const FName AnimAssetPropertiesID;
	static const FName MeshAssetPropertiesID;
	static const FName PreviewManagerID;
	static const FName SkeletonAnimNotifiesID;
	static const FName SkeletonSlotNamesID;
	static const FName SkeletonSlotGroupNamesID;
	static const FName CurveNameManagerID;
	static const FName BlendProfileManagerID;

	// Advanced Preview Scene
	static const FName AdvancedPreviewSceneSettingsID;
	static const FName DetailsID;

private:
	FPersonaTabs() {}
};