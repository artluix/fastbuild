// Based on https://github.com/Quanwei1992/FASTBuild_UnrealEngine
//
// Path: Engine\Source\Programs\UnrealBuildTool\Executors\NotForLicensees\FASTBuild.cs
// UnrealBuildTool.csproj has detection for that

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using Tools.DotNETCommon;
using System.Text.RegularExpressions;

namespace UnrealBuildTool
{
	class FASTBuild : ActionExecutor
	{
		/*---- Configurable User settings ----*/

		// Used to specify a non-standard location for the FBuild.exe, for example if you have not added it to your PATH environment variable.
		public static string FBuildExePathOverride = "";

		// Controls network build distribution
		private bool bEnableDistribution = false;

		// Controls whether to use caching at all. CachePath and CacheMode are only relevant if this is enabled.
		private bool bEnableCaching = true;

		// Location of the shared cache, it could be a local or network path (i.e: @"\\DESKTOP-BEAST\FASTBuildCache").
		// Only relevant if bEnableCaching is true;
		private string CachePath = "D:\\Fastbuild\\Cache";

		public enum eCacheMode
		{
			ReadWrite, // This machine will both read and write to the cache
			ReadOnly,  // This machine will only read from the cache, use for developer machines when you have centralized build machines
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

			var integratedPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, @"..\Binaries\ThirdParty\FASTBuild", fbuild));
			if (File.Exists(integratedPath))
			{
				FBuildExePathOverride = integratedPath;
				Console.WriteLine($"Using integrated FBuild at {integratedPath}");
				return true;
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

		private HashSet<string> ForceLocalCompileModules = new HashSet<string>(){ "Module.ProxyLODMeshReduction", "GoogleVRController" };

		private enum FBBuildType
		{
			Windows,
			XBOne,
			PS4,
			PS5
		}

		private FBBuildType BuildType = FBBuildType.Windows;

		private void DetectBuildType(List<Action> Actions)
		{
			foreach (Action action in Actions)
			{
				if (action.ActionType != ActionType.Compile && action.ActionType != ActionType.Link)
					continue;

				if (action.CommandPath.FullName.Contains("orbis"))
				{
					BuildType = FBBuildType.PS4;
					return;
				}
				else if (action.CommandPath.FullName.Contains("prospero"))
				{
					BuildType = FBBuildType.PS5;
					return;
				}
				else if (action.CommandArguments.Contains("Intermediate\\Build\\XboxOne"))
				{
					BuildType = FBBuildType.XBOne;
					return;
				}
				else if (action.CommandPath.FullName.Contains("Microsoft")) //Not a great test.
				{
					BuildType = FBBuildType.Windows;
					return;
				}
			}
		}

		private bool IsMSVC() { return BuildType == FBBuildType.Windows || BuildType == FBBuildType.XBOne; }

		private string GetCompilerName()
		{
			switch (BuildType)
			{
				default:
				case FBBuildType.XBOne:
				case FBBuildType.Windows: return "UE4Compiler";
				case FBBuildType.PS4: return "UE4PS4Compiler";
				case FBBuildType.PS5: return "UE4PS5Compiler";
			}
		}

		//Run FASTBuild on the list of actions. Relies on fbuild.exe being in the path.
		public override bool ExecuteActions(List<Action> Actions, bool bLogDetailedActionStats)
		{
			if (Actions.Count == 0)
				return true;
			
			DetectBuildType(Actions);

			string FASTBuildFilePath = Path.Combine(UnrealBuildTool.EngineDirectory.FullName, "Intermediate", "Build", "fbuild.bff");

			if (!CreateBffFile(Actions, FASTBuildFilePath))
				return false;

			return ExecuteBffFile(FASTBuildFilePath);
		}

		FileStream bffOutputFileStream;
		private void AddText(string StringToWrite)
		{
			byte[] Info = new System.Text.UTF8Encoding(true).GetBytes(StringToWrite);
			bffOutputFileStream.Write(Info, 0, Info.Length);
		}


		private string SubstituteEnvironmentVariables(string commandLineString)
		{
			string outputString = commandLineString
				.Replace("$(DurangoXDK)", "$DurangoXDK$") // Xbox ? SDK
				.Replace("$(SCE_ORBIS_SDK_DIR)", "$SCE_ORBIS_SDK_DIR$") // PS4 SDK
				.Replace("$(SCE_PROSPERO_SDK_DIR)", "SCE_PROSPERO_SDK_DIR") // PS5 SDK
				.Replace("$(DXSDK_DIR)", "$DXSDK_DIR$")
				.Replace("$(CommonProgramFiles)", "$CommonProgramFiles$");
			return outputString;
		}

		private void ParseOriginalCommandPathAndArguments(Action Action, ref string CommandPath, ref string CommandArguments)
		{
			if (Action.CommandPath.FullName.EndsWith("cl-filter.exe"))
			{
				// In case of using cl-filter.exe we have to extract original command path and arguments
				// Command line has format {Action.DependencyListFile.Location} -- {Action.CommandPath} {Action.CommandArguments} /showIncludes
				int SeparatorIndex = Action.CommandArguments.IndexOf("-- ");
				string CommandPathAndArguments = Action.CommandArguments.Substring(SeparatorIndex + 3).Replace("/showIncludes", "");
				List<string> Tokens = CommandLineParser.Parse(CommandPathAndArguments);
				CommandPath = Tokens[0];
				CommandArguments = string.Join(" ", Tokens.GetRange(1, Tokens.Count - 1));
			}
			else // other actions will be passed as they are
			{
				CommandPath = Action.CommandPath.FullName;
				CommandArguments = Action.CommandArguments;
			}
		}

		private Dictionary<string, string> ParseCommandArguments(string CommandArguments, string[] SpecialOptions, bool SkipInputFile = false)
		{
			CommandArguments = SubstituteEnvironmentVariables(CommandArguments);
			List<string> Tokens = CommandLineParser.Parse(CommandArguments);
			Dictionary<string, string> ParsedCompilerOptions = new Dictionary<string, string>();

			// Replace response file with its content
			for (int i = 0; i < Tokens.Count; i++)
			{
				if (!Tokens[i].StartsWith("@\""))
				{
					continue;
				}

				string ResponseFilePath = Tokens[i].Substring(2, Tokens[i].Length - 3);
				string ResponseFileText = SubstituteEnvironmentVariables(File.ReadAllText(ResponseFilePath));

				Tokens.RemoveAt(i);
				Tokens.InsertRange(i, CommandLineParser.Parse(ResponseFileText));

				if (ParsedCompilerOptions.ContainsKey("@"))
				{
					throw new Exception("Only one response file expected");
				}

				ParsedCompilerOptions["@"] = ResponseFilePath;
			}

			// Search tokens for special options
			foreach (string SpecialOption in SpecialOptions)
			{
				for (int i = 0; i < Tokens.Count; ++i)
				{
					if (Tokens[i] == SpecialOption && i + 1 < Tokens.Count)
					{
						ParsedCompilerOptions[SpecialOption] = Tokens[i + 1];
						Tokens.RemoveRange(i, 2);
						break;
					}
					else if (Tokens[i].StartsWith(SpecialOption))
					{
						ParsedCompilerOptions[SpecialOption] = Tokens[i].Replace(SpecialOption, null);
						Tokens.RemoveAt(i);
						break;
					}
				}
			}

			//The search for the input file... we take the first non-argument we can find
			if (!SkipInputFile)
			{
				for (int i = 0; i < Tokens.Count; ++i)
				{
					string Token = Tokens[i];
					// Skip tokens with values, I for cpp includes, l for resource compiler includes
					if (new[] { "/I", "/l", "/D", "-D", "-x", "-include" }.Contains(Token))
					{
						++i;
					}
					else if (!Token.StartsWith("/") && !Token.StartsWith("-") && !Token.StartsWith("\"-"))
					{
						ParsedCompilerOptions["InputFile"] = Token;
						Tokens.RemoveAt(i);
						break;
					}
				}
			}

			ParsedCompilerOptions["OtherOptions"] = string.Join(" ", Tokens) + " ";

			return ParsedCompilerOptions;
		}

		private bool AreActionsSorted(List<Action> InActions)
		{
			for (int ActionIndex = 0; ActionIndex < InActions.Count; ActionIndex++)
			{
				foreach (Action PrerequisiteAction in InActions[ActionIndex].PrerequisiteActions)
				{
					if (ActionIndex < InActions.IndexOf(PrerequisiteAction))
					{
						return false;
					}
				}
			}

			return true;
		}

		private void AddActionSorted(Action ActionToAdd, List<Action> Actions, HashSet<Action> AddedActions, Stack<Action> DependencyChain)
		{
			DependencyChain.Push(ActionToAdd);

			foreach (Action PrerequisiteAction in ActionToAdd.PrerequisiteActions)
			{
				if (DependencyChain.Contains(PrerequisiteAction))
				{
					Log.TraceError("Action is not topologically sorted.");
					Log.TraceError($"  {ActionToAdd.CommandPath} {ActionToAdd.CommandArguments}");
					Log.TraceError("Dependency");
					Log.TraceError($"  {PrerequisiteAction.CommandPath} {PrerequisiteAction.CommandArguments}");
					throw new BuildException("Cyclical Dependency in action graph.");
				}

				AddActionSorted(PrerequisiteAction, Actions, AddedActions, DependencyChain);
			}

			DependencyChain.Pop();

			if (!AddedActions.Contains(ActionToAdd))
			{
				AddedActions.Add(ActionToAdd);
				Actions.Add(ActionToAdd);
			}
		}

		private List<Action> SortActions(List<Action> InActions)
		{
			if (AreActionsSorted(InActions))
			{
				return InActions;
			}

			List<Action> Actions = new List<Action>();
			HashSet<Action> AddedActions = new HashSet<Action>();
			foreach (Action Action in InActions)
			{
				if (!AddedActions.Contains(Action))
				{
					AddActionSorted(Action, Actions, AddedActions, new Stack<Action>());
				}
			}

			if (!Actions.All(A => InActions.Contains(A)))
			{
				throw new BuildException("Prerequisite actions not in source list.");
			}

			return Actions;
		}

		private string GetOptionValue(Dictionary<string, string> OptionsDictionary, string Key, Action Action, bool ProblemIfNotFound = false)
		{
			string Value = string.Empty;
			if (OptionsDictionary.TryGetValue(Key, out Value))
			{
				return Value.Trim(new Char[] { '\"' });
			}

			if (ProblemIfNotFound)
			{
				Log.TraceError("We failed to find " + Key + ", which may be a problem.");
				Log.TraceError("Action.CommandArguments: " + Action.CommandArguments);
			}

			return Value;
		}

		private void WriteEnvironmentSetup()
		{
			DirectoryReference VCInstallDir = null;
			string VCToolPath64 = "";
			VCEnvironment VCEnv = null;

			try
			{
				 VCEnv = VCEnvironment.Create(WindowsPlatform.GetDefaultCompiler(null),UnrealTargetPlatform.Win64, WindowsArchitecture.x64, null, null,null);
			}
			catch (Exception)
			{
				Log.TraceError("Failed to get Visual Studio environment.");
			}

			// Copy environment into a case-insensitive dictionary for easier key lookups
			Dictionary<string, string> envVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
			{
				envVars[(string)entry.Key] = (string)entry.Value;
			}

			if (envVars.ContainsKey("CommonProgramFiles"))
			{
				AddText("#import CommonProgramFiles\n");
			}

			if (envVars.ContainsKey("DXSDK_DIR"))
			{
				AddText("#import DXSDK_DIR\n");
			}

			if (envVars.ContainsKey("DurangoXDK")) // Xbox SDK
			{
				AddText("#import DurangoXDK\n");
			}

			if (VCEnv != null)
			{
				string platformVersionNumber = "VSVersionUnknown";

				switch (VCEnv.Compiler)
				{

					case WindowsCompiler.VisualStudio2017:
						// For now we are working with the 140 version, might need to change to 141 or 150 depending on the version of the Toolchain you chose
						// to install
						platformVersionNumber = "140";
						break;

					case WindowsCompiler.VisualStudio2019:
						platformVersionNumber = "140";
						break;

					default:
						string exceptionString = "Error: Unsupported Visual Studio Version.";
						Log.TraceError(exceptionString);
						throw new BuildException(exceptionString);
				}


				if (!WindowsPlatform.TryGetVSInstallDir(WindowsPlatform.GetDefaultCompiler(null), out VCInstallDir))
				{
					string exceptionString = "Error: Cannot locate Visual Studio Installation.";
					Log.TraceError(exceptionString);
					throw new BuildException(exceptionString);
				}

				VCToolPath64 = VCEnv.CompilerPath.Directory.ToString() + "\\";

				AddText($".WindowsSDKBasePath = '{VCEnv.WindowsSdkDir}'\n");

				AddText("Compiler('UE4ResourceCompiler') \n{\n");
				AddText($"\t.Executable = '{VCEnv.ResourceCompilerPath}'\n");
				AddText("\t.CompilerFamily = 'custom'\n");
				AddText("}\n\n");

				AddText("Compiler('UE4Compiler') \n{\n");

				AddText($"\t.Root = '{VCEnv.CompilerPath.Directory}'\n");
				AddText("\t.Executable = '$Root$/cl.exe'\n");
				AddText("\t.ExtraFiles =\n\t{\n");
				AddText("\t\t'$Root$/c1.dll'\n");
				AddText("\t\t'$Root$/c1xx.dll'\n");
				AddText("\t\t'$Root$/c2.dll'\n");

				if (File.Exists(FileReference.Combine(VCEnv.CompilerPath.Directory, "1033/clui.dll").ToString())) //Check English first...
				{
					AddText("\t\t'$Root$/1033/clui.dll'\n");
				}
				else
				{
					var numericDirectories = Directory.GetDirectories(VCToolPath64).Where(d => Path.GetFileName(d).All(char.IsDigit));
					var cluiDirectories = numericDirectories.Where(d => Directory.GetFiles(d, "clui.dll").Any());
					if (cluiDirectories.Any())
					{
						string CluiDirectory = Path.GetFileName(cluiDirectories.First());
						AddText($"\t\t'$Root$/{CluiDirectory}/clui.dll'\n");
					}
				}
				AddText("\t\t'$Root$/mspdbsrv.exe'\n");
				AddText("\t\t'$Root$/mspdbcore.dll'\n");

				AddText($"\t\t'$Root$/mspft{platformVersionNumber}.dll'\n");
				AddText($"\t\t'$Root$/msobj{platformVersionNumber}.dll'\n");
				AddText($"\t\t'$Root$/mspdb{platformVersionNumber}.dll'\n");

				var redistDirs = Directory.GetDirectories(VCInstallDir.ToString() + "\\VC\\Redist\\MSVC\\", "*", SearchOption.TopDirectoryOnly);

				if (redistDirs.Length > 0)
				{
					Regex regex = new Regex(@"\d{2}\.\d{2}\.\d{5}$");
					string redistDir = redistDirs.First((s) =>
					{
						return regex.IsMatch(s);
					});
					if (VCEnv.Compiler == WindowsCompiler.VisualStudio2019)
					{
						AddText($"\t\t'{redistDir}/x64/Microsoft.VC142.CRT/msvcp{platformVersionNumber}.dll'\n");
						AddText($"\t\t'{redistDir}/x64/Microsoft.VC142.CRT/vccorlib{platformVersionNumber}.dll'\n");
						AddText("\t\t'$Root$/tbbmalloc.dll'\n");
					}
					else if (VCEnv.Compiler == WindowsCompiler.VisualStudio2017)
					{
						// VS 2017 is really confusing in terms of version numbers and paths so these values might need to be modified depending on what version of the tool chain you
						// chose to install.
						AddText($"\t\t'{redistDir}/x64/Microsoft.VC141.CRT/msvcp{platformVersionNumber}.dll'\n");
						AddText($"\t\t'{redistDir}/x64/Microsoft.VC141.CRT/vccorlib{platformVersionNumber}.dll'\n");
					}
				}

				AddText("\t}\n"); // End extra files

				AddText("}\n\n"); // End compiler
			}

			if (envVars.ContainsKey("SCE_ORBIS_SDK_DIR")) // PS4 SDK
			{
				AddText($".SCE_ORBIS_SDK_DIR = '{envVars["SCE_ORBIS_SDK_DIR"]}'\n");
				AddText($".PS4BasePath = '{envVars["SCE_ORBIS_SDK_DIR"]}/host_tools/bin'\n\n");
				AddText("Compiler('UE4PS4Compiler') \n{\n");
				AddText("\t.Executable = '$PS4BasePath$/orbis-clang.exe'\n");
				// AddText("\t.ExtraFiles = '$PS4BasePath$/orbis-snarl.exe'\n");
				AddText("}\n\n");
			}
			if (envVars.ContainsKey("SCE_PROSPERO_SDK_DIR")) // PS5 SDK
			{
				AddText($".SCE_PROSPERO_SDK_DIR = '{envVars["SCE_PROSPERO_SDK_DIR"]}'\n");
				AddText($".PS5BasePath = '{envVars["SCE_PROSPERO_SDK_DIR"]}/host_tools/bin'\n\n");
				AddText("Compiler('UE4PS5Compiler') \n{\n");
				AddText("\t.Executable = '$PS5BasePath$/prospero-clang.exe'\n");
				AddText("}\n\n");
			}

			AddText("Settings \n{\n");

			// Optional cachePath user setting
			if (bEnableCaching && CachePath != "")
			{
				AddText($"\t.CachePath = '{CachePath}'\n");
			}

			//Start Environment
			AddText("\t.Environment = \n\t{\n");
			if (VCEnv != null)
			{
				AddText($"\t\t\"PATH={VCInstallDir.ToString()}\\Common7\\IDE\\;{VCToolPath64};{VCEnv.ResourceCompilerPath.Directory}\",\n");
				if (VCEnv.IncludePaths.Count() > 0)
				{
					string JoinedIncludePaths = String.Join(";", VCEnv.IncludePaths.Select(x => x));
					AddText($"\t\t\"INCLUDE={JoinedIncludePaths}\",\n");
				}

				if (VCEnv.LibraryPaths.Count() > 0)
				{
					string JoinedLibs = String.Join(";", VCEnv.LibraryPaths.Select(x => x));
					AddText($"\t\t\"LIB={JoinedLibs}\",\n");
				}
			}
			if (envVars.ContainsKey("TMP"))
				AddText($"\t\t\"TMP={envVars["TMP"]}\",\n");
			if (envVars.ContainsKey("SystemRoot"))
				AddText($"\t\t\"SystemRoot={envVars["SystemRoot"]}\",\n");
			if (envVars.ContainsKey("INCLUDE"))
				AddText($"\t\t\"INCLUDE={envVars["INCLUDE"]}\",\n");
			if (envVars.ContainsKey("LIB"))
				AddText($"\t\t\"LIB={envVars["LIB"]}\",\n");

			AddText("\t}\n"); // End environment

			AddText("}\n\n"); // End Settings
		}

		private void AddCompileAction(Action Action, int ActionIndex, List<int> DependencyIndices)
		{
			string CompilerName = GetCompilerName();
			if (Action.CommandPath.FullName.Contains("rc.exe"))
			{
				CompilerName = "UE4ResourceCompiler";
			}

			string OriginalCommandPath = null;
			string OriginalCommandArguments = null;
			ParseOriginalCommandPathAndArguments(Action, ref OriginalCommandPath, ref OriginalCommandArguments);

			string[] SpecialCompilerOptions = { "/Fo", "/fo", "/Yc", "/Yu", "/Fp", "-o" };
			var ParsedCompilerOptions = ParseCommandArguments(OriginalCommandArguments, SpecialCompilerOptions);

			string OutputObjectFileName = null;
			if (IsMSVC())
			{
				OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, "/Fo", Action, ProblemIfNotFound: false);
				if (string.IsNullOrEmpty(OutputObjectFileName))
					OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, "/fo", Action, ProblemIfNotFound: true);
			}
			else
			{
				OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, "-o", Action, ProblemIfNotFound: true);
			}

			if (string.IsNullOrEmpty(OutputObjectFileName)) //No /Fo or /fo, we're probably in trouble.
			{
				Console.WriteLine("We have no OutputObjectFileName. Bailing.");
				return;
			}

			if (string.IsNullOrEmpty(OutputObjectFileName)) // Didn't find /Fo, try /fo
			{
				OutputObjectFileName = GetOptionValue(ParsedCompilerOptions, "/fo", Action, ProblemIfNotFound: true);
			}

			if (string.IsNullOrEmpty(OutputObjectFileName)) //No /Fo or /fo, we're probably in trouble.
			{
				Log.TraceError("We have no OutputObjectFileName. Bailing.");
				return;
			}

			string IntermediatePath = Path.GetDirectoryName(OutputObjectFileName);
			if (string.IsNullOrEmpty(IntermediatePath))
			{
				Log.TraceError("We have no IntermediatePath. Bailing.");
				Log.TraceError("Our Action.CommandArguments were: " + Action.CommandArguments);
				return;
			}

			string InputFile = GetOptionValue(ParsedCompilerOptions, "InputFile", Action, ProblemIfNotFound: true);
			if (string.IsNullOrEmpty(InputFile))
			{
				Log.TraceError("We have no InputFile. Bailing.");
				return;
			}

			AddText($"; \"{Action.CommandPath}\" {Action.CommandArguments}\n");
			AddText($"ObjectList('Action_{ActionIndex}')\n{{\n");
			AddText($"\t.Compiler = '{CompilerName}' \n");
			AddText($"\t.CompilerInputFiles = \"{InputFile}\"\n");
			AddText($"\t.CompilerOutputPath = \"{IntermediatePath}\"\n");
			AddText($"\t.WorkingDir = \"{Action.WorkingDirectory}\"\n");
			if(Action.DependencyListFile != null)
			{
				AddText($"\t.DependenciesListOutFile = \"{Action.DependencyListFile.FullName}\"\n");
			}

			bool bSkipDistribution = false;
			foreach (var it in ForceLocalCompileModules)
			{
				if (Path.GetFullPath(InputFile).Contains(it))
				{
					bSkipDistribution = true;
					break;
				}
			}

			if (!Action.bCanExecuteRemotely || !Action.bCanExecuteRemotelyWithSNDBS || bSkipDistribution/* || ParsedCompilerOptions.ContainsKey("/Yu")*/) // TODO: test
			{
				AddText("\t.AllowDistribution = false\n");
			}

			string OtherCompilerOptions = GetOptionValue(ParsedCompilerOptions, "OtherOptions", Action);

			if (IsMSVC())
			{
				OtherCompilerOptions = OtherCompilerOptions.Replace("we4668", "wd4668");
			}
			else
			{
				OtherCompilerOptions = OtherCompilerOptions.Replace("-Wundef", "-Wno-undef");
			}

			string CompilerOutputExtension = ".unset";

			if (ParsedCompilerOptions.ContainsKey("/Yc")) // Create PCH (MSVC)
			{
				string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yc", Action, ProblemIfNotFound: true);
				string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);

				AddText($"\t.CompilerOptions = '\"%1\" /Fo\"%2\" /Fp\"{PCHOutputFile}\" /Yu\"{PCHIncludeHeader}\" {OtherCompilerOptions} '\n");

				AddText($"\t.PCHOptions = '\"%1\" /Fp\"%2\" /Yc\"{PCHIncludeHeader}\" {OtherCompilerOptions} /Fo\"{OutputObjectFileName}\"'\n");
				AddText($"\t.PCHInputFile = \"{InputFile}\"\n");
				AddText($"\t.PCHOutputFile = \"{PCHOutputFile}\"\n");
				CompilerOutputExtension = ".obj";
			}
			else if (ParsedCompilerOptions.ContainsKey("/Yu")) // Use PCH (MSVC)
			{
				string PCHIncludeHeader = GetOptionValue(ParsedCompilerOptions, "/Yu", Action, ProblemIfNotFound: true);
				string PCHOutputFile = GetOptionValue(ParsedCompilerOptions, "/Fp", Action, ProblemIfNotFound: true);
				string PCHToForceInclude = PCHOutputFile.Replace(".pch", "");
				AddText($"\t.CompilerOptions = '\"%1\" /Fo\"%2\" /Fp\"{PCHOutputFile}\" /Yu\"{PCHIncludeHeader}\" /FI\"{PCHToForceInclude}\" {OtherCompilerOptions} '\n");
				CompilerOutputExtension = ".obj";
			}
			else
			{
				if (CompilerName == "UE4ResourceCompiler")
				{
					AddText($"\t.CompilerOptions = '{OtherCompilerOptions} /fo\"%2\" \"%1\" '\n");
					CompilerOutputExtension = ".res";
				}
				else
				{
					if (IsMSVC())
					{
						AddText($"\t.CompilerOptions = '{OtherCompilerOptions} /Fo\"%2\" \"%1\" '\n");
						CompilerOutputExtension = ".obj";
					}
					else
					{
						AddText($"\t.CompilerOptions = '{OtherCompilerOptions} -o \"%2\" \"%1\" '\n");
						CompilerOutputExtension = ".o";
					}
				}
			}

			AddText($"\t.CompilerOutputKeepBaseExtension = true\n");
			AddText($"\t.CompilerOutputExtension = '{CompilerOutputExtension}' \n");

			if (DependencyIndices.Count > 0)
			{
				List<string> DependencyNames = DependencyIndices.ConvertAll(Idx => $"'Action_{Idx}'");
				string DependencyNamesJoined = string.Join(",", DependencyNames.ToArray());
				AddText($"\t.PreBuildDependencies = {{ {DependencyNamesJoined} }}\n");
			}

			AddText("}\n\n");
		}

		private void AddExecNode(Action Action,int ActionIndex,List<int> DependencyIndices)
		{
			AddText($"; \"{Action.CommandPath}\" {Action.CommandArguments}\n");
			AddText($"Exec('Action_{ActionIndex}')\n");
			AddText("{\n");

			AddText($"\t.ExecExecutable = '{Action.CommandPath}'\n");
			AddText($"\t.ExecArguments = '{Action.CommandArguments}'\n");
			AddText($"\t.ExecWorkingDir = '{Action.WorkingDirectory}'\n");

			AddText($"\t.ExecOutput = \"{Action.ProducedItems[0]}\"\n");
			AddText("\t.ExecAlways = true\n");

			AddText("\t.PreBuildDependencies = {\n");
			foreach (var index in DependencyIndices)
			{
				AddText($"\t\t\"Action_{index}\"\n");
			}
			AddText("\t}\n"); // END OF PreBuildDependencies

			AddText("}\n"); // END OF Exec
		}

		private bool CreateBffFile(List<Action> InActions, string BffFilePath)
		{
			List<Action> Actions = SortActions(InActions);

			try
			{
				bffOutputFileStream = new FileStream(BffFilePath, FileMode.Create, FileAccess.Write);

				WriteEnvironmentSetup(); //Compiler, environment variables and base paths

				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					Action Action = Actions[ActionIndex];

					// Resolve dependencies
					List<int> DependencyIndices = new List<int>();
					foreach (Action PrerequisiteAction in Action.PrerequisiteActions)
					{
						DependencyIndices.Add(Actions.IndexOf(PrerequisiteAction));
					}

					if (Action.ActionType == ActionType.Compile && Action.bCanExecuteRemotely && Action.bCanExecuteRemotelyWithSNDBS)
					{
						AddCompileAction(Action, ActionIndex, DependencyIndices);
					}
					else
					{
						AddExecNode(Action, ActionIndex, DependencyIndices);
					}
				}

				AddText("Alias( 'all' ) \n{\n");
				AddText("\t.Targets = { \n");

				for (int ActionIndex = 0; ActionIndex < Actions.Count; ActionIndex++)
				{
					AddText($"\t\t'Action_{ActionIndex}',\n");
				}
				if (Actions.Count > 0)
				{
					AddText("\t}\n");
				}

				AddText("}\n"); // END Targets

				bffOutputFileStream.Close();
			}
			catch (Exception e)
			{
				Log.TraceError("Exception while creating bff file: " + e.ToString());
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
						cacheArgument = "-cacheread -cacheverbose";
						break;
					case eCacheMode.WriteOnly:
						cacheArgument = "-cachewrite -cacheverbose";
						break;
					case eCacheMode.ReadWrite:
						cacheArgument = "-cache -cacheverbose";
						break;
				}
			}

			string distArgument = bEnableDistribution ? "-dist" : "";

			//Interesting flags for FASTBuild: -nostoponerror, -verbose, -monitor (if FASTBuild Monitor Visual Studio Extension is installed!)
			// Yassine: The -clean is to bypass the FastBuild internal dependencies checks (cached in the fdb) as it could create some conflicts with UBT.
			//			Basically we want FB to stupidly compile what UBT tells it to.
			string FBCommandLine = $"-monitor -summary -clean {distArgument} {cacheArgument} -ide -config \"{BffFilePath}\"";

			ProcessStartInfo FBStartInfo = new ProcessStartInfo(string.IsNullOrEmpty(FBuildExePathOverride) ? "fbuild" : FBuildExePathOverride, FBCommandLine);

			FBStartInfo.UseShellExecute = false;
			FBStartInfo.WorkingDirectory = Path.Combine(UnrealBuildTool.EngineDirectory.MakeRelativeTo(DirectoryReference.GetCurrentDirectory()), "Source");

			try
			{
				Process FBProcess = new Process();
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
			catch (Exception e)
			{
				Log.TraceError("Exception launching fbuild process. Is it in your path?" + e.ToString());
				return false;
			}
		}
	}

	class CommandLineParser
	{
		enum ParserState
		{
			OutsideToken,
			InsideToken,
			InsideTokenQuotes,
		}

		public static List<string> Parse(string CommandLine)
		{
			List<string> Tokens = new List<string>();

			ParserState State = ParserState.OutsideToken;
			int Cursor = 0;
			int TokenStartPos = 0;
			while (Cursor < CommandLine.Length)
			{
				char c = CommandLine[Cursor];
				if (State == ParserState.OutsideToken)
				{
					if (c == ' ' || c == '\r' || c == '\n')
					{
						Cursor++;
					}
					else
					{
						TokenStartPos = Cursor;
						State = ParserState.InsideToken;
					}
				}
				else if (State == ParserState.InsideToken)
				{
					if (c == '\\')
					{
						Cursor += 2;
					}
					else if (c == '"')
					{
						State = ParserState.InsideTokenQuotes;
						Cursor++;
					}
					else if (c == ' ' || c == '\r' || c == '\n')
					{
						Tokens.Add(CommandLine.Substring(TokenStartPos, Cursor - TokenStartPos));
						State = ParserState.OutsideToken;
					}
					else
					{
						Cursor++;
					}
				}
				else if (State == ParserState.InsideTokenQuotes)
				{
					if (c == '\\')
					{
						Cursor++;
					}
					else if (c == '"')
					{
						State = ParserState.InsideToken;
					}

					Cursor++;
				}
				else
				{
					throw new NotImplementedException();
				}
			}

			if (State == ParserState.InsideTokenQuotes)
			{
				throw new Exception("Failed to parse command line, no closing quotes found: " + CommandLine);
			}

			if (State == ParserState.InsideToken)
			{
				Tokens.Add(CommandLine.Substring(TokenStartPos));
			}

			return Tokens;
		}
	}
}
