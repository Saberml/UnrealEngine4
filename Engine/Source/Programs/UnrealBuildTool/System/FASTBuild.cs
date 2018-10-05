// Downloaded from https://github.com/liamkf/Unreal_FASTBuild/

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

		private HashSet<string> ForceLocalCompileModules = new HashSet<string>()
			{"Module.ProxyLODMeshReduction", "Module.ClothingSystemRuntime"};

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

		private string GetCompilerName()
		{
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
			bool FASTBuildResult = true;
			if (Actions.Count > 0)
			{
				DetectBuildType(Actions);

				string FASTBuildFilePath = Path.Combine(UnrealBuildTool.EngineDirectory.FullName, "Intermediate",
					"Build", "fbuild.bff");
				if (CreateBffFile(Actions, FASTBuildFilePath))
				{
					FASTBuildResult = ExecuteBffFile(FASTBuildFilePath);
				}
				else
				{
					FASTBuildResult = false;
				}
			}

			return FASTBuildResult;
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

		private Dictionary<string, string> ParseCommandLineOptions(string CompilerCommandLine, string[] SpecialOptions,
			bool SaveResponseFile = false)
		{
			Dictionary<string, string> ParsedCompilerOptions = new Dictionary<string, string>();

			// Make sure we substituted the known environment variables with corresponding BFF friendly imported vars
			CompilerCommandLine = SubstituteEnvironmentVariables(CompilerCommandLine);

			// Some tricky defines /DTROUBLE=\"\\\" abc  123\\\"\" aren't handled properly by either Unreal or Fastbuild, but we do our best.
			var RawTokens = CompilerCommandLine.Trim().Split(' ').ToList();
			List<string> ProcessedTokens = new List<string>();
			bool QuotesOpened = false;
			string PartialToken = "";
			string ResponseFilePath = "";

			var responseFileIndex = RawTokens.FindIndex(token => token.StartsWith("@"));

			if (responseFileIndex != -1
			) //Response files are in 4.13 by default. Changing VCToolChain to not do this is probably better.
			{
				var responseCommandline = RawTokens[responseFileIndex];
				RawTokens.RemoveAt(responseFileIndex);

				// If we had spaces inside the response file path, we need to reconstruct the path.

				if (responseCommandline[1] == '\"')
				{
					for (int i = responseFileIndex; i < RawTokens.Count && RawTokens[i] != "\""; ++i)
					{
						responseCommandline += " " + RawTokens[i];
						RawTokens.RemoveAt(i);
					}

					// Skip the leading @, trim the quotes
					ResponseFilePath = responseCommandline.Substring(1).Trim('\"');
				}

				try
				{
					string ResponseFileText = File.ReadAllText(ResponseFilePath);

					// Make sure we substituted the known environment variables with corresponding BFF friendly imported vars
					ResponseFileText = SubstituteEnvironmentVariables(ResponseFileText);

					string[] Separators = {"\n", " ", "\r"};
					if (File.Exists(ResponseFilePath))
					{
						RawTokens.AddRange(ResponseFileText.Split(Separators,
							StringSplitOptions.RemoveEmptyEntries)); //Certainly not ideal
					}
				}
				catch (Exception e)
				{
					Console.WriteLine("Looks like a response file in: " + CompilerCommandLine +
					                  ", but we could not load it! " + e.Message);
					ResponseFilePath = "";
				}
			}

			// Raw tokens being split with spaces may have split up some two argument options and
			// paths with multiple spaces in them also need some love
			for (int i = 0; i < RawTokens.Count; ++i)
			{
				string Token = RawTokens[i];
				if (string.IsNullOrEmpty(Token))
				{
					if (ProcessedTokens.Count > 0 && QuotesOpened)
					{
						string CurrentToken = ProcessedTokens.Last();
						CurrentToken += " ";
					}

					continue;
				}

				int numQuotes = 0;
				// Look for unescaped " symbols, we want to stick those strings into one token.
				for (int j = 0; j < Token.Length; ++j)
				{
					if (Token[j] == '\\') //Ignore escaped quotes
						++j;
					else if (Token[j] == '"')
						numQuotes++;
				}

				// Defines can have escaped quotes and other strings inside them
				// so we consume tokens until we've closed any open unescaped parentheses.
				if ((Token.StartsWith("/D") || Token.StartsWith("-D")) && !QuotesOpened)
				{
					if (numQuotes == 0 || numQuotes == 2)
					{
						ProcessedTokens.Add(Token);
					}
					else
					{
						PartialToken = Token;
						++i;
						bool AddedToken = false;
						for (; i < RawTokens.Count; ++i)
						{
							string NextToken = RawTokens[i];
							if (string.IsNullOrEmpty(NextToken))
							{
								PartialToken += " ";
							}
							else if (!NextToken.EndsWith("\\\"") && NextToken.EndsWith("\"")
							) //Looking for a token that ends with a non-escaped "
							{
								ProcessedTokens.Add(PartialToken + " " + NextToken);
								AddedToken = true;
								break;
							}
							else
							{
								PartialToken += " " + NextToken;
							}
						}

						if (!AddedToken)
						{
							Console.WriteLine(
								"Warning! Looks like an unterminated string in tokens. Adding PartialToken and hoping for the best. Command line: " +
								CompilerCommandLine);
							ProcessedTokens.Add(PartialToken);
						}
					}

					continue;
				}

				if (!QuotesOpened)
				{
					if (numQuotes % 2 != 0) //Odd number of quotes in this token
					{
						PartialToken = Token + " ";
						QuotesOpened = true;
					}
					else
					{
						ProcessedTokens.Add(Token);
					}
				}
				else
				{
					if (numQuotes % 2 != 0) //Odd number of quotes in this token
					{
						ProcessedTokens.Add(PartialToken + Token);
						QuotesOpened = false;
					}
					else
					{
						PartialToken += Token + " ";
					}
				}
			}

			//Processed tokens should now have 'whole' tokens, so now we look for any specified special options
			foreach (string specialOption in SpecialOptions)
			{
				for (int i = 0; i < ProcessedTokens.Count; ++i)
				{
					if (ProcessedTokens[i] == specialOption && i + 1 < ProcessedTokens.Count)
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i + 1];
						ProcessedTokens.RemoveRange(i, 2);
						break;
					}
					else if (ProcessedTokens[i].StartsWith(specialOption))
					{
						ParsedCompilerOptions[specialOption] = ProcessedTokens[i].Replace(specialOption, null);
						ProcessedTokens.RemoveAt(i);
						break;
					}
				}
			}

			//The search for the input file... we take the first non-argument we can find
			for (int i = 0; i < ProcessedTokens.Count; ++i)
			{
				string Token = ProcessedTokens[i];
				if (Token.Length == 0)
				{
					continue;
				}

				if (Token == "/I" || Token == "/l" || Token == "/D" || Token == "-D" || Token == "-x" ||
				    Token == "-target" ||
				    Token == "-include"
				) // Skip tokens with values, I for cpp includes, l for resource compiler includes
				{
					++i;
				}
				else if (!Token.StartsWith("/") && !Token.StartsWith("-"))
				{
					ParsedCompilerOptions["InputFile"] = Token;

					if (Token.Trim('\"').IndexOfAny(Path.GetInvalidPathChars()) != -1)
					{
						Console.WriteLine("Found an input file that was invalid: {0}", Token);
					}

					ProcessedTokens.RemoveAt(i);
					break;
				}
			}

			ParsedCompilerOptions["OtherOptions"] = string.Join(" ", ProcessedTokens) + " ";

			if (SaveResponseFile && !string.IsNullOrEmpty(ResponseFilePath))
			{
				ParsedCompilerOptions["@"] = ResponseFilePath;
			}

			return ParsedCompilerOptions;
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

		private string GetOptionValue(Dictionary<string, string> OptionsDictionary, string Key, Action Action,
			bool ProblemIfNotFound = false)
		{
			string Value = string.Empty;
			if (OptionsDictionary.TryGetValue(Key, out Value))
			{
				return Value.Trim(new Char[] {'\"'});
			}

			if (ProblemIfNotFound)
			{
				Console.WriteLine("We failed to find " + Key + ", which may be a problem.");
				Console.WriteLine("Action.CommandArguments: " + Action.CommandArguments);
			}

			return Value;
		}

		private void WriteEnvironmentSetup(FileStream stream)
		{
			VCEnvironment VCEnv = null;
			try
			{
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
			}
			catch (Exception)
			{
				Console.WriteLine("Failed to get Visual Studio environment.");
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
						throw new Exception(
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

		private string GetCommandLineArguments(Action Action)
		{
			var MergedCommandArguments = Action.CommandArguments;

			if (TryGetResponseFile(Action.CommandArguments, out var ResponseFileName, out var ResponseFileContent))
			{
				MergedCommandArguments = MergedCommandArguments + " " + ResponseFileContent;
				MergedCommandArguments = MergedCommandArguments.Replace(string.Format("@\"{0}\"",ResponseFileName), string.Empty)
					.Replace("\r", string.Empty).Replace("\n", " ");
			}

			return MergedCommandArguments;
		}

		private void AddCompileAction(FileStream stream, Action Action, int ActionIndex,
			List<int> DependencyIndices)
		{
			string CompilerName = GetCompilerName();
			if (Action.CommandPath.Contains("rc.exe"))
			{
				CompilerName = "UE4ResourceCompiler";
			}

			string OutputObjectFileName;
			List<string> InputFiles;

			var MergedCommandArguments = GetCommandLineArguments(Action);
			MergedCommandArguments = RewriteCompilerArguments(MergedCommandArguments, out OutputObjectFileName, out InputFiles);

			if (string.IsNullOrEmpty(OutputObjectFileName)) 
			{
				//No /Fo or /fo, we're probably in trouble.
				Console.WriteLine("We have no OutputObjectFileName. Bailing.");
				return;
			}

			string IntermediatePath = Path.GetDirectoryName(OutputObjectFileName);
			if (string.IsNullOrEmpty(IntermediatePath))
			{
				Console.WriteLine("We have no IntermediatePath. Bailing.");
				Console.WriteLine("Our Action.CommandArguments were: " + Action.CommandArguments);
				return;
			}

			if (!InputFiles.Any())
			{
				Console.WriteLine("We have no InputFile. Bailing.");
				return;
			}

			stream.WriteText("; {0}\n", MergedCommandArguments);
			stream.WriteText("ObjectList('Action_{0}')\n{{\n", ActionIndex);
			stream.WriteText("\t.Compiler = '{0}' \n", CompilerName);
			stream.WriteText("\t.CompilerInputFiles = {{\n{0}\n}}\n", string.Join(",", InputFiles.Select(f => string.Format("'{0}'", f)).ToArray()));
			stream.WriteText("\t.CompilerOutputPath = \"{0}\"\n", IntermediatePath);

			if (!Action.bCanExecuteRemotely || !Action.bCanExecuteRemotelyWithSNDBS ||
			    ForceLocalCompileModules.Intersect(InputFiles.Select(Path.GetFileNameWithoutExtension)).Any())
			{
				stream.WriteText("\t.AllowDistribution = false\n");
			}

			string CompilerOutputExtension;

			if (TryGetValueForFlag(Action.CommandArguments, "/Yc", out var CreatePCHIncludeHeader)) //Create PCH
			{
				if (!TryGetValueForFlag(Action.CommandArguments, "/Fp", out var PCHOutputFile))
				{
					Console.WriteLine("We failed to find /Fp. Bailing.");
					return;
				}

				stream.WriteText("\t.CompilerOptions = '{0}'\n", MergedCommandArguments.Replace("/Yc", "/Yu");

				stream.WriteText("\t.PCHOptions = '\"%1\" /Fp\"%2\" /Yc\"{0}\" {1} /Fo\"{2}\"'\n", CreatePCHIncludeHeader, OtherCompilerOptions, OutputObjectFileName);
				stream.WriteText("\t.PCHInputFile = \"{0}\"\n", InputFiles.First());
				stream.WriteText("\t.PCHOutputFile = \"{0}\"\n", PCHOutputFile);
				CompilerOutputExtension = ".obj";
			}
			else if (TryGetValueForFlag(Action.CommandArguments, "/Yu", out var PCHIncludeHeader)) //Use PCH
			{
				string PCHOutputFile;
				if (!TryGetValueForFlag(Action.CommandArguments, "/Fp", out PCHOutputFile))
				{
					Console.WriteLine("We failed to find /Fp. Bailing.");
					return;
				}

				string PCHToForceInclude = PCHOutputFile.Replace(".pch", "");
				stream.WriteText("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /Fp\"{0}\" /Yu\"{1}\" /FI\"{2}\" {3} '\n", PCHOutputFile, PCHIncludeHeader, PCHToForceInclude, OtherCompilerOptions);
				CompilerOutputExtension = ".cpp.obj";
			}
			else if (CompilerName == "UE4ResourceCompiler")
			{
				stream.WriteText("\t.CompilerOptions = '{0}'\n", MergedCommandArguments);
				CompilerOutputExtension = Path.GetExtension(InputFiles[0]) + ".res";
			}
			else if (IsMSVC())
			{
				stream.WriteText("\t.CompilerOptions = '{0}'\n", MergedCommandArguments);
				CompilerOutputExtension = ".cpp.obj";
			}
			else
			{
				stream.WriteText("\t.CompilerOptions = '{0}'\n", MergedCommandArguments);
				CompilerOutputExtension = ".cpp.o";
			}

			stream.WriteText("\t.CompilerOutputExtension = '{0}' \n", CompilerOutputExtension);

			if (DependencyIndices.Count > 0)
			{
				var DependencyNames = DependencyIndices.Select(x => string.Format("'Action_{0}'", x));
				stream.WriteText("\t.PreBuildDependencies = {{ {0} }}\n", string.Join(",", DependencyNames.ToArray()));
			}

			stream.WriteText("}\n\n");
		}

		private void AddLinkAction(FileStream stream, List<Action> Actions, int ActionIndex,
			List<int> DependencyIndices)
		{
			Action Action = Actions[ActionIndex];
			var  knownCompilerOptions = new [] {"/OUT:", "/PDB", "/IMPLIB", "/NODEFAULTLIB", "/LIBPATH" ,"-o"};

			var MergedCommandArguments = GetCommandLineArguments(Action);

			string OutputFile;

			var isClangCrossCompiler =
				Action.CommandPath.Contains("cmd.exe") && MergedCommandArguments.Contains("gnu-ar.exe");
			if (isClangCrossCompiler)
			{
				// This is executed as a sub-shell (cmd.exe or /bin/sh), so the arguments need cleaning.
				MergedCommandArguments = MergedCommandArguments.Replace("/c", string.Empty).Trim(' ');

				// Remove first and last quote. Don't use Trim(), since there may be more leading and trailing quotes.
				MergedCommandArguments = MergedCommandArguments.Substring(1, MergedCommandArguments.Length - 2);

				// Strip the linker executable name.
				var Index = MergedCommandArguments.IndexOf("\"", 1, StringComparison.Ordinal);
				Action.CommandPath = MergedCommandArguments.Substring(1, Index - 1);
				MergedCommandArguments = MergedCommandArguments.Substring(Index + 2);
			}

			string OtherCompilerOptions =
				FilterArgumentsWithValues(MergedCommandArguments, knownCompilerOptions);
			var InputFiles = GetLooseArguments(OtherCompilerOptions);

			if (IsXBOnePDBUtil(Action))
			{
				OutputFile = InputFiles.First();
			}
			else if (IsMSVC())
			{
				if (!TryGetValueForFlag(MergedCommandArguments, "/OUT:", out OutputFile))
				{

				}
			}
			else if (isClangCrossCompiler)
			{
				OutputFile = InputFiles[0];
			}
			else //PS4
			{
				if (!TryGetValueForFlag(MergedCommandArguments, "-o", out OutputFile))
				{
					OutputFile = InputFiles.First();
				}
			}

			if (string.IsNullOrEmpty(OutputFile))
			{
				Console.WriteLine("Failed to find output file. Bailing.");
				return;
			}

			List<int> PrebuildDependencies = new List<int>();
			var InputFilesAsString = string.Join(",\n\t\t", InputFiles.Select(f => string.Format("'{0}'", f)).ToArray());

			stream.WriteText("; {0}\n", MergedCommandArguments);

			if (IsXBOnePDBUtil(Action))
			{				
				stream.WriteText("Exec('Action_{0}')\n{{\n", ActionIndex);
				stream.WriteText("\t.ExecExecutable = '{0}'\n", Action.CommandPath);
				stream.WriteText("\t.ExecArguments = '{0}'\n", Action.CommandArguments);
				stream.WriteText("\t.ExecInput = {{ {0} }} \n", InputFiles.First());
				stream.WriteText("\t.ExecOutput = '{0}' \n", OutputFile);
				stream.WriteText("\t.PreBuildDependencies = {{ {0} }} \n", InputFiles.First());
				stream.WriteText("}\n\n");
			}
			else if (Action.CommandPath.Contains("lib.exe") || Action.CommandPath.Contains("orbis-snarl") ||
			         isClangCrossCompiler)
			{
				if (DependencyIndices.Count > 0)
				{
					for (int i = 0;
						i < DependencyIndices.Count;
						++i) //Don't specify pch or resource files, they have the wrong name and the response file will have them anyways.
					{
						int depIndex = DependencyIndices[i];
						foreach (FileItem item in Actions[depIndex].ProducedItems)
						{
							if (item.ToString().Contains(".pch") || item.ToString().Contains(".res"))
							{
								DependencyIndices.RemoveAt(i);
								i--;
								PrebuildDependencies.Add(depIndex);
								break;
							}
						}
					}
				}

				stream.WriteText("Library('Action_{0}')\n{{\n", ActionIndex);
				stream.WriteText("\t.Compiler = '{0}'\n", GetCompilerName());
				if (IsMSVC())
					stream.WriteText("\t.CompilerOptions = '\"%1\" /Fo\"%2\" /c'\n");
				else
					stream.WriteText("\t.CompilerOptions = '\"%1\" -o \"%2\" -c'\n");
				stream.WriteText("\t.CompilerOutputPath = \"{0}\"\n", Path.GetDirectoryName(OutputFile));

				stream.WriteText("\t.Librarian = '{0}' \n", Action.CommandPath);


				if (IsMSVC())
				{
					stream.WriteText("\t.LibrarianOptions = ' /OUT:\"%2\" \"%1\" {0}'\n", OtherCompilerOptions);
				}
				else if (isClangCrossCompiler)
				{
					stream.WriteText("\t.LibrarianOptions = 'rcs \"%2\" \"%1\" {0}'\n", OtherCompilerOptions);
				}
				else
				{
					stream.WriteText("\t.LibrarianOptions = '\"%2\" @\"%1\" {0}'\n", OtherCompilerOptions);
				}


				if (DependencyIndices.Count > 0)
				{
//					List<string> DependencyNames = DependencyIndices.ConvertAll(x => string.Format("'Action_{0}'", x));
//
//					if (!string.IsNullOrEmpty(ResponseFilePath))
//						stream.WriteText("\t.LibrarianAdditionalInputs = {{ {0} }} \n",
//							DependencyNames[0]); // Hack...Because FastBuild needs at least one Input file
//					else if (IsMSVC())
//						stream.WriteText("\t.LibrarianAdditionalInputs = {{ {0} }} \n",
//							string.Join(",", DependencyNames.ToArray()));

					PrebuildDependencies.AddRange(DependencyIndices);
				}
				else
				{					
					stream.WriteText("\t.LibrarianAdditionalInputs = {{ {0} }} \n", InputFilesAsString);
				}

				if (PrebuildDependencies.Count > 0)
				{
					List<string> PrebuildDependencyNames =
						PrebuildDependencies.ConvertAll(x => string.Format("'Action_{0}'", x));
					stream.WriteText("\t.PreBuildDependencies = {{ {0} }} \n",
						string.Join(",", PrebuildDependencyNames.ToArray()));
				}

				stream.WriteText("\t.LibrarianOutput = '{0}' \n", OutputFile);
				stream.WriteText("}\n\n");
			}
			else if (Action.CommandPath.Contains("link.exe") || Action.CommandPath.Contains("orbis-clang") ||
			         Action.CommandPath.Contains("clang++"))
			{
				stream.WriteText("Executable('Action_{0}')\n{{ \n", ActionIndex);
				stream.WriteText("\t.Linker = '{0}' \n", Action.CommandPath);

				if (DependencyIndices.Count == 0)
				{
					// Just use the response file itself to stop FASTBuild from throwing an error.
					stream.WriteText("\t.Libraries = {{ '{0}' }} \n", "ResponseFilePath");
				}
				else
				{
					stream.WriteText("\t.Libraries = {{ {0} }} \n", InputFilesAsString);

					List<string> DependencyNames = DependencyIndices.ConvertAll(x =>
						string.Format("\t\t'Action_{0}', ; {1}", x, Actions[x].StatusDescription));
					stream.WriteText("\t.PreBuildDependencies = {{\n{0}\n\t}} \n",
						string.Join("\n", DependencyNames.ToArray()));
				}

				if (IsMSVC())
				{
					// The TLBOUT is a huge bodge to consume the %1.
					if (BuildType == FBBuildType.XBOne)
					{
						stream.WriteText("\t.LinkerOptions = '/TLBOUT:\"%1\" /Out:\"%2\" {0} ' \n", OtherCompilerOptions);
					}
					else
					{
						stream.WriteText("\t.LinkerOptions = '/TLBOUT:\"%1\" /Out:\"%2\" ' \n");
					}
				}
				else
				{
					// The MQ is a huge bodge to consume the %1.
					stream.WriteText("\t.LinkerOptions = '-o \"%2\" {0} -MQ \"%1\"' \n", OtherCompilerOptions);
				}

				stream.WriteText("\t.LinkerOutput = '{0}' \n", OutputFile);
				stream.WriteText("}\n\n");
			}
			else
			{
				throw new Exception("Error: Unknown toolchain " + Action.CommandPath);
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

					for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
					{
						Action Action = Actions[ActionIndex];

						// Resolve dependencies
						List<int> DependencyIndices = new List<int>();
						foreach (FileItem Item in Action.PrerequisiteItems)
						{
							if (Item.ProducingAction != null)
							{
								int ProducingActionIndex = Actions.IndexOf(Item.ProducingAction);
								if (ProducingActionIndex >= 0)
								{
									DependencyIndices.Add(ProducingActionIndex);
								}
							}
						}

						switch (Action.ActionType)
						{
							case ActionType.Compile:
								AddCompileAction(stream, Action, ActionIndex, DependencyIndices);
								break;
							case ActionType.Link:
								AddLinkAction(stream, Actions, ActionIndex, DependencyIndices);
								break;
							default:
								Console.WriteLine("Fastbuild is ignoring an unsupported action: " +
								                  Action.ActionType);
								break;
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
				Console.WriteLine("Exception while creating bff file: " + e.ToString());
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
							Console.WriteLine(Args.Data);
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

			while (startIndex < str.Length)
			{
				var c = str[startIndex];
				if (char.IsWhiteSpace(c))
				{
					// We've reached the end of a token run.
					break;
				}

				if ((c == '"' || c == '\'') && str[tokenBegin] != '@')
				{
					// Response files are a special case
					break;
				}

				startIndex++;
			}

			return str.Substring(tokenBegin, startIndex - tokenBegin);
		}

		private static bool TryGetValueForFlag(string str, string flagName, out string value,
			StringComparison comparison = StringComparison.InvariantCulture)
		{
			value = null;

			var startIndex = 0;
			while (startIndex >= 0)
			{
				var token = GetNextToken(str, ref startIndex);
				if (token == null)
				{
					return false;
				}

				if (token.StartsWith(flagName, comparison))
				{
					break;
				}
			}

			value = GetNextToken(str, ref startIndex);
			return true;
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

		private static List<string> GetLooseArguments(string str)
		{
			var startIndex = 0;
			var results = new List<string>();

			while (startIndex >= 0)
			{
				var token = GetNextToken(str, ref startIndex);
				if (token == null)
				{
					break;
				}

				if (IsFlag(token))
				{
					// Peek ahead to see if we need to skip the value.
					var testToken = startIndex;
					token = GetNextToken(str, ref testToken);
					if (token != null && !IsFlag(token))
					{
						startIndex = testToken;
					}

					
					continue;
				}

				results.Add(token);
			}

			return results;
		}

		private static bool IsFlag(string token)
		{
			return token.StartsWith("-") || token.StartsWith("/");
		}

		private static string FilterArgumentsWithValues(string str, params string[] toExclude)
		{
			var results = new List<string>();

			var startIndex = 0;
			while (startIndex >= 0)
			{
				var token = GetNextToken(str, ref startIndex);
				if (token == null)
				{
					break;
				}

				var keepToken = true;
				foreach (var exclude in toExclude)
				{
					if (token.StartsWith(exclude))
					{
						token = GetNextToken(str, ref startIndex);
						// For example "/Fp\"PathToFile\"".
						keepToken = false;
						break;
					}
				}

				if (keepToken)
				{
					results.Add(token);
				}
			}

			return string.Join(" ", results.ToArray());
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

		private static string RewriteCompilerArguments(string CompilerArguments, out string OutputFile, out List<string> InputFiles)
		{
			OutputFile = null;

			InputFiles = new List<string>();
			var Retokenized = new List<string>();
			bool AddedInputFileMarker = false;

			var parseIndex = 0;
			var token = "";
			while ((token = GetNextToken(CompilerArguments, ref parseIndex)) != null)
			{
				if (CompilerArguments.Contains(token))
				{
					Retokenized.Add(token);
					// Skip value;
					var value = GetNextToken(CompilerArguments, ref parseIndex);
					if (value == null)
					{
						throw new Exception($"Expected value after {token}");
					}

					if (token == "/Fo" || token == "/fo" || token == "-o")
					{
						Retokenized.Add("\"%2\"");
						OutputFile = value;
					}
					else
					{
						Retokenized.Add(value);
					}                   
				}
				else if (IsFlag(token))
				{
					Retokenized.Add(token);
				}
				else
				{
					InputFiles.Add(token);
					if (!AddedInputFileMarker)
					{
						AddedInputFileMarker = true;
						Retokenized.Add("\"%1\"");
					}                    
				}
			}

			return string.Join(" ", Retokenized.ToArray());
		}
	}

	static class FileStreamExtension
	{
		public static void WriteText(this FileStream stream, string StringToWrite)
		{
			var Bytes = new System.Text.UTF8Encoding(true).GetBytes(StringToWrite);
			stream.Write(Bytes, 0, Bytes.Length);
		}

		public static void WriteText(this FileStream stream, string StringToWrite, params object[] args)
		{
			var Bytes = new System.Text.UTF8Encoding(true).GetBytes(string.Format(StringToWrite, args));
			stream.Write(Bytes, 0, Bytes.Length);
		}
	}
}