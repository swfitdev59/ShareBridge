# ShareBridge

A LAN file- and message-sharing desktop app for Windows, in the spirit of IP Messenger.
Peers find each other automatically over UDP broadcast; messages and files travel
directly between machines over TCP. No server, no account, no cloud.

## Features

- **Automatic discovery** — every instance announces itself over UDP broadcast
  (HELLO / heartbeat / BYE), so peers appear and disappear on their own.
- **Text chat** — one conversation per peer, with sent / delivered / failed status.
  Message text is selectable and copyable (drag-select + Ctrl+C, or right-click → Copy).
  A message or file that fails to send shows a **Retry** button to try again.
- **Replies** — right-click any message → **Reply** to answer it. The message being
  answered sits above the message box until you send (Esc drops it), and the reply arrives
  carrying a quote of it; click that quote to jump to the original. The quoted line travels
  with the reply, so it still reads correctly if the original is later deleted.
- **Reactions** — hover a bubble and hit the 🙂 button (or right-click → **React…**) to put
  an emoji on it. One reaction each: picking another emoji swaps yours, picking the same one
  takes it back. Reactions show as chips under the bubble — yours outlined — and the same
  emoji from both sides collapses into one chip with a count.
- **Edit & delete messages** — right-click one of your own messages to edit it inline or
  delete it; press Arrow-Up in an empty message box to edit your last one. Changes sync to
  the peer: edited messages are marked "edited"; deleted ones leave a "message deleted"
  placeholder on both sides.
- **Pictures and screenshots** — paste a screenshot straight into the chat (Ctrl+V),
  drop image files on the conversation, or use **Attach → Picture…**. They stage as
  thumbnails above the message box, where you can add a caption or drop one before
  sending, and arrive as inline previews rather than file cards. Click a picture to open
  it full size, with copy, save-a-copy and show-in-folder. Pictures up to 32 MB are
  accepted without a prompt, the way a message is; larger ones should go as files.
- **File and folder transfer** — pick from a dialog or drag-and-drop onto the
  conversation. Folders keep their structure. Live progress, speed and ETA, with cancel.
- **Accept / reject prompt** — incoming offers ask before writing to disk (auto-accept
  is opt-in), and time out on their own so a sender is never left hanging.
- **Desktop notifications** — a themed toast slides in (bottom-right) for a new message
  when you are not already viewing that conversation; click it to jump straight there.
  Toasts never steal focus and can be turned off in Settings.
- **Single instance** — only one copy runs per machine session; launching it again just
  surfaces the window that is already open (or restores it from the tray).
- **System tray** — minimizing or closing the window keeps the app running in the tray
  (both configurable). The tray icon's menu opens the window, jumps to Settings, or exits;
  left-click or double-click restores the window. A red unread badge appears on the tray
  icon, and notifications keep working while hidden.
- **Persistent chat history** — conversations (messages and transfer records) are saved
  per peer and reopen after a restart, keyed to a stable per-user identity. Can be turned
  off, and cleared per-conversation or all at once, from Settings.
- **Search** — the sidebar box (Ctrl+F) filters people (name/host/IP) and searches message
  text across every saved conversation; click a hit to jump to that message, which is
  scrolled into view and briefly flashed, with the matched term highlighted inside the bubble.
  A find bar over the open chat shows the match count and steps through hits with
  next/previous (Enter / Shift+Enter, or F3 / Shift+F3).
- **Export** — save any conversation to a `.txt` or `.md` transcript from the chat header.
- **Settings** — display name, download folder, ports, auto-accept, save-history. Persisted to
  `%APPDATA%\ShareBridge\settings.json`. **Restore defaults** puts every field back to its
  stock value; it takes effect only when you then hit Save, and leaves your identity and
  saved conversations alone.

## Requirements

- Windows 10 / 11
- To **build**: the .NET 9 SDK.
- To **run**: nothing, if you use the self-contained build below (the runtime is bundled).
  A framework-dependent build instead needs the .NET 9 Desktop Runtime installed.

## Build and run (development)

```powershell
dotnet build ShareBridge.sln
dotnet run --project src/ShareBridge.Wpf/ShareBridge.Wpf.csproj
```

Only one instance runs per machine session: launching it again just brings the existing
window to the front. To run two instances side by side for testing, give each a distinct
identity, which also lets them discover each other:

```powershell
$env:SHAREBRIDGE_ID = "a"; dotnet run --project src/ShareBridge.Wpf/ShareBridge.Wpf.csproj
# in another terminal:
$env:SHAREBRIDGE_ID = "b"; dotnet run --project src/ShareBridge.Wpf/ShareBridge.Wpf.csproj
```

## Standalone build (no .NET install needed)

Produce a single self-contained `ShareBridge.exe` that carries its own copy of the .NET 9
runtime, so it runs on a clean Windows PC with nothing installed. Run this same command any
time you want to **rebuild** the standalone exe (e.g. after a version bump or code change):

```powershell
dotnet publish src/ShareBridge.Wpf/ShareBridge.Wpf.csproj -p:PublishProfile=win-x64-selfcontained
```

The result is a single file at `publish/win-x64/ShareBridge.exe` (~75 MB — larger than a
framework-dependent build because the runtime is inside it). Copy just that one `.exe` to
the target machine and double-click it; no SDK or runtime install is required.

The build settings live in
[win-x64-selfcontained.pubxml](src/ShareBridge.Wpf/Properties/PublishProfiles/win-x64-selfcontained.pubxml):
self-contained, single-file, compressed, ReadyToRun (for faster startup). Trimming is left
off because it is unsafe for WPF. For a 32-bit or Arm64 machine, change `win-x64` to `win-x86`
or `win-arm64`.

## Project layout

| Project | Contents |
| --- | --- |
| `ShareBridge.Core` | Networking and protocol, with no UI dependency. `DiscoveryService` (UDP), `TransferServer` / `TransferClient` (TCP), and the `ShareBridgeNode` facade the UI talks to. |
| `ShareBridge.Wpf` | The WPF app: MVVM view models, the main window, settings, and the incoming-offer dialog. |

## How it works

- **Discovery** — UDP on port **47425** (configurable). Packets are small JSON blobs
  carrying an id, display name, host and the TCP port the sender is really listening on.
  Broadcasts go to `255.255.255.255` and to each interface's directed broadcast, so
  multi-NIC hosts are covered. Peers that stop announcing age out after a timeout.
- **Transport** — TCP on port **47426** (configurable, with ephemeral fallback). Each
  exchange is its own short-lived connection: a length-prefixed JSON frame selects the
  kind (message, picture, reaction, or file offer), and bytes stream directly after the
  receiver agrees to take them. Edits, deletes, replies and reactions all refer to a
  message by the id it was sent under, so both sides stay in step.
- **Stable identity** — each install has a per-user id (`nodeId` in `settings.json`,
  generated on first run) so a peer keeps the same conversation across restarts. Set the
  `SHAREBRIDGE_ID` environment variable to override it when running several instances on
  one machine for testing — they would otherwise share an id and filter each other out.

## Where data is stored

Everything lives under `%APPDATA%\ShareBridge\`:

| Data | Location |
| --- | --- |
| Settings (name, ports, download folder, flags, `nodeId`) | `settings.json` |
| Chat history — one transcript per peer (newline-delimited JSON) | `history\<peerId>.jsonl` |
| Known-peer index (so past conversations reopen offline) | `history\peers.json` |
| Pictures sent and received in chat | `media\<peer>\` |
| Received files | your download folder (default `%USERPROFILE%\Downloads\ShareBridge\<sender>\`) |

History can be disabled in Settings (nothing is written when off), cleared for one
conversation (the **Clear** button in a chat), or wiped entirely (**Clear all history**
in Settings). Transcripts never leave the machine.

Pictures are kept in the media folder rather than the download folder, so screenshots do
not pile up among your real downloads — **Save as…** on a picture is how one gets out.
A picture you send is copied in, so its bubble survives the original being moved; clearing
a conversation (or all history) deletes the pictures that went with it.

## Safety notes

- Incoming relative paths are sanitized (no `..`, no drive letters, no reserved names)
  and every write is confirmed to land inside the chosen download folder.
- Files are written to a `.sbpart` temp name and moved into place only on success, so an
  interrupted transfer never leaves a half-written file under the real name.
- Frame sizes are capped to keep a malformed or hostile peer from forcing large allocations.
- Inline pictures are capped at 32 MB and must be named as a picture, so the no-prompt
  path cannot be used to drop an arbitrary file (or a huge one) on your disk.

This is a LAN tool with no authentication or encryption — run it on networks you trust.
On first run, allow it through Windows Firewall on your **private** network so discovery
and transfers can reach other machines.
