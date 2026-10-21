"""Verify development build artifacts without starting any binary."""
import argparse
import json
from pathlib import Path
from e2e_case_runtime import ROOT,require,read_json,digest,safe_path,CaseError
from e2e_case_campaign import inventory,verify_candidate

def verify(candidate_directory,tools_directory):
    candidate_directory=safe_path(candidate_directory);tools_directory=safe_path(tools_directory)
    candidate=verify_candidate({'candidateDirectory':str(candidate_directory),'caseId':'C01'})
    require(candidate.get('sources'),'candidate-source-inventory')
    for row in candidate['sources']:
        path=safe_path(str(ROOT/row['path']),ROOT,True)
        require(digest(path)==row['sha256'].lower(),'candidate-source-changed')
    payload=candidate_directory/'payload'
    for name in ('Service','Tray','Helper','AdminHelper','Recovery','RunbookWorker'):
        prefix='RemoteAssist.Host.'+name
        for suffix in ('.exe','.dll','.deps.json','.runtimeconfig.json'):safe_path(str(payload/(prefix+suffix)),payload,True)
    manifest=read_json(ROOT/'third_party/ffmpeg/bin/manifest.json')
    expected={r['name']:r['sha256'] for r in manifest['files']}
    require(set(expected)=={p.name for p in (payload/'ffmpeg').glob('*.dll')},'ffmpeg-abi-inventory')
    for name,sha in expected.items():require(digest(payload/'ffmpeg'/name)==sha.lower(),'ffmpeg-dependency-changed')
    buildconf=(ROOT/'third_party/ffmpeg/bin/buildconf.txt').read_text(encoding='utf-8-sig')
    require(not any(flag in buildconf for flag in ('--enable-gpl','--enable-nonfree','--enable-libx264')) and
            '--enable-libopenh264' in buildconf,'ffmpeg-license-policy')
    result=read_json(tools_directory/'build-result.json')
    require(result.get('state')=='BUILT_NOT_EXECUTED' and result.get('sources'),'tool-build-source-inventory')
    for row in result['sources']:
        path=safe_path(row['path'],ROOT,True);require(digest(path)==row['sha256'].lower(),'tool-build-source-changed')
    for row in result['files']:
        path=safe_path(str(tools_directory/row['path']),tools_directory,True)
        require(digest(path)==row['sha256'].lower() and path.stat().st_size==row['bytes'],'tool-build-file-changed')
    actual={folder+'/'+row['path'] for folder in ('desktop-tool','input-target','scm-fixture') for row in inventory(tools_directory/folder)}
    listed=[row['path'].replace('\\','/') for row in result['files']]
    require(len(set(listed))==len(listed) and set(listed)==actual,'tool-build-extra-or-missing-file')
    for folder,exe in (('desktop-tool','E2eDesktop.exe'),('input-target','RemoteAssist.InputTarget.exe')):
        safe_path(str(tools_directory/folder/exe),tools_directory,True)
    return {'state':'LOCAL_ARTIFACTS_VERIFIED_NOT_READY','candidateSha256':digest(candidate_directory/'candidate.json'),
            'buildResultSha256':digest(tools_directory/'build-result.json'),'payloadFiles':len(candidate['files']),
            'toolFiles':len(result['files']),'ffmpegDlls':len(expected),'encodersRuntimeVerified':False,'functionalActions':0}

def main(argv=None):
    p=argparse.ArgumentParser();p.add_argument('--candidate',required=True);p.add_argument('--tools',required=True);p.add_argument('--output');a=p.parse_args(argv)
    try:
        result=verify(a.candidate,a.tools)
        if a.output:
            from e2e_case_runtime import new_json
            new_json(safe_path(a.output),result)
        print(json.dumps(result));return 0
    except (CaseError,OSError,KeyError,TypeError) as e:
        print(json.dumps({'state':'NOT_READY','code':getattr(e,'code',type(e).__name__)}));return 10

if __name__=='__main__':raise SystemExit(main())
