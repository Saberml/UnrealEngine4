// Copyright (c) Improbable Worlds Ltd, All Rights Reserved
// IMPROBABLE-BEGIN - Added ESpatialClassFlags
#include "SpatialGDKCustomVersion.h"
#include "Serialization/CustomVersion.h"

const FGuid FSpatialGDKCustomVersion::GUID(0x536043e7, 0x44593c7d, 0x752eb2b0, 0xdd1dd268);

// Register the custom version with core
FCustomVersionRegistration GRegisterSpatialGDKCustomVersion(FSpatialGDKCustomVersion::GUID, FSpatialGDKCustomVersion::LatestVersion, TEXT("SpatialGDKVer"));

// IMPROBABLE-END