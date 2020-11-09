// --------------------------------------------------------------------------------------
// FAKE build script
// --------------------------------------------------------------------------------------
#r "paket:
source https://api.nuget.org/v3/index.json

nuget FSharp.Core 4.7.2

nuget Fake
nuget Fake.Api.GitHub
nuget Fake.Core.ReleaseNotes
nuget Fake.Core.Target
nuget Fake.BuildServer.AppVeyor
nuget Fake.BuildServer.Travis
nuget Fake.DotNet.Cli
nuget Fake.DotNet.Paket
nuget Fake.DotNet.Fsi

nuget FSharp.Formatting
//"

#l "./.fake/build.fsx/intellisense.fsx"

open Fake.Api
open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.Tools.Git

open System.IO
open Fake.BuildServer

BuildServer.install [
    AppVeyor.Installer
    Travis.Installer
]

// Git configuration (used for publishing documentation in gh-pages branch)
// The profile where the project is posted
let gitOwner = "fsprojects" 
let gitHome = "https://github.com/" + gitOwner

// The name of the project on GitHub
let gitName = "FSharpx.Async"

let solutionFile = "FSharpx.Async.sln"

// The url for the raw files hosted
let gitRaw = Environment.environVarOrDefault "gitRaw" "https://raw.github.com/fsprojects"

// Read additional information from the release notes document
let release = ReleaseNotes.load "RELEASE_NOTES.md"

let repositoryDir = DirectoryInfo(__SOURCE_DIRECTORY__).FullName
let outputDir = Path.Combine(repositoryDir, "bin")

let isAppVeyorBuild = BuildServer.buildServer = AppVeyor
let hasRepoVersionTag = isAppVeyorBuild && AppVeyor.Environment.RepoTag
let versionSuffix =
    if hasRepoVersionTag || (not BuildServer.isLocalBuild && (BuildServer.buildVersion = "LocalBuild"))
    then ""
    else sprintf "b%s" BuildServer.buildVersion

// --------------------------------------------------------------------------------------
// Clean build results

Target.create "Clean" (fun _ ->
    Shell.cleanDirs [outputDir; "docs/output"; "temp"]
)

// --------------------------------------------------------------------------------------
// Build library & test project

Target.create "Build" (fun _ ->
    solutionFile
    |> DotNet.build (fun p ->
        { p with
            Configuration=DotNet.BuildConfiguration.Release
        })
)

// --------------------------------------------------------------------------------------
// Run the unit tests using test runner

Target.create "RunTests" (fun _ ->
    solutionFile
    |> DotNet.test (fun c ->
        { c with
            Configuration=DotNet.BuildConfiguration.Release
        }) 
)

// --------------------------------------------------------------------------------------
// Build a NuGet package

Target.create "NuGet" (fun _ ->
    solutionFile
    |> DotNet.pack  (fun c ->
        { c with
            Configuration=DotNet.BuildConfiguration.Release
            OutputPath=Some outputDir
            VersionSuffix=Some versionSuffix
        })
)

Target.create "PublishNuget" (fun _ ->
    Paket.push (fun p -> 
        { p with WorkingDir="bin" })
)

// --------------------------------------------------------------------------------------
// Generate the documentation

Target.create "GenerateReferenceDocs" (fun _ ->
    if fst (Fsi.exec (fun p -> {p with WorkingDirectory = "docs/tools"; Definitions=["RELEASE"; "REFERENCE"]})  "generate.fsx" []) = 0 then
      failwith "generating reference documentation failed"
)

let generateHelp' fail debug =
    let args =
        if debug then ["HELP"]
        else ["RELEASE"; "HELP"]
    if fst (Fsi.exec (fun p -> {p with WorkingDirectory = "docs/tools"; Definitions=args})  "generate.fsx" []) <> 0 then
        Trace.traceImportant "Help generated"
    else
        if fail then
            failwith "generating help documentation failed"
        else
            Trace.traceImportant "generating help documentation failed"

let generateHelp fail =
    generateHelp' fail false

Target.create "GenerateHelp" (fun _ ->
    Shell.rm "docs/content/release-notes.md"
    Shell.copyFile "docs/content/" "RELEASE_NOTES.md"
    Shell.rename "docs/content/release-notes.md" "docs/content/RELEASE_NOTES.md"

    Shell.rm "docs/content/license.md"
    Shell.copyFile "docs/content/" "LICENSE.txt"
    Shell.rename "docs/content/license.md" "docs/content/LICENSE.txt"

    generateHelp true
)

Target.create "GenerateHelpDebug" (fun _ ->
    Shell.rm "docs/content/release-notes.md"
    Shell.copyFile "docs/content/" "RELEASE_NOTES.md"
    Shell.rename "docs/content/release-notes.md" "docs/content/RELEASE_NOTES.md"

    Shell.rm "docs/content/license.md"
    Shell.copyFile "docs/content/" "LICENSE.txt"
    Shell.rename "docs/content/license.md" "docs/content/LICENSE.txt"

    generateHelp' true true
)

Target.create "KeepRunning" (fun _ ->    
    use watcher = new FileSystemWatcher(DirectoryInfo("docs/content").FullName,"*.*")
    watcher.EnableRaisingEvents <- true
    watcher.Changed.Add(fun e -> generateHelp false)
    watcher.Created.Add(fun e -> generateHelp false)
    watcher.Renamed.Add(fun e -> generateHelp false)
    watcher.Deleted.Add(fun e -> generateHelp false)

    Trace.traceImportant "Waiting for help edits. Press any key to stop."

    System.Console.ReadKey() |> ignore

    watcher.EnableRaisingEvents <- false
    watcher.Dispose()
)

Target.create "GenerateDocs" ignore

let createIndexFsx lang =
    let content = """(*** hide ***)
// This block of code is omitted in the generated HTML documentation. Use 
// it to define helpers that you do not want to show in the documentation.
#I "../../../bin"

(**
F# Project Scaffold ({0})
=========================
*)
"""
    let targetDir = "docs/content" @@ lang
    let targetFile = targetDir @@ "index.fsx"
    Directory.ensure targetDir
    System.IO.File.WriteAllText(targetFile, System.String.Format(content, lang))

Target.create "AddLangDocs" (fun _ ->
    let args = System.Environment.GetCommandLineArgs()
    if args.Length < 4 then
        failwith "Language not specified."

    args.[3..]
    |> Seq.iter (fun lang ->
        if lang.Length <> 2 && lang.Length <> 3 then
            failwithf "Language must be 2 or 3 characters (ex. 'de', 'fr', 'ja', 'gsw', etc.): %s" lang

        let templateFileName = "template.cshtml"
        let templateDir = "docs/tools/templates"
        let langTemplateDir = templateDir @@ lang
        let langTemplateFileName = langTemplateDir @@ templateFileName

        if System.IO.File.Exists(langTemplateFileName) then
            failwithf "Documents for specified language '%s' have already been added." lang

        Directory.ensure langTemplateDir
        Shell.copy langTemplateDir [ templateDir @@ templateFileName ]

        createIndexFsx lang)
)

// --------------------------------------------------------------------------------------
// Release Scripts

Target.create "ReleaseDocs" (fun _ ->
    let tempDocsDir = "temp/gh-pages"
    Shell.cleanDir tempDocsDir
    Repository.cloneSingleBranch "" (gitHome + "/" + gitName + ".git") "gh-pages" tempDocsDir

    Shell.copyRecursive "docs/output" tempDocsDir true |> Trace.tracefn "%A"
    Staging.stageAll tempDocsDir
    Commit.exec tempDocsDir (sprintf "Update generated documentation for version %s" release.NugetVersion)
    Branches.push tempDocsDir
)

Target.create "Release" (fun _ ->
    Staging.stageAll ""
    Commit.exec "" (sprintf "Bump version to %s" release.NugetVersion)
    Branches.push ""

    Branches.tag "" release.NugetVersion
    Branches.pushTag "" "origin" release.NugetVersion
    
    // release on github
    GitHub.createClient (Environment.environVarOrDefault "github-user" "") (Environment.environVarOrDefault "github-pw" "")
    |> GitHub.draftNewRelease gitOwner gitName release.NugetVersion (release.SemVer.PreRelease <> None) release.Notes 
    // TODO: |> uploadFile "PATH_TO_FILE"    
    |> GitHub.publishDraft
    |> Async.RunSynchronously
)

Target.create "BuildPackage" ignore

// --------------------------------------------------------------------------------------
// Run all targets by default. Invoke 'build <Target>' to override

Target.create "All" ignore

"Build"
  ==> "RunTests"
  ==> "NuGet"
  ==> "BuildPackage"
  ==> "All"

"GenerateHelp"
  ==> "GenerateReferenceDocs"
  ==> "GenerateDocs"

"GenerateHelp"
  ==> "KeepRunning"

"Clean"
  ==> "Release"

"ReleaseDocs"
  ==> "Release"

"BuildPackage"
  ==> "PublishNuget"
  ==> "Release"

Target.runOrDefault "All"
