
<div align="center">

<img src="assets/curtain-logo.png" alt="Curtain app icon" width="92" />

# Curtain

### Privacy when you need it. Clarity when you don't.

A lightweight frosted-glass privacy shield for Windows. Cover one window, protect your whole desktop, or let Curtain step in automatically when you step away.

<p>
  <img alt="Windows x64" src="https://img.shields.io/badge/Windows-x64-0078D4?style=flat-square" />
  <img alt="WinUI 3" src="https://img.shields.io/badge/WinUI-3-536DFE?style=flat-square" />
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-6A4C93?style=flat-square" />
  <img alt="Stable 0.3.0" src="https://img.shields.io/badge/release-stable%200.3.0-2e8b57?style=flat-square" />
</p>

**[Download for Windows](https://curtain-updates.cheneylab.cn/beta/CurtainPrivacy-0.3.0-Setup.exe)** · **[GitHub Release](https://github.com/Cheney-0821/Curtain/releases/tag/v0.3.0)** · **[简体中文](README.zh-CN.md)**

<sub>Windows x64 · Unsigned stable release · Windows 10 22H2 and Windows 11 25H2 tested</sub>

</div>

---

<div align="center">
  <img src="assets/02-window-shield.png" alt="A fictional demo window protected by Curtain's frosted-glass overlay" width="920" />

  <sub>Real Curtain screenshot. The sample app, Project Nimbus, contains fictional content.</sub>
</div>

## One privacy tool, three simple ways to use it

<table>
  <tr>
    <td width="33%" align="center"><b>Pick a window</b></td>
    <td width="33%" align="center"><b>Toggle in one click</b></td>
    <td width="33%" align="center"><b>Shield the whole desktop</b></td>
  </tr>
  <tr>
    <td><img src="assets/01-picker.png" alt="Curtain highlighting a fictional window in window-selection mode" width="390" /></td>
    <td><img src="assets/03-window-shield-closeup.png" alt="Frosted protected window and compact top-edge handle" width="390" /></td>
    <td><img src="assets/04-fullscreen-privacy.png" alt="Curtain's full-screen privacy overlay on a Windows 11 desktop" width="390" /></td>
  </tr>
  <tr>
    <td>Hover to highlight a window, then click to add it to Curtain.</td>
    <td>Use the slim handle above a protected window to show or hide its contents.</td>
    <td>Cover your desktop with a single action, including connected displays.</td>
  </tr>
</table>

## Made for the moments you step away

Curtain can watch for a period of keyboard and mouse inactivity and start protection automatically. You choose both **when** and **what** to protect.

<table>
  <tr>
    <td width="48%" align="center"><img src="assets/05-idle-settings.png" width="440" alt="Real Curtain settings for automatic protection timeout and scope" /></td>
    <td valign="middle">
      <b>Automatic protection</b>
      <ul>
        <li>Choose 1, 3, 5 or 10 minutes of inactivity.</li>
        <li>Protect only added windows, or all displays.</li>
        <li>Automatic protection is off by default.</li>
        <li>Explicitly end a protection session; ordinary mouse movement won't instantly reveal everything.</li>
        <li>Manual window preferences are preserved when an automatic session ends.</li>
      </ul>
    </td>
  </tr>
</table>

## Designed to stay out of the way

- **Independent window controls.** Protect multiple chosen windows without blurring everything else.
- **Adjustable Gaussian blur.** Set a global default, then customize blur strength and click-through for individual windows.
- **Optional click-through.** Interact with the protected app while its blur overlay remains visible.
- **A small, responsive handle.** Quick to toggle, attached to the window as it moves or resizes.
- **Whole-desktop privacy.** Manual full-screen protection, including multi-monitor coverage.
- **Smart idle behavior.** Temporarily uncover an individual window during an idle-protection session; when the next idle threshold is reached, Curtain protects it again.
- **Updates without surprise restarts.** Check and download updates in the background; you decide when to install and restart.
- **Taskbar-friendly minimize.** Minimize Curtain to the taskbar; closing the window still sends it to the tray.
- **Remembered applications.** Save app protection rules and let Curtain rebind matching windows when those apps start again, including after a restart.
- **Diagnostics and feedback.** Keep local diagnostics, create a ZIP, and submit it only when you choose; a failed upload leaves the ZIP on your device.
- **Fullscreen controls.** Reveal the restore button from the upper quarter of the display; it fades after one second outside the area or without pointer movement.
- **Custom shortcuts.** `Ctrl+Down` toggles fullscreen protection and `Alt+S` starts window selection by default. Both can be changed. Optional mouse Back/Forward buttons are off by default.
- **Native Windows feel.** Built with WinUI 3 and Windows App SDK.

## Get started

1. **Download** the [Windows x64 stable installer](https://curtain-updates.cheneylab.cn/beta/CurtainPrivacy-0.3.0-Setup.exe). This installer is **not code-signed**; Windows may warn that its publisher is unrecognized. Verify the source and do not disable security protections just to install it.
2. **Launch Curtain** and choose **Add window** or **Protect entire screen**.
3. **Set your preferences** in Settings if you want Curtain to activate after inactivity.

> The source commit and installer SHA-256 for this release are recorded in the GitHub Release notes.

<details>
<summary><b>Keyboard shortcuts and compatibility</b></summary>

| Shortcut | Action |
|---|---|
| <kbd>Ctrl</kbd> + <kbd>↓</kbd> | Toggle fullscreen protection or end the current automatic protection session |
| <kbd>Alt</kbd> + <kbd>S</kbd> | Start window selection |

Both shortcuts are customizable. The mouse Back and Forward buttons can optionally toggle fullscreen protection and are off by default. The saved app rules persist and matching windows are rebound when an app starts again; the live list of individual windows is rebuilt from running applications.

Curtain targets Windows x64. The minimum supported Windows build is Windows 10 2004 (build 19041). This release was tested on Windows 10 22H2 (build 19045) and Windows 11 25H2. Windows 10 1903 (build 18362) is below the minimum and is not supported by this validation.

</details>

## What Curtain is — and isn't

Curtain is a **visual privacy overlay**, not a Windows sign-in lock, a defense against screenshots/recording, or a security boundary. Some notifications or transient popups may briefly appear above the full-screen overlay before its Z-order is recovered. Native ETW capture may require administrator privileges and is not verified in a standard-user session. Physical Windows 10 hardware, mixed DPI, HDR, monitor hot-plug, complex multi-display behavior, and long-running stress behavior have not received separate validation.

## Feedback and open source

Curtain's source code is publicly available under **GPL-3.0-only**. Use [GitHub Issues](https://github.com/Cheney-0821/Curtain/issues) to report bugs or suggest improvements.

The stable installer and in-app update feed are hosted on the official update domain above. The [GitHub Release](https://github.com/Cheney-0821/Curtain/releases/tag/v0.3.0) includes release notes, the source commit, asset checksums, and GitHub-hosted downloads.

## License

Curtain's own code is licensed under **GPL-3.0-only**. See [LICENSE](LICENSE).
Third-party components retain their respective licenses. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the archived texts in [licenses/third-party](licenses/third-party).

<sub>Curtain is an independent application and is not affiliated with or endorsed by Microsoft. Windows and other third-party marks and imagery belong to their respective owners. The real-world Windows 11 desktop screenshots are included solely to demonstrate Curtain running on Windows.</sub>
