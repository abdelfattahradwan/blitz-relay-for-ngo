# Blitz Relay for NGO

A relay transport
for [Netcode for GameObjects](https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.13/manual/index.html)
that routes all client traffic through a [Blitz Relay](https://github.com/abdelfattahradwan/blitz-relay) server
instance, removing the need for clients to have publicly reachable IP addresses or open ports. This makes it suitable
for deployments where NAT traversal or firewall restrictions make direct peer-to-peer connections unreliable or
impractical.

The transport uses a modified build of [LiteNetLib](https://github.com/RevenantX/LiteNetLib) internally for its UDP
connections to the relay.

## Requirements

- Unity 6000.0 or later
- [Netcode for GameObjects](https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.13/manual/index.html)

## Installation

Open the Unity Package Manager (Window > Package Manager) and add the package via git URL:

```
https://github.com/abdelfattahradwan/blitz-relay-for-ngo.git?path=/Packages/com.winterboltgames.blitzrelayforngo
```

Or add the following entry to your project's `Packages/manifest.json`:

```json
"com.winterboltgames.blitzrelayforngo": "https://github.com/abdelfattahradwan/blitz-relay-for-ngo.git?path=/Packages/com.winterboltgames.blitzrelayforngo"
```

## Features

- **Relay-based architecture**: All game traffic flows through a relay server, so clients never need to expose their
  IP addresses or open firewall ports.
- **Room-based**: Hosts create rooms and receive a room code from the relay; clients join by supplying
  that room code.
- **Basic Host migration**: When the current host disconnects, the relay can promote an existing client to become the
  new host.

## Setup

1. Add a GameObject with a `NetworkManager` component to your scene.
2. Add a `BlitzRelayTransport` component to the same GameObject.
3. Assign the `BlitzRelayTransport` component to `NetworkManager.NetworkConfig.NetworkTransport`.
4. For hosts, set the relay address, port, key, and maximum clients, then call `NetworkManager.StartHost()` or
   `NetworkManager.StartServer()`.
5. For clients, set the relay address, port, key, and room code, then call `NetworkManager.StartClient()`.

## Configuration

The following fields are exposed in the Inspector:

| Field                               | Type     | Description                                                                                |
|-------------------------------------|----------|--------------------------------------------------------------------------------------------|
| **Relay Address**                   | `string` | IP address or hostname of the relay server. Defaults to `127.0.0.1`.                       |
| **Relay Port**                      | `ushort` | Port number the relay server is listening on. Defaults to `7770`.                          |
| **Relay Key**                       | `string` | Connection key used to authenticate with the relay.                                        |
| **Room Code**                       | `string` | Room code to join on the relay. Hosts receive this from the relay after room creation.     |
| **Do Not Route**                    | `bool`   | When enabled, packets are sent directly to the network interface without OS-level routing. |
| **Maximum Clients**                 | `int`    | Maximum number of simultaneous client connections. Defaults to `4096`.                     |
| **Disconnect Timeout Milliseconds** | `int`    | LiteNetLib inactivity timeout. Defaults to `30000`.                                        |

## Runtime API

Several methods and events are available for script-driven configuration:

- `SetRelayAddress(string)` / `GetRelayAddress()` - Change or read the relay server address.
- `SetRelayPort(ushort)` / `GetRelayPort()` - Change or read the relay server port.
- `SetRelayKey(string)` / `GetRelayKey()` - Change or read the relay connection key.
- `SetRoomCode(string)` / `GetRoomCode()` - Set or retrieve the current room code.
- `SetRoomHostToken(string)` / `GetRoomHostToken()` - Manage the host token assigned by the relay.
- `SetMaximumClients(int)` / `GetMaximumClients()` - Adjust or read the client limit.
- `IsRelayHostAvailable` - Indicates whether the relay host is currently available.
- `OnRelayHostAvailabilityChanged` - Event that fires when host availability changes.
- `HasPendingHostPromotion` - Indicates whether this client has promotion data waiting to be claimed.
- `ClearPendingHostPromotion()` - Clears pending host promotion data.
- `OnHostPromotionReceived` - Event that fires when the relay promotes this client to host.

## Host Promotion

When a client is promoted to host, the transport stores the claim data and raises `OnHostPromotionReceived`. NGO owns
its own lifecycle, so your game code should shut down the current client session and start host mode. The next server
start will claim the promoted room automatically.

## License

MIT License. See [LICENSE](LICENSE) for details.

Copyright 2026 Abdelfattah Radwan
