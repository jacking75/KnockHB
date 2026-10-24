# RemoteAssist

<p align="center">
  <strong><a href="README_kr.md">🇰🇷 한국어 문서 · Read in Korean →</a></strong>
</p>

![RemoteAssist — Windows remote support with a single launch and a fixed 9-digit support ID](docs/assets/readme/remoteassist-hero.png)

<p align="center">
  <strong>The remote user launches once. The technician resolves issues in a browser.</strong><br>
  Screen control · Admin tasks · File transfer · Diagnostics · Remote commands, all in one place for internal PC support.
</p>

<p align="center">
  <a href="#key-benefits">Key benefits</a> ·
  <a href="#support-workflow">Support workflow</a> ·
  <a href="#connection-architecture">Architecture</a> ·
  <a href="#development-setup">Development setup</a> ·
  <a href="#usage-example">Usage example</a> ·
  <a href="docs/README.md">Developer docs</a>
</p>

**RemoteAssist** is a Windows remote support tool for internal PC operations teams. The person requesting support **runs the app once as administrator**, and a technician connects using a **fixed 9-digit support ID**. The technician does not need to install a separate Viewer application.

The repository is named **KnockHB**; the product name and code namespace are **RemoteAssist**. The image above illustrates the product's features.

## Key benefits

| Benefit | What it means for your team |
|---|---|
| **Launch once to enable support** | The remote user's initial administrator launch authorizes support. There is no PIN to share or additional approval for each connection. |
| **Your browser is the support console** | Technicians connect through the web Viewer and handle input, files, chat, diagnostics, and commands while viewing the remote screen. |
| **Reconnect with the same ID** | Each PC has a fixed 9-digit support ID. Connections recover automatically, and support authorization survives service restarts and updates. |
| **Control administrator workflows** | Administrator mode activates automatically on connection. Technicians can interact with elevated windows, the UAC secure desktop, and the lock screen, and send Ctrl+Alt+Del. |
| **Run routine tasks remotely** | System diagnostics, predefined Runbooks, and PowerShell commands running as the user or SYSTEM support remote investigation and remediation. |
| **Direct P2P between the two PCs** | Screen, input, and files travel directly between the PCs at their designated IP addresses. The tunnel server serves the Viewer and handles connection setup. |

The remote user can see the **connection indicator and the tray's Stop support button**. When the remote user stops support, support authorization is also revoked.

## Support workflow

| Step | Remote user | Technician |
|---|---|---|
| **01 · Launch** | Download the app and run it once as administrator. | Prepare the tunnel server and web Viewer on the technician's PC. |
| **02 · Connect** | Share the fixed 9-digit support ID shown in the app. | Enter the support ID in a browser and connect. |
| **03 · Resolve** | Receive support without further interaction. | Control the screen, perform admin tasks, transfer files, run diagnostics, and execute commands. |

Support authorization is tied to the current Windows logon. After a **reboot, logoff, Windows user change, Stop support action, or tray exit**, the remote user must run the app as administrator again. See the [product contract](docs/README.md#product-contract) and [workflows](docs/README.md#workflows) for details.

## Features

| Area | Capabilities |
|---|---|
| **Screen** | H.264 streaming, hardware encoding with a software fallback, WGC/DXGI capture, adaptive quality, and multi-monitor switching |
| **Input and admin control** | Mouse, keyboard, and Korean text input; interaction with elevated windows, UAC, and the lock screen; Ctrl+Alt+Del |
| **Files and clipboard** | Bidirectional file transfer with SHA-256 integrity verification; send and read clipboard text |
| **Communication** | Bidirectional chat, a visible connection indicator, and immediate support termination |
| **Diagnostics and automation** | System diagnostics, predefined Runbooks, and remote PowerShell commands running as the user or SYSTEM |
| **Connection management** | Fixed support IDs, automatic reconnection, and Viewer handoff when another technician connects |

Clipboard text is sent or read explicitly through the Viewer. It is not synchronized automatically.

## Connection architecture

![RemoteAssist architecture — SYSTEM service and user-session helpers on the remote PC, tunnel server and web Viewer on the technician's PC, with separate connection setup and direct P2P data paths](docs/assets/readme/remoteassist-architecture.png)

**Connection setup** uses the Go tunnel server on the technician's PC over HTTPS/WSS. The remote PC connects to the server address provided during installation. The server serves the Viewer page, manages support ID rooms, and handles the initial SDP/ICE exchange.

**Screen and data** travel over a direct WebRTC P2P connection between Host.Helper and the web Viewer. Media uses DTLS-SRTP; data channels use SCTP over DTLS. There is no STUN, TURN, or relay.

**Inside the remote PC**, the SYSTEM service manages support authorization and helper lifetimes. Helper and SYSTEM AdminHelper handle capture and input in the target user's session. The tray provides the connection indicator and Stop support control. RunbookWorker executes predefined tasks and remote commands. See the [architecture](docs/README.md#architecture) for responsibilities and process boundaries.

### Technology stack

| Area | Technology |
|---|---|
| **Host** | C# · .NET 10 LTS · WPF · Windows 10/11 x64 |
| **Screen and media** | SIPSorcery 10.0.x · FFmpeg 8.1 LGPL · H.264 · WGC with DXGI fallback |
| **Admin control** | SYSTEM AdminHelper · Win32 `SendInput` · `SendSAS` |
| **Tunnel server** | Go · `net/http` · `coder/websocket` · TCP 25001 |
| **Viewer** | Static HTML/CSS/vanilla JavaScript · Browser-native WebRTC · No Node.js build step |
| **Connection protection** | HTTPS/WSS · Self-signed certificates with Host certificate pinning on private networks (`RA_MODE=lan`) |

## Development setup

Run the following commands in PowerShell from the repository root.

| Prerequisite | Purpose |
|---|---|
| Windows 10/11 x64 + .NET 10 SDK, including WindowsDesktop | Host builds and tests |
| Go 1.27 | Tunnel server builds and tests |
| Node.js 22 or later | Viewer tests |
| Python + `cryptography` | TLS test certificate generation |

### 1. Prepare FFmpeg

Download and place the pinned LGPL DLLs. Do not commit the binaries to the repository.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-ffmpeg.ps1
```

### 2. Build and run regression tests

```powershell
dotnet restore RemoteAssist.slnx --locked-mode
dotnet build RemoteAssist.slnx --no-restore
dotnet run --project tests/RemoteAssist.Host.Core.Tests --no-build

Push-Location server/tunnel
try {
    go build ./...
    go vet ./...
    go test ./...
} finally {
    Pop-Location
}

node --test web/viewer/tests/*.test.js
```

Use `scripts/verify-local.ps1` for batch verification and evidence collection. For Host deployment and first launch, see [installation and updates](deploy/installation.md), [initial administrator launch](docs/README.md#startup-preparation), and [tunnel server operations](deploy/tunnel/README.md).

Single-PC loopback checks involving screen capture and live two-PC tests require a separate plan, preparation, and explicit approval before execution. See the [live two-PC test guide](docs/README.md#live-e2e-test) for procedures and acceptance criteria.

## Repository structure

| Path | Contents |
|---|---|
| `src/` | Host.Service · Host.Tray · Host.Helper · Host.AdminHelper · Host.RunbookWorker · Host.Core · Protocol · Update |
| `server/tunnel/` | Go tunnel server, built separately from the .NET solution |
| `web/viewer/` | Web Viewer for technicians |
| `tests/` | Host.Core regression tests, installation and preparation script contract tests, and shared fixtures |
| `tools/` | Development diagnostics such as HostRun, CaptureDump, and LoopbackCheck |
| `scripts/` | Build, packaging, development installation, and live-test preparation and measurement scripts |
| `deploy/` | Installation and preparation scripts, Runbooks, and the tunnel server container |
| `third_party/ffmpeg/` | FFmpeg supply specifications and LGPL notices; binaries are not committed |
| `docs/` | Maintained reference documentation and documentation images |
| `docs_working/` | Temporary plans, investigations, reviews, and work records |

## Documentation

Start with the [developer guide](docs/README.md). The consolidated guide and linked deployment documents are in Korean.

| Document | What it covers |
|---|---|
| [Product contract](docs/README.md#product-contract) | Trust model, feature scope, and acceptance criteria |
| [Architecture](docs/README.md#architecture) | Process structure, support authorization and session lifetimes, and privilege boundaries |
| [Technical specification](docs/README.md#technical-specification) | Configuration, limits, and dependencies |
| [Protocol](docs/README.md#protocol) | Single source of truth for the wire format |
| [Workflows](docs/README.md#workflows) | Startup, connection, features, and reconnection |
| [Initial administrator launch](docs/README.md#startup-preparation) | Support authorization, firewall, SAS, and GPU preparation |
| [Live two-PC tests](docs/README.md#live-e2e-test) | Test procedures, measurements, and remaining work |
| [Installation and updates](deploy/installation.md) | Signing, protected installation, and rollback |
| [Tunnel server operations](deploy/tunnel/README.md) | TLS, auditing, and containers |

## Licensing and operations

- Bundle only **LGPL builds of FFmpeg through dynamic linking**. GPL components such as `libx264` are not used. See the [FFmpeg guide](third_party/ffmpeg/README.md) for supply specifications and notices.
- Operation assumes controlled internal distribution, a visible connection indicator, immediate support termination, and audit records. Review organizational security policies and applicable laws before deployment.

## Usage example

An employee requests help from the internal support team after encountering an issue on their work PC. The employee launches the app and shares the support ID; the technician connects in a browser and resolves the issue. This example assumes the tunnel server is already running on the technician's PC.

![RemoteAssist usage example — 1. Run the app as administrator. 2. Share the 9-digit support ID. 3. The technician enters the ID in a browser and connects. 4. Resolve the issue with screen control, admin tasks, file transfer, and diagnostics. 5. The remote user ends support with the tray's Stop support button.](docs/assets/readme/remoteassist-usage-en.png)

The ID `418-207-935` in the illustration is an example. Use the support ID displayed on the remote PC for an actual connection.
