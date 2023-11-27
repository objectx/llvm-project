open BlackFox.CommandLine
open Fake.Core
open Fake.Core.TargetOperators
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open FSharp.Collections


let isCI = Environment.environVarAsBool "CI"

let cmakeCmd = "C:/tools/cmake/bin/cmake.exe"
let cpackCmd = "C:/tools/cmake/bin/cpack.exe"

let libXml2Dir = None // Some "C:/tools/libxml2"

let toSlash x = x |> String.replace "\\" "/"

let vcInstallDir = Environment.environVarOrFail "VCINSTALLDIR"
let vsInstallDir = Environment.environVarOrFail "VSINSTALLDIR"
let sdkBinDir = Environment.environVarOrFail "WindowsSdkVerBinPath"

let mtBin = (sdkBinDir @@ "x64/mt.exe") |> toSlash

// let clangCL = "C:/tools/llvm/bin/clang-cl.exe" |> toSlash
// let clangCmd = "C:/tools/llvm/bin/clang.exe" |> toSlash
// let clangPPCmd = "C:/tools/llvm/bin/clang++.exe" |> toSlash

let projectsToBuild = [ "clang"; "clang-tools-extra"; "lld" ] |> String.concat ";"

let runtimesToBuild =
    [ // "libcxx"
      // "libcxxabi"
      // "libunwind"
      "compiler-rt" ]
    |> String.concat ";"

type BuildType =
    | Debug
    | Release

    member self.AsString =
        match self with
        | Debug -> "Debug"
        | Release -> "Release"

    override self.ToString() = self.AsString

type Stage =
    | Stage1 // Uses the host environment.
    | Stage2 // Uses Stage1 artifacts.
    | Stage3 // Creates instrumented version clang
    | Instrumented
    | Stage4 // Build with PGO

    member self.AsString =
        match self with
        | Stage1 -> "Stage1"
        | Stage2 -> "Stage2"
        | Stage3 -> "Stage3"
        | Instrumented -> "Instrumented"
        | Stage4 -> "Stage4"

let buildRoot =
    (__SOURCE_DIRECTORY__ </> ".." </> "00.B") |> System.IO.Path.GetFullPath

let sourceRoot = (__SOURCE_DIRECTORY__ </> "..") |> System.IO.Path.GetFullPath

let stage1Dir = buildRoot </> Stage1.AsString

let stageDir (stage: Stage) = buildRoot </> stage.AsString

let clangCL = stageDir (Stage1) </> "Release/bin/clang-cl.exe" |> toSlash

let installDir (typ: BuildType) : string =
    match typ with
    | Debug -> "C:/tools/llvm-debug"
    | Release -> "C:/tools/llvm"

let wire (tasks: seq<string>) =
    if 1 < (tasks |> Seq.length) then
        tasks |> Seq.pairwise |> Seq.iter (fun (x, y) -> x ?=> y |> ignore)
    else
        ()

let createPackage (typ: BuildType) (dir: string) =
    let artifacts = !!(dir </> "*.7z") ++ (dir </> "*.tgz") ++ (dir </> "*.txz")
    Trace.logfn "Create package (in %s)" dir
    artifacts |> Seq.iter Shell.rm_rf

    CmdLine.empty
    |> CmdLine.appendPrefix "-D" "CPACK_ARCHIVE_THREADS=8"
    |> CmdLine.appendPrefix "-G" "TXZ"
    |> CmdLine.appendPrefix "-C" typ.AsString
    |> CmdLine.toArray
    |> CreateProcess.fromRawCommand cpackCmd
    |> CreateProcess.ensureExitCode
    |> CreateProcess.withWorkingDirectory dir
    |> Proc.run
    |> ignore

let removeLLVM_MT (dir: string) : unit =
    if false then
        Trace.logfn "# Removes unusable llvm-mt.exe (in %s)" dir
        (dir @@ "llvm-mt.exe") |> Shell.rm_rf

module Tasks =
    let clean _ =
        Trace.logfn "Clean: %s" buildRoot
        Shell.rm_rf buildRoot

    let injectEnvironment x =
        x
        |> CreateProcess.setEnvironmentVariable "CC" clangCL
        |> CreateProcess.setEnvironmentVariable "CXX" clangCL
        |> CreateProcess.setEnvironmentVariable "CFLAGS" "-m64 /bigobj"
        |> CreateProcess.setEnvironmentVariable "CXXFLAGS" "-m64 /bigobj"

    let injectLibXml2 x =
        match libXml2Dir with
        | None -> x |> CmdLine.appendPrefix "-D" "LLVM_ENALBE_LIBXML2=NO"
        | Some path ->
            x
            |> CmdLine.appendPrefix "-D" "LLVM_ENALBE_LIBXML2=YES"
            |> CmdLine.appendPrefixf "-D" "LibXml2_ROOT=%s" path

    let enableClangd (sw: bool) x =
        let f = if sw then "ON" else "OFF"
        x |> CmdLine.appendPrefix "-D" $"CLANG_ENABLE_CLANGD=%s{f}"

    module Stage1 =
        let stage = Stage.Stage1
        let buildDir = buildRoot </> stage.AsString

        let configure _ =
            Trace.logfn "Configure (in %s)" buildDir

            Directory.ensure buildDir

            CmdLine.empty
            |> CmdLine.appendPrefix "-G" "Visual Studio 17 2022"
            |> CmdLine.appendPrefix "-A" "x64"
            |> CmdLine.appendPrefix "-T" "host=x64"
            |> CmdLine.appendPrefix "-S" (sourceRoot </> "llvm")
            |> CmdLine.appendPrefix "-B" buildDir
            |> CmdLine.appendPrefixSeq
                "-D"
                [| "CMAKE_BUILD_TYPE=Release"
                   "CMAKE_CXX_FLAGS_INIT=/utf-8"
                   "CMAKE_C_FLAGS_INIT=/utf-8"
                   "LLVM_TARGETS_TO_BUILD=X86"
                   "LLVM_ENABLE_PROJECTS=clang"
                   "LLVM_OPTIMIZED_TABLEGEN=YES"
                   "LLVM_USE_NEW_PM=YES"
                   "LLVM_COMPILER_JOBS=18"
                   "LLVM_ENABLE_EH=YES"
                   "LLVM_ENABLE_RTTI=YES"
                   "LLVM_BUILD_LLVM_C_DYLIB=NO"
                   $"CMAKE_MT={mtBin}"
                   $"CMAKE_GENERATOR_INSTANCE={vsInstallDir}" |]
            |> injectLibXml2
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand cmakeCmd
            |> CreateProcess.ensureExitCode
            |> Proc.run
            |> ignore

        let build _ =
            Trace.logfn "Build (in %s)" buildDir

            Directory.ensure buildDir

            CmdLine.empty
            |> CmdLine.appendPrefix "--build" buildDir
            |> CmdLine.appendPrefix "--config" "Release"
            |> CmdLine.append "-j"
            |> CmdLine.append "--"
            |> CmdLine.append "-graphBuild:True"
            //    |> CmdLine.append "-isolateProjects:True"
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand cmakeCmd
            |> CreateProcess.ensureExitCode
            |> Proc.run
            |> ignore

            (buildDir @@ "Release/bin") |> removeLLVM_MT

        let createTasks () =
            Target.create $"{stage.AsString}.All" ignore

            Target.create $"{stage.AsString}.Configure" configure
            Target.create $"{stage.AsString}.Build" build
            wire [ "Clean"; $"{stage.AsString}.Configure"; $"{stage.AsString}.Build" ]

            $"{stage.AsString}.All"
            <== [ $"{stage.AsString}.Configure"; $"{stage.AsString}.Build" ]

    module Stage2 =
        let stage = Stage.Stage2

        let buildDir (typ: BuildType) =
            buildRoot </> $"{stage.AsString}.{typ.AsString}"

        let configure (typ: BuildType) _ =
            let d = typ |> buildDir
            Trace.logfn "Configure (in %s)" d

            Directory.ensure d
            let clangCmd = Stage1.buildDir </> "Release/bin/clang-cl.exe" |> toSlash
            let clangPPCmd = Stage1.buildDir </> "Release/bin/clang-cl.exe" |> toSlash

            CmdLine.empty
            |> CmdLine.appendPrefix "-G" "Ninja"
            |> CmdLine.appendPrefix "-S" (sourceRoot </> "llvm")
            |> CmdLine.appendPrefix "-B" d
            |> CmdLine.appendPrefixSeq
                "-D"
                [| $"CMAKE_C_COMPILER={clangCmd}"
                   $"CMAKE_CXX_COMPILER={clangPPCmd}"
                   $"CMAKE_MT={mtBin}"
                   $"CMAKE_BUILD_TYPE={typ.AsString}"
                   $"CMAKE_INSTALL_PREFIX={installDir typ}"
                   "LLVM_TARGETS_TO_BUILD=X86"
                   $"LLVM_ENABLE_PROJECTS={projectsToBuild}"
                   $"LLVM_ENABLE_RUNTIMES={runtimesToBuild}"
                   "LLVM_ENABLE_EH=YES"
                   "LLVM_ENABLE_RTTI=YES"
                   "LLVM_CCACHE_BUILD=YES"
                   "LLVM_OPTIMIZED_TABLEGEN=YES"
                   "LLVM_ENABLE_DIA_SDK=NO"
                   "LLVM_USE_NEW_PM=YES"
                   "LLVM_BUILD_TESTS=YES"
                   "LLVM_BUILD_BENCHMARKS=YES"
                   "LLVM_COMPILER_JOBS=18" |]
            |> injectLibXml2
            |> enableClangd true
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand cmakeCmd
            |> CreateProcess.ensureExitCode
            |> injectEnvironment
            |> Proc.run
            |> ignore

        let build (typ: BuildType) _ =
            let d = typ |> buildDir
            Trace.logfn "Build (in %s)" d

            Directory.ensure d

            CmdLine.empty
            |> CmdLine.appendPrefix "--build" d
            |> CmdLine.appendPrefix "--config" typ.AsString
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand cmakeCmd
            |> CreateProcess.ensureExitCode
            |> injectEnvironment
            |> Proc.run
            |> ignore

            (d @@ "bin") |> removeLLVM_MT

        let package (typ: BuildType) _ =
            let d = buildRoot </> (sprintf "%s.%s" Stage2.AsString typ.AsString)
            d |> createPackage typ

        let createTasks () =
            Target.create $"{stage.AsString}.All" ignore

            [ BuildType.Debug; BuildType.Release ]
            |> Seq.iter (fun x ->
                Target.create $"{stage.AsString}.Configure.{x.AsString}" (configure x)
                Target.create $"{stage.AsString}.Build.{x.AsString}" (build x)
                Target.create $"{stage.AsString}.Package.{x.AsString}" (package x)

                wire
                    [ "Clean"
                      $"{stage.AsString}.Configure.{x.AsString}"
                      $"{stage.AsString}.Build.{x.AsString}"
                      $"{stage.AsString}.Package.{x.AsString}" ]

                $"{stage.AsString}.All"
                <== [ $"{stage.AsString}.Configure.{x.AsString}"
                      $"{stage.AsString}.Build.{x.AsString}"
                      $"{stage.AsString}.Package.{x.AsString}" ])

    module Stage3 =
        let stage = Stage.Stage3
        let buildDir = buildRoot </> stage.AsString

        let configure _ =
            let stage2Dir = Stage2.buildDir BuildType.Release
            Trace.logfn "Configure (in %s)" buildDir
            Directory.ensure buildDir
            // let clangCmd = "C:/tools/llvm/bin/clang.exe"
            // let clangPPCmd = "C:/tools/llvm/bin/clang++.exe"
            let clangCmd = stage2Dir </> "bin/clang-cl.exe" |> toSlash
            let clangPPCmd = stage2Dir </> "bin/clang-cl.exe" |> toSlash

            CmdLine.empty
            |> CmdLine.appendPrefix "-G" "Ninja"
            |> CmdLine.appendPrefix "-S" (sourceRoot </> "llvm")
            |> CmdLine.appendPrefix "-B" buildDir
            |> CmdLine.appendPrefixSeq
                "-D"
                [| $"CMAKE_C_COMPILER={clangCmd}"
                   $"CMAKE_CXX_COMPILER={clangPPCmd}"
                   $"CMAKE_MT={mtBin}"
                   "CMAKE_BUILD_TYPE=RelWithDebInfo"
                   "LLVM_TARGETS_TO_BUILD=X86"
                   $"LLVM_ENABLE_PROJECTS={projectsToBuild}"
                   "LLVM_USE_NEW_PM=YES"
                   $"LLVM_COMPILER_JOBS=18"
                   "LLVM_BUILD_INSTRUMENTED=IR"
                   "LLVM_BUILD_RUNTIME=No"
                   "LLVM_ENABLE_EH=YES"
                   "LLVM_ENABLE_RTTI=YES"
                   "LLVM_CCACHE_BUILD=YES"
                   $"""LLVM_TBLGEN={stage2Dir </> "bin/clang-tblgen.exe"}""" |]
            |> injectLibXml2
            |> enableClangd true
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand cmakeCmd
            |> CreateProcess.setEnvironmentVariable "CC" clangCmd
            |> CreateProcess.setEnvironmentVariable "CXX" clangPPCmd
            |> CreateProcess.ensureExitCode
            |> Proc.run
            |> ignore

        let build _ =
            Trace.logfn "Build (in %s)" buildDir

            CmdLine.empty
            |> CmdLine.appendPrefix "--build" buildDir
            |> CmdLine.appendPrefix "--config" "RelWithDebInfo"
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand cmakeCmd
            |> CreateProcess.ensureExitCode
            |> injectEnvironment
            |> Proc.run
            |> ignore

        let check _ =
            Trace.logfn "Check (in %s)" buildDir

            CmdLine.empty
            |> CmdLine.appendPrefix "--build" buildDir
            |> CmdLine.appendPrefix "--config" "RelWithDebInfo"
            |> CmdLine.appendPrefixSeq "--target" [| "check-llvm"; "check-clang" |]
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand cmakeCmd
            // |> CreateProcess.ensureExitCode
            |> injectEnvironment
            |> Proc.run
            |> ignore

        let createTasks () =
            Target.create $"{stage.AsString}.All" ignore
            Target.create $"{stage.AsString}.Configure" configure
            Target.create $"{stage.AsString}.Build" build
            Target.create $"{stage.AsString}.Check" check

            wire
                [ "Clean"
                  $"{stage.AsString}.Configure"
                  $"{stage.AsString}.Build"
                  $"{stage.AsString}.Check" ]

            $"{stage.AsString}.All"
            <== [ $"{stage.AsString}.Configure"
                  $"{stage.AsString}.Build"
                  $"{stage.AsString}.Check" ]

    module Instrumentation =
        let stage = Stage.Instrumented
        let stage3Dir = Stage3.buildDir
        let buildDir = buildRoot </> stage.AsString

        let configure _ =
            Trace.logfn "Configure (in %s)" buildDir
            Directory.ensure buildDir
            // let clangCmd = "C:/tools/llvm/bin/clang.exe"
            // let clangPPCmd = "C:/tools/llvm/bin/clang++.exe"
            let clangCmd = stage3Dir </> "bin/clang-cl.exe" |> toSlash
            let clangPPCmd = stage3Dir </> "bin/clang-cl.exe" |> toSlash

            CmdLine.empty
            |> CmdLine.appendPrefix "-G" "Ninja"
            |> CmdLine.appendPrefix "-S" (sourceRoot </> "llvm")
            |> CmdLine.appendPrefix "-B" buildDir
            |> CmdLine.appendPrefixSeq
                "-D"
                [| $"CMAKE_C_COMPILER={clangCmd}"
                   $"CMAKE_CXX_COMPILER={clangPPCmd}"
                   $"CMAKE_MT={mtBin}"
                   "CMAKE_BUILD_TYPE=RelWithDebInfo"
                   "LLVM_TARGETS_TO_BUILD=X86"
                   $"LLVM_ENABLE_PROJECTS={projectsToBuild}"
                   "LLVM_USE_NEW_PM=YES"
                   "LLVM_COMPILER_JOBS=18"
                   "LLVM_BUILD_RUNTIME=No"
                   "LLVM_ENABLE_EH=YES"
                   "LLVM_ENABLE_RTTI=YES"
                   $"""LLVM_TBLGEN={buildRoot </> "{stage.AsString}.Release/bin/clang-tblgen.exe"}""" |]
            |> injectLibXml2
            |> enableClangd true
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand cmakeCmd
            |> CreateProcess.setEnvironmentVariable "CC" clangCmd
            |> CreateProcess.setEnvironmentVariable "CXX" clangPPCmd
            |> CreateProcess.ensureExitCode
            |> Proc.run
            |> ignore

        let build _ =
            Trace.logfn "Build (in %s)" buildDir

            CmdLine.empty
            |> CmdLine.appendPrefix "--build" buildDir
            |> CmdLine.appendPrefix "--config" "RelWithDebInfo"
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand cmakeCmd
            |> CreateProcess.ensureExitCode
            |> injectEnvironment
            |> Proc.run
            |> ignore

        let createTasks () =
            Target.create $"{stage.AsString}.All" ignore
            Target.create $"{stage.AsString}.Configure" configure
            Target.create $"{stage.AsString}.Build" build

            wire [ "Clean"; $"{stage.AsString}.Configure"; $"{stage.AsString}.Build" ]

            $"{stage.AsString}.All"
            <== [ $"{stage.AsString}.Configure"; $"{stage.AsString}.Build" ]

    module Stage4 =
        let stage = Stage.Stage4
        let stage2Dir = Stage2.buildDir BuildType.Release
        let buildDir = buildRoot </> stage.AsString

        let mergeProfiles _ =
            let rawProfiles = !!(buildRoot </> Stage.Stage3.AsString </> "profiles/*.profraw")
            let sampleList = buildRoot </> "samples.list"
            rawProfiles |> Seq.map toSlash |> File.writeNew sampleList
            let merger = stage2Dir </> "bin/llvm-profdata"

            CmdLine.empty
            |> CmdLine.append "merge"
            |> CmdLine.append "--enable-name-compression"
            |> CmdLine.appendf "--input-files=%s" sampleList
            |> CmdLine.appendf "--output=%s" (buildRoot </> "llvm.profdata")
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand merger
            |> CreateProcess.ensureExitCode
            |> Proc.run
            |> ignore

        let configure _ =
            Trace.logfn "Configure (in %s)" buildDir
            Directory.ensure buildDir
            // let clangCmd = "C:/tools/llvm/bin/clang.exe"
            // let clangPPCmd = "C:/tools/llvm/bin/clang++.exe"
            let clangCmd = stage2Dir </> "bin/clang-cl.exe" |> toSlash
            let clangPPCmd = stage2Dir </> "bin/clang-cl.exe" |> toSlash

            CmdLine.empty
            |> CmdLine.appendPrefix "-G" "Ninja"
            |> CmdLine.appendPrefix "-S" (sourceRoot </> "llvm")
            |> CmdLine.appendPrefix "-B" buildDir
            |> CmdLine.appendPrefixSeq
                "-D"
                [| $"CMAKE_C_COMPILER={clangCmd}"
                   $"CMAKE_CXX_COMPILER={clangPPCmd}"
                   "CMAKE_BUILD_TYPE=Release"
                   $"CMAKE_MT={mtBin}"
                   "LLVM_TARGETS_TO_BUILD=X86"
                   $"LLVM_ENABLE_PROJECTS={projectsToBuild}"
                   $"LLVM_ENABLE_RUNTIMES={runtimesToBuild}"
                   "LLVM_USE_NEW_PM=YES"
                   "LLVM_ENABLE_EH=YES"
                   "LLVM_ENABLE_RTTI=YES"
                   "LLVM_ENABLE_DIA_SDK=NO"
                   "LIBCXX_ENABLE_STATIC=NO"
                   "LLVM_COMPILER_JOBS=18"
                   $"""LLVM_PROFDATA_FILE={buildRoot </> "llvm.profdata"}"""
                   $"""LLVM_TBLGEN={stage2Dir </> "bin/clang-tblgen.exe"}""" |]
            |> injectLibXml2
            |> enableClangd true
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand cmakeCmd
            |> CreateProcess.setEnvironmentVariable "CC" clangCmd
            |> CreateProcess.setEnvironmentVariable "CXX" clangPPCmd
            |> CreateProcess.ensureExitCode
            |> Proc.run
            |> ignore

        let build _ =
            Trace.logfn "Build (in %s)" buildDir

            CmdLine.empty
            |> CmdLine.appendPrefix "--build" buildDir
            |> CmdLine.appendPrefix "--config" "Release"
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand cmakeCmd
            |> CreateProcess.ensureExitCode
            |> injectEnvironment
            |> Proc.run
            |> ignore

        let check _ =
            Trace.logfn "Check (in %s)" buildDir

            CmdLine.empty
            |> CmdLine.appendPrefix "--build" buildDir
            |> CmdLine.appendPrefix "--config" "Release"
            |> CmdLine.appendPrefixSeq "--target" [ "check-llvm"; "check-clang" ]
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand cmakeCmd
            // |> CreateProcess.ensureExitCode
            |> injectEnvironment
            |> Proc.run
            |> ignore

        let package _ =
            buildDir |> createPackage BuildType.Release

        let createTasks () =
            Target.create $"{stage.AsString}.All" ignore
            Target.create $"{stage.AsString}.MergeProfiles" mergeProfiles
            Target.create $"{stage.AsString}.Configure" configure
            Target.create $"{stage.AsString}.Build" build
            Target.create $"{stage.AsString}.Check" check
            Target.create $"{stage.AsString}.Package" package

            wire
                [ "Clean"
                  $"{stage.AsString}.MergeProfiles"
                  $"{stage.AsString}.Configure"
                  $"{stage.AsString}.Build"
                  $"{stage.AsString}.Check"
                  $"{stage.AsString}.Package" ]

            $"{stage.AsString}.All"
            <== [ $"{stage.AsString}.MergeProfiles"
                  $"{stage.AsString}.Configure"
                  $"{stage.AsString}.Build"
                  $"{stage.AsString}.Check"
                  $"{stage.AsString}.Package" ]

let initTargets () =
    Target.create "Clean" Tasks.clean

    Tasks.Stage1.createTasks ()
    Tasks.Stage2.createTasks ()
    Tasks.Stage3.createTasks ()
    Tasks.Instrumentation.createTasks ()
    Tasks.Stage4.createTasks ()

    let instr = Tasks.Instrumentation.stage

    [ "Stage1.Configure"
      "Stage2.Configure.Debug"
      "Stage2.Configure.Release"
      "Stage3.Configure"
      $"{instr.AsString}.Configure"
      "Stage4.Configure" ]
    |> Seq.iter (fun x -> "Clean" ?=> x |> ignore)

    "Stage1.Build" ?=> "Stage2.Configure.Debug" |> ignore
    "Stage1.Build" ?=> "Stage2.Configure.Release" |> ignore

    "Stage2.Build.Release" ?=> "Stage3.Configure" |> ignore
    "Stage3.Build" ?=> $"{instr.AsString}.Configure" |> ignore

    "Stage2.Build.Release" ?=> "Stage4.MergeProfiles" |> ignore
    $"{instr.AsString}.Build" ?=> "Stage4.MergeProfiles" |> ignore

    "Stage2.Build.Release" ?=> "Stage4.Configure" |> ignore

    Target.create "All" ignore

    wire
        [ "Clean"
          "Stage1.All"
          "Stage2.All"
          "Stage3.All"
          "Instrumented.All"
          "Stage4.All" ]

    "All"
    <== [ "Stage1.All"; "Stage2.All"; "Stage3.All"; "Instrumented.All"; "Stage4.All" ]

    Target.create "Rebuild" ignore
    "Rebuild" <== [ "Clean"; "All" ] |> ignore

[<EntryPoint>]
let main args =
    args
    |> Array.toList
    |> Context.FakeExecutionContext.Create false "build.fsx"
    |> Context.RuntimeContext.Fake
    |> Context.setExecutionContext

    initTargets ()
    Target.runOrDefaultWithArguments "All"

    0 // return an integer exit code
