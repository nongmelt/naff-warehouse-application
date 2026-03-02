import ctypes
import win32gui
import win32con
import sys

import win32api
import win32gui
import win32con
import ctypes

# Get the list of raw input devices
num_devices = ctypes.c_uint()
ctypes.windll.user32.GetRawInputDeviceList(
    None, ctypes.byref(num_devices), ctypes.sizeof(ctypes.c_uint64) * 2
)

device_list = (ctypes.c_uint64 * (num_devices.value * 2))()
ctypes.windll.user32.GetRawInputDeviceList(
    device_list, ctypes.byref(num_devices), ctypes.sizeof(ctypes.c_uint64) * 2
)

print(f"Found {num_devices.value} devices:")

for i in range(num_devices.value):
    h_device = device_list[i * 2]

    # Get device name length
    name_len = ctypes.c_uint()
    ctypes.windll.user32.GetRawInputDeviceInfoA(
        h_device, 0x20000007, None, ctypes.byref(name_len)
    )

    # Get device name
    name_buffer = ctypes.create_string_buffer(name_len.value)
    ctypes.windll.user32.GetRawInputDeviceInfoA(
        h_device, 0x20000007, name_buffer, ctypes.byref(name_len)
    )

    print(f"Handle: {h_device} | Name: {name_buffer.value.decode('utf-8', 'ignore')}")


# Define Raw Input structures and constants
class RAWINPUTDEVICE(ctypes.Structure):
    _fields_ = [
        ("usUsagePage", ctypes.c_ushort),
        ("usUsage", ctypes.c_ushort),
        ("dwFlags", ctypes.c_ulong),
        ("hwndTarget", ctypes.c_void_p),
    ]


# Register to receive raw input for Keyboards (UsagePage 0x01, Usage 0x06)
rid = RAWINPUTDEVICE()
rid.usUsagePage = 0x01
rid.usUsage = 0x06
rid.dwFlags = 0x00000100  # RIDEV_INPUTSINK to receive input even if window not focused
rid.hwndTarget = None

# Register the device
if not ctypes.windll.user32.RegisterRawInputDevices(
    ctypes.byref(rid), 1, ctypes.sizeof(rid)
):
    print("Failed to register raw input devices.")
    sys.exit(1)


def wnd_proc(hwnd, msg, wparam, lparam):
    if msg == win32con.WM_INPUT:
        # This is where the raw data arrives.
        # You would use GetRawInputData here to parse the RAWKEYBOARD struct.
        print("Raw input event detected!")
    return win32gui.DefWindowProc(hwnd, msg, wparam, lparam)


# Create a hidden window class to listen to the message loop
wc = win32gui.WNDCLASS()
wc.lpfnWndProc = wnd_proc
wc.lpszClassName = "RawInputListener"
class_atom = win32gui.RegisterClass(wc)
hwnd = win32gui.CreateWindow(class_atom, "HiddenListener", 0, 0, 0, 0, 0, 0, 0, 0, None)

print("Listening for HID input... (Ctrl+C to stop)")
win32gui.PumpMessages()
