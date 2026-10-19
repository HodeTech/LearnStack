#!/usr/bin/env python3
from pathlib import Path
import argparse
import hashlib
import json
import shutil
import subprocess
import tempfile

parser=argparse.ArgumentParser(description="Exercise falsifiable distribution and recipient-replacement controls against an actual publish")
parser.add_argument("publish",type=Path)
parser.add_argument("--assets",required=True,type=Path)
parser.add_argument("--infrastructure-assets",required=True,type=Path)
args=parser.parse_args()
verifier=Path(__file__).with_name("verify-distribution.py")
with tempfile.TemporaryDirectory(prefix='learnstack-hangfire-distribution-controls-') as temporary:
    task=Path(temporary)
    published=task/'publish'
    shutil.copytree(args.publish,published)
    api=task/'api-assets.json';api.write_bytes(args.assets.read_bytes())
    infrastructure=task/'infrastructure-assets.json';infrastructure.write_bytes(args.infrastructure_assets.read_bytes())
    command=['python3',str(verifier),str(published),'--assets',str(api),'--infrastructure-assets',str(infrastructure)]
    def run():return subprocess.run(command,capture_output=True,text=True)
    baseline=run();assert baseline.returncode==0,baseline.stderr
    results=[]
    self_qualified=subprocess.run(['python3',str(published/'third-party/hangfire'/verifier.name),*command[2:]],capture_output=True,text=True)
    assert self_qualified.returncode!=0 and self_qualified.stderr.strip()=='Run the verifier from the reviewed repository checkout'
    results.append({'control':'published verifier cannot qualify its own baseline','exit':self_qualified.returncode,
        'reason':self_qualified.stderr.strip()})
    def rejects_together(name,paths,mutation,override=None,reason=None):
        originals={path:path.read_bytes() for path in paths}
        try:
            mutation()
            result=subprocess.run(override or command,capture_output=True,text=True)
            assert result.returncode!=0,f'{name} unexpectedly passed'
            if reason is not None:assert result.stderr.strip()==reason,result.stderr
            results.append({'control':name,'exit':result.returncode,'reason':result.stderr.splitlines()[0]})
        finally:
            for path,original in originals.items():path.write_bytes(original)
    def rejects(name,path,mutation):
        rejects_together(name,[path],lambda:mutation(path))
    def wrong_library(path,package,wrong):
        value=json.loads(path.read_text());key=next(k for k in value['libraries'] if k.startswith(package+'/'));value['libraries'][package+'/'+wrong]=value['libraries'].pop(key);path.write_text(json.dumps(value))
    rejects('wrong Hangfire build pin',api,lambda p:wrong_library(p,'Hangfire.Core','1.8.24'))
    rejects('wrong pre-pruning Hosting pin',infrastructure,lambda p:wrong_library(p,'Microsoft.Extensions.Hosting','9.0.12'))
    rejects('wrong Dapper notice build pin',api,lambda p:wrong_library(p,'Dapper','2.0.124'))
    rejects('wrong Dapper.AOT notice build pin',infrastructure,lambda p:wrong_library(p,'Dapper.AOT','1.0.49'))
    kit=published/'third-party/hangfire'
    rejects('missing corresponding source',kit/'postgres/corresponding-source.tar.gz',lambda p:p.unlink())
    rejects('corrupt corresponding source',kit/'core/corresponding-source.tar.gz',lambda p:p.write_bytes(b'dirty-source-control'))
    rejects('missing merged Cronos source',kit/'cronos/corresponding-source.tar.gz',lambda p:p.unlink())
    rejects('corrupt merged Cronos source',kit/'cronos/corresponding-source.tar.gz',lambda p:p.write_bytes(b'dirty-source-control'))
    rejects('missing merged Cronos MIT notice',kit/'cronos/LICENSE',lambda p:p.unlink())
    rejects('changed merged Cronos MIT notice',kit/'cronos/LICENSE',lambda p:p.write_bytes(b'dirty-licence-control'))
    def wrong_embedded_cronos_pin(path):
        value=json.loads(path.read_text());value['cronos']['version']='0.11.0';path.write_text(json.dumps(value))
    rejects('wrong merged Cronos pin',kit/'source-sha256.json',wrong_embedded_cronos_pin)
    rejects('missing LGPL licence',kit/'postgres/COPYING.LESSER',lambda p:p.unlink())
    rejects('changed licence',kit/'core/LICENSE.md',lambda p:p.write_bytes(b'dirty-licence-control'))
    rejects('missing replaceable assembly',published/'Hangfire.PostgreSql.dll',lambda p:p.unlink())
    rejects('modified original assembly',published/'Hangfire.Core.dll',lambda p:p.write_bytes(p.read_bytes()+b'replacement-control'))
    def cotamper(path,manifest,key):
        path.write_bytes(path.read_bytes()+b'co-tamper-control')
        value=json.loads(manifest.read_text())
        if isinstance(value[key],dict):value[key]['sha256']=hashlib.sha256(path.read_bytes()).hexdigest()
        else:value[key]=hashlib.sha256(path.read_bytes()).hexdigest()
        manifest.write_text(json.dumps(value))
    core=published/'Hangfire.Core.dll';original_manifest=kit/'original-binaries.json'
    rejects_together('assembly and manifest co-tamper',[core,original_manifest],
        lambda:cotamper(core,original_manifest,core.name),reason='Changed distribution manifest: original-binaries.json')
    notice=kit/'core/LICENSE.md';material_manifest=kit/'material-sha256.json'
    rejects_together('notice and manifest co-tamper',[notice,material_manifest],
        lambda:cotamper(notice,material_manifest,'core/LICENSE.md'),reason='Changed distribution manifest: material-sha256.json')
    source=kit/'core/corresponding-source.tar.gz';source_manifest=kit/'source-sha256.json'
    def swap_source():
        source.write_bytes((kit/'cronos/corresponding-source.tar.gz').read_bytes())
        value=json.loads(source_manifest.read_text());value['core']['sha256']=hashlib.sha256(source.read_bytes()).hexdigest()
        source_manifest.write_text(json.dumps(value))
    rejects_together('source archive and manifest co-tamper',[source,source_manifest],swap_source,
        reason='Changed distribution manifest: source-sha256.json')
    # Independently reach the PAX check after hash validation: only the temporary
    # checkout's baseline is adjusted; the repository authority stays untouched.
    trusted=task/'checkout/third-party/hangfire';shutil.copytree(verifier.parent,trusted,ignore=shutil.ignore_patterns('__pycache__'))
    def swapped_source_identity():
        swap_source();(trusted/'source-sha256.json').write_bytes(source_manifest.read_bytes())
    rejects_together('source archive commit identity',[source,source_manifest,trusted/'source-sha256.json'],
        swapped_source_identity,override=['python3',str(trusted/verifier.name),*command[2:]],
        reason='Unqualified corresponding source archive identity: core')
    deps=next(published.glob('*.deps.json'))
    rejects('wrong shipped Newtonsoft pin',deps,lambda p:wrong_library(p,'Newtonsoft.Json','11.0.1'))
    runtime=next(published.glob('*.runtimeconfig.json'))
    def no_framework(path):
        value=json.loads(path.read_text());value['runtimeOptions']['frameworks']=[];path.write_text(json.dumps(value))
    rejects('pruned Hosting without framework',runtime,no_framework)
    original=(published/'Hangfire.Core.dll').read_bytes();(published/'Hangfire.Core.dll').write_bytes(original+b'replacement-control')
    replacement=subprocess.run(command+['--allow-replacements'],capture_output=True,text=True)
    assert replacement.returncode==0,replacement.stderr
    assert json.loads(replacement.stdout)['original_stock_assemblies']['Hangfire.Core.dll'] is False
    results.append({'control':'recipient replacement inspection','exit':0,'stock_core':False})
    receipt={'baseline_exit':baseline.returncode,'dirty_and_replacement_controls':results}
    print(json.dumps(receipt,indent=2))
