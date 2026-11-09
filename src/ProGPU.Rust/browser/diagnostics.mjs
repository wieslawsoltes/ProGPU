// Optional test-only observations. No descriptor, submission, compiler, timeout,
// render path or recovery policy changes. GPU objects are never retained here.
export function installWebGpuDiagnostics() {
  const started=performance.now();
  const events=[],slow=[];
  const totals={submissions:0,completed:0,pending:0,maxPending:0};
  let serial=0;
  const stamp=()=>Math.round((performance.now()-started)*10)/10;
  const record=(kind,details={})=>{
    const event={ms:stamp(),kind,...details};events.push(event);
    if(events.length>128)events.shift();
    if(event.elapsedMs>=100||kind.endsWith('rejected')||kind==='device-lost'){
      slow.push(event);if(slow.length>64)slow.shift();
    }
  };
  // A plain-data snapshot can be recovered after a device loss. It does not
  // expose or retain a device, queue, surface, buffer or command generation.
  window.progpuGpuDiagnostics=()=>({elapsedMs:stamp(),totals:{...totals},events:[...events],slow:[...slow]});
  window.progpuGpuDiagnosticsMark=stage=>{
    record('host-stage',{stage});
    // The separate observer must arm before resize: a lost device can prevent
    // the host from reaching final capture. Console delivery never awaits GPU work.
    if(stage==='warm canvas burst: ten original draws')console.info('ProGPU diagnostic: warm canvas burst');
  };
  if(!globalThis.GPUQueue){record('WebGPU-unavailable');return;}
  const queues=new WeakMap();
  const submit=GPUQueue.prototype.submit;
  GPUQueue.prototype.submit=function(...args){
    const value=Reflect.apply(submit,this,args);
    let state=queues.get(this);
    if(!state){state={id:++serial,submitted:0,completed:0};queues.set(this,state);}
    const sequence=++state.submitted,at=performance.now();
    totals.submissions++;totals.pending++;totals.maxPending=Math.max(totals.maxPending,totals.pending);
    record('submit',{queue:state.id,sequence,pending:totals.pending});
    // Additional completion observations never await, delay or retry a submit.
    const done=()=>{const newlyCompleted=sequence-state.completed;if(newlyCompleted>0){state.completed=sequence;totals.completed+=newlyCompleted;totals.pending-=newlyCompleted;}};
    this.onSubmittedWorkDone().then(()=>{
      done();record('submit-completed',{queue:state.id,sequence,elapsedMs:Math.round((performance.now()-at)*10)/10,pending:totals.pending});
    },error=>{record('submit-rejected',{queue:state.id,sequence,error:String(error)});});
    return value;
  };
  for(const [prototype,name]of [[GPUDevice.prototype,'createRenderPipeline'],[GPUDevice.prototype,'createComputePipeline'],[GPUCanvasContext.prototype,'configure']]){
    const original=prototype[name];prototype[name]=function(...args){
      const at=performance.now();record(name+'-begin',{label:args[0]?.label??'',...(name==='configure'?{width:this.canvas.width,height:this.canvas.height}:{})});
      const result=Reflect.apply(original,this,args);
      record(name+'-returned',{elapsedMs:Math.round((performance.now()-at)*10)/10});return result;
    };
  }
  const request=GPUAdapter.prototype.requestDevice;
  GPUAdapter.prototype.requestDevice=function(...args){
    const promise=Reflect.apply(request,this,args);
    promise.then(device=>{record('device-created');device.lost.then(info=>record('device-lost',{reason:info.reason,message:info.message}));},error=>record('device-rejected',{error:String(error)}));
    return promise;
  };
}
