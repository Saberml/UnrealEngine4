// Copyright (c) Improbable Worlds Ltd, All Rights Reserved

// Based on https://github.com/liamkf/Unreal_FASTBuild/
//   Copyright 2018 Yassine Riahi and Liam Flookes. Provided under a MIT License, see license file on github.

// Used to generate a fastbuild .bff file from UnrealBuildTool to allow caching and distributed builds.
// Tested with Windows 10, Visual Studio 2017, Unreal Engine 4.20.3, FastBuild v0.96

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
	class FASTBuildExecutor : ActionExecutor
	{
		/// <summary>
		/// Used to specify a non-standard location for the FBuild.exe, for example if you have not added it to your PATH environment variable.
		/// </summary>
		public static string FBuildExePath =
			Environment.GetEnvironmentVariable("FASTBUILD_EXE_PATH") ?? string.Empty;

		/// <summary>
		/// Controls network build distribution.
		/// </summary>
		public static bool bEnableDistribution = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FASTBUILD_BROKERAGE_PATH"));

		/// <summary>
		/// Location of the shared cache. It can be a local or network path.
		/// </summary>
		public static string CachePath = Environment.GetEnvironmentVariable("FASTBUILD_CACHE_PATH");

		/// <summary>
		/// The command line flag to pass to FASTBuild. One of:
		/// "r" - Read-only access to the cache.
		/// "w" - Write-only access to the cache.
		/// "rw" - Read and write to the cache.
		/// </summary>
		public static string CacheFlag = Environment.GetEnvironmentVariable("FASTBUILD_CACHE_MODE");

		public override string Name
		{
			get { return "FASTBuild"; }
		}

		/// <summary>
		/// Some modules include files which break when pre-processed with the -E (/E) flag, which is used by FASTBuild for both caching and distribution.
		/// Symptoms include Internal Compiler Errors (ICE) or other parsing errors.
		/// These modules are disabled specifically here.
		/// VS 15.8.x has a compiler regression which can be worked around by disabling caching/distribution.
		/// See https://developercommunity.visualstudio.com/content/problem/313306/vs2017-158-internal-compiler-error-msc1cpp-line-15-1.html
		/// </summary>
		private static readonly HashSet<string> ForceNoCachingModules = new HashSet<string>
			{"Module.DatabaseSupport", "Module.VisualStudioSourceCodeAccess"};		

		public static bool IsAvailable()
		{
			if (!string.IsNullOrEmpty(FBuildExePath))
			{
				return File.Exists(FBuildExePath);
			}

			// Get the name of the FASTBuild executable.
			var FBuild = "fbuild";
			if (BuildHostPlatform.Current.Platform == UnrealTargetPlatform.Win64)
			{
				FBuild = "fbuild.exe";
			}

			// Search the PATH.
			var PathVariable = Environment.GetEnvironmentVariable("PATH");
			foreach (var SearchPath in PathVariable.Split(Path.PathSeparator))
			{
				try
				{
					var PotentialPath = Path.Combine(SearchPath, FBuild);
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

		public override bool ExecuteActions(List<Action> Actions, bool bLogDetailedActionStats)
		{
			Console.WriteLine("Converting {0} actions to FASTBuild", Actions.Count);
			if (Actions.Count == 0)
			{
				return true;
			}

			var FASTBuildFilePath = Path.Combine(UnrealBuildTool.EngineDirectory.FullName, "Intermediate",
				"Build", "fbuild.bff");

			CreateBffFile(Actions, FASTBuildFilePath);

			return ExecuteBffFile(FASTBuildFilePath);
		}


		private void WriteEnvironment(List<Action> Actions, Stream stream)
		{
			// Copy environment into a case-insensitive dictionary for easier key lookups
			Dictionary<string, string> envVars =
				new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

			foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
			{
				envVars[(string) entry.Key] = (string) entry.Value;
			}

			if (envVars.ContainsKey("CommonProgramFiles"))
			{
				stream.WriteLine("#import CommonProgramFiles");
			}

			DirectoryReference VCInstallDir = null;

			VCEnvironment VCEnv = null;
			if (Actions.Any(IsWindows))
			{
				VCEnv = VCEnvironment.Create(WindowsCompiler.VisualStudio2017, CppPlatform.Win64, "Latest", null);
			}

			if (VCEnv != null)
			{
				string platformVersionNumber;

				switch (VCEnv.Compiler)
				{
					case WindowsCompiler.VisualStudio2017:
						// For now we are working with the 140 version.
						// You might need to change to 141 or 150 depending on the version of the Toolchain you chose to install.
						platformVersionNumber = "140";
						break;
					default:
						throw new BuildException("Error: Unsupported Visual Studio Version {0}.", VCEnv.Compiler);
				}

				stream.WriteLine("Compiler('UE4ResourceCompiler')\n{");
				stream.WriteLine("\t.Executable = '{0}'", VCEnv.ResourceCompilerPath);
				stream.WriteLine("\t.CompilerFamily  = 'custom'");
				stream.WriteLine("}\n");

				stream.WriteLine("Compiler('UE4Compiler') \n{");

				var ToolchainRoot = Path.GetDirectoryName(VCEnv.CompilerPath.ToString());

				stream.WriteLine("\t.Root = '{0}'", ToolchainRoot);
				stream.WriteLine("\t.Executable = '$Root$/cl.exe'");
				stream.WriteLine("\t.ExtraFiles =\n\t{");
				stream.WriteLine("\t\t'$Root$/c1.dll'");
				stream.WriteLine("\t\t'$Root$/c1xx.dll'");
				stream.WriteLine("\t\t'$Root$/c2.dll'");

				if (File.Exists(Path.Combine(ToolchainRoot, "1033/clui.dll"))) // Check English first...
				{
					stream.WriteLine("\t\t'$Root$/1033/clui.dll'");
				}
				else
				{
					var numericDirectories = Directory.GetDirectories(ToolchainRoot)
						.Where(d => Path.GetFileName(d).All(char.IsDigit));
					var cluiDirectories =
						numericDirectories.Where(d => Directory.GetFiles(d, "clui.dll").Any()).ToList();
					if (cluiDirectories.Any())
					{
						stream.WriteLine("\t\t'$Root$/{0}/clui.dll'", Path.GetFileName(cluiDirectories.First()));
					}
				}

				stream.WriteLine("\t\t'$Root$/mspdbsrv.exe'");
				stream.WriteLine("\t\t'$Root$/mspdbcore.dll'");

				stream.WriteLine("\t\t'$Root$/mspft{0}.dll'", platformVersionNumber);
				stream.WriteLine("\t\t'$Root$/msobj{0}.dll'", platformVersionNumber);
				stream.WriteLine("\t\t'$Root$/mspdb{0}.dll'", platformVersionNumber);

				// Find the directory named "VC", which is the base installation directory.
				VCInstallDir = VCEnv.ToolChainDir;
				while (!VCInstallDir.IsRootDirectory() && VCInstallDir.GetDirectoryName() != "VC")
				{
					VCInstallDir = VCInstallDir.ParentDirectory;
				}

				// Scan in the Visual studio directory for the installed version. There's only one present at a time, so far.
				var rootPath = Path.Combine(VCInstallDir.FullName, "Redist", "MSVC");
				var version = Directory.GetDirectories(rootPath, "*.*").FirstOrDefault();
				if (version == null)
				{
					throw new BuildException(
						string.Format("Could not find a version of the VC runtime at {0}", rootPath));
				}

				version = Path.GetFileName(version);

				// VS 2017 is really confusing in terms of version numbers and paths so these values might need to be modified depending on what version of the tool chain you
				// chose to install.
				stream.WriteLine("\t\t'{0}/Redist/MSVC/{2}/x64/Microsoft.VC141.CRT/msvcp{1}.dll'",
					VCInstallDir, platformVersionNumber, version);
				stream.WriteLine("\t\t'{0}/Redist/MSVC/{2}/x64/Microsoft.VC141.CRT/vccorlib{1}.dll'",
					VCInstallDir, platformVersionNumber, version);
			
				stream.WriteLine("\t}"); //End extra files

				stream.WriteLine("}\n"); //End compiler
			}

			if (envVars.ContainsKey("LINUX_MULTIARCH_ROOT"))
			{
				stream.WriteLine(".LinuxCrossCompilerBasePath = '{0}/x86_64-unknown-linux-gnu/bin'\n",
					envVars["LINUX_MULTIARCH_ROOT"]);
				stream.WriteLine("Compiler('UE4LinuxCrossCompiler') \n{");
				stream.WriteLine("\t.Executable = '$LinuxCrossCompilerBasePath$/clang++.exe'");
				stream.WriteLine("}\n");
			}

			if (envVars.ContainsKey("ComSpec"))
			{
				stream.WriteLine(".ComSpec = '{0}'", envVars["ComSpec"]);
			}

			stream.WriteLine("Settings\n{");

			if (!string.IsNullOrEmpty(CacheFlag) && !string.IsNullOrEmpty(CachePath))
			{
				stream.WriteLine("\t.CachePath = '{0}'", CachePath);
			}

			//Start Environment
			stream.WriteLine("\t.Environment = \n\t{");
			if (VCEnv != null)
			{
				stream.WriteLine("\t\t\"PATH={0}\\Common7\\IDE\\;{1}\",", VCInstallDir, VCEnv.ToolChainDir);
			}

			TryWriteEnvVar(stream, envVars, "TMP");
			TryWriteEnvVar(stream, envVars, "SystemRoot");
			TryWriteEnvVar(stream, envVars, "INCLUDE");
			TryWriteEnvVar(stream, envVars, "LIB");

			stream.WriteLine("\t}"); //End environment
			stream.WriteLine("}\n"); //End Settings
		}

		private static void TryWriteEnvVar(Stream Stream, Dictionary<string, string> EnvVars, string EnvVar)
		{
			if (EnvVars.ContainsKey(EnvVar))
			{
				Stream.WriteLine("\t\t\"{0}={1}\",", EnvVar, EnvVars[EnvVar]);
			}
		}

		private static void AddCompileAction(Stream stream, List<ActionNode> Actions, int ActionIndex)
		{
			var ActionNode = Actions[ActionIndex];
			var Action = ActionNode.SourceAction;

			string CompilerName = GetCompilerName(Action);

			stream.WriteLine("ObjectList('{0}') ; {1}\n{{", CreateActionName(ActionIndex), Action.StatusDescription);
			stream.WriteLine("\t.Compiler = '{0}'", CompilerName);

			stream.WriteLine("\t.CompilerInputFiles = ");
			stream.WriteQuotedList(ActionNode.InputFiles);

			stream.WriteLine("\t.CompilerOutputPath = '{0}'", Path.GetDirectoryName(ActionNode.OutputFileName));

			var ForceNoCache = ForceNoCachingModules
				.Intersect(ActionNode.InputFiles.Select(Path.GetFileNameWithoutExtension)).Any();

			if (!Action.bCanExecuteRemotely || !Action.bCanExecuteRemotelyWithSNDBS || ForceNoCache)
			{
				stream.WriteLine("\t.AllowDistribution = false");

				if (ForceNoCache)
				{
					stream.WriteLine(
						"\t ; Disabled caching to work-around compler issues. See FastBuild.cs for more details.");
					stream.WriteLine("\t.AllowCaching = false");
				}
			}

			// MSVC: Create precompiled header: /Yc<FileName>.
			var CreateMSVCPCH = ActionNode.FASTBuildOptions.IndexOf("/Yc", StringComparison.InvariantCulture) != -1;

			// Clang: Create precopmiled header: -x c++-header.
			var CreateClangPCH =
				ActionNode.FASTBuildOptions.IndexOf("-x c++-header", StringComparison.InvariantCulture) != -1;

			if (CreateMSVCPCH || CreateClangPCH)
			{
				// Swap from creating the PCH (/Yc) to using the PCH (/Yu) for the Compiler command line.
				var CompilerCommandLine = IsMSVC(Action)
					? ActionNode.FASTBuildOptions.Replace("/Yc", "/Yu")
					: ActionNode.FASTBuildOptions.Replace("-x c++-header", "-x c++");

				stream.WriteLine("\t.CompilerOptions = '{0}'", CompilerCommandLine);
				stream.WriteLine("\t.PCHOptions = '{0}'", ActionNode.FASTBuildOptions);
				stream.WriteLine("\t.PCHInputFile = '{0}'", ActionNode.InputFiles.First());
				stream.WriteLine("\t.PCHOutputFile = '{0}'", ActionNode.OutputFileName);
				stream.WriteLine("\t.CompilerOutputExtension = '{0}'", IsMSVC(Action) ? ".h.pch" : ".h.gch");
			}
			else if (CompilerName == "UE4ResourceCompiler")
			{
				stream.WriteLine("\t.CompilerOptions = '{0}'", ActionNode.FASTBuildOptions);
				stream.WriteLine("\t.CompilerOutputExtension = '{0}'",
					Path.GetExtension(ActionNode.InputFiles[0]) + ".res");
			}
			else
			{
				var extension = IsMSVC(Action) ? ".cpp.obj" : ".cpp.o";
				stream.WriteLine("\t.CompilerOptions = '{0}'", ActionNode.FASTBuildOptions);
				stream.WriteLine("\t.CompilerOutputExtension = '{0}' ", extension);
			}

			if (ActionNode.DependencyIndices.Count > 0)
			{
				stream.WriteLine("\t.PreBuildDependencies = ");
				stream.WriteLine("\t{");
				foreach (var Name in ActionNode.DependencyIndices.Select(CreateActionName))
				{
					stream.WriteLine("\t\t'{0}',", Name);
				}

				stream.WriteLine("\t}");
			}

			stream.WriteLine("}\n");
		}

		private static bool ProducesLibrary(Action Action)
		{
			return Action.CommandPath.Contains("lib.exe") || Action.CommandPath.Contains("orbis-snarl") ||
			       Action.CommandPath.Contains("gnu-ar.exe");
		}

		private static bool ProducesExecutable(Action Action)
		{
			return Action.CommandPath.Contains("link.exe") || Action.CommandPath.Contains("orbis-clang") ||
			       IsClangCrossCompiler(Action);
		}

		private static string CreateActionName(int index)
		{
			return string.Format("Action_{0}", index);
		}

		private void AddLinkAction(Stream stream, List<ActionNode> Actions, int ActionIndex)
		{
			var ActionNode = Actions[ActionIndex];
			var Action = ActionNode.SourceAction;

			// Filter out ".so" files and make them pre-build dependencies.
			// MSVC Quirk: FASTBuild improperly appends the ".h.pch" suffix twice when the node is referenced by its 'Action_x' name.
			// Instead reference the PCH file directly here.
			var InputObjects = ActionNode.DependencyIndices
				.Where(i => Path.GetExtension(Actions[i].OutputFileName) != ".so" &&
				            !Actions[i].OutputFileName.EndsWith(".h.pch"))
				.Select(CreateActionName)
				.ToList();

			var PreBuildDependencies = ActionNode.DependencyIndices
				.Where(i => Path.GetExtension(Actions[i].OutputFileName) == ".so" ||
				            Actions[i].OutputFileName.EndsWith(".h.pch"))
				.Select(CreateActionName)
				.ToList();

			// Filter out libraries that are produced by other actions in this work set; they will be implicitly included by FASTBuild.
			var FilteredInputFiles = ActionNode.InputFiles.Where(Input => !ActionNode.DependencyIndices.Any(Index =>
				Actions[Index].SourceAction.ProducedItems.Any(f =>
					Path.GetFullPath(f.AbsolutePath).Equals(Path.GetFullPath(Input),
						StringComparison.InvariantCultureIgnoreCase))));

			InputObjects = InputObjects.Union(FilteredInputFiles).ToList();

			if (ProducesLibrary(Action))
			{
				stream.WriteLine("Library('{0}') ; {1}\n{{", CreateActionName(ActionIndex), Action.StatusDescription);

				stream.WriteLine("\t.Compiler = '{0}'", GetCompilerName(Action));
				stream.WriteLine("\t.CompilerOptions = '{0} {1}'", ActionNode.FASTBuildOptions,
					IsMSVC(Action) ? "/c" : "-c");
				stream.WriteLine("\t.CompilerOutputPath = '{0}'", Path.GetDirectoryName(ActionNode.OutputFileName));

				stream.WriteLine("\t.Librarian = '{0}'", Action.CommandPath);
				stream.WriteLine("\t.LibrarianOptions = '{0}'", ActionNode.FASTBuildOptions);

				if (InputObjects.Count > 0)
				{
					stream.WriteLine("\t.LibrarianAdditionalInputs = ");
					stream.WriteQuotedList(InputObjects);
				}

				stream.WriteLine("\t.LibrarianOutput = '{0}'", ActionNode.OutputFileName);

				if (PreBuildDependencies.Count > 0)
				{
					stream.WriteLine("\t.PreBuildDependencies = ");
					stream.WriteQuotedList(PreBuildDependencies);
				}
			}
			else if (ProducesExecutable(Action))
			{
				var CommandPath = Action.CommandPath.Replace("cmd.exe", "$ComSpec$");
				stream.WriteLine("Executable('{0}') ; {1}\n{{", CreateActionName(ActionIndex),
					Action.StatusDescription);
				stream.WriteLine("\t.Linker = '{0}'", CommandPath);

				if (IsClangCrossCompiler(Action))
				{
					// Needed so that response files are enabled in FASTBuild.
					stream.WriteLine("\t.LinkerType = 'clang-orbis'");
				}

				var commandLine = ActionNode.FASTBuildOptions;
				if (CommandPath == "$ComSpec$")
				{
					// If the link command is a batch file, it contains everything necessary to build.
					// Pass along superfluous substitution markers so FASTBuild doesn't complain.
					commandLine = string.Format("{0} %1 %2", Action.CommandArguments);

					stream.WriteLine("\t.Libraries = 'NoOp'");

					var Combined = InputObjects.Union(PreBuildDependencies).ToList();
					if (Combined.Count > 0)
					{
						stream.WriteLine("\t.PreBuildDependencies = ");
						stream.WriteQuotedList(Combined);
					}
				}
				else
				{
					if (InputObjects.Count > 0)
					{
						stream.WriteLine("\t.Libraries = ");
						stream.WriteQuotedList(InputObjects);
					}

					if (PreBuildDependencies.Count > 0)
					{
						stream.WriteLine("\t.PreBuildDependencies = ");
						stream.WriteQuotedList(PreBuildDependencies);
					}
				}

				stream.WriteLine("\t.LinkerOptions = '{0}'", commandLine);
				stream.WriteLine("\t.LinkerOutput = '{0}'", ActionNode.OutputFileName);
			}
			else
			{
				throw new BuildException("Error: Unknown toolchain " + Action.CommandPath);
			}

			stream.WriteLine("}\n");
		}

		private void CreateBffFile(List<Action> InActions, string BffFilePath)
		{
			MemoryStream stream = null;

			using (new ScopedTimer("Creating FASTBuild actions...", LogEventType.Console))
			{
				try
				{
					stream = new MemoryStream();
					WriteEnvironment(InActions, stream);

					var ActionNodes = new List<ActionNode>();

					using (new ScopedTimer("Gathering dependencies...", LogEventType.Console))
					{
						for (int ActionIndex = 0; ActionIndex < InActions.Count; ActionIndex++)
						{
							var Action = InActions[ActionIndex];

							var Dependencies = Action.PrerequisiteItems
								.Where(Item => Item.ProducingAction != null)
								.Select(Item => InActions.IndexOf(Item.ProducingAction))
								.Where(Index => Index >= 0);

							ActionNodes.Add(new ActionNode(Action, ActionIndex, Dependencies));
						}
					}

					for (var ActionIndex = 0; ActionIndex < ActionNodes.Count; ActionIndex++)
					{
						var ActionNode = ActionNodes[ActionIndex];
						try
						{
							stream.WriteLine(" ; {0}", ActionNode.AllArguments);

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
									stream.WriteText("; IGNORED: Action_{0} - {1} {2}", ActionIndex,
										ActionNode.SourceAction.CommandPath, ActionNode.SourceAction.CommandArguments);
									break;
							}
						}
						catch (Exception e)
						{
							throw new BuildException(
								string.Format("Building action {0}\n{1}\n{2}\n{3} - {4}", ActionIndex,
									e.Message,
									e.StackTrace, ActionNode.SourceAction.ActionType,
									ActionNode.SourceAction.CommandArguments),
								e);
						}
					}

					stream.WriteLine("Alias('all')\n{");
					stream.WriteLine("\t.Targets = ");
					stream.WriteQuotedList(Enumerable.Range(0, InActions.Count).Select(CreateActionName));
					stream.WriteLine("}\n");
				}
				finally
				{
					using (var FileStream = new FileStream(BffFilePath, FileMode.Create, FileAccess.Write))
					{
						stream?.WriteTo(FileStream);
					}

					stream?.Dispose();
				}
			}
		}

		private bool ExecuteBffFile(string BffFilePath)
		{
			var DistArgument = bEnableDistribution ? "-dist" : string.Empty;

			//Interesting flags for FASTBuild: -showcmds, -nostoponerror, -verbose, -monitor (if FASTBuild Monitor Visual Studio Extension is installed!)
			// Yassine: The -clean is to bypass the FastBuild internal dependencies checks (cached in the fdb) as it could create some conflicts with UBT.
			//			Basically we want FB to stupidly compile what UBT tells it to.
			var FBCommandLine = string.Format("-monitor -summary {0} {1} -ide -clean -config {2}",
				DistArgument, GetCacheModeAsCommandArgument(CacheFlag), BffFilePath);

			var FBStartInfo =
				new ProcessStartInfo(string.IsNullOrEmpty(FBuildExePath) ? "fbuild" : FBuildExePath,
					FBCommandLine);

			FBStartInfo.UseShellExecute = false;
			FBStartInfo.WorkingDirectory =
				Path.Combine(UnrealBuildTool.EngineDirectory.MakeRelativeTo(DirectoryReference.GetCurrentDirectory()),
					"Source");

			try
			{
				using (var FBProcess = new Process())
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
				Console.WriteLine("Exception launching fbuild process. Is it in your path?\n" + e);
				return false;
			}
		}

		private static string GetCacheModeAsCommandArgument(string Mode)
		{
			if (string.IsNullOrEmpty(Mode))
			{
				return string.Empty;
			}

			if (Mode == "rw")
			{
				return "-cache";
			}

			if (Mode == "r")
			{
				return "-cacheread";
			}

			if (Mode == "w")
			{
				return "-cachewrite";
			}

			throw new BuildException(string.Format("Unknown FASTBUILD_CACHE_MODE '{0}'. Only, null, 'r', 'w', or 'rw' are allowed.", Mode));
		}

		private class ActionNode
		{
			public ActionNode(Action Source, int Index, IEnumerable<int> Dependencies)
			{
				SourceAction = Source;
				SourceActionIndex = Index;
				DependencyIndices = new List<int>(Dependencies);

				tokens = GetAllCommandLineArguments(Source, out responseFileTokenIndices, out responseFileToken);
				tokens = tokens.Select(EscapeTokens).ToList();

				FindInputFiles();
				FASTBuildOptions = InterpolateOptions();
			}

			public Action SourceAction { get; }
			public int SourceActionIndex { get; }
			public IReadOnlyList<int> DependencyIndices { get; }

			/// <summary>
			/// The Action's command line arguments ready for passing to FASTBuild.
			/// Input files are replaced by a single "%1" marker, and the output file replaced by "%2".
			/// </summary>
			public string FASTBuildOptions { get; }

			public IReadOnlyList<string> InputFiles
			{
				get { return inputFileIndices.Select(i => tokens[i].Trim('"')).ToList(); }
			}

			public string OutputFileName
			{
				get
				{
					// Future-proofing: ensure that non-executable targets produced by the MSVC compiler are excluded.
					// .exp = Export files
					return SourceAction.ProducedItems.First(Item =>
						!Item.AbsolutePath.Contains(".exp") && !Item.AbsolutePath.Contains(".suppresed")).AbsolutePath;
				}
			}

			public string AllArguments
			{
				get { return string.Join(" ", tokens); }
			}

			private readonly List<string> tokens;
			private readonly List<int> responseFileTokenIndices;
			private readonly HashSet<int> inputFileIndices = new HashSet<int>();
			private readonly string responseFileToken;

			private string InterpolateOptions()
			{
				if (!string.IsNullOrEmpty(FASTBuildOptions))
				{
					return FASTBuildOptions;
				}

				var ResponseTokenPrefix = string.Empty;
				if (!string.IsNullOrEmpty(responseFileToken))
				{
					var AtIndex = responseFileToken.IndexOf("@");
					if (AtIndex > 0)
					{
						ResponseTokenPrefix = responseFileToken.Substring(0, AtIndex);
					}
				}

				var AddedInputMarker = false;
				var Builder = new StringBuilder();
				for (var i = 0; i < tokens.Count; i++)
				{
					if (inputFileIndices.Contains(i))
					{
						if (!AddedInputMarker)
						{
							AddedInputMarker = true;
							Builder.Append("\"%1\" ");
						}
					}
					else if (!string.IsNullOrEmpty(ResponseTokenPrefix) && responseFileTokenIndices.Contains(i))
					{
						// Add the respose file prefix to the token.
						Builder.AppendFormat("{0}{1} ", ResponseTokenPrefix, tokens[i]);
					}
					else
					{
						Builder.AppendFormat("{0} ", tokens[i]);
					}
				}

				if (SourceAction.ActionType == ActionType.Compile)
				{
					if (IsClangCrossCompiler(SourceAction))
					{
						// Clang warning diagnostics aren't disabled properly when FASTBuild pre-processes them via "-frewrite-includes"
						// See: https://github.com/fastbuild/fastbuild/issues/143
						// Unforunately, the workaround of disabling it causes the pre-processed file macros to be expanded, and then trip the
						// "|| uses only constants" diagnostic for some engine macros.
						// The current solution just globally disables warnings that trip, and keeps the "-frewrite-includes" functionality,
						// since it seems like an improvement over all.
						Builder.Append("-Wno-undef");
					}
					else if (IsMSVC(SourceAction))
					{
						Builder.Append("/wd4668");
					}
				}

				var interpolatedOptions = Builder.ToString().Trim();

				// Handle both Windows and Unix style path separators.				
				interpolatedOptions = interpolatedOptions.ReplaceCaseInsensitive(OutputFileName, "%2");
				interpolatedOptions =
					interpolatedOptions.ReplaceCaseInsensitive(OutputFileName.Replace("\\", "/"), "%2");

				return interpolatedOptions;
			}

			private void FindInputFiles()
			{
				for (var i = 0; i < tokens.Count; i++)
				{
					var Token = tokens[i].Trim('"');

					if (IsTokenOutputFileName(Token))
					{
						continue;
					}

					switch (SourceAction.ActionType)
					{
						case ActionType.Compile:
							if (IsCompilerInputFileName(Token) || IsClangPrecompiledHeader(Token))
							{
								inputFileIndices.Add(i);
							}

							break;
						case ActionType.Link:
							if (IsLinkerInputFileExtention(Token) &&
							    IsAllowedLinkerFileName(Token))
							{
								inputFileIndices.Add(i);
							}

							break;
					}
				}
			}

			private bool IsTokenOutputFileName(string Token)
			{
				return Token.IndexOf(OutputFileName, StringComparison.InvariantCultureIgnoreCase) != -1;
			}

			private static bool IsCompilerInputFileName(string FileName)
			{
				return FileName.EndsWith(".cpp") || FileName.EndsWith(".rc");
			}

			private bool IsClangPrecompiledHeader(string FileName)
			{
				return FileName.EndsWith(".h") && OutputFileName.EndsWith(".gch");
			}

			private static bool IsLinkerInputFileExtention(string FileName)
			{
				return FileName.EndsWith(".obj") || FileName.EndsWith(".o") || FileName.EndsWith(".lib") ||
				       FileName.EndsWith(".a") || FileName.EndsWith(".rc.res");
			}

			private static bool IsAllowedLinkerFileName(string FileName)
			{
				return !FileName.Contains(".suppressed") &&
				       !FileName.Contains(".h.obj") &&
				       FileName.Contains("Intermediate");
			}


			/// <summary>
			/// Tokenizes the action's command line. If the command line contains a response file (@"filepath.response")
			/// the contents of the file will be inlined, replacing the response file token.
			/// </summary>
			/// <param name="Action">The source action</param>
			/// <param name="ResponseFileTokens">The indices of tokens loaded from the response file.</param>
			/// <param name="ResponseFileToken">The token that represents the response file.</param>
			/// <returns>The final set of tokens, including all tokens loaded from a response file (if present.)</returns>
			private static List<string> GetAllCommandLineArguments(Action Action,
				out List<int> ResponseFileTokens, out string ResponseFileToken)
			{
				var OutTokens = new List<string>();
				ResponseFileTokens = new List<int>();
				string ResponseFileName = null;
				ResponseFileToken = null;

				var InTokens = Tokenizer.Tokenize(Action.CommandArguments);

				const string ClangLinkerResponseFile = "-Wl,@";

				for (var index = 0; index < InTokens.Count; index++)
				{
					var Token = InTokens[index];
					if (Token.StartsWith("@") || Token.StartsWith(ClangLinkerResponseFile))
					{
						if (!string.IsNullOrEmpty(ResponseFileName))
						{
							throw new BuildException(string.Format("Found multiple response files. {0}", Token));
						}

						ResponseFileName = Token.Replace("-Wl,", string.Empty).Trim('@').Trim('"');
						ResponseFileToken = Token;

						var lines = Tokenizer.Tokenize(File.ReadAllText(ResponseFileName));

						var startIndex = OutTokens.Count;
						OutTokens.AddRange(lines);
						ResponseFileTokens.AddRange(Enumerable.Range(startIndex, lines.Count));
					}
					else
					{
						OutTokens.Add(Token);
					}
				}

				return OutTokens;
			}

			private static string EscapeTokens(string commandLineString)
			{
				return commandLineString.Replace("${ORIGIN}", "^${ORIGIN}");
			}
		}

		private static bool IsMSVC(Action Action)
		{
			return IsWindows(Action);
		}

		private static bool IsWindows(Action Action)
		{
			// Not a great test.
			return Action.CommandPath.Contains("Microsoft");
		}

		private static bool IsClangCrossCompiler(Action Action)
		{
			return Action.CommandPath.Contains("clang++") || Action.CommandArguments.Contains(".link.bat");
		}

		private static string GetCompilerName(Action Action)
		{
			if (Action.CommandPath.Contains("rc.exe"))
			{
				return "UE4ResourceCompiler";
			}

			if (IsMSVC(Action))
			{
				return "UE4Compiler";
			}

			if (IsClangCrossCompiler(Action))
			{
				return "UE4LinuxCrossCompiler";
			}

			throw new BuildException(string.Format("Unknown compiler for {0}", Action.CommandPath));
		}
	}

	static class StreamExtensions
	{
		private static readonly byte[] NewLine = Encoding.UTF8.GetBytes("\n");

		public static void WriteText(this Stream stream, string StringToWrite)
		{
			var Bytes = Encoding.UTF8.GetBytes(StringToWrite);
			stream.Write(Bytes, 0, Bytes.Length);
		}

		public static void WriteText(this Stream Stream, string StringToWrite, params object[] args)
		{
			var Bytes = Encoding.UTF8.GetBytes(string.Format(StringToWrite, args));
			Stream.Write(Bytes, 0, Bytes.Length);
		}

		public static void WriteLine(this Stream Stream, string StringToWrite)
		{
			Stream.WriteText(StringToWrite);
			Stream.Write(NewLine);
		}

		public static void WriteLine(this Stream Stream, string StringToWrite, params object[] args)
		{
			Stream.WriteText(StringToWrite, args);
			Stream.Write(NewLine);
		}

		public static void WriteQuotedList(this Stream Stream, IEnumerable<string> Items)
		{
			Stream.WriteLine("\t{");
			foreach (var Item in Items)
			{
				Stream.WriteLine("\t\t'{0}',", Item);
			}

			Stream.WriteLine("\t}");
		}
	}

	static class StringExtensions
	{
		// The string class doesn't have a case-insensitive Replace overload.
		public static string ReplaceCaseInsensitive(this string Source, string OldValue, string NewValue)
		{
			var Builder = new StringBuilder();
			var Start = 0;
			var Index = Source.IndexOf(OldValue, StringComparison.InvariantCultureIgnoreCase);
			while (Index >= 0)
			{
				if (Index > Start)
				{
					Builder.Append(Source.Substring(Start, Index));
				}

				Builder.Append(NewValue);
				Start = Index + OldValue.Length;
				Index = Source.IndexOf(OldValue, Index + OldValue.Length, StringComparison.InvariantCultureIgnoreCase);
			}

			Builder.Append(Source.Substring(Start, Source.Length - Start));

			return Builder.ToString();
		}
	}

	static class Tokenizer
	{
		/// <summary>
		/// Breaks up an input string into tokens separated by whitespace.
		/// Quoted strings form a single token, and are returned with the surrounding quotes intact.
		/// For example: two calls to GetNextToken for '-o "path to file with spaces"' would return [-o] and ["path to file with spaces"]
		/// For example: a call to GetNextToken for '/Yc"path to file with spaces"' would return [/Yc"path to file with spaces"]
		/// </summary>
		/// <param name="InString">The source string.</param>
		/// <param name="StartIndex">The index within the string to start parsing tokens. Upon return, this is the index of the character that caused the token to end, or -1 if no further tokens are available.</param>
		/// <returns>The next token, or null if there are no further tokens.</returns>
		private static string GetNextToken(string InString, ref int StartIndex)
		{
			// Skip leading whitespace
			while (StartIndex < InString.Length && Char.IsWhiteSpace(InString[StartIndex]))
			{
				StartIndex++;
			}

			if (StartIndex >= InString.Length)
			{
				StartIndex = -1;
				return null;
			}

			var TokenBegin = StartIndex;
			var ParsingString = false;

			while (StartIndex < InString.Length)
			{
				var c = InString[StartIndex];
				if (!ParsingString && Char.IsWhiteSpace(c))
				{
					// We've reached the end of a token run.
					break;
				}

				if (ParsingString)
				{
					if (InString[StartIndex] == '\\')
					{
						if (StartIndex + 1 < InString.Length)
						{
							var NextC = InString[StartIndex + 1];
							if (NextC == '"')
							{
								// Skip escaped quotes inside of a string.
								StartIndex++;
							}
						}
					}
					else if (InString[StartIndex] == '"')
					{
						ParsingString = false;
					}
				}
				else if (InString[StartIndex] == '\\' && StartIndex + 1 < InString.Length &&
				         InString[StartIndex + 1] == '"')
				{
					// There's an odd construct of passing through a double-escaped string to the pre-processor. Handle this weird edge-case here.
					// For example: /DORIGINAL_FILENAME=\\\"Name\\\"
					StartIndex = InString.IndexOf("\\\"", StartIndex + 2, StringComparison.InvariantCulture) + 2;
					continue;
				}
				else if (InString[StartIndex] == '"')
				{
					ParsingString = true;
				}

				StartIndex++;
			}

			return InString.Substring(TokenBegin, StartIndex - TokenBegin);
		}

		public static List<string> Tokenize(string Args)
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
	}
}