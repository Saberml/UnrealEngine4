// Copyright 1998-2018 Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "AssetTypeActions/AssetTypeActions_Texture.h"
#include "Engine/TextureCube.h"

class FAssetTypeActions_TextureCube : public FAssetTypeActions_Texture
{
public:
	// IAssetTypeActions Implementation
	virtual FText GetName() const override { return NSLOCTEXT("AssetTypeActions", "AssetTypeActions_TextureCube", "Texture Cube"); }
	virtual FColor GetTypeColor() const override { return FColor(192,64,128); }
	virtual UClass* GetSupportedClass() const override { return UTextureCube::StaticClass(); }
	virtual bool CanFilter() override { return true; }
};
