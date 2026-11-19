# Security model

**Goal:** only devices you paired, on your own network, can trigger sounds or change settings. Nothing is reachable from the internet.

| Threat | Control |
| --- | --- |
| Internet clients | Kestrel binds IPv4 only, and every request from a non-private address is aborted (`NetworkGuard`: loopback, 10/8, 172.16/12, 192.168/16, 169.254/16). The app never opens router ports or edits firewall rules. |
| Other people on the Wi-Fi | Pairing: a 6-digit code, shown only on the PC, valid for 5 minutes and usable once. After 5 failures pairing locks for 60 s and the code rotates. |
| Stolen or replayed session | 256-bit random token, sent as an `HttpOnly`, `SameSite=Strict` cookie. Only its SHA-256 is stored and comparisons are constant-time. Devices can be revoked individually from Settings, or all at once from the tray. |
| Malicious website in the PC's browser (CSRF) | `SameSite=Strict` cookie. State-changing requests and WebSocket upgrades with a foreign `Origin` are rejected. |
| DNS rebinding | The `Host` header must be an IP literal, `localhost` or the PC's own name; anything else gets 421. |
| Hostile uploads | MP3/WAV only, 50 MB per file, decoded before acceptance, stored under a generated name (never the client's path). |
| XSS | React escaping plus a strict CSP: `script-src 'self'`, no inline scripts, `frame-ancestors 'none'`. |

**Known limitation:** LAN traffic is plain HTTP, so someone who can sniff your Wi-Fi could read a session cookie. It's fine on a home WPA2/WPA3 network you trust, not on shared or public Wi-Fi. A planned improvement is HTTPS with a self-signed certificate pinned at pairing time.
