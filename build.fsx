#load ".paket/load/main.group.fsx"

open Fake.Core
open BlackFox.CommandLine
open BlackFox.Fake
open Fake.Core.TargetOperators
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators

// Boilerplate for adapter
let makeExecutionContext () =
    System.Environment.GetCommandLineArgs()
    |> Array.skip 2 // skip fsi.exe; build.fsx
    |> Array.toList
    |> Context.FakeExecutionContext.Create false __SOURCE_FILE__
    |> Context.RuntimeContext.Fake

makeExecutionContext ()
|> Context.setExecutionContext

Target.initEnvironment ()

module BuildEnv =
    let homeDir = Environment.environVarOrFail "HOME"

    let srcDir = __SOURCE_DIRECTORY__ </> "llvm"
    let buildDir = __SOURCE_DIRECTORY__ </> "00.B"

    let cmakeBin = homeDir </> ".local/bin/cmake"
    //let cmakeBin = "/usr/local/bin/cmake"
    let ccacheBin: string option = Some "/usr/local/bin/ccache" // None
    //let clangBin = "/usr/local/opt/llvm/bin/clang"
    let clangBin = "/usr/bin/clang"
    //let clangPPBin = "/usr/local/opt/llvm/bin/clang++"
    let clangPPBin = "/usr/bin/clang++"

    let installPrefix = homeDir </> ".local"

    let findTool (name: string) =
        let r =
            CmdLine.empty
            |> CmdLine.append "--find"
            |> CmdLine.append name
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand "xcrun"
            |> CreateProcess.warnOnExitCode $"missing {name}"
            |> CreateProcess.redirectOutput
            |> CreateProcess.mapResult (fun (x: ProcessOutput) -> x.Output |> String.trim)
            |> Proc.run

        match r.ExitCode with
        | 0 -> r.Result |> Some
        | _ -> None

    let libTool = findTool "libtool"

/// Converts supplied string list into CMake's list.
let toCMakeList (x: seq<string>) = x |> String.concat ";"

let enabledProjects =
    [| "clang"
       "clang-tools-extra"
       "cross-project-tests"
       // "libc"
       // "libclc"
       "lld"
       "lldb"
       "openmp"
       "polly"
       "pstl" |]

let enabledRuntimes =
    [| "libcxx"
       "libcxxabi"
       "compiler-rt"
       "libunwind" |]

let ensureBuildDirectory () =
    Directory.ensure BuildEnv.buildDir

    ("build-dir: 19BBC6F7-3FBF-49BD-BAA4-EA60CE5A0735\n"
     + "# Excluded from backups")
    |> File.writeString false (BuildEnv.buildDir </> ".BUILDDIR.TAG")

let cleanTask =
    BuildTask.createFn "Clean" [] (fun _ -> Directory.delete BuildEnv.buildDir)

let cleanConfigureTask =
    BuildTask.createFn "CleanConfigure" [] (fun _ ->
        (BuildEnv.buildDir </> "CMakeCache.txt")
        |> File.delete)

let setCommonCMakeSettings x =
    let maxParallelCompile = 21
    let maxParallelLink = 10

    x
    |> CmdLine.append "-Wno-dev"
    |> CmdLine.appendPrefixSeq
        "-D"
        [| $"CMAKE_INSTALL_PREFIX=%s{BuildEnv.installPrefix}"
           "CMAKE_EXPORT_COMPILE_COMMANDS=YES"
           "CMAKE_OPTIMIZE_DEPENDENCIES=YES"
           $"LLVM_PARALLEL_COMPILE_JOBS=%d{maxParallelCompile}"
           $"LLVM_PARALLEL_LINK_JOBS=%d{maxParallelLink}"
           "LLVM_TARGETS_TO_BUILD=X86"
           "LLVM_INCLUDE_EXAMPLES=OFF"
           "LLVM_ENABLE_RTTI=YES"
           "LLVM_ENABLE_EH=YES"
           "LLVM_OPTIMIZED_TABLEGEN=ON" |]
    |> CmdLine.appendPrefixIfSomef "-D" "CMAKE_LIBTOOL=%s" BuildEnv.libTool

let setBuildEnvironment srcDir buildDir x =
    x
    |> CmdLine.appendPrefix "-G" "Ninja"
    |> CmdLine.appendPrefix "-S" srcDir
    |> CmdLine.appendPrefix "-B" buildDir

// Build with system's compiler
module Stage1 =
    let stageName = "Stage1"
    let mkName x = $"{stageName}.%s{x}"
    let buildDir = BuildEnv.buildDir </> stageName

    let configureTask =
        BuildTask.createFn
            ("Configure" |> mkName)
            [ cleanTask.IfNeeded
              cleanConfigureTask.IfNeeded ]
            (fun _ ->
                ensureBuildDirectory ()

                CmdLine.empty
                |> setBuildEnvironment BuildEnv.srcDir buildDir
                |> setCommonCMakeSettings
                |> CmdLine.appendPrefixIfSomef "-D" "CMAKE_C_COMPILER_LAUNCHER=%s" BuildEnv.ccacheBin
                |> CmdLine.appendPrefixIfSomef "-D" "CMAKE_CXX_COMPILER_LAUNCHER=%s" BuildEnv.ccacheBin
                |> CmdLine.appendPrefixSeq
                    "-D"
                    [| "CMAKE_BUILD_TYPE=Release"
                       $"CMAKE_C_COMPILER=%s{BuildEnv.clangBin}"
                       $"CMAKE_CXX_COMPILER=%s{BuildEnv.clangPPBin}"
                       "LLVM_INCLUDE_EXAMPLES=OFF"
                       "LLVM_INCLUDE_TESTS=OFF"
                       "LLVM_BUILD_BENCHMARKS=OFF"
                       "LLVM_INCLUDE_BENCHMARKS=OFF"
                       "LIBCXX_INCLUDE_BENCHMARKS=OFF" |]
                |> CmdLine.appendPrefixf
                    "-D"
                    "LLVM_ENABLE_PROJECTS=%s"
                    ([| "clang"
                        "lld"
                        "clang-tools-extra" |]
                     |> toCMakeList)
                |> CmdLine.appendPrefixf
                    "-D"
                    "LLVM_ENABLE_RUNTIMES=%s"
                    ([| "libcxxabi"
                        "libcxx"
                        "compiler-rt" |]
                     |> toCMakeList)
                |> CmdLine.toArray
                |> CreateProcess.fromRawCommand BuildEnv.cmakeBin
                |> CreateProcess.ensureExitCode
                |> Proc.run
                |> ignore)

    let reconfigureTask =
        BuildTask.createEmpty
            ("Reconfigure" |> mkName)
            [ cleanConfigureTask
              cleanTask.IfNeeded ]

    let buildTask =
        BuildTask.createFn ("Build" |> mkName) [ configureTask.IfNeeded ] (fun _ ->
            CmdLine.empty
            |> CmdLine.appendPrefix "--build" buildDir
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand BuildEnv.cmakeBin
            |> CreateProcess.ensureExitCode
            |> Proc.run
            |> ignore)

    let allTask = BuildTask.createEmpty ("All" |> mkName) [ configureTask; buildTask ]

// Build with Stage1 clang (for instrumentation)
module Stage2 =
    let stageName = "Stage2"
    let mkName x = $"{stageName}.%s{x}"

    let instrumentedProjects =
        [| "clang"
           "lld"
           //    "libcxx"
           //    "libcxxabi"
           |]

    let buildDir = BuildEnv.buildDir </> stageName

    let configureTask =
        BuildTask.createFn
            ("Configure" |> mkName)
            [ cleanTask.IfNeeded
              cleanConfigureTask.IfNeeded
              Stage1.buildTask.IfNeeded ]
            (fun _ ->
                ensureBuildDirectory ()

                let stage1Clang = Stage1.buildDir </> "bin/clang"
                let stage1ClangPP = Stage1.buildDir </> "bin/clang++"

                CmdLine.empty
                |> setBuildEnvironment BuildEnv.srcDir buildDir
                |> setCommonCMakeSettings
                |> CmdLine.appendPrefixIfSomef "-D" "CMAKE_C_COMPILER_LAUNCHER=%s" BuildEnv.ccacheBin
                |> CmdLine.appendPrefixIfSomef "-D" "CMAKE_CXX_COMPILER_LAUNCHER=%s" BuildEnv.ccacheBin
                |> CmdLine.appendPrefixSeq
                    "-D"
                    [| "CMAKE_BUILD_TYPE=Release"
                       $"CMAKE_C_COMPILER=%s{stage1Clang}"
                       $"CMAKE_CXX_COMPILER=%s{stage1ClangPP}"
                       "CMAKE_CXX_FLAGS_INIT=-Xclang -mllvm -Xclang -vp-counters-per-site=64"
                       "CMAKE_C_FLAGS_INIT=-Xclang -mllvm -Xclang -vp-counters-per-site=64"
                       "LLVM_INCLUDE_EXAMPLES=OFF"
                       "LLVM_INCLUDE_TESTS=ON"
                       "LLVM_INCLUDE_BENCHMARKS=ON" |]
                |> CmdLine.appendPrefixSeq
                    "-D"
                    [| "LLVM_BUILD_INSTRUMENTED=IR"
                       "LLVM_BUILD_RUNTIME=YES" |]
                |> CmdLine.appendPrefixf "-D" "LLVM_ENABLE_PROJECTS=%s" (instrumentedProjects |> toCMakeList)
                |> CmdLine.appendPrefixf "-D" "LLVM_ENABLE_RUNTIMES=%s" (enabledRuntimes |> toCMakeList)
                |> CmdLine.toArray
                |> CreateProcess.fromRawCommand BuildEnv.cmakeBin
                |> CreateProcess.ensureExitCode
                |> Proc.run
                |> ignore)

    let reconfigureTask =
        BuildTask.createEmpty
            ("Reconfigure" |> mkName)
            [ cleanConfigureTask
              cleanTask.IfNeeded ]

    let buildTask =
        BuildTask.createFn ("Build" |> mkName) [ configureTask.IfNeeded ] (fun _ ->
            CmdLine.empty
            |> CmdLine.appendPrefix "--build" buildDir
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand BuildEnv.cmakeBin
            |> CreateProcess.ensureExitCode
            |> Proc.run
            |> ignore)

    let testTask =
        BuildTask.createFn ("Test" |> mkName) [ buildTask.IfNeeded ] (fun _ ->
            CmdLine.empty
            |> CmdLine.appendPrefix "--build" buildDir
            |> CmdLine.appendPrefixSeq
                "--target"
                [| "check-llvm"
                   "check-clang"
                   "cxx-benchmarks" |]
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand BuildEnv.cmakeBin
            |> Proc.run
            |> ignore)

    let allTask =
        BuildTask.createEmpty ("All" |> mkName) [ configureTask; buildTask; testTask ]

// Bulid with State2 clang (for collecting instrumentation data)
module Stage3 =
    let stageName = "Stage3"
    let mkName x = $"{stageName}.%s{x}"
    let instrumentedProjects = [| "clang"; "lld" |]
    let buildDir = BuildEnv.buildDir </> stageName

    let configureTask =
        BuildTask.createFn
            ("Configure" |> mkName)
            [ cleanTask.IfNeeded
              cleanConfigureTask.IfNeeded
              Stage2.testTask.IfNeeded ]
            (fun _ ->
                ensureBuildDirectory ()

                let stage2Clang = Stage2.buildDir </> "bin/clang"
                let stage2ClangPP = Stage2.buildDir </> "bin/clang++"

                CmdLine.empty
                |> setBuildEnvironment BuildEnv.srcDir buildDir
                |> setCommonCMakeSettings
                |> CmdLine.appendPrefixSeq
                    "-D"
                    [| "CMAKE_BUILD_TYPE=RelWithDebInfo"
                       $"CMAKE_C_COMPILER=%s{stage2Clang}"
                       $"CMAKE_CXX_COMPILER=%s{stage2ClangPP}"
                       "LLVM_INCLUDE_EXAMPLES=OFF"
                       "LLVM_INCLUDE_TESTS=ON"
                       "LLVM_ENABLE_LIBCXX=YES"
                       "LLVM_INCLUDE_BENCHMARKS=ON" |]
                |> CmdLine.appendPrefixf "-D" "LLVM_ENABLE_PROJECTS=%s" (enabledProjects |> toCMakeList)
                |> CmdLine.appendPrefixf "-D" "LLVM_ENABLE_RUNTIMES=%s" (enabledRuntimes |> toCMakeList)
                |> CmdLine.toArray
                |> CreateProcess.fromRawCommand BuildEnv.cmakeBin
                |> CreateProcess.ensureExitCode
                |> Proc.run
                |> ignore)

    let reconfigureTask =
        BuildTask.createEmpty
            ("Reconfigure" |> mkName)
            [ cleanConfigureTask
              cleanTask.IfNeeded ]

    let buildTask =
        BuildTask.createFn ("Build" |> mkName) [ configureTask.IfNeeded ] (fun _ ->
            CmdLine.empty
            |> CmdLine.appendPrefix "--build" buildDir
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand BuildEnv.cmakeBin
            |> CreateProcess.ensureExitCode
            |> Proc.run
            |> ignore)

    let allTask = BuildTask.createEmpty ("All" |> mkName) [ configureTask; buildTask ]

module Stage4 =
    let stageName = "Stage4"
    let mkName x = $"{stageName}.%s{x}"
    let buildDir = BuildEnv.buildDir </> stageName
    let profData = BuildEnv.buildDir </> "llvm.prof"

    let mergeProfileTask =
        BuildTask.createFn
            ("MergeProfiles" |> mkName)
            [ cleanTask.IfNeeded
              cleanConfigureTask.IfNeeded
              Stage2.testTask.IfNeeded
              Stage3.buildTask.IfNeeded ]
            (fun _ ->
                ensureBuildDirectory ()

                let profRaws = !!(Stage2.buildDir </> "profiles/*.profraw")

                CmdLine.empty
                |> CmdLine.append "merge"
                |> CmdLine.appendf "--output=%s" profData
                |> CmdLine.appendSeq profRaws
                |> CmdLine.toArray
                |> CreateProcess.fromRawCommand (Stage1.buildDir </> "bin/llvm-profdata")
                |> CreateProcess.ensureExitCode
                |> Proc.run
                |> ignore)

    let configureTask =
        BuildTask.createFn
            ("Configure" |> mkName)
            [ cleanTask.IfNeeded
              cleanConfigureTask.IfNeeded
              Stage3.buildTask.IfNeeded
              mergeProfileTask ]
            (fun _ ->
                ensureBuildDirectory ()

                let stage1Clang = Stage1.buildDir </> "bin/clang"
                let stage1ClangPP = Stage1.buildDir </> "bin/clang++"

                CmdLine.empty
                |> setBuildEnvironment BuildEnv.srcDir buildDir
                |> setCommonCMakeSettings
                |> CmdLine.appendPrefixSeq
                    "-D"
                    [| "CMAKE_BUILD_TYPE=Release"
                       $"CMAKE_C_COMPILER=%s{stage1Clang}"
                       $"CMAKE_CXX_COMPILER=%s{stage1ClangPP}"
                       "LLVM_INCLUDE_EXAMPLES=OFF"
                       "LLVM_INCLUDE_TESTS=ON"
                       "LLVM_INCLUDE_BENCHMARKS=ON"
                       "LLVM_ENABLE_LIBCXX=YES"
                       "LLVM_STATIC_LINK_CXX_STDLIB=YES"
                       "LLVM_BUILD_RUNTIME=YES" |]
                |> CmdLine.appendPrefixf "-D" "LLVM_ENABLE_PROJECTS=%s" (enabledProjects |> toCMakeList)
                |> CmdLine.appendPrefixf "-D" "LLVM_PROFDATA_FILE=%s" profData
                |> CmdLine.appendPrefixf "-D" "LLVM_ENABLE_RUNTIMES=%s" (enabledRuntimes |> toCMakeList)
                |> CmdLine.toArray
                |> CreateProcess.fromRawCommand BuildEnv.cmakeBin
                |> CreateProcess.ensureExitCode
                |> Proc.run
                |> ignore)

    let reconfigureTask =
        BuildTask.createEmpty
            ("Reconfigure" |> mkName)
            [ cleanConfigureTask
              cleanTask.IfNeeded ]

    let buildTask =
        BuildTask.createFn ("Build" |> mkName) [ configureTask.IfNeeded ] (fun _ ->
            CmdLine.empty
            |> CmdLine.appendPrefix "--build" buildDir
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand BuildEnv.cmakeBin
            |> CreateProcess.ensureExitCode
            |> Proc.run
            |> ignore)

    let testTask =
        BuildTask.createFn ("Test" |> mkName) [ buildTask.IfNeeded ] (fun _ ->
            CmdLine.empty
            |> CmdLine.appendPrefix "--build" buildDir
            |> CmdLine.appendPrefixSeq "--target" [ "check-clang"; "check-llvm" ]
            |> CmdLine.toArray
            |> CreateProcess.fromRawCommand BuildEnv.cmakeBin
            |> Proc.run
            |> ignore)

    let allTask =
        BuildTask.createEmpty ("All" |> mkName) [ configureTask; buildTask; testTask ]

// Toplevel tasks
let allTask =
    BuildTask.createEmpty
        "All"
        [ Stage1.configureTask
          Stage1.buildTask
          Stage2.configureTask
          Stage2.buildTask
          Stage2.testTask
          Stage3.configureTask
          Stage3.buildTask
          Stage4.mergeProfileTask
          Stage4.configureTask
          Stage4.buildTask
          Stage4.testTask ]

let rebuildTask = BuildTask.createEmpty "Rebuild" [ cleanTask; allTask ]

BuildTask.runOrDefault allTask
