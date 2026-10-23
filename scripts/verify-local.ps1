param([string]$Output,[switch]$Native,[switch]$Race)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));if(!$Output){$Output=Join-Path $root ('.artifacts/local-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))};$Output=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $Output){throw 'New evidence output directory required'};New-Item -ItemType Directory -Path $Output|Out-Null
$utf8=New-Object Text.UTF8Encoding($false);$results=New-Object 'System.Collections.Generic.List[object]';$complete=$false
Add-Type -TypeDefinition @'
using System;using System.IO;using System.Diagnostics;using System.Text;using System.Threading.Tasks;using System.Linq;
public static class RemoteAssistCheckRunner{
 static async Task Copy(StreamReader reader,string path){using(var output=new StreamWriter(path,false,new UTF8Encoding(false))){var buffer=new char[4096];int count,total=0;while((count=await reader.ReadAsync(buffer,0,buffer.Length))!=0){if((total+=count)>8*1024*1024)throw new IOException("verification output bound");await output.WriteAsync(buffer,0,count);}}}
 static string Quote(string value){var result=new StringBuilder("\"");int slashes=0;foreach(var c in value){if(c=='\\'){slashes++;continue;}if(c=='\"'){result.Append('\\',slashes*2+1);result.Append(c);}else{result.Append('\\',slashes);result.Append(c);}slashes=0;}result.Append('\\',slashes*2);return result.Append('"').ToString();}
 public static string Arguments(string[] values){return string.Join(" ",values.Select(Quote));}
 public static int Run(ProcessStartInfo start,string path,int seconds){return RunAsync(start,path,seconds).GetAwaiter().GetResult();}
 static async Task<int> RunAsync(ProcessStartInfo start,string path,int seconds){using(var p=Process.Start(start)){if(p==null)throw new IOException("process start");try{var work=Task.WhenAll(Copy(p.StandardOutput,path+".stdout.txt"),Copy(p.StandardError,path+".stderr.txt"),Task.Run(()=>p.WaitForExit()));if(await Task.WhenAny(work,Task.Delay(seconds*1000))!=work)throw new TimeoutException("verification deadline");await work;return p.ExitCode;}catch{if(!p.HasExited){using(var kill=Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"taskkill.exe"),"/PID "+p.Id+" /T /F"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})){if(kill!=null)kill.WaitForExit(2000);}if(!p.HasExited)p.Kill();}throw;}}}
}
'@
function Invoke-Check([string]$Name,[string]$File,[string[]]$Arguments,[string]$Directory,[int]$Seconds=180){
    $start=New-Object Diagnostics.ProcessStartInfo((Get-Command $File -CommandType Application).Source);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true;$start.WorkingDirectory=$Directory
    $start.StandardOutputEncoding=[Text.Encoding]::UTF8;$start.StandardErrorEncoding=[Text.Encoding]::UTF8
    if($start.PSObject.Properties['ArgumentList']){foreach($argument in $Arguments){$start.ArgumentList.Add($argument)}}else{$start.Arguments=[RemoteAssistCheckRunner]::Arguments($Arguments)};$clock=[Diagnostics.Stopwatch]::StartNew();$exitCode=-1;$status='FAIL'
    try{$exitCode=[RemoteAssistCheckRunner]::Run($start,(Join-Path $Output $Name),$Seconds);if($exitCode-ne0){Get-Content -LiteralPath (Join-Path $Output ($Name+'.stdout.txt')) -Tail 30 -Encoding UTF8;Get-Content -LiteralPath (Join-Path $Output ($Name+'.stderr.txt')) -Tail 30 -Encoding UTF8;throw ($Name+' failed: '+$exitCode)};$status='PASS';Write-Output ($Name+' PASS')}finally{$results.Add(@{name=$Name;status=$status;exitCode=$exitCode;seconds=$clock.Elapsed.TotalSeconds;deadlineSeconds=$Seconds;command=$File;arguments=$Arguments})}
}
try{
    Invoke-Check 'restore' 'dotnet' @('restore','RemoteAssist.slnx','--locked-mode') $root 600
    Invoke-Check 'build' 'dotnet' @('build','RemoteAssist.slnx','--no-restore','-v','minimal') $root 600
    Invoke-Check 'core-tests' 'dotnet' @('run','--project','tests/RemoteAssist.Host.Core.Tests','--no-build') $root
    Invoke-Check 'go-build' 'go' @('build','./...') (Join-Path $root 'server/signaling')
    Invoke-Check 'go-executable' 'go' @('build','-o',(Join-Path $Output 'signaling.exe'),'.') (Join-Path $root 'server/signaling')
    Invoke-Check 'go-tests' 'go' @('test','./...') (Join-Path $root 'server/signaling')
    Invoke-Check 'go-vet' 'go' @('vet','./...') (Join-Path $root 'server/signaling')
    Invoke-Check 'viewer-tests' 'node' @('--test','web/viewer/tests/*.test.js') $root
    Invoke-Check 'installer-contract' 'powershell.exe' @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File','tests/installer-contract.ps1') $root
    if($Race){Invoke-Check 'go-race' 'go' @('test','-race','./...') (Join-Path $root 'server/signaling') 600}
    if($Native){Invoke-Check 'encoders' 'dotnet' @('run','--project','tools/RemoteAssist.HostRun','--no-build','--','--encoder-check','--out',(Join-Path $Output 'encoders')) $root;Invoke-Check 'native-media' 'dotnet' @('run','--project','tools/RemoteAssist.MediaProbe','--no-build') $root}
    $complete=$true
}finally{
    $sha=(& git -C $root rev-parse HEAD|Out-String).Trim();$dirty=(& git -C $root status --porcelain|Out-String).Trim().Length-gt0
    [IO.File]::WriteAllText((Join-Path $Output 'result.json'),(@{utc=[DateTimeOffset]::UtcNow.ToString('o');commit=$sha;dirty=$dirty;status=$(if($complete){'PASS'}else{'FAIL'});checks=@($results.ToArray());excluded='interactive UI/UAC, signed installer, long-load and two physical PC acceptance'}|ConvertTo-Json -Depth 8),$utf8)
}
Write-Output ('Local verification evidence: '+$Output)
