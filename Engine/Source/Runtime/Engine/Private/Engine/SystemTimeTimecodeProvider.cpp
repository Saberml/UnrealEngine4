// Copyright 1998-2018 Epic Games, Inc. All Rights Reserved.

#include "Engine/SystemTimeTimecodeProvider.h"

#include "Misc/CoreMisc.h"
#include "Misc/DateTime.h"


FTimecode USystemTimeTimecodeProvider::GetTimecode() const
{
	const FDateTime DateTime = FDateTime::Now();
	const FTimespan Timespan = DateTime.GetTimeOfDay();
	const double TotalSeconds = Timespan.GetTotalSeconds();
	FFrameNumber FrameNumber = FrameRate.AsFrameNumber(TotalSeconds);

	return FTimecode::FromFrameNumber(FrameNumber, FrameRate, FTimecode::IsDropFormatTimecodeSupported(FrameRate));
}
