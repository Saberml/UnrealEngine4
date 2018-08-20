// Copyright (c) Improbable Worlds Ltd, All Rights Reserved
// IMPROBABLE-BEGIN - Added ESpatialClassFlags
#pragma once

#include "CoreMinimal.h"
#include "Misc/Guid.h"

// Custom serialization version for assets associated with SpatialGDK
struct CORE_API FSpatialGDKCustomVersion
{
	enum Type
	{
		// Before any version changes were made due to ESpatialClassFlags addition
		BeforeCustomVersionWasAdded = 0,

		// Added Spatial Flags to UClass serialization
		AddedSpatialFlags = 1,

		// -----<new versions can be added above this line>-------------------------------------------------
		VersionPlusOne,
		LatestVersion = VersionPlusOne - 1
	};

	// The GUID for this custom version number
	const static FGuid GUID;

private:
	FSpatialGDKCustomVersion() {}
};
// IMPROBABLE-END