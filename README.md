# YUHU Admin BLE — A.3.0 (Windows central spike)

A minimal **Windows** console app that proves the laptop can talk to a YUHU
phone over the **custom BLE** protocol as a **BLE central** (laptop scans and
connects outward). This is the de-risking spike for the Windows admin node.

**What it does:**
- Scans for the frozen YUHU Service UUID (`0da35ffe-f05d-4824-a6ad-0f6a1277c82b`)
- Lists discovered devices (name, logical id, address, RSSI)
- Connects to one, resolves the TX/RX/Identity/Capability characteristics
- Subscribes to TX notifications, reads the Identity characteristic
- Runs the keepalive PING loop (2s) and link-dead timeout (6s)

**What it deliberately does NOT do (yet):**
- No advertising (central-only; peripheral is a future extension behind the same
  `IBleTransport` interface)
- No admin auth / super-user challenge
- No Noise handshake
- No dashboard integration

Those come in the full A.3 once this spike confirms Windows BLE works.

## Requirements (Windows machine)

- **Windows 10 (2004 / build 19041) or Windows 11** with a BLE adapter.
- **.NET 8 SDK or newer** — <https://dotnet.microsoft.com/download>
- Bluetooth turned ON. First run may prompt for Bluetooth permission
  (Settings → Privacy → Bluetooth → allow desktop apps).
- A YUHU phone running with BLE transport active and advertising.

## Build & run (Windows)

```powershell
cd YUHU-admin-ble
dotnet run
```

If your installed Windows SDK differs, edit `TargetFramework` in
`YuhuAdminBle.csproj` (min `net8.0-windows10.0.17763.0`).

### Using the spike

```
Scanning… commands: <number> connect | r rescan list | q quit

  [0] Alice id=46b3a5... addr=AA:BB:CC:DD:EE:FF rssi=-54
  [1] (no name) id=? addr=11:22:33:44:55:66 rssi=-78

0            <- type the index to connect to device [0]
r            <- reprint the discovered list
q            <- quit
```

On a successful connect you'll see `>>> CONNECTED`, the `[identity]` string,
the TX subscribe status, and keepalive activity.

## Success criteria (what "the spike worked" means)

1. Your YUHU phone appears in the scan list (by Service UUID), ideally with its
   name + logical id from the advert.
2. `dotnet run` → connect by index → `>>> CONNECTED`.
3. TX subscribe returns `Success`; the Identity read prints
   `<base64 pubkey>|<name>`.
4. The link stays up (keepalive) rather than immediately dropping.

If all four hold, Windows BLE central works against a real phone and we proceed
to full A.3 (admin challenge + direct-peer log streaming). If it fails, we learn
the exact Windows constraint cheaply.

## A.3 — Admin direct-peer logs (authenticated)

Once the spike connects, the admin workflow adds authenticated log access:

### One-time setup (per admin laptop)
1. Run the app once; it generates an admin keypair in `admin-identity/admin-key.dpapi`
   (DPAPI-encrypted, bound to your Windows user) and prints the admin PUBLIC key.
2. Offline, mint a root-signed cert for that public key:
   ```bash
   node tools/mint-admin-cert.js --rootPriv /offline/root-private.key \
     --adminPub <printed admin public key> --label admin-laptop-1 --days 365 \
     --out admin-identity/admin-cert.json
   ```
3. Re-run the app; it loads the cert.

### Using it
With a phone built with `admin.enabled=true` and `transport.impl=ble`:
```
<index>      connect to the phone
auth         run the admin handshake (HELLO -> CHALLENGE -> PROOF -> OK)
logs         pull a log tail
logs MeshFlow  pull a tail filtered to the MeshFlow tag
live         start live log streaming
stop         stop live streaming
```

A successful `auth` prints `>>> ADMIN AUTHENTICATED`. `logs` then returns the
phone's recent log lines over the direct BLE link — no cable. The phone only
honors this if its cert verifies AND `admin.enabled` is on; otherwise it drops
the admin frames.

## Tests (cross-platform, run anywhere)

The framing logic is pure .NET (no WinRT) and verifies the interop **golden
vector** against the Android `BleFraming.kt`:

```bash
cd tests
dotnet test
```

(Target `net10.0` locally, or `net8.0` on the Windows box — adjust
`tests/YuhuAdminBle.Tests.csproj` TargetFramework to a runtime you have.)

## Project layout

| File | Role |
|------|------|
| `Protocol.cs` | Frozen UUIDs + 9-byte frame header constants (platform-neutral) |
| `BleFraming.cs` | Split/reassemble chunks, golden-vector compatible (platform-neutral) |
| `IBleTransport.cs` | Role-agnostic transport interface (central now, peripheral/multi-laptop later) |
| `WinRtCentralTransport.cs` | WinRT central implementation (scan/connect/subscribe/keepalive) |
| `Program.cs` | Interactive spike driver |
| `tests/` | Cross-platform framing tests (golden vector) |

## Known Windows BLE caveats to watch for

- **Permission prompt:** if scanning returns nothing, check Bluetooth privacy
  settings for desktop apps.
- **Pairing/bonding:** some phones require the devices be bonded before GATT
  reads succeed; if Identity read fails with `AccessDenied`, pair the phone with
  the laptop once in Windows Bluetooth settings.
- **Active scan needed for name:** the advertised name/id live in the scan
  response, so the watcher uses Active scanning.
