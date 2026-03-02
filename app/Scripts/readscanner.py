import hid
import time

for device_dict in hid.enumerate():
    keys = list(device_dict.keys())
    keys.sort()
    for key in keys:
        print("%s : %s" % (key, device_dict[key]))
    print()

# Your scanner's IDs (extracted from the string you provided)
VENDOR_ID = 1504
PRODUCT_ID = 4608

# VENDOR_ID = 9969
# PRODUCT_ID = 34817


def read_barcode_scanner():
    try:
        # Connect to the device
        device = hid.device()
        device.open(VENDOR_ID, PRODUCT_ID)

        print(f"Successfully connected to: {device.get_product_string()}")
        print("Listening for scans... (Press Ctrl+C to stop)")

        # Non-blocking read mode
        device.set_nonblocking(True)

        while True:
            # Read 64 bytes (the standard HID report size)
            data = device.read(64)
            if data:
                # Scanners send data in HID reports (often raw bytes)
                # You may need to translate these bytes based on your scanner's manual
                print(f"Raw data received: {data}")

            time.sleep(0.01)  # Small sleep to prevent CPU spiking

    except IOError as e:
        print(f"Error: Could not open device. Is it connected? {e}")
    except KeyboardInterrupt:
        print("\nDisconnected.")
    finally:
        device.close()


if __name__ == "__main__":
    read_barcode_scanner()
