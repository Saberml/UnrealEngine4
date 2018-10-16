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
using System.Text;
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
			{"/Fo", "/fo", "/Yc", "/Yu", "/Fp", "-o", "/I", "-I", "/D", "-D", "/l", "/FI", "-x", "-target", "-include"};

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

		private static bool IsXBOnePDBUtil(Action action)
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


		private static string SubstituteEnvironmentVariables(string commandLineString)
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

		private static void GetAllCommandLineArguments(Action Action, out List<string> OutTokens,
			out List<int> ResponseFileTokens, out string ResponseFileToken, out string ResponseFileName)
		{
			OutTokens = new List<string>();
			ResponseFileTokens = new List<int>();
			ResponseFileName = null;
			ResponseFileToken = null;

			var InTokens = Tokenize(Action.CommandArguments);

			for (var index = 0; index < InTokens.Count; index++)
			{
				var Token = InTokens[index];
				if (Token.StartsWith("@") || Token.StartsWith("-Wl,@"))
				{
					if (!string.IsNullOrEmpty(ResponseFileName))
					{
						throw new Exception($"Found multiple response files. {Token}");
					}

					ResponseFileName = Token.Replace("-Wl,", "").Trim('@').Trim('"');
					ResponseFileToken = Token;

					var lines = Tokenize(File.ReadAllText(ResponseFileName));

					var startIndex = OutTokens.Count;
					OutTokens.AddRange(lines.Select(SubstituteEnvironmentVariables));
					ResponseFileTokens.AddRange(Enumerable.Range(startIndex, lines.Count));
				}
				else
				{
					OutTokens.Add(SubstituteEnvironmentVariables(Token));
				}
			}
		}

		private void AddCompileAction(Stream stream, List<ActionNode> Actions, int ActionIndex)
		{
			var ActionNode = Actions[ActionIndex];
			var Action = ActionNode.SourceAction;

			var IntermediatePath = Path.GetDirectoryName(ActionNode.OutputFileName);
			if (string.IsNullOrEmpty(IntermediatePath))
			{
				throw new BuildException("We have no IntermediatePath.");
			}

			string CompilerName = GetCompilerName(Action);

			stream.WriteText("ObjectList('Action_{0}') ; {1}\n{{ \n", ActionIndex, Action.StatusDescription);
			stream.WriteText("\t.Compiler = '{0}' \n", CompilerName);

			stream.WriteText("\t.CompilerInputFiles = {{\n\t\t{0}\n\t}}\n",
				string.Join("\n\t\t", ActionNode.InputFiles.Select(f => $"'{f}',").ToArray()));

			stream.WriteText("\t.CompilerOutputPath = \"{0}\"\n", IntermediatePath);

			if (!Action.bCanExecuteRemotely || !Action.bCanExecuteRemotelyWithSNDBS ||
			    ForceLocalCompileModules
				    .Intersect(ActionNode.InputFiles.Select(Path.GetFileNameWithoutExtension)).Any())
			{
				stream.WriteText("\t.AllowDistribution = false\n");
			}

			string CompilerOutputExtension;

			if (ActionNode.IntepolatedOptions.Contains("/Yc")) // Create PCH.
			{
				WriteCreatePCHOptions(stream, ActionNode);
				CompilerOutputExtension = ".obj";
			}
			else if (CompilerName == "UE4ResourceCompiler")
			{
				stream.WriteText("\t.CompilerOptions = '{0}'\n", ActionNode.IntepolatedOptions);
				CompilerOutputExtension = Path.GetExtension(ActionNode.InputFiles[0]) + ".res";
			}
			else
			{
				stream.WriteText("\t.CompilerOptions = '{0}'\n", ActionNode.IntepolatedOptions);
				CompilerOutputExtension = IsMSVC() ? ".cpp.obj" : ".cpp.o";
			}

			stream.WriteText("\t.CompilerOutputExtension = '{0}' \n", CompilerOutputExtension);

			stream.WriteText("}\n\n");

			// UBT has a quirk where it sometimes links to the PCH.obj file. FASTBuild's automatic PC naming doesn't match up to what UBT expects.
			// This step copies the PCH.obj into the right place.
			if (ActionNode.IntepolatedOptions.Contains("/Yc"))
			{
				stream.WriteText("Copy('Action_{0}_Fix_PCH_Name')\n{{\n", ActionIndex);
				stream.WriteText("\t.Source = '{0}' \n", ActionNode.OutputFileName);
				stream.WriteText("\t.Dest = '{0}' \n", ActionNode.OutputFileName.Replace(".h.obj", ".h.pch.obj"));
				stream.WriteText("\t.PreBuildDependencies = 'Action_{0}' \n", ActionIndex);
				stream.WriteText("}\n\n");
			}
		}

		private static void GetCompilerOutputObjectFileName(IReadOnlyList<string> InTokens,
			out List<int> OutputFileIndices)
		{
			OutputFileIndices = new List<int>();

			for (var i = 0; i < InTokens.Count; i++)
			{
				var Token = InTokens[i];
				if (Token.StartsWith("/Fo") || Token.StartsWith("/fo") || Token.StartsWith("-o"))
				{
					OutputFileIndices.Add(i);
				}
			}
		}

		private static void GetLinkerOutputObjectFileName(IReadOnlyList<string> InTokens,
			out List<int> OutputFileIndices)
		{
			OutputFileIndices = new List<int>();

			for (var i = 0; i < InTokens.Count; i++)
			{
				var Token = InTokens[i];
				if (Token.StartsWith("-o") || Token.StartsWith("/OUT:", StringComparison.InvariantCultureIgnoreCase))
				{
					OutputFileIndices.Add(i);
				}
			}
		}

		private void AddLinkAction(FileStream stream, List<ActionNode> Actions, int ActionIndex)
		{
			var ActionNode = Actions[ActionIndex];
			var Action = ActionNode.SourceAction;

			// Filter out ".so" and made them pre-build dependencies.
			var Dependencies = ActionNode.DependencyIndices
				.Where(i => Path.GetExtension(Actions[i].SourceAction.ProducedItems.First().AbsolutePath) != ".so")
				.Select(i =>
				{
					if (Actions[i].IntepolatedOptions.Contains("/Yc"))
					{
						return $"\t\t'Action_{i}_Fix_PCH_Name', ; {Actions[i].SourceAction.StatusDescription}";
					}

					return $"\t\t'Action_{i}', ; {Actions[i].SourceAction.StatusDescription}";
				})
				.ToList();

			var PreBuildDependencies = ActionNode.DependencyIndices
				.Where(i => Path.GetExtension(Actions[i].SourceAction.ProducedItems.First().AbsolutePath) == ".so")
				.Select(i => $"\t\t'Action_{i}', ; {Actions[i].SourceAction.StatusDescription}")
				.ToList();

			// Filter out libraries that are produced by other actions in this work set.
			var FilteredInputFiles = ActionNode.InputFiles.Where(Input => !Actions.Any(a =>
				a.SourceAction.ProducedItems.Any(f => Path.GetFullPath(f.AbsolutePath) == Path.GetFullPath(Input))));

			Dependencies = Dependencies.Union(FilteredInputFiles.Select(f => $"\t\t'{f}',")).ToList();

			var DependencyNames = string.Join("\n", Dependencies);


			if (IsXBOnePDBUtil(Action))
			{
				stream.WriteText("Exec('Action_{0}')\n{{\n", ActionIndex);
				stream.WriteText("\t.ExecExecutable = '{0}'\n", Action.CommandPath);
				stream.WriteText("\t.ExecArguments = '{0}'\n", Action.CommandArguments);
				stream.WriteText("\t.ExecInput = {{ {0} }} \n", ActionNode.InputFiles.First());
				stream.WriteText("\t.ExecOutput = '{0}' \n", ActionNode.OutputFileName);
				stream.WriteText("}\n\n");
			}
			else if (Action.CommandPath.Contains("lib.exe") || Action.CommandPath.Contains("orbis-snarl") ||
			         Action.CommandPath.Contains("gnu-ar.exe"))
			{
				stream.WriteText("Library('Action_{0}') ; {1}\n{{ \n", ActionIndex, Action.StatusDescription);
				stream.WriteText("\t.Compiler = '{0}'\n", GetCompilerName(Action));
				stream.WriteText("\t.CompilerOptions = '{0} {1}' \n", ActionNode.IntepolatedOptions,
					IsMSVC() ? "/c" : "-c");
				stream.WriteText("\t.CompilerOutputPath = '{0}' \n", Path.GetDirectoryName(ActionNode.OutputFileName));

				stream.WriteText("\t.Librarian = '{0}' \n", Action.CommandPath);
				stream.WriteText("\t.LibrarianOptions = '{0}'\n", ActionNode.IntepolatedOptions);

				if (Dependencies.Count > 0)
				{
					stream.WriteText("\t.LibrarianAdditionalInputs = {{\n{0}\n\t}} \n", DependencyNames);
				}

				stream.WriteText("\t.LibrarianOutput = '{0}' \n", ActionNode.OutputFileName);
			}
			else
			{
				var IsClang = Action.CommandPath.Contains("clang++");
				if (Action.CommandPath.Contains("link.exe") || Action.CommandPath.Contains("orbis-clang") ||
				    IsClang)
				{
					stream.WriteText("Executable('Action_{0}') ; {1}\n{{ \n", ActionIndex, Action.StatusDescription);
					stream.WriteText("\t.Linker = '{0}' \n", Action.CommandPath);
					if (IsClang)
					{
						// Needed so that response files are enabled in FASTBuild.
						stream.WriteText("\t.LinkerType = 'clang-orbis' \n");
					}


					if (Dependencies.Count > 0)
					{
						stream.WriteText("\t.Libraries = {{\n{0}\n\t}} \n", DependencyNames);
					}

					stream.WriteText("\t.LinkerOptions = '{0}'\n", ActionNode.IntepolatedOptions);
					stream.WriteText("\t.LinkerOutput = '{0}' \n", ActionNode.OutputFileName);
				}
				else
				{
					throw new BuildException("Error: Unknown toolchain " + Action.CommandPath);
				}
			}

			if (PreBuildDependencies.Count > 0)
			{
				stream.WriteText("\t.PreBuildDependencies = {{\n {0} \n\t}}\n",
					string.Join("\n", PreBuildDependencies));
			}

			stream.WriteText("}\n\n");
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

						if (Action.CommandPath.Contains("cmd.exe") && Action.CommandArguments.Contains("gnu-ar.exe"))
						{
							// This is executed as a sub-shell (cmd.exe or /bin/sh), so the arguments need cleaning.
							Action.CommandArguments = Action.CommandArguments.Replace("/c", string.Empty).Trim(' ');

							// Remove first and last quote. Don't use Trim(), since there may be more leading and trailing quotes.
							Action.CommandArguments =
								Action.CommandArguments.Substring(1, Action.CommandArguments.Length - 2);

							// Strip the linker executable name.
							var Index = Action.CommandArguments.IndexOf("\"", 1, StringComparison.Ordinal);
							Action.CommandPath = Action.CommandArguments.Substring(1, Index - 1);
							Action.CommandArguments = Action.CommandArguments.Substring(Index + 2);
						}


						var Node = new ActionNode(Action, ActionIndex);

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
						ActionNode.WriteHeader(stream);

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
								$"Building action {ActionIndex}\n{e.Message}\n{e.StackTrace}\n{ActionNode.SourceAction.ActionType} - {ActionNode.AllArguments}",
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
				Console.WriteLine(e.StackTrace);
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

			Console.WriteLine($"Running {FBStartInfo.Arguments}");

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

		private static string GetNextToken(string str, ref int StartIndex)
		{
			// Skip leading whitespace
			while (StartIndex < str.Length && char.IsWhiteSpace(str[StartIndex]))
			{
				StartIndex++;
			}

			if (StartIndex >= str.Length)
			{
				StartIndex = -1;
				return null;
			}

			var TokenBegin = StartIndex;
			var ParsingString = false;

			while (StartIndex < str.Length)
			{
				var c = str[StartIndex];
				if (!ParsingString && char.IsWhiteSpace(c))
				{
					// We've reached the end of a token run.
					break;
				}

				if (ParsingString)
				{
					if (str[StartIndex] == '\\')
					{
						if (StartIndex + 1 < str.Length)
						{
							var NextC = str[StartIndex + 1];
							if (NextC == '"')
							{
								// Skip escaped quotes inside of a string.
								StartIndex++;
							}
						}
					}
					else if (str[StartIndex] == '"')
					{
						ParsingString = false;
					}
				}
				else if (str[StartIndex] == '\\' && StartIndex + 1 < str.Length && str[StartIndex + 1] == '"')
				{
					// There's an odd construct of passing through a double-escaped string to the pre-processor. Handle this weird edge-case here.
					// /DORIGINAL_FILENAME=\\\"Name\\\"
					StartIndex = str.IndexOf("\\\"", StartIndex + 2, StringComparison.InvariantCulture) + 2;
					continue;
				}
				else if (str[StartIndex] == '"')
				{
					ParsingString = true;
				}

				StartIndex++;
			}

			return str.Substring(TokenBegin, StartIndex - TokenBegin);
		}

		private static string GetStringValue(IReadOnlyList<string> InTokens, int Index)
		{
			return GetStringValue(InTokens, ref Index);
		}

		private static string GetStringValue(IReadOnlyList<string> InTokens, ref int Index)
		{
			var Token = InTokens[Index];
			var QuoteIndex = Token.IndexOf("\"");
			if (QuoteIndex == -1)
			{
				// /flag "value"
				return InTokens[++Index].Trim('"');
			}

			// /flag"value"
			return InTokens[Index].Substring(QuoteIndex).Trim('"');
		}

		private static void WriteCreatePCHOptions(Stream Stream, ActionNode Node)
		{
			var PCHOutputFile = Node.GetStringValue("/Fp");
			var PCHIncludeHeader = Node.GetStringValue("/Yc");

			Stream.WriteText("\t.CompilerOptions = '{0}'\n", Node.IntepolatedOptions.Replace("/Yc", "/Yu"));
			Stream.WriteText("\t.PCHOptions = ' {0} /Fp\"%2\" /Yc\"{1}\" '\n",
				Node.IntepolatedOptions.Replace("\"%2\"", Node.OutputFileName), PCHIncludeHeader);
			Stream.WriteText("\t.PCHInputFile = '{0}' \n", Node.InputFiles.First());
			Stream.WriteText("\t.PCHOutputFile = '{0}' \n", PCHOutputFile);
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

		private static void GetInputFiles(string[] KnownOptions, IReadOnlyList<string> InTokens,
			out List<int> InputFileIndices, Func<string, bool> Predicate = null)
		{
			InputFileIndices = new List<int>();

			for (var i = 0; i < InTokens.Count; i++)
			{
				var Token = InTokens[i];
				if (KnownOptions.Contains(Token))
				{
					// Skip the value.
					i++;
					continue;
				}

				if (Token.StartsWith("/") || Token.StartsWith("-"))
				{
					// /flagValue, e.g. /Fo"Path"
					continue;
				}

				if (Predicate != null && !Predicate(Token.Trim('"')))
				{
					// "Path/To/File" -> passed through a predicate
					continue;
				}

				// "Path/To/File"
				InputFileIndices.Add(i);
			}
		}

		private class ActionNode
		{
			public ActionNode(Action Source, int Index)
			{
				SourceAction = Source;
				SourceActionIndex = Index;
				DependencyIndices = new List<int>();

				GetAllCommandLineArguments(Source, out tokens, out ResponseFileTokenIndices, out ResponseFileToken,
					out ResponseFileName);

				switch (Source.ActionType)
				{
					case ActionType.Compile:
						GetInputFiles(KnownCompilerOptions, tokens, out InputFileIndices);
						GetCompilerOutputObjectFileName(tokens, out OutputFileIndices);

						break;
					case ActionType.Link:
						// Only include intermediate input libaries (ie inputs that won't be found as part of the LIB search paths.)
						GetInputFiles(KnownLinkerOptions, tokens, out InputFileIndices,
							f => Path.IsPathRooted(f) && f.Contains("Intermediate"));
						GetLinkerOutputObjectFileName(tokens, out OutputFileIndices);

						// The first argument will be the output file in these cases.
						if (IsXBOnePDBUtil(Source) || Source.CommandPath.Contains("gnu-ar.exe") || !OutputFileIndices.Any())
						{
							OutputFileIndices.Add(tokens.FindIndex(t => t.Contains("Binaries")));
						}
						break;
				}

				if (!InputFileIndices.Any())
				{
					throw new BuildException("We have no InputFile.");
				}

				if (!OutputFileIndices.Any())
				{
					throw new BuildException("We have no OutputFile.");
				}

				if (string.IsNullOrEmpty(OutputFileName))
				{
					throw new BuildException("Failed to find output file.");
				}
			}

			public Action SourceAction { get; }
			public int SourceActionIndex { get; }
			public List<int> DependencyIndices { get; }

			public List<string> InputFiles
			{
				get { return InputFileIndices.Select(i => tokens[i].Trim('"')).ToList(); }
			}

			public string OutputFileName
			{
				get
				{
					var Token = tokens[OutputFileIndices.First()];
					if (Token.Equals("/OUT:", StringComparison.InvariantCultureIgnoreCase) || Token == "/Fo" ||
					    Token == "/fo" ||
					    Token == "-o")
					{
						Token = tokens[OutputFileIndices.First() + 1];
					}
					else if (Token.StartsWith("/OUT:", StringComparison.InvariantCultureIgnoreCase))
					{
						Token = Token.Substring(5);
					}
					else if (Token.StartsWith("/Fo", StringComparison.InvariantCultureIgnoreCase))
					{
						Token = Token.Substring(3);
					}

					return Token.Trim('"');
				}
			}

			public string AllArguments => allArguments ?? (allArguments = string.Join(" ", tokens));

			public string IntepolatedOptions
			{
				get
				{
					var ResponseTokenPrefix = "";
					if (!string.IsNullOrEmpty(ResponseFileToken))
					{
						var AtIndex = ResponseFileToken.IndexOf("@");
						if (AtIndex > 0)
						{
							ResponseTokenPrefix = ResponseFileToken.Substring(0, AtIndex);
						}
					}

					var AddedInputMarker = false;
					var Builder = new StringBuilder();
					for (var i = 0; i < tokens.Count; i++)
					{
						if (InputFileIndices.Contains(i))
						{
							if (!AddedInputMarker)
							{
								AddedInputMarker = true;
								Builder.Append("\"%1\"");
							}
						}
						else if (OutputFileIndices.Contains(i))
						{
							var Token = tokens[i];

							if (Token.Equals("/OUT:", StringComparison.InvariantCultureIgnoreCase) || Token == "/Fo" ||
							    Token == "/fo" ||
							    Token == "-o")
							{
								// Replace the output filename with the %2 interpolation.
								Builder.AppendFormat("{0} \"%2\"", Token);
								// Skip the real filename
								i++;
							}
							else if (Token.StartsWith("/OUT:", StringComparison.InvariantCultureIgnoreCase))
							{
								Builder.AppendFormat("{0}\"%2\"", Token.Substring(0, 5));
							}
							else if (Token.StartsWith("/Fo", StringComparison.InvariantCultureIgnoreCase))
							{
								Builder.AppendFormat("{0}\"%2\"", Token.Substring(0, 3));
							}
							else
							{
								Builder.Append("\"%2\"");
							}
						}
						else if (!string.IsNullOrEmpty(ResponseTokenPrefix) && ResponseFileTokenIndices.Contains(i))
						{
							Builder.AppendFormat("{0}{1}", ResponseTokenPrefix, tokens[i]);
						}
						else
						{
							Builder.AppendFormat("{0}", tokens[i]);
						}

						Builder.Append(" ");
					}

					return Builder.ToString().Trim();
				}
			}

			private readonly List<string> tokens;

			private readonly string ResponseFileName;

			private readonly List<int> ResponseFileTokenIndices;

			private readonly List<int> InputFileIndices = new List<int>();

			private readonly List<int> OutputFileIndices = new List<int>();

			private string allArguments;

			private readonly string ResponseFileToken;

			public void WriteHeader(Stream stream)
			{
				if (!string.IsNullOrEmpty(ResponseFileName))
				{
					stream.WriteText("; ResponseFile: @'{0}'\n", ResponseFileName);
				}

				stream.WriteText("; Raw arguments: {0}\n", SourceAction.CommandArguments);
				stream.WriteText("; Raw arguments: {0}\n", AllArguments);
			}

			public string GetStringValue(string FlagName)
			{
				var Index = tokens.FindIndex(t => t.StartsWith(FlagName));
				if (Index == -1)
				{
					return null;
				}

				return FASTBuild.GetStringValue(tokens, Index);
			}
		}
	}

	static class StreamExtensions
	{
		public static void WriteText(this Stream stream, string StringToWrite)
		{
			var Bytes = new UTF8Encoding(true).GetBytes(StringToWrite);
			stream.Write(Bytes, 0, Bytes.Length);
		}

		public static void WriteText(this Stream stream, string StringToWrite, params object[] args)
		{
			var Bytes = new UTF8Encoding(true).GetBytes(string.Format(StringToWrite, args));
			stream.Write(Bytes, 0, Bytes.Length);
		}
	}
}