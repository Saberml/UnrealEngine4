// Copyright 1998-2018 Epic Games, Inc. All Rights Reserved.

using UnrealBuildTool;

public class LibVpx : ModuleRules
{
	public LibVpx(ReadOnlyTargetRules Target) : base(Target)
	{
		Type = ModuleType.External;

		string RootPath = ModuleDirectory + "/libvpx";

		if (Target.Platform == UnrealTargetPlatform.Win64)
		{
			string LibPath = RootPath + "/lib/Win64/VS" + Target.WindowsPlatform.GetVisualStudioCompilerVersionName();
			PublicLibraryPaths.Add(LibPath);

			string LibFileName = "vpxmd.lib";
			PublicAdditionalLibraries.Add(LibFileName);
		}
		else if (Target.IsInPlatformGroup(UnrealPlatformGroup.Unix))
		{
			PublicAdditionalLibraries.Add(RootPath + "/lib/Unix" + ((Target.LinkType == TargetLinkType.Monolithic) ? "/libvpx" : "/libvpx_fPIC") + ".a");
		}

		string IncludePath = RootPath + "/include";
		PublicIncludePaths.Add(IncludePath);
	}
}
