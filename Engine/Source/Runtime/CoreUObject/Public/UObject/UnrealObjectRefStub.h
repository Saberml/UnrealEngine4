#pragma once

#include "SharedPointer.h"

#include <cstdint>

#include "UnrealObjectRefStub.generated.h"

using Worker_EntityId = std::int64_t;

USTRUCT()
struct FUnrealObjectRefStub
{
	GENERATED_BODY()

	FUnrealObjectRefStub() = default;

	FUnrealObjectRefStub(Worker_EntityId Entity, uint32 Offset)
		: Entity(Entity)
		, Offset(Offset)
	{}

	FUnrealObjectRefStub(Worker_EntityId Entity, uint32 Offset, FString Path, FUnrealObjectRefStub Outer)
		: Entity(Entity)
		, Offset(Offset)
		, Path(new FString(Path))
		, Outer(new FUnrealObjectRefStub(Outer))
	{}

	FUnrealObjectRefStub(const FUnrealObjectRefStub& In)
		: Entity(In.Entity)
		, Offset(In.Offset)
		//, Path(In.Path)
		//, Outer(In.Outer)
	{}

	FORCEINLINE FUnrealObjectRefStub& operator=(const FUnrealObjectRefStub& In)
	{
		Entity = In.Entity;
		Offset = In.Offset;
		//Path = In.Path.Release();
		//Outer = In.Outer;
		return *this;
	}

	FORCEINLINE FString ToString() const
	{
		return FString::Printf(TEXT("(entity ID: %lld, offset: %u)"), Entity, Offset);
	}

	FORCEINLINE bool operator==(const FUnrealObjectRefStub& Other) const
	{
		return Entity == Other.Entity &&
			Offset == Other.Offset &&
			((!Path && !Other.Path) || (Path && Other.Path && Path->Equals(*Other.Path))) &&
			((!Outer && !Other.Outer) || (Outer && Other.Outer && *Outer == *Other.Outer));
	}

	FORCEINLINE bool operator!=(const FUnrealObjectRefStub& Other) const
	{
		return !operator==(Other);
	}

	friend uint32 GetTypeHash(const FUnrealObjectRefStub& ObjectRef);

	Worker_EntityId Entity;
	uint32 Offset;
	// TODO: Write our own Option class
	TUniquePtr<FString> Path;
	TUniquePtr<FUnrealObjectRefStub> Outer;
};

inline uint32 GetTypeHash(const FUnrealObjectRefStub& ObjectRef)
{
	uint32 Result = 1327u;
	Result = (Result * 977u) + GetTypeHash(static_cast<int64>(ObjectRef.Entity));
	Result = (Result * 977u) + GetTypeHash(ObjectRef.Offset);
	Result = (Result * 977u) + (ObjectRef.Path ? 1327u * (GetTypeHash(*ObjectRef.Path) + 977u) : 977u);
	Result = (Result * 977u) + (ObjectRef.Outer ? 1327u * (GetTypeHash(*ObjectRef.Outer) + 977u) : 977u);
	return Result;
}
