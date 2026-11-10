import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import path from 'node:path';
const root=path.resolve(process.argv[2]);
const types={'.html':'text/html','.js':'text/javascript','.wasm':'application/wasm'};
const server=createServer(async (req,res)=>{
  try {
    const pathname=decodeURIComponent(new URL(req.url,'http://localhost').pathname);
    if(pathname==='/favicon.ico'){res.writeHead(204);res.end();return;}
    const file=path.resolve(root, '.'+(pathname==='/'?'/index.html':pathname));
    if(!file.startsWith(root+path.sep)){res.writeHead(403);res.end();return;}
    const body=await readFile(file);
    res.writeHead(200,{'Content-Type':types[path.extname(file)]??'application/octet-stream','Cache-Control':'no-store'});
    res.end(body);
  } catch {res.writeHead(404);res.end();}
});
await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
const child=spawn(process.execPath,['src/ProGPU.Rust/browser/test.mjs'],{stdio:'inherit',env:{...process.env,
  PROGPU_RUST_BROWSER_URL:`http://127.0.0.1:${server.address().port}`}});
const timeout=setTimeout(()=>{
  console.error('Browser validation exceeded its 180-second deadline; the last stage is logged above.');
  child.kill('SIGTERM');
},180000);
child.on('exit',(code)=>{clearTimeout(timeout);server.close();process.exitCode=code??1;});
for(const signal of ['SIGINT','SIGTERM']) process.on(signal,()=>{child.kill(signal);server.close();});
