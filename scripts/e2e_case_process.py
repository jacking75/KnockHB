"""Read-only Windows process identity checks; handles prevent PID reuse races."""
import ctypes
from ctypes import wintypes
from e2e_case_runtime import require


def exists(pid):
    """Conservatively treat a reused PID as live; this function never kills it."""
    api=ctypes.WinDLL('kernel32',use_last_error=True)
    api.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD];api.OpenProcess.restype=wintypes.HANDLE
    api.GetExitCodeProcess.argtypes=[wintypes.HANDLE,ctypes.POINTER(wintypes.DWORD)]
    api.CloseHandle.argtypes=[wintypes.HANDLE]
    handle=api.OpenProcess(0x1000,False,pid)
    if not handle:
        require(ctypes.get_last_error()==87,'controller-liveness-unavailable')
        return False
    try:
        code=wintypes.DWORD();require(api.GetExitCodeProcess(handle,ctypes.byref(code)),'controller-liveness-unavailable')
        return code.value==259
    finally:api.CloseHandle(handle)


def alive(identity):
    api=ctypes.WinDLL('kernel32',use_last_error=True)
    api.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD]
    api.OpenProcess.restype=wintypes.HANDLE
    api.CloseHandle.argtypes=[wintypes.HANDLE]
    api.GetProcessTimes.argtypes=[wintypes.HANDLE,*([ctypes.POINTER(wintypes.FILETIME)]*4)]
    api.GetExitCodeProcess.argtypes=[wintypes.HANDLE,ctypes.POINTER(wintypes.DWORD)]
    api.QueryFullProcessImageNameW.argtypes=[wintypes.HANDLE,wintypes.DWORD,wintypes.LPWSTR,ctypes.POINTER(wintypes.DWORD)]
    handle=api.OpenProcess(0x1000,False,identity['pid'])
    if not handle:
        require(ctypes.get_last_error()==87,'process-identity-unavailable')
        return False
    try:
        code=wintypes.DWORD();require(api.GetExitCodeProcess(handle,ctypes.byref(code)),'process-exit-unavailable')
        times=[wintypes.FILETIME() for _ in range(4)]
        require(api.GetProcessTimes(handle,*[ctypes.byref(t) for t in times]),'process-times-unavailable')
        start=(times[0].dwHighDateTime<<32)+times[0].dwLowDateTime+504911232000000000
        name=ctypes.create_unicode_buffer(32768);size=wintypes.DWORD(len(name))
        require(api.QueryFullProcessImageNameW(handle,0,name,ctypes.byref(size)),'process-image-unavailable')
        require(start==identity['start'] and name.value.casefold()==identity['image'].replace('/','\\').casefold(),'process-identity-changed')
        return code.value==259
    finally:api.CloseHandle(handle)
