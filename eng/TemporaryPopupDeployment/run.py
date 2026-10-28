"""Owned hosted hidden-window deployment checks; no input or visible windows."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

PRODUCT = "dcaac6c708371ffee2f6dfa7f304307e57623437"
MANIFEST = "src/ProGPU.Backend/Win32PopupCommonControls.manifest"
CANONICAL_MANIFEST_SHA = "2cbb8c61d0f9501ad964de1877bd1e210046694fda606e376efe8756d1a7483a"
MODES = {
    "regular": ["--self-contained", "false"],
    "single-file": ["--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:PublishTrimmed=false"],
    "native-aot": ["--self-contained", "true", "-p:PublishAot=true"],
}

def require(condition, message):
    if not condition: raise ValueError(message)

def sha(path):
    with path.open("rb") as stream: return hashlib.file_digest(stream,"sha256").hexdigest()

def validate_manifest(canonical, checkout):
    require(hashlib.sha256(canonical).hexdigest()==CANONICAL_MANIFEST_SHA,"Original Git manifest blob changed")
    if checkout==canonical:representation="original-LF"
    elif checkout==canonical.replace(b"\n",b"\r\n"):representation="Git-CRLF"
    else:raise ValueError("Manifest checkout is neither exact original nor exact Git CRLF transport")
    return dict(blobSha256=CANONICAL_MANIFEST_SHA,checkoutSha256=hashlib.sha256(checkout).hexdigest(),
                checkoutRepresentation=representation,blobBytes=len(canonical),checkoutBytes=len(checkout))

def publish(command, destination):
    started=time.monotonic();process=None
    receipt=dict(command=command,deadlineSeconds=600,exitCode=1,timedOut=False)
    try:
        with (destination/"publish.log").open("x") as log:
            process=subprocess.Popen(command,stdout=log,stderr=subprocess.STDOUT)
            try:receipt["exitCode"]=process.wait(timeout=max(.001,started+600-time.monotonic()))
            except subprocess.TimeoutExpired:
                receipt.update(timedOut=True,exitCode=124)
                raise
    except BaseException as error:receipt["error"]=type(error).__name__+": "+str(error)
    finally:
        if process is not None and process.poll() is None:
            try:
                subprocess.run(["taskkill","/PID",str(process.pid),"/T","/F"],capture_output=True,timeout=5)
                process.wait(timeout=5)
                receipt["ownedTreeCleanupAttempted"]=True
            except BaseException as error:receipt["cleanupError"]=str(error)
        receipt["elapsedSeconds"]=time.monotonic()-started
        (destination/"publish.json").write_text(json.dumps(receipt,indent=2)+"\n")
    return receipt

def verify(result, mode, expected_manifest_sha):
    require(result["Mode"]==mode and result["Passed"] is True and result["Error"] is None,"Original native probe failed")
    require((result["BackendLocation"]=="")== (mode!="regular"),"Unproven deployment branch")
    require(result["DynamicCodeSupported"] is (mode!="native-aot"),"Runtime is not the selected deployment")
    require(result["ManifestSha256"]==result["ExpectedManifestSha256"]==expected_manifest_sha,"Embedded resource differs from exact validated checkout bytes")
    require(all(result[name] is True for name in ("Accepted","ActivationContextPreserved","PopupDestroyed","OwnerDestroyed")),"Original native ownership/lifetime gate failed")
    require(result["MouseActivateResponse"]==3 and result["ManifestFilesAfter"]==0,"Original hook/temp retirement gate failed")
    require(result["ForegroundBefore"]==result["ForegroundAfter"],"Foreground changed")
    require(type(result["ElapsedSeconds"]) in (int,float) and 0 <= result["ElapsedSeconds"] < 60,"Original process deadline exhausted")

def main():
    require(os.name=="nt" and os.environ.get("RUNNER_ENVIRONMENT")=="github-hosted" and
            os.environ.get("GITHUB_REPOSITORY")=="wieslawsoltes/ProGPU","Disposable owned Windows runner required")
    project=Path("eng/TemporaryPopupDeployment/Probe.csproj")
    output=Path("artifacts/temporary-popup-deployment")
    output.mkdir(parents=True,exist_ok=False)
    source_paths=["src/ProGPU.Backend","Directory.Build.props","Directory.Build.targets","Directory.Packages.props","global.json"]
    changed=subprocess.check_output(["git","diff","--name-only",PRODUCT,"HEAD","--",*source_paths],text=True)
    require(changed=="","Original product source/configuration changed")
    manifest=validate_manifest(subprocess.check_output(["git","show",PRODUCT+":"+MANIFEST]),Path(MANIFEST).read_bytes())
    head=subprocess.check_output(["git","rev-parse","HEAD"],text=True).strip()
    require(head==os.environ["PROBE_EVENT_HEAD"],"Diagnostic checkout differs from exact event head")
    sdk_version=subprocess.check_output(["dotnet","--version"],text=True).strip()
    runtime_info=subprocess.check_output(["dotnet","--info"],text=True)
    (output/"dotnet-info.txt").write_text(runtime_info)
    identity=dict(product=PRODUCT,eventHead=head,productTree=subprocess.check_output(
        ["git","rev-parse",PRODUCT+":src/ProGPU.Backend"],text=True).strip(),
        diagnosticSources={str(path):sha(path) for path in Path("eng/TemporaryPopupDeployment").glob("*") if path.is_file()},
        inputActions=0,visibleWindowsRequested=0,deadlineSecondsPerProcess=60,qualified=False,manifest=manifest,
        sdkVersion=sdk_version,globalJson=json.loads(Path("global.json").read_text()))
    (output/"identity.json").write_text(json.dumps(identity,indent=2)+"\n")
    failures=[]
    for mode,options in MODES.items():
        destination=output/mode;destination.mkdir()
        payload=Path(os.environ["RUNNER_TEMP"])/("popup-deployment-"+mode)
        require(not payload.exists(),"Fresh task-owned publish path required")
        command=["dotnet","publish",str(project),"-c","Release","-r","win-x64","-m:1",
                 "-p:UseSharedCompilation=false","--artifacts-path",str(Path("artifacts/popup-deployment-build")/mode),
                 "--output",str(payload),*options]
        build=publish(command,destination)
        if build["exitCode"]:
            failures.append(mode+": publish failed")
            continue
        executable=payload/"Probe.exe"
        require(executable.is_file(),"Selected actual apphost is missing")
        files={str(p.relative_to(payload)):sha(p) for p in sorted(payload.rglob("*")) if p.is_file()}
        (destination/"payload.json").write_text(json.dumps(files,indent=2)+"\n")
        private=Path(os.environ["RUNNER_TEMP"])/("popup-deployment-private-"+mode)
        private.mkdir(exist_ok=False)
        environment=os.environ.copy()
        environment.update(TEMP=str(private),TMP=str(private),PROBE_OWNED_TEMP=str(private),
                           DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(private/"bundle"),PROGPU_NATIVE_TRACE_POPUP_OWNER="1",
                           PROBE_MANIFEST_SHA256=manifest["checkoutSha256"])
        runtime=dict(mode=mode,inputActions=0,deadlineSeconds=60,timedOut=False,completed=False,command=[str(executable),mode])
        process=None;started=time.monotonic()
        try:
            with (destination/"native.log").open("x") as log:
                process=subprocess.Popen(runtime["command"],env=environment,stdout=log,stderr=subprocess.STDOUT)
                try:code=process.wait(timeout=max(.001,started+60-time.monotonic()))
                except subprocess.TimeoutExpired:
                    runtime["timedOut"]=True
                    subprocess.run(["taskkill","/PID",str(process.pid),"/T","/F"],capture_output=True,timeout=5)
                    process.wait(timeout=5)
                    raise TimeoutError("Owned native process exceeded original60-second deadline")
            runtime.update(exitCode=code,elapsedSeconds=time.monotonic()-started)
            require(runtime["elapsedSeconds"] < 60 and code==0,"Original hidden native process failed")
            lines=(destination/"native.log").read_text().splitlines()
            records=[json.loads(line) for line in lines if line.startswith("{")]
            require(len(records)==1,"Exact original native receipt required")
            verify(records[0],mode,manifest["checkoutSha256"])
            runtime["native"]=records[0]
            after={str(p.relative_to(payload)):sha(p) for p in sorted(payload.rglob("*")) if p.is_file()}
            require(after==files,"Published files changed during native execution")
            runtime.update(completed=True,payloadUnchanged=True)
        except BaseException as error:
            runtime["error"]=type(error).__name__+": "+str(error)
            failures.append(mode+": "+runtime["error"])
        finally:
            if process is not None and process.poll() is None:
                try:
                    subprocess.run(["taskkill","/PID",str(process.pid),"/T","/F"],capture_output=True,timeout=5)
                    process.wait(timeout=5)
                except BaseException as error:runtime["cleanupError"]=str(error)
            runtime["elapsedSeconds"]=time.monotonic()-started
            (destination/"receipt.json").write_text(json.dumps(runtime,indent=2)+"\n")
            print(mode,json.dumps(runtime))
    (output/"summary.json").write_text(json.dumps(dict(completed=not failures,failures=failures,
        modes=list(MODES),product=PRODUCT,inputActions=0),indent=2)+"\n")
    if failures:raise SystemExit(1)

if __name__=="__main__":main()
