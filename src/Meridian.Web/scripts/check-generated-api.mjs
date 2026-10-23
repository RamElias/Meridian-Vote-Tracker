import { mkdtempSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, relative } from 'node:path';
import { spawnSync } from 'node:child_process';
const temporary = mkdtempSync(join(tmpdir(), 'meridian-client-'));
const files = root => readdirSync(root, {recursive:true, withFileTypes:true}).filter(entry=>entry.isFile()).map(entry=>relative(root,join(entry.parentPath,entry.name))).sort();
try {
  const result=spawnSync(process.execPath,['node_modules/ng-openapi-gen/lib/index.js','--input','../Meridian.Api/openapi.json','--output',temporary,'--services','true','--promises','false','--indexFile','true','--silent','true'],{stdio:'inherit'});
  if(result.status!==0) process.exitCode=1;
  else {
    const current='src/app/generated-api';
    const generated=files(temporary), checkedIn=files(current);
    if(JSON.stringify(generated)!==JSON.stringify(checkedIn) || generated.some(file=>readFileSync(join(temporary,file),'utf8').replaceAll('\r\n','\n')!==readFileSync(join(current,file),'utf8').replaceAll('\r\n','\n'))) {
      throw new Error('Generated API client differs from openapi.json. Run npm run generate:api and commit its output.');
    }
    console.log('PASS: generated Angular client matches OpenAPI.');
  }
} finally { rmSync(temporary,{recursive:true,force:true}); }
