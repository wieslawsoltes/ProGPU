"""Read-only Windows CPU/private-byte observation of this diagnostic child."""
import ctypes
from ctypes import wintypes
import os


def sample(handle, pid):
    if os.name != 'nt' or handle is None:
        return {'available':False,'reason':'Windows-only observation'}
    class Memory(ctypes.Structure):
        _fields_ = [('cb',wintypes.DWORD),('PageFaultCount',wintypes.DWORD)] + [
            (n,ctypes.c_size_t) for n in ['PeakWorkingSetSize','WorkingSetSize','QuotaPeakPagedPoolUsage',
              'QuotaPagedPoolUsage','QuotaPeakNonPagedPoolUsage','QuotaNonPagedPoolUsage',
              'PagefileUsage','PeakPagefileUsage','PrivateUsage']]
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    psapi=ctypes.WinDLL('psapi',use_last_error=True)
    kernel.GetProcessTimes.argtypes=[wintypes.HANDLE]+[ctypes.POINTER(wintypes.FILETIME)]*4
    kernel.GetProcessTimes.restype=wintypes.BOOL
    psapi.GetProcessMemoryInfo.argtypes=[wintypes.HANDLE,ctypes.POINTER(Memory),wintypes.DWORD]
    psapi.GetProcessMemoryInfo.restype=wintypes.BOOL
    # Borrow Popen's retained exact process handle; never reopen a reusable PID.
    created,ended,kernel_time,user_time=(wintypes.FILETIME() for _ in range(4))
    result={'pid':pid,'available':True}
    if kernel.GetProcessTimes(handle,ctypes.byref(created),ctypes.byref(ended),ctypes.byref(kernel_time),ctypes.byref(user_time)):
        ticks=lambda v:(v.dwHighDateTime<<32)|v.dwLowDateTime
        result['cpuSeconds']=(ticks(kernel_time)+ticks(user_time))/10_000_000
    else: result['timesError']=ctypes.get_last_error()
    memory=Memory();memory.cb=ctypes.sizeof(memory)
    if psapi.GetProcessMemoryInfo(handle,ctypes.byref(memory),memory.cb):
        result.update(privateBytes=memory.PrivateUsage,workingSetBytes=memory.WorkingSetSize,peakWorkingSetBytes=memory.PeakWorkingSetSize)
    else:result['memoryError']=ctypes.get_last_error()
    return result
