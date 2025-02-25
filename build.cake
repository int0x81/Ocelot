﻿#tool dotnet:?package=GitVersion.Tool&version=5.12.0 // 6.0.0-beta.7 supports .NET 8, 7, 6
#tool dotnet:?package=coveralls.net&version=4.0.1
#tool nuget:?package=ReportGenerator&version=5.2.4
#addin nuget:?package=Newtonsoft.Json&version=13.0.3
#addin nuget:?package=System.Text.Encodings.Web&version=8.0.0
#addin nuget:?package=Cake.Coveralls&version=4.0.0

#r "Spectre.Console"
using Spectre.Console

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

const string Release = "Release"; // task name, target, and Release config name
const string AllFrameworks = "net6.0;net7.0;net8.0";
const string LatestFramework = "net8.0";

var compileConfig = Argument("configuration", Release); // compile

// build artifacts
var artifactsDir = Directory("artifacts");

// unit testing
var artifactsForUnitTestsDir = artifactsDir + Directory("UnitTests");
var unitTestAssemblies = @"./test/Ocelot.UnitTests/Ocelot.UnitTests.csproj";
var minCodeCoverage = 0.80d;
var coverallsRepoToken = "OCELOT_COVERALLS_TOKEN";
var coverallsRepo = "https://coveralls.io/github/ThreeMammals/Ocelot";

// acceptance testing
var artifactsForAcceptanceTestsDir = artifactsDir + Directory("AcceptanceTests");
var acceptanceTestAssemblies = @"./test/Ocelot.AcceptanceTests/Ocelot.AcceptanceTests.csproj";

// integration testing
var artifactsForIntegrationTestsDir = artifactsDir + Directory("IntegrationTests");
var integrationTestAssemblies = @"./test/Ocelot.IntegrationTests/Ocelot.IntegrationTests.csproj";

// benchmark testing
var artifactsForBenchmarkTestsDir = artifactsDir + Directory("BenchmarkTests");
var benchmarkTestAssemblies = @"./test/Ocelot.Benchmarks";

// packaging
var packagesDir = artifactsDir + Directory("Packages");
var artifactsFile = packagesDir + File("artifacts.txt");
var releaseNotesFile = packagesDir + File("ReleaseNotes.md");
var releaseNotes = new List<string>();

// internal build variables - don't change these.
string committedVersion = "0.0.0-dev";
GitVersion versioning = null;
bool IsTechnicalRelease = false;

var target = Argument("target", "Default");
var slnFile = (target == Release) ? $"./Ocelot.{Release}.sln" : "./Ocelot.sln";
Information("\nTarget: " + target);
Information("Build: " + compileConfig);
Information("Solution: " + slnFile);

TaskTeardown(context => {
	AnsiConsole.Markup($"[green]DONE[/] {context.Task.Name}\n");
});

Task("Default")
	.IsDependentOn("Build");

Task("Build")
	.IsDependentOn("RunTests");

Task("ReleaseNotes")
	.IsDependentOn("CreateReleaseNotes");

Task("RunTests")
	.IsDependentOn("RunUnitTests")
	.IsDependentOn("RunAcceptanceTests")
	.IsDependentOn("RunIntegrationTests");

Task(Release)
	.IsDependentOn("Build")
	.IsDependentOn("CreateReleaseNotes")
	.IsDependentOn("CreateArtifacts")
	.IsDependentOn("PublishGitHubRelease")
    .IsDependentOn("PublishToNuget");

Task("Compile")
	.IsDependentOn("Clean")
	.IsDependentOn("Version")
	.Does(() =>
	{	
		Information("Build: " + compileConfig);
		Information("Solution: " + slnFile);
		var settings = new DotNetBuildSettings
		{
			Configuration = compileConfig,
		};
		if (target != Release)
		{
			settings.Framework = LatestFramework; // build using .NET 8 SDK only
		}
		string frameworkInfo = string.IsNullOrEmpty(settings.Framework) ? AllFrameworks : settings.Framework;
		Information($"Settings {nameof(DotNetBuildSettings.Framework)}: {frameworkInfo}");
		Information($"Settings {nameof(DotNetBuildSettings.Configuration)}: {settings.Configuration}");
		DotNetBuild(slnFile, settings);
	});

Task("Clean")
	.Does(() =>
	{
        if (DirectoryExists(artifactsDir))
        {
            DeleteDirectory(artifactsDir, new DeleteDirectorySettings {
				Recursive = true,
				Force = true
			});
        }
        CreateDirectory(artifactsDir);
	});

Task("Version")
	.Does(() =>
	{
		versioning = GetNuGetVersionForCommit();
		var nugetVersion = versioning.NuGetVersion;
		Information("SemVer version number: " + nugetVersion);

		if (IsRunningOnCircleCI())
		{
			Information("Persisting version number...");
			PersistVersion(committedVersion, nugetVersion);
		}
		else
		{
			Information("We are not running on build server, so we won't persist the version number.");
		}
	});

Task("GitLogUniqContributors")
	.Does(() =>
	{
		var command = "log --format=\"%aN|%aE\" ";
		// command += IsRunningOnCircleCI() ? "| sort | uniq" :
		// 	IsRunningInPowershell() ? "| Sort-Object -Unique" : "| sort | uniq";
		List<string> output = GitHelper(command);
		output.Sort();
		List<string> contributors = output.Distinct().ToList();
		contributors.Sort();
        Information($"Detected {contributors.Count} unique contributors:");
        Information(string.Join(Environment.NewLine, contributors));
		// TODO Search example in bash: curl -L -H "X-GitHub-Api-Version: 2022-11-28"   "https://api.github.com/search/users?q=Chris+Swinchatt"
        Information(Environment.NewLine + "Unicode test: 1) Raynald Messié; 2) 彭伟 pengweiqhca");
        AnsiConsole.Markup("Unicode test: 1) Raynald Messié; 2) 彭伟 pengweiqhca" + Environment.NewLine);
		// Powershell life hack: $OutputEncoding = [Console]::InputEncoding = [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding
		// https://stackoverflow.com/questions/40098771/changing-powershells-default-output-encoding-to-utf-8
		// https://stackoverflow.com/questions/49476326/displaying-unicode-in-powershell/49481797#49481797
		// https://stackoverflow.com/questions/57131654/using-utf-8-encoding-chcp-65001-in-command-prompt-windows-powershell-window/57134096#57134096
	});

Task("CreateReleaseNotes")
	.IsDependentOn("Version")
	//.IsDependentOn("GitLogUniqContributors")
	.Does(() =>
	{
        Information($"Generating release notes at {releaseNotesFile}");
        var lastReleaseTags = GitHelper("describe --tags --abbrev=0 --exclude net*");
        var lastRelease = lastReleaseTags.First(t => !t.StartsWith("net")); // skip 'net*-vX.Y.Z' tag and take 'major.minor.build'
        var releaseVersion = versioning.NuGetVersion;

        // Read main header from Git file, substitute version in header, and add content further...
        Information("{0}  New release tag is " + releaseVersion);
        Information("{1} Last release tag is " + lastRelease);
        var body = System.IO.File.ReadAllText("./ReleaseNotes.md");
        var releaseHeader = string.Format(body, releaseVersion, lastRelease);
        releaseNotes = new List<string> { releaseHeader };
        if (IsTechnicalRelease)
        {
            WriteReleaseNotes();
            return;
        }

        const bool debugUserEmail = false;
        var shortlogSummary = GitHelper($"shortlog --no-merges --numbered --summary --email {lastRelease}..HEAD")
            .ToList();
        var re = new Regex(@"^[\s\t]*(?'commits'\d+)[\s\t]+(?'author'.*)[\s\t]+<(?'email'.*)>.*$");
        static SummaryItem CreateSummaryItem(System.Text.RegularExpressions.Match m) => new()
        {
            Commits = int.Parse(m.Groups["commits"]?.Value ?? "0"),
            Author = m.Groups["author"]?.Value?.Trim() ?? string.Empty,
            Email = m.Groups["email"]?.Value?.Trim() ?? string.Empty,
        };
        var summary = shortlogSummary
            .Where(x => re.IsMatch(x))
            .Select(x => re.Match(x))
            .Select(CreateSummaryItem)
            .ToList();

        // Starring aka Release Influencers
        var starring = new List<string>();
        string CreateStars(int count, string name)
        {
            var contributor = summary.Find(x => x.Author.Equals(name));
            var stars = string.Join(string.Empty, Enumerable.Repeat(":star:", count));
            var emailInfo = debugUserEmail ? ", " + contributor.Email : string.Empty;
            return $"{stars}  {contributor.Author}{emailInfo}";
        }

        Information("------==< Old Starring >==------");
        foreach (var contributor in summary)
        {
            starring.Add(CreateStars(contributor.Commits, contributor.Author));
        }
        Information(string.Join(Environment.NewLine, starring));

        var commitsGrouping = summary
            .GroupBy(x => x.Commits)
            .Select(CreateCommitsGroupingItem)
            .OrderByDescending(x => x.Commits)
            .ToList();
        starring = IterateCommits(commitsGrouping,
            breaker: log => false, // don't break, so iterate all groups (summary)
            byCommits: (log, group) => CreateStars(group.Commits, group.Authors.First()),
            byFiles: (log, group, fGroup) => CreateStars(group.Commits, fGroup.Contributors.First().Contributor),
            byInsertions: (log, group, fGroup, insGroup) => CreateStars(group.Commits, insGroup.Contributors.First().Contributor),
            byDeletions: (log, group, fGroup, insGroup, contributor) => CreateStars(group.Commits, contributor.Contributor));
        Information("------==< New Starring >==------");
        Information(string.Join(Environment.NewLine, starring));

        // Honoring aka Top Contributors
        var coreTeamNames = new List<string> { "Raman Maksimchuk", "Raynald Messié", "Guillaume Gnaegi" }; // Ocelot Core team members should not be in Top 3 Chart
        var coreTeamEmails = new List<string> { "dotnet044@gmail.com", "redbird_project@yahoo.fr", "58469901+ggnaegi@users.noreply.github.com" };
        static CommitsGroupingItem CreateCommitsGroupingItem(IGrouping<int, SummaryItem> g) => new()
        {
            Commits = g.Key,
            Count = g.Count(),
            Authors = g.Select(x => x.Author).ToArray(),
        };
        commitsGrouping = summary
            .Where(x => !coreTeamNames.Contains(x.Author) && !coreTeamEmails.Contains(x.Email)) // filter out Ocelot Core team members
            .GroupBy(x => x.Commits)
            .Select(CreateCommitsGroupingItem)
            .OrderByDescending(x => x.Commits)
            .ToList();
        var topContributors = IterateCommits(commitsGrouping,
            breaker: log => false, // (log.Count >= 3), // going to create Top 3
            byCommits: (log, group) =>
            {
                var place = Place(log.Count);
                var author = group.Authors.First();
                return Honor(place, author, group.Commits);
            },
            byFiles: (log, group, fGroup) =>
            {
                var place = Place(log.Count);
                var contributor = fGroup.Contributors.First();
                return HonorForFiles(place, contributor.Contributor, group.Commits, contributor.Files);
            },
            byInsertions: (log, group, fGroup, insGroup) =>
            {
                var place = Place(log.Count);
                var contributor = insGroup.Contributors.First();
                return HonorForInsertions(place, contributor.Contributor, group.Commits, contributor.Files, contributor.Insertions);
            },
            byDeletions: (log, group, fGroup, insGroup, contributor) =>
            {
                var place = Place(log.Count);
                return HonorForDeletions(place, contributor.Contributor, group.Commits, contributor.Files, contributor.Insertions, contributor.Deletions);
            });
        Information("---==< TOP Contributors >==---");
        Information(string.Join(Environment.NewLine, topContributors));

        // local helpers
        static string Place(int i) => ++i == 1 ? "1st" : i == 2 ? "2nd" : i == 3 ? "3rd" : $"{i}th";
        static string Plural(int n) => n == 1 ? "" : "s";
        static string Honor(string place, string author, int commits, string suffix = null)
            => $"{place[0]}<sup>{place[1..]}</sup> :{place}_place_medal: goes to **{author}** for delivering **{commits}** feature{Plural(commits)} {suffix ?? ""}";
        static string HonorForFiles(string place, string author, int commits, int files, string suffix = null)
            => Honor(place, author, commits, $"in **{files}** file{Plural(files)} changed {suffix ?? ""}");
        static string HonorForInsertions(string place, string author, int commits, int files, int insertions, string suffix = null)
            => HonorForFiles(place, author, commits, files, $"with **{insertions}** insertion{Plural(insertions)} {suffix ?? ""}");
        static string HonorForDeletions(string place, string author, int commits, int files, int insertions, int deletions)
            => HonorForInsertions(place, author, commits, files, insertions, $"and **{deletions}** deletion{Plural(deletions)}");
        List<string> IterateCommits(List<CommitsGroupingItem> commitsGrouping, Predicate<List<string>> breaker,
            Func<List<string>, CommitsGroupingItem, string> byCommits,
            Func<List<string>, CommitsGroupingItem, FilesGroupingItem, string> byFiles,
            Func<List<string>, CommitsGroupingItem, FilesGroupingItem, InsertionsGroupingItem, string> byInsertions,
            Func<List<string>, CommitsGroupingItem, FilesGroupingItem, InsertionsGroupingItem, FilesChangedItem, string> byDeletions)
        {
            var log = new List<string>();
            foreach (var group in commitsGrouping)
            {
                if (breaker.Invoke(log)) break; // (log.Count >= top3)
                if (group.Count == 1)
                {
                    log.Add(byCommits.Invoke(log, group));
                }
                else // multiple candidates with the same number of commits, so, group by files changed
                {
                    var statistics = new List<FilesChangedItem>();
                    var shortstatRegex = new Regex(@"^\s*(?'files'\d+)\s+files?\s+changed(?'ins',\s+(?'insertions'\d+)\s+insertions?\(\+\))?(?'del',\s+(?'deletions'\d+)\s+deletions?\(\-\))?\s*$");
                    static FilesChangedItem CreateFilesChangedItem(System.Text.RegularExpressions.Match m) => new()
                    {
                        Files = int.Parse(m.Groups["files"]?.Value ?? "0"),
                        Insertions = int.Parse(m.Groups["insertions"]?.Value ?? "0"),
                        Deletions = int.Parse(m.Groups["deletions"]?.Value ?? "0"),
                    };
                    foreach (var author in group.Authors) // Collect statistics from git log & shortlog
                    {
                        if (!statistics.Exists(s => s.Contributor == author))
                        {
                            var shortstat = GitHelper($"log --no-merges --author=\"{author}\" --shortstat --pretty=oneline {lastRelease}..HEAD");
                            var data = shortstat
                                .Where(x => shortstatRegex.IsMatch(x))
                                .Select(x => shortstatRegex.Match(x))
                                .Select(CreateFilesChangedItem)
                                .ToList();
                            statistics.Add(new FilesChangedItem(author, data.Sum(x => x.Files), data.Sum(x => x.Insertions), data.Sum(x => x.Deletions)));
                        }
                    }
                    var filesGrouping = statistics
                        .GroupBy(x => x.Files)
                        .Select(g => new FilesGroupingItem
                        {
                            Files = g.Key,
                            Count = g.Count(),
                            Contributors = g.SelectMany(x => statistics.Where(s => s.Contributor == x.Contributor && s.Files == g.Key)).ToArray(),
                        })
                        .OrderByDescending(x => x.Files)
                        .ToList();
                    foreach (var fGroup in filesGrouping)
                    {
                        if (breaker.Invoke(log)) break;
                        if (fGroup.Count == 1)
                        {
                            log.Add(byFiles.Invoke(log, group, fGroup));
                        }
                        else // multiple candidates with the same number of commits, with the same number of changed files, so, group by additions (insertions)
                        {
                            var insertionsGrouping = fGroup.Contributors
                                .GroupBy(x => x.Insertions)
                                .Select(g => new InsertionsGroupingItem
                                {
                                    Insertions = g.Key,
                                    Count = g.Count(),
                                    Contributors = g.SelectMany(x => fGroup.Contributors.Where(s => s.Contributor == x.Contributor && s.Insertions == g.Key)).ToArray(),
                                })
                                .OrderByDescending(x => x.Insertions)
                                .ToList();
                            foreach (var insGroup in insertionsGrouping)
                            {
                                if (breaker.Invoke(log)) break;
                                if (insGroup.Count == 1)
                                {
                                    log.Add(byInsertions.Invoke(log, group, fGroup, insGroup));
                                }
                                else // multiple candidates with the same number of commits, with the same number of changed files, with the same number of insertions, so, order desc by deletions
                                {
                                    foreach (var contributor in insGroup.Contributors.OrderByDescending(x => x.Deletions))
                                    {
                                        if (breaker.Invoke(log)) break;
                                        log.Add(byDeletions.Invoke(log, group, fGroup, insGroup, contributor));
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return log;
        } // END of IterateCommits
        // releaseNotes.Add("### Honoring :medal_sports: aka Top Contributors :clap:");
        // releaseNotes.AddRange(topContributors.Take(3)); // Top 3 only, disabled 'breaker' logic
        // releaseNotes.Add("");
        // releaseNotes.Add("### Starring :star: aka Release Influencers :bowtie:");
        // releaseNotes.AddRange(starring);
        // releaseNotes.Add("");
        // releaseNotes.Add($"### Features in Release {releaseVersion}");
        // releaseNotes.Add("");
        // releaseNotes.Add("<details><summary>Logbook</summary>");
        // releaseNotes.Add("");
        // var commitsHistory = GitHelper($"log --no-merges --date=format:\"%A, %B %d at %H:%M\" --pretty=format:\"- <sub>%h by **%aN** on %ad &rarr;</sub>%n  %s\" {lastRelease}..HEAD");
        // releaseNotes.AddRange(commitsHistory);
        // releaseNotes.Add("</details>");
        // releaseNotes.Add("");
        WriteReleaseNotes();
	});

struct SummaryItem
{
	public int Commits;
	public string Author;
	public string Email;
}
struct CommitsGroupingItem
{
	public int Commits;
	public int Count;
	public string[] Authors;
}
struct FilesChangedItem
{
	public string Contributor;
	public int Files;
	public int Insertions;
	public int Deletions;
	public FilesChangedItem(string author, int files, int insertions, int deletions)
	{
		Contributor = author;
		Files = files;
		Insertions = insertions;
		Deletions = deletions;
	}
}
struct FilesGroupingItem
{
	public int Files;
	public int Count;
	public FilesChangedItem[] Contributors;
}
struct InsertionsGroupingItem
{
	public int Insertions;
	public int Count;
	public FilesChangedItem[] Contributors;
}
private List<string> GitHelper(string command)
{
	IEnumerable<string> output;
	var exitCode = StartProcess(
		"git",
		new ProcessSettings { Arguments = command, RedirectStandardOutput = true },
		out output);
	if (exitCode != 0)
		throw new Exception("Failed to execute Git command: " + command);
	return output.ToList();
}
private void WriteReleaseNotes()
{
	Information($"RUN {nameof(WriteReleaseNotes)} ...");

	EnsureDirectoryExists(packagesDir);
	System.IO.File.WriteAllLines(releaseNotesFile, releaseNotes, Encoding.UTF8);

	var content = System.IO.File.ReadAllText(releaseNotesFile, Encoding.UTF8);
	if (string.IsNullOrEmpty(content))
	{
		System.IO.File.WriteAllText(releaseNotesFile, "No commits since last release");
	}

	Information("Release notes are >>>\n{0}<<<", content);
	//Information($"EXITED {nameof(WriteReleaseNotes)}");
}

Task("RunUnitTests")
	.IsDependentOn("Compile")
	.Does(() =>
	{
		var settings = new DotNetTestSettings
		{
			Configuration = compileConfig,
			ResultsDirectory = artifactsForUnitTestsDir,
			ArgumentCustomization = args => args
				.Append("--no-restore")
				.Append("--no-build")
				.Append("--collect:\"XPlat Code Coverage\"") // this create the code coverage report
				.Append("--verbosity:detailed")
				.Append("--consoleLoggerParameters:ErrorsOnly")
		};
		if (target != Release)
		{
			settings.Framework = LatestFramework; // .NET 8 SDK only
		}
		string frameworkInfo = string.IsNullOrEmpty(settings.Framework) ? AllFrameworks : settings.Framework;
		Information($"Settings {nameof(DotNetTestSettings.Framework)}: {frameworkInfo}");
		EnsureDirectoryExists(artifactsForUnitTestsDir);
		DotNetTest(unitTestAssemblies, settings);

		var coverageSummaryFile = GetSubDirectories(artifactsForUnitTestsDir)
			.First()
			.CombineWithFilePath(File("coverage.cobertura.xml"));
		Information(coverageSummaryFile);
		Information(artifactsForUnitTestsDir);

		GenerateReport(coverageSummaryFile);
		
		if (IsRunningOnCircleCI() && IsMainOrDevelop())
		{
			var repoToken = EnvironmentVariable(coverallsRepoToken);
			if (string.IsNullOrEmpty(repoToken))
			{
				throw new Exception(string.Format("Coveralls repo token not found. Set environment variable '{0}'", coverallsRepoToken));
			}

			Information(string.Format("Uploading test coverage to {0}", coverallsRepo));
			CoverallsNet(coverageSummaryFile, CoverallsNetReportType.OpenCover, new CoverallsNetSettings()
			{
				RepoToken = repoToken
			});
		}
		else
		{
			Information("We are not running on the build server so we won't publish the coverage report to coveralls.io");
		}

		var sequenceCoverage = XmlPeek(coverageSummaryFile, "//coverage/@line-rate");
		var branchCoverage = XmlPeek(coverageSummaryFile, "//coverage/@line-rate");

		Information("Sequence Coverage: " + sequenceCoverage);
	
		if(double.Parse(sequenceCoverage) < minCodeCoverage)
		{
			var whereToCheck = !IsRunningOnCircleCI() ? coverallsRepo : artifactsForUnitTestsDir;
			throw new Exception(string.Format("Code coverage fell below the threshold of {0}%. You can find the code coverage report at {1}", minCodeCoverage, whereToCheck));
		};
	});

Task("RunAcceptanceTests")
	.IsDependentOn("Compile")
	.Does(() =>
	{
		var settings = new DotNetTestSettings
		{
			Configuration = compileConfig,
			// Framework = LatestFramework, // .NET 8 SDK only
			ArgumentCustomization = args => args
				.Append("--no-restore")
				.Append("--no-build")
		};
		if (target != Release)
		{
			settings.Framework = LatestFramework; // .NET 8 SDK only
		}
		string frameworkInfo = string.IsNullOrEmpty(settings.Framework) ? AllFrameworks : settings.Framework;
		Information($"Settings {nameof(DotNetTestSettings.Framework)}: {frameworkInfo}");
		EnsureDirectoryExists(artifactsForAcceptanceTestsDir);
		DotNetTest(acceptanceTestAssemblies, settings);
	});

Task("RunIntegrationTests")
	.IsDependentOn("Compile")
	.Does(() =>
	{
		var settings = new DotNetTestSettings
		{
			Configuration = compileConfig,
			// Framework = LatestFramework, // .NET 8 SDK only
			ArgumentCustomization = args => args
				.Append("--no-restore")
				.Append("--no-build")
		};
		if (target != Release)
		{
			settings.Framework = LatestFramework; // .NET 8 SDK only
		}
		string frameworkInfo = string.IsNullOrEmpty(settings.Framework) ? AllFrameworks : settings.Framework;
		Information($"Settings {nameof(DotNetTestSettings.Framework)}: {frameworkInfo}");
		EnsureDirectoryExists(artifactsForIntegrationTestsDir);
		DotNetTest(integrationTestAssemblies, settings);
	});

Task("CreateArtifacts")
	.IsDependentOn("CreateReleaseNotes")
	.IsDependentOn("Compile")
	.Does(() =>
	{
		WriteReleaseNotes();
		System.IO.File.AppendAllLines(artifactsFile, new[] { "ReleaseNotes.md" });

		if (!IsTechnicalRelease)
		{
			CopyFiles("./src/**/Release/Ocelot.*.nupkg", packagesDir);
			var projectFiles = GetFiles("./src/**/Release/Ocelot.*.nupkg");
			foreach(var projectFile in projectFiles)
			{
				System.IO.File.AppendAllLines(
					artifactsFile,
					new[] { projectFile.GetFilename().FullPath }
				);
			}
		}

		var artifacts = System.IO.File.ReadAllLines(artifactsFile)
			.Distinct();

		Information($"Listing all {nameof(artifacts)}...");
		foreach (var artifact in artifacts)
		{
			var codePackage = packagesDir + File(artifact);
			if (FileExists(codePackage))
			{
				Information("Created package " + codePackage);
			} else {
				Information("Package does not exist: " + codePackage);
			}
		}
	});

Task("PublishGitHubRelease")
	.IsDependentOn("CreateArtifacts")
	.Does(() => 
	{
		if (!IsRunningOnCircleCI()) return;

		dynamic release = CreateGitHubRelease();
		var path = packagesDir.ToString() + @"/**/*";
		foreach (var file in GetFiles(path))
		{
			UploadFileToGitHubRelease(release, file);
		}

		CompleteGitHubRelease(release);
	});

Task("EnsureStableReleaseRequirements")
    .Does(() =>	
    {
		Information("Check if stable release...");

        if (!IsRunningOnCircleCI())
		{
           throw new Exception("Stable release should happen via circleci");
		}

		Information("Release is stable...");
	});

Task("DownloadGitHubReleaseArtifacts")
    .Does(async () =>
    {
		try
		{
			// hack to let GitHub catch up, todo - refactor to poll
			System.Threading.Thread.Sleep(5000);
			EnsureDirectoryExists(packagesDir);

			var releaseUrl = "https://api.github.com/repos/ThreeMammals/ocelot/releases/tags/" + versioning.NuGetVersion;
			var releaseInfo = await GetResourceAsync(releaseUrl);
        	var assets_url = Newtonsoft.Json.Linq.JObject.Parse(releaseInfo)
				.Value<string>("assets_url");

			var assets = await GetResourceAsync(assets_url);
			foreach(var asset in Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JArray>(assets))
			{
				var file = packagesDir + File(asset.Value<string>("name"));
				DownloadFile(asset.Value<string>("browser_download_url"), file);
			}
		}
		catch(Exception exception)
		{
			Information("There was an exception " + exception);
			throw;
		}
	});

Task("PublishToNuget")
    .IsDependentOn("DownloadGitHubReleaseArtifacts")
    .Does(() =>
    {
		if (IsTechnicalRelease)
		{
			Information("Skipping of publishing to NuGet because of technical release...");
			return;
		}

		if (IsRunningOnCircleCI())
		{
			// stable releases
			var nugetFeedStableKey = EnvironmentVariable("OCELOT_NUGET_API_KEY_3Mammals");
			var nugetFeedStableUploadUrl = "https://www.nuget.org/api/v2/package";
			var nugetFeedStableSymbolsUploadUrl = "https://www.nuget.org/api/v2/package";
			PublishPackages(packagesDir, artifactsFile, nugetFeedStableKey, nugetFeedStableUploadUrl, nugetFeedStableSymbolsUploadUrl);
		}
	});

Task("Void").Does(() => {});

RunTarget(target);

private void GenerateReport(Cake.Core.IO.FilePath coverageSummaryFile)
{
	var dir = System.IO.Directory.GetCurrentDirectory();
	Information(dir);

	var reportSettings = new ProcessArgumentBuilder();
	reportSettings.Append($"-targetdir:" + $"{dir}/{artifactsForUnitTestsDir}");
	reportSettings.Append($"-reports:" + coverageSummaryFile);

	var toolpath = Context.Tools.Resolve("net7.0/ReportGenerator.dll");
	Information($"Tool Path : {toolpath.ToString()}");

	DotNetExecute(toolpath, reportSettings);
}

/// Gets unique nuget version for this commit
private GitVersion GetNuGetVersionForCommit()
{
    GitVersion(new GitVersionSettings{
        UpdateAssemblyInfo = false,
        OutputType = GitVersionOutput.BuildServer
    });

    return GitVersion(new GitVersionSettings{ OutputType = GitVersionOutput.Json });
}

/// Updates project version in all of our projects
private void PersistVersion(string committedVersion, string newVersion)
{
	Information(string.Format("We'll search all csproj files for {0} and replace with {1}...", committedVersion, newVersion));
	var projectFiles = GetFiles("./**/*.csproj");
	foreach(var projectFile in projectFiles)
	{
		var file = projectFile.ToString();
		Information(string.Format("Updating {0}...", file));

		var updatedProjectFile = System.IO.File.ReadAllText(file)
			.Replace(committedVersion, newVersion);

		System.IO.File.WriteAllText(file, updatedProjectFile);
	}
}

/// Publishes code and symbols packages to nuget feed, based on contents of artifacts file
private void PublishPackages(ConvertableDirectoryPath packagesDir, ConvertableFilePath artifactsFile, string feedApiKey, string codeFeedUrl, string symbolFeedUrl)
{
		Information("PublishPackages: Publishing to NuGet...");
        var artifacts = System.IO.File
            .ReadAllLines(artifactsFile)
			.Distinct();
		
		foreach(var artifact in artifacts)
		{
			if (artifact == "ReleaseNotes.md") 
				continue;

			var codePackage = packagesDir + File(artifact);
			Information("PublishPackages: Pushing package " + codePackage + "...");
			DotNetNuGetPush(
				codePackage,
				new DotNetNuGetPushSettings { ApiKey = feedApiKey, Source = codeFeedUrl }
			);
		}
}

private void SetupGitHubClient(System.Net.Http.HttpClient client)
{
	string token = Environment.GetEnvironmentVariable("OCELOT_GITHUB_API_KEY_2");
	client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
	client.DefaultRequestHeaders.Add("User-Agent", "Ocelot Release");
	client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
	client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
}

private dynamic CreateGitHubRelease()
{
	var json = $"{{ \"tag_name\": \"{versioning.NuGetVersion}\", \"target_commitish\": \"main\", \"name\": \"{versioning.NuGetVersion}\", \"body\": \"{ReleaseNotesAsJson()}\", \"draft\": true, \"prerelease\": true, \"generate_release_notes\": false }}";
	var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");

	using (var client = new System.Net.Http.HttpClient())
	{	
		SetupGitHubClient(client);
		var result = client.PostAsync("https://api.github.com/repos/ThreeMammals/Ocelot/releases", content).Result;
		if (result.StatusCode != System.Net.HttpStatusCode.Created) 
		{
			var msg = "CreateGitHubRelease: StatusCode = " + result.StatusCode;
			Information(msg);
			throw new Exception(msg);
		}
		var releaseData = result.Content.ReadAsStringAsync().Result;
		dynamic releaseJSON = Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(releaseData);
		Information("CreateGitHubRelease: Release ID is " + releaseJSON.id);
		return releaseJSON;
	}
}

private string ReleaseNotesAsJson()
{
	return System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(System.IO.File.ReadAllText(releaseNotesFile));
}

private void UploadFileToGitHubRelease(dynamic release, FilePath file)
{
	var data = System.IO.File.ReadAllBytes(file.FullPath);
	var content = new System.Net.Http.ByteArrayContent(data);
	content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

	using (var client = new System.Net.Http.HttpClient())
	{	
		SetupGitHubClient(client);
		int releaseId = release.id;
		var fileName = file.GetFilename();
		string uploadUrl = release.upload_url.ToString();
		Information($"UploadFileToGitHubRelease: uploadUrl is {uploadUrl}");
		string[] parts = uploadUrl.Replace("{", "").Split(',');
		uploadUrl = parts[0] + "=" + fileName; // $"https://uploads.github.com/repos/ThreeMammals/Ocelot/releases/{releaseId}/assets?name={fileName}"
		Information($"UploadFileToGitHubRelease: uploadUrl is {uploadUrl}");
		var result = client.PostAsync(uploadUrl, content).Result;
		if (result.StatusCode != System.Net.HttpStatusCode.Created) 
		{
			Information($"UploadFileToGitHubRelease: StatusCode is {result.StatusCode}. Release ID is {releaseId}. Failed to upload file '{fileName}' to URL: {uploadUrl}");
			throw new Exception("UploadFileToGitHubRelease: StatusCode is " + result.StatusCode);
		}
	}
}

private void CompleteGitHubRelease(dynamic release)
{
	int releaseId = release.id;
	string url = release.url.ToString();
	var json = $"{{ \"tag_name\": \"{versioning.NuGetVersion}\", \"target_commitish\": \"main\", \"name\": \"{versioning.NuGetVersion}\", \"body\": \"{ReleaseNotesAsJson()}\", \"draft\": false, \"prerelease\": false }}";
	var request = new System.Net.Http.HttpRequestMessage(new System.Net.Http.HttpMethod("Patch"), url); // $"https://api.github.com/repos/ThreeMammals/Ocelot/releases/{releaseId}");
	request.Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");

	using (var client = new System.Net.Http.HttpClient())
	{	
		SetupGitHubClient(client);
		var result = client.SendAsync(request).Result;
		if (result.StatusCode != System.Net.HttpStatusCode.OK) 
		{
			Information($"CompleteGitHubRelease: StatusCode is {result.StatusCode}. Release ID is {releaseId}. Failed to patch release with URL: {url}");
			throw new Exception("CompleteGitHubRelease: StatusCode = " + result.StatusCode);
		}
	}
}

/// gets the resource from the specified url
private async Task<string> GetResourceAsync(string url)
{
	try
	{
		Information("Getting resource from " + url);

		using var client = new System.Net.Http.HttpClient();
		client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");
		client.DefaultRequestHeaders.UserAgent.ParseAdd("BuildScript");

		using var response = await client.GetAsync(url);
		response.EnsureSuccessStatusCode();
		var content = await response.Content.ReadAsStringAsync();
		Information("Response is >>>" + Environment.NewLine + content + Environment.NewLine + "<<<");
		return content;
	}
	catch(Exception exception)
	{
		Information("There was an exception " + exception);
		throw;
	}
}

private bool IsRunningOnCircleCI()
{
    return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CIRCLECI"));
}

private bool IsMainOrDevelop()
{
	var env = Environment.GetEnvironmentVariable("CIRCLE_BRANCH").ToLower();

    return env == "main" || env == "develop";
}
