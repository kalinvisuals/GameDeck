# Open GameDeck on an iPhone

1. **Same network.** Connect the iPhone to the same Wi-Fi as the gaming PC. A wired PC on the same router works too. Guest networks and "AP/client isolation" block devices from seeing each other.
2. **Start GameDeck** on the PC. On first launch Windows asks whether to allow it through the firewall: tick **Private networks** only and click Allow.
3. **Double-click the tray icon** to open the *Connect your phone* window.
4. **Scan the QR code** with the iPhone Camera app and tap the banner. Safari opens and pairs on its own.
   Without the camera: type the address shown (for example `http://192.168.1.20:5280`) into Safari and enter the 6-digit code.
5. Optional: **Share → Add to Home Screen** gives a full-screen, app-like soundboard with no Safari toolbar.

Each code works once and expires after 5 minutes. Five wrong attempts lock pairing for 60 seconds.

## If the phone can't connect

- **Is the network marked Private on the PC?** Go to Settings → Network & internet → Wi-Fi/Ethernet → your network → *Private network*. On *Public* networks Windows blocks incoming connections.
- **Did you click Allow on the firewall prompt?** If you dismissed it, either open *Windows Security → Firewall & network protection → Allow an app through firewall* and tick *Private* for GameDeck, or run this in an **administrator** PowerShell (adjust the path):

  ```powershell
  New-NetFirewallRule -DisplayName "GameDeck (LAN)" -Direction Inbound -Action Allow `
    -Program "C:\path\to\GameDeck.exe" -Protocol TCP -LocalPort 5280 -Profile Private
  ```

  GameDeck never changes firewall rules itself.
- **Wrong address?** The pairing window lists every LAN address the PC has. VPN and virtual adapters (Hyper-V, VirtualBox) can show extra ones. Use the one on your Wi-Fi/Ethernet subnet.
- **Chrome or another app on iOS** may ask for *Local Network* access. Allow it, or use Safari.
- **Opening by hostname** (for example `http://gaming-pc:5280`) only works with the PC's own name. Other hostnames are rejected on purpose, as a DNS-rebinding defence.

Never port-forward 5280 on your router. GameDeck refuses non-LAN clients anyway, but keep it off the internet.
