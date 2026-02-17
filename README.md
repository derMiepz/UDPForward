# UDPForward (LFS OutGauge UDP Forwarder)

`UDPForward` listens on one LFS OutGauge UDP port and forwards each packet to multiple UDP endpoints.  
This allows multiple OutGauge apps to run at the same time.

## Requirements

- Windows
- .NET 8 SDK (for building from source)

## Setup

1. Clone the repository.
2. Build or publish.
3. Run the forwarder once to generate `forwarder.json` (if it does not exist).
4. Edit `forwarder.json` with your app ports.
5. Point LFS OutGauge to the forwarder listen port.

## Configure LFS

Set in LFS:

1. `OutGauge IP`: IP of the machine running `UDPForward` (usually `127.0.0.1`).
2. `OutGauge Port`: same value as `listenPort` in `forwarder.json`.
3. Keep your preferred `OutGauge Mode` / `OutGauge Delay`.

## Configuration file

The program always reads `forwarder.json` from the same folder as `UdpForwarder.exe`.

If missing, it is auto-created on startup with defaults.

Fields:

- `listenHost`: local bind address for incoming OutGauge packets (`0.0.0.0` or `127.0.0.1`).
- `listenPort`: UDP port where LFS sends OutGauge packets.
- `statsIntervalSeconds`: periodic stats print interval (`0` disables stats).
- `targets`: destination app endpoints.

Target fields:

- `name`: label in logs.
- `host`: destination host (usually `127.0.0.1`).
- `port`: destination UDP port of the app.
- `enabled`: set `false` to disable a target temporarily.

Example config template: `UdpForwarder/forwarder.example.json`

## Run from source

```powershell
dotnet run --project UdpForwarder
```

## Build a standalone EXE

```powershell
dotnet publish UdpForwarder -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Output:

`UdpForwarder/bin/Release/net8.0/win-x64/publish/UdpForwarder.exe`

Run:

```powershell
.\UdpForwarder.exe
```

Stop with `Ctrl+C`.
