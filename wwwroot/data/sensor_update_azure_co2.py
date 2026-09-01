import csv
import json
import os
import sys

import requests

CSV_FILE = "sensor_log.csv"
HTTP_TIMEOUT = 10

# This is the same endpoint and override logic used by sensor_logger_azure.py.
DEFAULT_INGEST_URL = (
    "https://environmentalmonitor20260715220237-etdsargvdjaueygd"
    ".canadacentral-01.azurewebsites.net/api/sensor-ingest"
)
SENSOR_INGEST_URL = os.environ.get("SENSOR_INGEST_URL", DEFAULT_INGEST_URL)


def send_to_app(
    timestamp_utc,
    device_name,
    temp_c,
    temp_f,
    humidity,
    sensor_ip,
    co2,
):
    """POST one JSON telemetry reading using the same logic as sensor_logger_azure.py."""
    payload = {
        "recordedUtc": timestamp_utc,
        "deviceId": device_name,
        "temperatureC": temp_c,
        "temperatureF": temp_f,
        "humidity": humidity,
        "sensorIp": sensor_ip,
        "co2": co2,
    }

    response = requests.post(
        SENSOR_INGEST_URL,
        data=json.dumps(payload),
        headers={"Content-Type": "application/json"},
        timeout=HTTP_TIMEOUT,
    )
    response.raise_for_status()


def read_last_records(filename, count):
    with open(filename, "r", newline="", encoding="utf-8") as file:
        rows = list(csv.DictReader(file))

    return rows[-count:]


def main():
    if len(sys.argv) != 2:
        print("Usage: python3 sensor_update_azure.py <number_of_records>")
        print("Example: python3 sensor_update_azure.py 12")
        sys.exit(1)

    try:
        count = int(sys.argv[1])
        if count <= 0:
            raise ValueError
    except ValueError:
        print("Error: number_of_records must be a positive whole number.")
        sys.exit(1)

    try:
        rows = read_last_records(CSV_FILE, count)
    except FileNotFoundError:
        print(f"Error: could not find {CSV_FILE}")
        sys.exit(1)
    except Exception as exc:
        print(f"Error reading {CSV_FILE}: {exc}")
        sys.exit(1)

    if not rows:
        print(f"No records found in {CSV_FILE}.")
        return

    print(f"Sending last {len(rows)} record(s) to:")
    print(SENSOR_INGEST_URL)
    print()

    sent = 0
    failed = 0

    for number, row in enumerate(rows, start=1):
        try:
            timestamp_utc = row.get("timestamp_utc") or row.get("timestamp")
            device_name = row["device"]
            temp_c = float(row["temp_c"])
            temp_f = float(row["temp_f"])
            humidity = float(row["humid"])
            sensor_ip = row["ip_of_sensor"]

            # Indoor DHT22 rows have a blank CO2 field.
            # OUTSIDE SCD41 rows contain the CO2 reading.
            co2_text = row.get("co2", "").strip()
            co2 = float(co2_text) if co2_text else None

            send_to_app(
                timestamp_utc,
                device_name,
                temp_c,
                temp_f,
                humidity,
                sensor_ip,
                co2,
            )

            sent += 1
            print(
                f"[{number}/{len(rows)}] Sent "
                f"{timestamp_utc} {device_name} "
                f"{temp_f} F {humidity}%"
                + (f" CO2={co2:g} ppm" if co2 is not None else "")
            )

        except requests.RequestException as exc:
            failed += 1
            print(
                f"[{number}/{len(rows)}] Failed to send "
                f"{row.get('device', 'UNKNOWN')}: {exc}"
            )
        except (KeyError, TypeError, ValueError) as exc:
            failed += 1
            print(
                f"[{number}/{len(rows)}] Bad CSV record: {exc}"
            )

    print()
    print(f"Finished. Sent: {sent}, Failed: {failed}")


if __name__ == "__main__":
    main()
