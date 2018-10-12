// Based on https://github.com/liamkf/Unreal_FASTBuild/

// Copyright 2018 Yassine Riahi and Liam Flookes. Provided under a MIT License, see license file on github.
// Used to generate a fastbuild .bff file from UnrealBuildTool to allow caching and distributed builds.
// Tested with Windows 10, Visual Studio 2015/2017, Unreal Engine 4.19.1, FastBuild v0.95
// Durango is fully supported (Compiles with VS2015).
// Orbis will likely require some changes.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using Tools.DotNETCommon;

namespace UnrealBuildTool
{
	class FASTBuild : ActionExecutor
	{
		/*---- Configurable User settings ----*/

		// Used to specify a non-standard location for the FBuild.exe, for example if you have not added it to your PATH environment variable.
		public static string FBuildExePathOverride =
			Environment.GetEnvironmentVariable("FASTBUILD_EXE_PATH") ?? string.Empty;

		// Controls network build distribution
		private bool bEnableDistribution = true;


		// Controls whether to use caching at all. CachePath and CacheMode are only relevant if this is enabled.
		private bool bEnableCaching = true;

		// Location of the shared cache, it could be a local or network path (i.e: @"\\DESKTOP-BEAST\FASTBuildCache").
		// Only relevant if bEnableCaching is true;
		private readonly string CachePath = Environment.GetEnvironmentVariable("FASTBUILD_CACHE_PATH");

		public enum eCacheMode
		{
			ReadWrite, // This machine will both read and write to the cache
			ReadOnly, // This machine will only read from the cache, use for developer machines when you have centralized build machines
			WriteOnly, // This machine will only write from the cache, use for build machines when you have centralized build machines
		}

		// Cache access mode
		// Only relevant if bEnableCaching is true;
		private eCacheMode CacheMode = eCacheMode.ReadWrite;

		/*--------------------------------------*/

		public override string Name
		{
			get { return "FASTBuild"; }
		}

		private static readonly string[] KnownCompilerOptions =
			{"/Fo", "/fo", "/Yc", "/Yu", "/Fp", "-o", "/I", "-I", "/D", "-D", "/l", "/FI"};

		private static readonly string[] KnownLinkerOptions =
			{"/OUT:", "/PDB", "/IMPLIB", "/NODEFAULTLIB:", "/LIBPATH", "-o"};

		public static bool IsAvailable()
		{
			if (FBuildExePathOverride != "")
			{
				return File.Exists(FBuildExePathOverride);
			}

			// Get the name of the FASTBuild executable.
			string fbuild = "fbuild";
			if (BuildHostPlatform.Current.Platform == UnrealTargetPlatform.Win64)
			{
				fbuild = "fbuild.exe";
			}

			// Search the path for it
			string PathVariable = Environment.GetEnvironmentVariable("PATH");
			foreach (string SearchPath in PathVariable.Split(Path.PathSeparator))
			{
				try
				{
					string PotentialPath = Path.Combine(SearchPath, fbuild);
					if (File.Exists(PotentialPath))
					{
						return true;
					}
				}
				catch (ArgumentException)
				{
					// PATH variable may contain illegal characters; just ignore them.
				}
			}

			return false;
		}

		private readonly HashSet<string> ForceLocalCompileModules = new HashSet<string>()
			{"Module.ProxyLODMeshReduction"};

		private enum FBBuildType
		{
			Windows,
			XBOne,
			PS4,
			LinuxCrossCompile
		}

		private FBBuildType BuildType = FBBuildType.Windows;

		private void DetectBuildType(List<Action> Actions)
		{
			foreach (Action action in Actions)
			{
				if (action.ActionType != ActionType.Compile && action.ActionType != ActionType.Link)
					continue;

				if (action.CommandPath.Contains("orbis"))
				{
					BuildType = FBBuildType.PS4;
					return;
				}
				else if (action.CommandArguments.Contains("Intermediate\\Build\\XboxOne"))
				{
					BuildType = FBBuildType.XBOne;
					return;
				}
				else if (action.CommandPath.Contains("Microsoft")) // Not a great test.
				{
					BuildType = FBBuildType.Windows;
					return;
				}
				else if (action.CommandPath.Contains("clang++") || action.CommandArguments.Contains("gnu-ar.exe")
				) // Not a great test.
				{
					BuildType = FBBuildType.LinuxCrossCompile;
					return;
				}
			}
		}

		private bool IsMSVC()
		{
			return BuildType == FBBuildType.Windows || BuildType == FBBuildType.XBOne;
		}

		private bool IsXBOnePDBUtil(Action action)
		{
			return action.CommandPath.Contains("XboxOnePDBFileUtil.exe");
		}

		private string GetCompilerName(Action Action)
		{
			if (Action.CommandPath.Contains("rc.exe"))
			{
				return "UE4ResourceCompiler";
			}

			switch (BuildType)
			{
				default:
				case FBBuildType.XBOne:
				case FBBuildType.Windows: return "UE4Compiler";
				case FBBuildType.PS4: return "UE4PS4Compiler";
				case FBBuildType.LinuxCrossCompile: return "UE4LinuxCrossCompiler";
			}
		}

		//Run FASTBuild on the list of actions. Relies on fbuild.exe being in the path.
		public override bool ExecuteActions(List<Action> Actions, bool bLogDetailedActionStats)
		{
			if (Actions.Count == 0)
			{
				return true;
			}

			DetectBuildType(Actions);

			var FASTBuildFilePath = Path.Combine(UnrealBuildTool.EngineDirectory.FullName, "Intermediate",
				"Build", "fbuild.bff");
			if (CreateBffFile(Actions, FASTBuildFilePath))
			{
				return ExecuteBffFile(FASTBuildFilePath);
			}

			return false;
		}


		private string SubstituteEnvironmentVariables(string commandLineString)
		{
			string outputString = commandLineString.Replace("$(DurangoXDK)", "$DurangoXDK$");
			outputString = outputString.Replace("$(SCE_ORBIS_SDK_DIR)", "$SCE_ORBIS_SDK_DIR$");
			outputString = outputString.Replace("$(DXSDK_DIR)", "$DXSDK_DIR$");
			outputString = outputString.Replace("$(CommonProgramFiles)", "$CommonProgramFiles$");
			outputString = outputString.Replace("${ORIGIN}", "^${ORIGIN}");

			return outputString;
		}


		private List<Action> SortActions(List<Action> InActions)
		{
			List<Action> Actions = InActions;

			int NumSortErrors = 0;
			for (int ActionIndex = 0; ActionIndex < InActions.Count; ActionIndex++)
			{
				Action Action = InActions[ActionIndex];
				foreach (FileItem Item in Action.PrerequisiteItems)
				{
					if (Item.ProducingAction != null && InActions.Contains(Item.ProducingAction))
					{
						int DepIndex = InActions.IndexOf(Item.ProducingAction);
						if (DepIndex > ActionIndex)
						{
							NumSortErrors++;
						}
					}
				}
			}

			if (NumSortErrors > 0)
			{
				Actions = new List<Action>();
				var UsedActions = new HashSet<int>();
				for (int ActionIndex = 0; ActionIndex < InActions.Count; ActionIndex++)
				{
					if (UsedActions.Contains(ActionIndex))
					{
						continue;
					}

					Action Action = InActions[ActionIndex];
					foreach (FileItem Item in Action.PrerequisiteItems)
					{
						if (Item.ProducingAction != null && InActions.Contains(Item.ProducingAction))
						{
							int DepIndex = InActions.IndexOf(Item.ProducingAction);
							if (UsedActions.Contains(DepIndex))
							{
								continue;
							}

							Actions.Add(Item.ProducingAction);
							UsedActions.Add(DepIndex);
						}
					}

					Actions.Add(Action);
					UsedActions.Add(ActionIndex);
				}

				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					Action Action = Actions[ActionIndex];
					foreach (FileItem Item in Action.PrerequisiteItems)
					{
						if (Item.ProducingAction != null && Actions.Contains(Item.ProducingAction))
						{
							int DepIndex = Actions.IndexOf(Item.ProducingAction);
							if (DepIndex > ActionIndex)
							{
								Console.WriteLine("Action is not topologically sorted.");
								Console.WriteLine("  {0} {1}", Action.CommandPath, Action.CommandArguments);
								Console.WriteLine("Dependency");
								Console.WriteLine("  {0} {1}", Item.ProducingAction.CommandPath,
									Item.ProducingAction.CommandArguments);
								throw new BuildException("Cyclical Dependency in action graph.");
							}
						}
					}
				}
			}

			return Actions;
		}

		private void WriteEnvironmentSetup(Stream stream)
		{
			VCEnvironment VCEnv = null;
			// This may fail if the caller emptied PATH; we try to ignore the problem since
			// it probably means we are building for another platform.
			if (BuildType == FBBuildType.Windows)
			{
				VCEnv = VCEnvironment.SetEnvironment(CppPlatform.Win64, WindowsCompiler.VisualStudio2017);
			}
			else if (BuildType == FBBuildType.XBOne)
			{
				// If you have XboxOne source access, uncommenting the line below will be better for selecting the appropriate version of the compiler.
				// Translate the XboxOne compiler to the right Windows compiler to set the VC environment vars correctly...
				//WindowsCompiler windowsCompiler = XboxOnePlatform.GetDefaultCompiler() == XboxOneCompiler.VisualStudio2015 ? WindowsCompiler.VisualStudio2015 : WindowsCompiler.VisualStudio2017;
				//VCEnv = VCEnvironment.SetEnvironment(CppPlatform.Win64, windowsCompiler);
			}

			// Copy environment into a case-insensitive dictionary for easier key lookups
			Dictionary<string, string> envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
			{
				envVars[(string) entry.Key] = (string) entry.Value;
			}

			if (envVars.ContainsKey("CommonProgramFiles"))
			{
				stream.WriteText("#import CommonProgramFiles\n");
			}

			if (envVars.ContainsKey("DXSDK_DIR"))
			{
				stream.WriteText("#import DXSDK_DIR\n");
			}

			if (envVars.ContainsKey("DurangoXDK"))
			{
				stream.WriteText("#import DurangoXDK\n");
			}

			if (VCEnv != null)
			{
				string platformVersionNumber = "VSVersionUnknown";

				switch (VCEnv.Compiler)
				{
					case WindowsCompiler.VisualStudio2015:
						platformVersionNumber = "140";
						break;

					case WindowsCompiler.VisualStudio2017:
						// For now we are working with the 140 version, might need to change to 141 or 150 depending on the version of the Toolchain you chose
						// to install
						platformVersionNumber = "140";
						break;

					default:
						string exceptionString = "Error: Unsupported Visual Studio Version.";
						Console.WriteLine(exceptionString);
						throw new BuildException(exceptionString);
				}

				stream.WriteText(".WindowsSDKBasePath = '{0}'\n", VCEnv.WindowsSDKDir);

				stream.WriteText("Compiler('UE4ResourceCompiler') \n{\n");
				stream.WriteText("\t.Executable = '$WindowsSDKBasePath$/bin/x64/rc.exe'\n");
				stream.WriteText("\t.CompilerFamily  = 'custom'\n");
				stream.WriteText("}\n\n");


				stream.WriteText("Compiler('UE4Compiler') \n{\n");

				stream.WriteText("\t.Root = '{0}'\n", VCEnv.VCToolPath64);
				stream.WriteText("\t.Executable = '$Root$/cl.exe'\n");
				stream.WriteText("\t.ExtraFiles =\n\t{\n");
				stream.WriteText("\t\t'$Root$/c1.dll'\n");
				stream.WriteText("\t\t'$Root$/c1xx.dll'\n");
				stream.WriteText("\t\t'$Root$/c2.dll'\n");

				if (File.Exists(VCEnv.VCToolPath64 + "1033/clui.dll")) //Check English first...
				{
					stream.WriteText("\t\t'$Root$/1033/clui.dll'\n");
				}
				else
				{
					var numericDirectories = Directory.GetDirectories(VCEnv.VCToolPath64.ToString())
						.Where(d => Path.GetFileName(d).All(char.IsDigit));
					var cluiDirectories = numericDirectories.Where(d => Directory.GetFiles(d, "clui.dll").Any());
					if (cluiDirectories.Any())
					{
						stream.WriteText("\t\t'$Root$/{0}/clui.dll'\n", Path.GetFileName(cluiDirectories.First()));
					}
				}

				stream.WriteText("\t\t'$Root$/mspdbsrv.exe'\n");
				stream.WriteText("\t\t'$Root$/mspdbcore.dll'\n");

				stream.WriteText("\t\t'$Root$/mspft{0}.dll'\n", platformVersionNumber);
				stream.WriteText("\t\t'$Root$/msobj{0}.dll'\n", platformVersionNumber);
				stream.WriteText("\t\t'$Root$/mspdb{0}.dll'\n", platformVersionNumber);

				if (VCEnv.Compiler == WindowsCompiler.VisualStudio2015)
				{
					stream.WriteText("\t\t'{0}/redist/x64/Microsoft.VC{1}.CRT/msvcp{2}.dll'\n", VCEnv.VCInstallDir,
						platformVersionNumber, platformVersionNumber);
					stream.WriteText("\t\t'{0}/redist/x64/Microsoft.VC{1}.CRT/vccorlib{2}.dll'\n", VCEnv.VCInstallDir,
						platformVersionNumber, platformVersionNumber);
				}
				else
				{
					// Scan in the Visual studio directory for the installed version. There's only one present at a time, so far.
					var rootPath = Path.Combine(VCEnv.VCInstallDir.FullName, "Redist", "MSVC");
					var version = Directory.GetDirectories(rootPath, "*.*").FirstOrDefault();
					if (version == null)
					{
						throw new BuildException(
							string.Format("Could not find a version of the VC runtime at {0}", rootPath));
					}

					version = Path.GetFileName(version);

					// VS 2017 is really confusing in terms of version numbers and paths so these values might need to be modified depending on what version of the tool chain you
					// chose to install.
					stream.WriteText("\t\t'{0}/Redist/MSVC/{2}/x64/Microsoft.VC141.CRT/msvcp{1}.dll'\n",
						VCEnv.VCInstallDir, platformVersionNumber, version);
					stream.WriteText("\t\t'{0}/Redist/MSVC/{2}/x64/Microsoft.VC141.CRT/vccorlib{1}.dll'\n",
						VCEnv.VCInstallDir, platformVersionNumber, version);
				}

				stream.WriteText("\t}\n"); //End extra files

				stream.WriteText("}\n\n"); //End compiler
			}

			if (envVars.ContainsKey("SCE_ORBIS_SDK_DIR"))
			{
				stream.WriteText(".SCE_ORBIS_SDK_DIR = '{0}'\n", envVars["SCE_ORBIS_SDK_DIR"]);
				stream.WriteText(".PS4BasePath = '{0}/host_tools/bin'\n\n", envVars["SCE_ORBIS_SDK_DIR"]);
				stream.WriteText("Compiler('UE4PS4Compiler') \n{\n");
				stream.WriteText("\t.Executable = '$PS4BasePath$/orbis-clang.exe'\n");
				stream.WriteText("}\n\n");
			}

			if (envVars.ContainsKey("LINUX_MULTIARCH_ROOT"))
			{
				stream.WriteText(".LINUX_MULTIARCH_ROOT = '{0}'\n", envVars["LINUX_MULTIARCH_ROOT"]);
				stream.WriteText(".LinuxCrossCompilerBasePath = '{0}/x86_64-unknown-linux-gnu/bin'\n\n",
					envVars["LINUX_MULTIARCH_ROOT"]);
				stream.WriteText("Compiler('UE4LinuxCrossCompiler') \n{\n");
				stream.WriteText("\t.Executable = '$LinuxCrossCompilerBasePath$/clang++.exe'\n");
				stream.WriteText("}\n\n");
			}

			stream.WriteText("Settings \n{\n");

			// Optional cachePath user setting
			if (bEnableCaching && CachePath != "")
			{
				stream.WriteText("\t.CachePath = '{0}'\n", CachePath);
			}

			//Start Environment
			stream.WriteText("\t.Environment = \n\t{\n");
			if (VCEnv != null)
			{
				stream.WriteText("\t\t\"PATH={0}\\Common7\\IDE\\;{1}\",\n", VCEnv.VCInstallDir, VCEnv.VCToolPath64);
			}

			if (envVars.ContainsKey("TMP"))
				stream.WriteText("\t\t\"TMP={0}\",\n", envVars["TMP"]);
			if (envVars.ContainsKey("SystemRoot"))
				stream.WriteText("\t\t\"SystemRoot={0}\",\n", envVars["SystemRoot"]);
			if (envVars.ContainsKey("INCLUDE"))
				stream.WriteText("\t\t\"INCLUDE={0}\",\n", envVars["INCLUDE"]);
			if (envVars.ContainsKey("LIB"))
				stream.WriteText("\t\t\"LIB={0}\",\n", envVars["LIB"]);

			stream.WriteText("\t}\n"); //End environment
			stream.WriteText("}\n\n"); //End Settings
		}

		private string GetAllCommandLineArguments(Action Action)
		{
			var MergedCommandArguments = Action.CommandArguments;

			if (TryGetResponseFile(Action.CommandArguments, out var ResponseFileName, out var ResponseFileContent))
			{
				MergedCommandArguments = MergedCommandArguments + " " + ResponseFileContent;
				MergedCommandArguments = MergedCommandArguments
					.Replace($"@\"{ResponseFileName}\"", string.Empty)
					.Replace("\r", string.Empty).Replace("\n", " ");
			}

			MergedCommandArguments = SubstituteEnvironmentVariables(MergedCommandArguments);
			MergedCommandArguments = MergedCommandArguments.Trim();

			return MergedCommandArguments;
		}

		private void AddCompileAction(Stream stream, List<ActionNode> Actions, int ActionIndex)
		{
			var ActionNode = Actions[ActionIndex];
			var Action = ActionNode.SourceAction;

			var Tokens = Tokenize(ActionNode.MergedCommandLineArguments);
			Tokens = GetInputFiles(KnownCompilerOptions, Tokens, out var InputFiles);
			Tokens = GetCompilerOutputObjectFileName(Tokens, out var OutputObjectFileName);

			if (!InputFiles.Any())
			{
				throw new BuildException("We have no InputFile.");
			}

			if (string.IsNullOrEmpty(OutputObjectFileName)) //No /Fo or /fo, we're probably in trouble.
			{
				throw new BuildException("We have no OutputObjectFileName.");
			}

			var IntermediatePath = Path.GetDirectoryName(OutputObjectFileName);
			if (string.IsNullOrEmpty(IntermediatePath))
			{
				throw new BuildException("We have no IntermediatePath.");
			}

			string CompilerName = GetCompilerName(Action);

			stream.WriteText("; {0}\n", ActionNode.MergedCommandLineArguments);
			stream.WriteText("ObjectList('Action_{0}') ; {1}\n{{ \n", ActionIndex, Action.StatusDescription);
			stream.WriteText("\t.Compiler = '{0}' \n", CompilerName);

			if (InputFiles.Count > 1)
			{
				stream.WriteText("\t.CompilerInputFiles = {{\n\t\t{0}\n\t}}\n",
					string.Join("\n\t\t,", InputFiles.Select(f => $"'{f}'").ToArray()));
			}
			else
			{
				stream.WriteText($"\t.CompilerInputFiles = '{InputFiles.First()}'\n");
			}

			stream.WriteText("\t.CompilerOutputPath = \"{0}\"\n", IntermediatePath);

			if (!Action.bCanExecuteRemotely || !Action.bCanExecuteRemotelyWithSNDBS ||
			    ForceLocalCompileModules
				    .Intersect(InputFiles.Select(Path.GetFileNameWithoutExtension)).Any())
			{
				stream.WriteText("\t.AllowDistribution = false\n");
			}

			var AllOtherArguments = string.Join(" ", Tokens);
			string CompilerOutputExtension;

			if (ActionNode.MergedCommandLineArguments.Contains("/Yc")) //Create PCH
			{
				WriteCreatePCHOptions(stream, Tokens, InputFiles, OutputObjectFileName);
				CompilerOutputExtension = ".obj";
			}
			else if (ActionNode.MergedCommandLineArguments.Contains("/Yu")) //Use PCH
			{
				WriteUsePCHOptions(stream, Tokens);
				CompilerOutputExtension = ".cpp.obj";
			}
			else if (CompilerName == "UE4ResourceCompiler")
			{
				stream.WriteText("\t.CompilerOptions = '/fo\"%2\" {0} '\n", AllOtherArguments);
				CompilerOutputExtension = Path.GetExtension(InputFiles[0]) + ".res";
			}
			else
			{
				stream.WriteText("\t.CompilerOptions = '{0} /Fo\"%2\" '\n", AllOtherArguments);
				CompilerOutputExtension = IsMSVC() ? ".cpp.obj" : ".cpp.o";
			}

			stream.WriteText("\t.CompilerOutputExtension = '{0}' \n", CompilerOutputExtension);

			stream.WriteText("}\n\n");

			if (ActionNode.MergedCommandLineArguments.Contains("/Yc"))
			{
				stream.WriteText("Copy('Action_{0}_Fix_PCH_Name')\n{{\n", ActionIndex);
				stream.WriteText("\t.Source = '{0}' \n", OutputObjectFileName);
				stream.WriteText("\t.Dest = '{0}' \n", OutputObjectFileName.Replace(".h.obj", ".h.pch.obj"));
				stream.WriteText("\t.PreBuildDependencies = 'Action_{0}' \n", ActionIndex);
				stream.WriteText("}\n\n");
			}
		}

		private static List<string> GetCompilerOutputObjectFileName(List<string> InTokens, out string FileName)
		{
			FileName = null;

			var Tokens = new List<string>();
			for (var i = 0; i < InTokens.Count; i++)
			{
				if (InTokens[i].StartsWith("/Fo") || InTokens[i].StartsWith("/fo") || InTokens[i].StartsWith("-o"))
				{
					var Index = InTokens[i].IndexOf('"');
					if (Index < 0)
					{
						// /Fo "path" - the value is in the next token.
						FileName = InTokens[++i].Trim('"');
					}
					else
					{
						// /Fo"path" - the value is combined with the flag.
						var ResultToken = InTokens[i].Substring(Index);
						FileName = ResultToken.Trim('"');
						;
					}
				}
				else
				{
					Tokens.Add(InTokens[i]);
				}
			}

			return Tokens;
		}

		private static List<string> GetLinkerOutputObjectFileName(List<string> InTokens, out string FileName)
		{
			FileName = null;

			var Tokens = new List<string>();
			for (var i = 0; i < InTokens.Count; i++)
			{
				switch (InTokens[i])
				{
					case "-o":
						FileName = InTokens[++i];
						break;
					default:
						if (InTokens[i].StartsWith("/OUT:", StringComparison.InvariantCultureIgnoreCase))
						{
							var FirstIndex = InTokens[i].IndexOf(':');
							FileName = InTokens[i].Substring(FirstIndex + 1).Trim('"');
						}
						else
						{
							Tokens.Add(InTokens[i]);
						}

						break;
				}
			}

			return Tokens;
		}

		private void AddLinkAction(FileStream stream, List<ActionNode> Actions, int ActionIndex)
		{
			var ActionNode = Actions[ActionIndex];
			var Action = ActionNode.SourceAction;

			var Tokens = Tokenize(ActionNode.MergedCommandLineArguments);
			Tokens = GetInputFiles(KnownLinkerOptions, Tokens, out var InputFiles);
			Tokens = GetLinkerOutputObjectFileName(Tokens, out var OutputFile);

			var isClangCrossCompiler =
				Action.CommandPath.Contains("cmd.exe") && ActionNode.MergedCommandLineArguments.Contains("gnu-ar.exe");
			if (isClangCrossCompiler)
			{
				// This is executed as a sub-shell (cmd.exe or /bin/sh), so the arguments need cleaning.
				ActionNode.MergedCommandLineArguments =
					ActionNode.MergedCommandLineArguments.Replace("/c", string.Empty).Trim(' ');

				// Remove first and last quote. Don't use Trim(), since there may be more leading and trailing quotes.
				ActionNode.MergedCommandLineArguments =
					ActionNode.MergedCommandLineArguments.Substring(1,
						ActionNode.MergedCommandLineArguments.Length - 2);

				// Strip the linker executable name.
				var Index = ActionNode.MergedCommandLineArguments.IndexOf("\"", 1, StringComparison.Ordinal);
				Action.CommandPath = ActionNode.MergedCommandLineArguments.Substring(1, Index - 1);
				ActionNode.MergedCommandLineArguments = ActionNode.MergedCommandLineArguments.Substring(Index + 2);
			}

			if (IsXBOnePDBUtil(Action) || isClangCrossCompiler)
			{
				OutputFile = InputFiles.First();
			}
			else //PS4
			{
				if (string.IsNullOrEmpty(OutputFile))
				{
					OutputFile = InputFiles.First();
				}
			}

			if (string.IsNullOrEmpty(OutputFile))
			{
				throw new BuildException("Failed to find output file.");
			}

			var AllOtherOptions = string.Join(" ", Tokens);
			var Dependencies = ActionNode.DependencyIndices.ConvertAll(i =>
			{
				if (Actions[i].MergedCommandLineArguments.Contains("/Yc"))
				{
					return $"\t\t'Action_{i}_Fix_PCH_Name', ; {Actions[i].SourceAction.StatusDescription}";
				}

				return $"\t\t'Action_{i}', ; {Actions[i].SourceAction.StatusDescription}";
			});

			//Dependencies = Dependencies.Union(InputFiles.Select(f => $"\t\t'{f},'")).ToList();

			var DependencyNames = string.Join("\n", Dependencies);

			stream.WriteText("; {0}\n", ActionNode.MergedCommandLineArguments);
			if (IsXBOnePDBUtil(Action))
			{
				stream.WriteText("Exec('Action_{0}')\n{{\n", ActionIndex);
				stream.WriteText("\t.ExecExecutable = '{0}'\n", Action.CommandPath);
				stream.WriteText("\t.ExecArguments = '{0}'\n", Action.CommandArguments);
				stream.WriteText("\t.ExecInput = {{ {0} }} \n", InputFiles.First());
				stream.WriteText("\t.ExecOutput = '{0}' \n", OutputFile);
				stream.WriteText("}\n\n");
			}
			else if (Action.CommandPath.Contains("lib.exe") || Action.CommandPath.Contains("orbis-snarl") ||
			         isClangCrossCompiler)
			{
				stream.WriteText("Library('Action_{0}') ; {1}\n{{ \n", ActionIndex, Action.StatusDescription);
				stream.WriteText("\t.Compiler = '{0}'\n", GetCompilerName(Action));
				if (IsMSVC())
				{
					stream.WriteText("\t.CompilerOptions = '{0} /Fo\"%2\" /c' \n", AllOtherOptions);
				}
				else
				{
					stream.WriteText("\t.CompilerOptions = '{0} -o \"%2\" -c' \n", AllOtherOptions);
				}

				stream.WriteText("\t.CompilerOutputPath = '{0}' \n", Path.GetDirectoryName(OutputFile));

				stream.WriteText("\t.Librarian = '{0}' \n", Action.CommandPath);

				if (IsMSVC())
				{
					stream.WriteText("\t.LibrarianOptions = '/OUT:\"%2\" {0} '\n", AllOtherOptions);
				}
				else
				{
					stream.WriteText("\t.LibrarianOptions = '-o \"%2\" {0} '\n", AllOtherOptions);
				}

				if (ActionNode.DependencyIndices.Count > 0)
				{
					stream.WriteText("\t.LibrarianAdditionalInputs = {{\n{0}\n\t}} \n", DependencyNames);
				}

				stream.WriteText("\t.LibrarianOutput = '{0}' \n", OutputFile);
				stream.WriteText("}\n\n");
			}
			else if (Action.CommandPath.Contains("link.exe") || Action.CommandPath.Contains("orbis-clang") ||
			         Action.CommandPath.Contains("clang++"))
			{
				stream.WriteText("Executable('Action_{0}') ; {1}\n{{ \n", ActionIndex, Action.StatusDescription);
				stream.WriteText("\t.Linker = '{0}' \n", Action.CommandPath);

				if (ActionNode.DependencyIndices.Count > 0)
				{
					stream.WriteText("\t.Libraries = {{\n{0}\n\t}} \n", DependencyNames);
				}

				if (IsMSVC())
				{
					stream.WriteText("\t.LinkerOptions = ' {0} /OUT:\"%2\" '\n", AllOtherOptions);
				}
				else
				{
					stream.WriteText("\t.LinkerOptions = ' {0} -o \"%2\" '\n", AllOtherOptions);
				}

				stream.WriteText("\t.LinkerOutput = '{0}' \n", OutputFile);
				stream.WriteText("}\n\n");
			}
			else
			{
				throw new BuildException("Error: Unknown toolchain " + Action.CommandPath);
			}
		}

		private bool CreateBffFile(List<Action> InActions, string BffFilePath)
		{
			List<Action> Actions = SortActions(InActions);

			try
			{
				using (var stream = new FileStream(BffFilePath, FileMode.Create, FileAccess.Write))
				{
					WriteEnvironmentSetup(stream); //Compiler, environment variables and base paths

					var ActionNodes = new List<ActionNode>();

					for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
					{
						var Action = Actions[ActionIndex];

						var Node = new ActionNode
						{
							MergedCommandLineArguments = GetAllCommandLineArguments(Action),
							SourceAction = Action,
							SourceActionIndex = ActionIndex,
							DependencyIndices = new List<int>()
						};

						// Resolve dependencies
						foreach (var Item in Action.PrerequisiteItems.Where(Item => Item.ProducingAction != null))
						{
							int ProducingActionIndex = Actions.IndexOf(Item.ProducingAction);
							if (ProducingActionIndex >= 0)
							{
								Node.DependencyIndices.Add(ProducingActionIndex);
							}
						}

						ActionNodes.Add(Node);
					}


					for (int ActionIndex = 0; ActionIndex < ActionNodes.Count; ActionIndex++)
					{
						var ActionNode = ActionNodes[ActionIndex];

						try
						{
							switch (ActionNode.SourceAction.ActionType)
							{
								case ActionType.Compile:
									AddCompileAction(stream, ActionNodes, ActionIndex);
									break;
								case ActionType.Link:
									AddLinkAction(stream, ActionNodes, ActionIndex);
									break;
								default:
									Console.WriteLine("Fastbuild is ignoring an unsupported action: " +
									                  ActionNode.SourceAction.ActionType);
									stream.WriteText("; Action_{0} - {1}", ActionIndex,
										ActionNode.SourceAction.CommandArguments);
									break;
							}
						}
						catch (Exception e)
						{
							throw new BuildException(
								$"{e.Message}\n{ActionNode.SourceAction.ActionType} - {ActionNode.MergedCommandLineArguments}",
								e);
						}
					}

					stream.WriteText("Alias( 'all' ) \n{\n");
					stream.WriteText("\t.Targets = { \n");
					for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
					{
						stream.WriteText("\t\t'Action_{0}'{1}", ActionIndex,
							ActionIndex < (Actions.Count - 1) ? ",\n" : "\n\t}\n");
					}

					stream.WriteText("}\n");
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception while creating bff file: " + e);
				return false;
			}

			return true;
		}

		private bool ExecuteBffFile(string BffFilePath)
		{
			string cacheArgument = "";

			if (bEnableCaching)
			{
				switch (CacheMode)
				{
					case eCacheMode.ReadOnly:
						cacheArgument = "-cacheread";
						break;
					case eCacheMode.WriteOnly:
						cacheArgument = "-cachewrite";
						break;
					case eCacheMode.ReadWrite:
						cacheArgument = "-cache";
						break;
				}
			}

			string distArgument = bEnableDistribution ? "-dist" : "";

			//Interesting flags for FASTBuild: -nostoponerror, -verbose, -monitor (if FASTBuild Monitor Visual Studio Extension is installed!)
			// Yassine: The -clean is to bypass the FastBuild internal dependencies checks (cached in the fdb) as it could create some conflicts with UBT.
			//			Basically we want FB to stupidly compile what UBT tells it to.
			string FBCommandLine = string.Format("-monitor -summary {0} {1} -ide -clean -showcmds -config {2}",
				distArgument, cacheArgument, BffFilePath);

			ProcessStartInfo FBStartInfo =
				new ProcessStartInfo(string.IsNullOrEmpty(FBuildExePathOverride) ? "fbuild" : FBuildExePathOverride,
					FBCommandLine);

			FBStartInfo.UseShellExecute = false;
			FBStartInfo.WorkingDirectory =
				Path.Combine(UnrealBuildTool.EngineDirectory.MakeRelativeTo(DirectoryReference.GetCurrentDirectory()),
					"Source");

			try
			{
				using (Process FBProcess = new Process())
				{
					FBProcess.StartInfo = FBStartInfo;

					FBStartInfo.RedirectStandardError = true;
					FBStartInfo.RedirectStandardOutput = true;
					FBProcess.EnableRaisingEvents = true;

					DataReceivedEventHandler OutputEventHandler = (Sender, Args) =>
					{
						if (Args.Data != null)
						{
							Console.WriteLine(Args.Data);
						}
					};

					FBProcess.OutputDataReceived += OutputEventHandler;
					FBProcess.ErrorDataReceived += OutputEventHandler;

					FBProcess.Start();

					FBProcess.BeginOutputReadLine();
					FBProcess.BeginErrorReadLine();

					FBProcess.WaitForExit();

					return FBProcess.ExitCode == 0;
				}
			}
			catch (Exception e)
			{
				Console.WriteLine("Exception launching fbuild process. Is it in your path?" + e.ToString());
				return false;
			}
		}

		private static string GetNextToken(string str, ref int startIndex)
		{
			// Skip leading whitespace
			while (startIndex < str.Length && char.IsWhiteSpace(str[startIndex]))
			{
				startIndex++;
			}

			if (startIndex >= str.Length)
			{
				startIndex = -1;
				return null;
			}

			var tokenBegin = startIndex;


			while (startIndex < str.Length)
			{
				var c = str[startIndex];
				if (char.IsWhiteSpace(c))
				{
					// We've reached the end of a token run.
					break;
				}

				if (str[startIndex] == '"')
				{
					// Move past the ".
					startIndex++;

					while (startIndex < str.Length)
					{
						switch (str[startIndex])
						{
							// Terminating quote for this token.
							case '"':
								// Move to the next character for the next call.
								startIndex++;
								return str.Substring(tokenBegin, startIndex - tokenBegin);
						}

						startIndex++;
					}

					Console.WriteLine("ERROR: Unterminated string at {0}", str.Substring(tokenBegin));
					startIndex = -1;
					return null;
				}

				startIndex++;
			}

			return str.Substring(tokenBegin, startIndex - tokenBegin);
		}

		private static string MatchToken(string str, Func<string, bool> predicate)
		{
			var startIndex = 0;
			while (startIndex >= 0)
			{
				var token = GetNextToken(str, ref startIndex);
				if (token == null)
				{
					return null;
				}

				if (predicate(token))
				{
					return token;
				}
			}

			return null;
		}

		private static bool TryGetResponseFile(string str, out string responseFileName, out string responseFileContent)
		{
			responseFileContent = null;

			responseFileName = MatchToken(str, t => t.StartsWith("@"));
			if (responseFileName == null)
			{
				return false;
			}

			// Skip leading @, trim quotes.

			responseFileName = responseFileName.Substring(1).Trim('"');
			responseFileContent = File.ReadAllText(responseFileName);
			return true;
		}

		private static string GetStringValue(List<string> InTokens, ref int Index, string Flag)
		{
			var Token = InTokens[Index];
			if (Token == Flag)
			{
				// /flag "value"
				return InTokens[++Index].Trim('"');
			}

			// /flag"value"
			return InTokens[Index].Substring(Flag.Length).Trim('"');
		}

		private static void WriteCreatePCHOptions(Stream Stream, List<string> InTokens, List<string> InputFiles,
			string OutputObjectFileName)
		{
			string PCHIncludeHeader = null;
			string PCHOutputFile = null;

			var Tokens = new List<string>();

			for (var i = 0; i < InTokens.Count; i++)
			{
				var Argument = InTokens[i];
				if (Argument.StartsWith("/Yc"))
				{
					PCHIncludeHeader = GetStringValue(InTokens, ref i, "/Yc");
				}
				else if (Argument.StartsWith("/Fp"))
				{
					PCHOutputFile = GetStringValue(InTokens, ref i, "/Fp");
				}
				else
				{
					Tokens.Add(Argument);
				}
			}

			if (string.IsNullOrEmpty(PCHIncludeHeader))
			{
				throw new BuildException("Missing value for /Yc");
			}

			if (string.IsNullOrEmpty(PCHOutputFile))
			{
				throw new BuildException("Missing value for /Fp");
			}

			if (InputFiles.Count > 1)
			{
				throw new BuildException("Only one input file is allowed when creating a PCH.");
			}

			var AllOtherArguments = string.Join(" ", Tokens);
			Stream.WriteText("\t.CompilerOptions = ' {0} /Fo\"%2\" /Fp\"{1}\" /Yu\"{2}\" '\n", AllOtherArguments,
				PCHOutputFile,
				PCHIncludeHeader);
			Stream.WriteText("\t.PCHOptions = ' {0} /Fp\"%2\" /Yc\"{1}\" /Fo\"{2}\" '\n", AllOtherArguments,
				PCHIncludeHeader,
				OutputObjectFileName);
			Stream.WriteText("\t.PCHInputFile = \"{0}\" \n", InputFiles.First());
			Stream.WriteText("\t.PCHOutputFile = \"{0}\" \n", PCHOutputFile);
		}

		private static void WriteUsePCHOptions(Stream Stream, List<string> InTokens)
		{
			string PCHIncludeHeader = null;
			string PCHOutputFile = null;

			var Tokens = new List<string>();

			for (var i = 0; i < InTokens.Count; i++)
			{
				var Argument = InTokens[i];
				if (Argument.StartsWith("/Yu"))
				{
					PCHIncludeHeader = GetStringValue(InTokens, ref i, "/Yu");
				}
				else if (Argument.StartsWith("/Fp"))
				{
					PCHOutputFile = GetStringValue(InTokens, ref i, "/Fp");
				}
				else
				{
					Tokens.Add(Argument);
				}
			}

			if (string.IsNullOrEmpty(PCHIncludeHeader))
			{
				throw new BuildException("Missing value for /Yc");
			}

			if (string.IsNullOrEmpty(PCHOutputFile))
			{
				throw new BuildException("Missing value for /Fp");
			}

			var AllOtherArguments = string.Join(" ", Tokens);
			Stream.WriteText("\t.CompilerOptions = ' {0} /Fo\"%2\" /Fp\"{1}\" /Yu\"{2}\" '\n", AllOtherArguments,
				PCHOutputFile, PCHIncludeHeader);
		}

		private static List<string> Tokenize(string Args)
		{
			var Tokens = new List<string>();

			var ParseIndex = 0;
			string Token;
			while ((Token = GetNextToken(Args, ref ParseIndex)) != null)
			{
				Tokens.Add(Token);
			}

			return Tokens;
		}

		private static List<string> GetInputFiles(string[] KnownOptions, List<string> InTokens,
			out List<string> InputFiles)
		{
			InputFiles = new List<string>();
			var FinalTokens = new List<string>();
			var AddedFileMarker = false;

			for (var i = 0; i < InTokens.Count; i++)
			{
				if (KnownOptions.Contains(InTokens[i]))
				{
					// /flag <value>
					FinalTokens.Add(InTokens[i]);
					FinalTokens.Add(InTokens[++i]);
				}
				else if (InTokens[i].StartsWith("/") || InTokens[i].StartsWith("-"))
				{
					// /flagValue, e.g. /Fo"Path"
					FinalTokens.Add(InTokens[i]);
				}
				else
				{
					// "Path/To/File"
					InputFiles.Add(InTokens[i].Trim('"'));
					if (!AddedFileMarker)
					{
						AddedFileMarker = true;
						FinalTokens.Add("\"%1\"");
					}
				}
			}

			return FinalTokens;
		}

		private class ActionNode
		{
			public Action SourceAction { get; set; }
			public int SourceActionIndex { get; set; }
			public string MergedCommandLineArguments { get; set; }
			public List<int> DependencyIndices { get; set; }
		}
	}

	static class StreamExtensions
	{
		public static void WriteText(this Stream stream, string StringToWrite)
		{
			var Bytes = new System.Text.UTF8Encoding(true).GetBytes(StringToWrite);
			stream.Write(Bytes, 0, Bytes.Length);
		}

		public static void WriteText(this Stream stream, string StringToWrite, params object[] args)
		{
			var Bytes = new System.Text.UTF8Encoding(true).GetBytes(string.Format(StringToWrite, args));
			stream.Write(Bytes, 0, Bytes.Length);
		}
	}
}