"""Windows-only, suspended launch into a kill-on-close job; no PID lookup."""
import ctypes as C
from ctypes import wintypes as W
import json
import pathlib
import subprocess
import time


def run(argv, log_path, receipt_path, seconds=60, observer=None):
    if not 1 <= seconds <= 60:
        raise ValueError('The diagnostic bound is at most 60 seconds.')
    k = C.WinDLL('kernel32', use_last_error=True)
    class SA(C.Structure):
        _fields_ = [('length', W.DWORD), ('descriptor', C.c_void_p), ('inherit', W.BOOL)]
    class SI(C.Structure):
        _fields_ = [('cb', W.DWORD), ('reserved', W.LPWSTR), ('desktop', W.LPWSTR), ('title', W.LPWSTR),
                    ('x', W.DWORD), ('y', W.DWORD), ('xs', W.DWORD), ('ys', W.DWORD),
                    ('xc', W.DWORD), ('yc', W.DWORD), ('fill', W.DWORD), ('flags', W.DWORD),
                    ('show', W.WORD), ('cbReserved', W.WORD), ('reserved2', C.c_void_p),
                    ('input', W.HANDLE), ('output', W.HANDLE), ('error', W.HANDLE)]
    class PI(C.Structure):
        _fields_ = [('process', W.HANDLE), ('thread', W.HANDLE), ('pid', W.DWORD), ('tid', W.DWORD)]
    class BASIC(C.Structure):
        _fields_ = [('processTime', C.c_int64), ('jobTime', C.c_int64), ('flags', W.DWORD),
                    ('minWorking', C.c_size_t), ('maxWorking', C.c_size_t), ('activeLimit', W.DWORD),
                    ('affinity', C.c_size_t), ('priority', W.DWORD), ('scheduling', W.DWORD)]
    class IO(C.Structure):
        _fields_ = [(name, C.c_uint64) for name in ('readOps','writeOps','otherOps','readBytes','writeBytes','otherBytes')]
    class EXT(C.Structure):
        _fields_ = [('basic', BASIC), ('io', IO), ('processMemory', C.c_size_t),
                    ('jobMemory', C.c_size_t), ('peakProcessMemory', C.c_size_t), ('peakJobMemory', C.c_size_t)]
    signatures = {
        'CreateJobObjectW': ([C.c_void_p, W.LPCWSTR], W.HANDLE),
        'SetInformationJobObject': ([W.HANDLE, C.c_int, C.c_void_p, W.DWORD], W.BOOL),
        'CreateFileW': ([W.LPCWSTR,W.DWORD,W.DWORD,C.POINTER(SA),W.DWORD,W.DWORD,W.HANDLE],W.HANDLE),
        'CreateProcessW': ([W.LPCWSTR,W.LPWSTR,C.c_void_p,C.c_void_p,W.BOOL,W.DWORD,C.c_void_p,W.LPCWSTR,C.POINTER(SI),C.POINTER(PI)],W.BOOL),
        'AssignProcessToJobObject': ([W.HANDLE,W.HANDLE],W.BOOL),
        'ResumeThread': ([W.HANDLE],W.DWORD),
        'WaitForSingleObject': ([W.HANDLE,W.DWORD],W.DWORD),
        'GetExitCodeProcess': ([W.HANDLE,C.POINTER(W.DWORD)],W.BOOL),
        'TerminateProcess': ([W.HANDLE,W.UINT],W.BOOL),
        'TerminateJobObject': ([W.HANDLE,W.UINT],W.BOOL),
        'CloseHandle': ([W.HANDLE],W.BOOL),
    }
    for name,(args,result) in signatures.items():
        fn=getattr(k,name);fn.argtypes=args;fn.restype=result
    def check(ok):
        if not ok: raise C.WinError(C.get_last_error())
    handles=[]; pi=PI(); assigned=False; finished=False
    receipt={'argv':argv,'seconds':seconds,'started':time.time(),'timedOut':False}
    try:
        job=k.CreateJobObjectW(None,None);check(job);handles.append(job)
        limits=EXT();limits.basic.flags=0x2000 # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        check(k.SetInformationJobObject(job,9,C.byref(limits),C.sizeof(limits)))
        sa=SA(C.sizeof(SA),None,True)
        out=k.CreateFileW(str(pathlib.Path(log_path).resolve()),0x40000000,1,C.byref(sa),2,0x80,None)
        check(out not in (None,C.c_void_p(-1).value));handles.append(out)
        inp=k.CreateFileW('NUL',0x80000000,3,C.byref(sa),3,0x80,None)
        check(inp not in (None,C.c_void_p(-1).value));handles.append(inp)
        si=SI();si.cb=C.sizeof(SI);si.flags=0x100;si.input=inp;si.output=out;si.error=out
        command=C.create_unicode_buffer(subprocess.list2cmdline(argv))
        start=time.monotonic()
        check(k.CreateProcessW(str(pathlib.Path(argv[0]).resolve()),command,None,None,True,0x08000004,None,None,C.byref(si),C.byref(pi)))
        handles.extend([pi.process,pi.thread]);receipt['pid']=pi.pid
        check(k.AssignProcessToJobObject(job,pi.process));assigned=True
        check(k.ResumeThread(pi.thread)!=0xffffffff)
        while True:
            remaining=max(0,int((seconds-(time.monotonic()-start))*1000))
            wait=k.WaitForSingleObject(pi.process,min(50,remaining) if observer else remaining)
            if wait!=0x102 or remaining<=50:break
            observer(pi.process)
        if wait==0x102:
            receipt['timedOut']=True;check(k.TerminateJobObject(job,124));check(k.WaitForSingleObject(pi.process,5000)==0)
        else: check(wait==0)
        code=W.DWORD();check(k.GetExitCodeProcess(pi.process,C.byref(code)))
        receipt['exitUnsigned']=code.value;receipt['exitHex']=f'0x{code.value:08X}'
        receipt['elapsedSeconds']=time.monotonic()-start;finished=True
    finally:
        if pi.process and not finished:
            if assigned:k.TerminateJobObject(job,125)
            else:k.TerminateProcess(pi.process,125)
            k.WaitForSingleObject(pi.process,5000)
        # Closing the owned job also retires any descendant left by ProcDump.
        for h in reversed(handles):k.CloseHandle(h)
        receipt['finished']=time.time()
        pathlib.Path(receipt_path).write_text(json.dumps(receipt,indent=2)+'\n',encoding='utf-8')
    return receipt
