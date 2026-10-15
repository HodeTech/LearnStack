#!/usr/bin/env python3
from pathlib import Path
import argparse
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
    def rejects(name,path,mutation):
        original=path.read_bytes();mutation(path)
        try:
            result=run();assert result.returncode!=0,f'{name} unexpectedly passed'
            results.append({'control':name,'exit':result.returncode,'reason':result.stderr.splitlines()[0]})
        finally:path.write_bytes(original)
    def wrong_library(path,package,wrong):
        value=json.loads(path.read_text());key=next(k for k in value['libraries'] if k.startswith(package+'/'));value['libraries'][package+'/'+wrong]=value['libraries'].pop(key);path.write_text(json.dumps(value))
    rejects('wrong Hangfire build pin',api,lambda p:wrong_library(p,'Hangfire.Core','1.8.24'))
    rejects('wrong pre-pruning Hosting pin',infrastructure,lambda p:wrong_library(p,'Microsoft.Extensions.Hosting','9.0.12'))
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
