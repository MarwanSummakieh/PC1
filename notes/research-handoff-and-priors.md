# Research handoff & priors — PC1 console shell

Target platform (verified on this machine, 2026-08-13):

```
ProductName    : Windows 10 IoT Enterprise LTSC 2024   <- legacy ProductName string; it *is* Windows 11
EditionID      : IoTEnterpriseS
DisplayVersion : 24H2
CurrentBuild   : 26100
UBR            : 9168
```

Local facts checked directly on this box (not from the web):

- `C:\Windows\System32\CustomShellHost.exe` exists (10.0.26100.8972) — Shell Launcher **v2** host ships in the base image.
- `C:\Windows\System32\Eshell.exe` does **not** exist and the WMI class `root\standardcimv2\embedded:WESL_UserSetting` is **not present** → the `Client-EmbeddedShellLauncher` optional feature is **not yet enabled** here.
- `CLSID_ApplicationActivationManager` `{45BA127D-10A8-46EA-8AB7-56EA9078943C}` is registered as **InProcServer32 → `C:\Windows\System32\twinui.appcore.dll`, ThreadingModel=Both** (i.e. it is *not* an explorer-hosted out-of-proc server).

Confidence labels used throughout: **[PRIMARY]** = Microsoft docs / Microsoft KB / actual source code / this machine's registry; **[COMMUNITY]** = forum, Q&A poster, GitHub issue, blog; **[UNCERTAIN]** = inference or conflicting evidence.

---

## 1. Shell Launcher V2 on Windows 11 IoT Enterprise LTSC 2024

### 1.1 Feature enablement

- Shell Launcher replaces `Explorer.exe` with a Win32 app *or* a UWP app. Supported editions: "✅ Enterprise / Enterprise LTSC ✅ Education ✅ IoT Enterprise / IoT Enterprise LTSC" — IoT Enterprise LTSC is explicitly in scope. **[PRIMARY]**
- v1 replaces `Explorer.exe` with `Eshell.exe` (Win32 shells only); **v2 replaces `Explorer.exe` with `CustomShellHost.exe`** and can host a Win32 *or* UWP shell. v2 arrived in Windows 10 1809. **[PRIMARY]**
- Enable it with any of:
  - `Enable-WindowsOptionalFeature -FeatureName Client-DeviceLockdown,Client-EmbeddedShellLauncher -Online`
  - `Dism /online /Enable-Feature /all /FeatureName:Client-EmbeddedShellLauncher`
  - `optionalfeatures.exe` → Device Lockdown → Shell Launcher
  - `WESL_UserSetting.SetEnabled($true)` via WMI
  - Applying the **Assigned Access CSP `ShellLauncher` node auto-enables the feature** "if the device supports it" — no separate enable step needed. **[PRIMARY]**
- The IoT Enterprise lab page (ms.date **2026-06-24**, i.e. current, not carried over from Win10) shows the canonical minimal setup:
  ```powershell
  $ShellLauncherClass = [wmiclass]"\\localhost\root\standardcimv2\embedded:WESL_UserSetting"
  $ShellLauncherClass.SetDefaultShell("powershell.exe",1)
  $ShellLauncherClass.SetEnabled($TRUE)
  ```
  and reverting is `SetDefaultShell("explorer.exe",1)` + reboot. **[PRIMARY]**

### 1.2 WMI path — `WESL_UserSetting` (namespace `root\standardcimv2\embedded`)

MOF (verbatim from Learn): **[PRIMARY]**

```mof
class WESL_UserSetting {
    [read, write, Required] string Sid;
    [read, write, Required] string Shell;
    [read, write]  Sint32 CustomReturnCodes[];
    [read, write]  Sint32 CustomReturnCodesAction[];
    [read, write] sint32 DefaultAction;

    [Static] uint32 SetCustomShell(Sid, Shell, CustomReturnCodes[], CustomReturnCodesAction[], DefaultAction);
    [Static] uint32 GetCustomShell(Sid, out Shell, out CustomReturnCodes[], out CustomReturnCodesAction[], out DefaultAction);
    [Static] uint32 RemoveCustomShell(Sid);
    [Static] uint32 GetDefaultShell(out Shell, out DefaultAction);
    [Static] uint32 SetDefaultShell(Shell, DefaultAction);
    [Static] uint32 IsEnabled(out Enabled);
    [Static] uint32 SetEnabled(Enabled);
};
```

Key semantics: **[PRIMARY]**

- Only **one** `WESL_UserSetting` instance exists per device.
- Resolution order at sign-in: config for the **user SID** → config for a **group SID** the user belongs to (first valid match, *search order undefined*) → **default** config. Avoid putting a user in two groups with different configs.
- WMI configuration is **SID-only** — user/group *names* are not accepted.
- `Shell` may be a bare filename resolved via `PATH`, or a full path, may contain `%env%` vars, and **may include arguments** (spaces must be inside a quote-delimited string).
- Changes take effect **only at next sign-in**.
- Shell Launcher processes **`Run` and `RunOnce`** registry keys *before* starting the custom shell — you don't need to re-implement autostart in the shell.

### 1.3 Return codes / DefaultAction

Action values (identical on `WESL_UserSetting`, `SetCustomShell`, and the XML enum): **[PRIMARY]**

| Value | XML enum          | Meaning              |
|-------|-------------------|----------------------|
| 0     | `RestartShell`    | Restart the shell    |
| 1     | `RestartDevice`   | Restart the device   |
| 2     | `ShutdownDevice`  | Shut down the device |
| 3     | `DoNothing`       | Do nothing           |

- Prose says "You can specify at most **four** custom actions mapping to four exit codes, and one default action for all other exit codes." The **XSD contradicts this**: `ReturnCodeAction` is `minOccurs="1" maxOccurs="unbounded"` (with a uniqueness constraint on `@ReturnCode`). Assume the four-mapping limit is real (enforced by the provider, not the schema) and design the shell's exit codes around ≤4 distinct values. **[UNCERTAIN — prose vs XSD conflict, both primary]**
- If the exit code isn't in `CustomReturnCodes` **or** the mapped action is invalid → `DefaultAction`. If `DefaultAction` is missing/invalid → **Shell Launcher restarts the shell**. **[PRIMARY]**
- **Doc conflict, noted:** the XML article says "if the exit code isn't found in the custom action mapping, or there's no default action defined, **nothing happens**", while the WMI `SetCustomShell` reference says Shell Launcher **restarts the shell application** in that case. Treat the fallback as undefined and always set `DefaultAction` explicitly. **[UNCERTAIN — conflicting Microsoft docs]**
- Microsoft's own warning: if the shell can exit on its own (or is closed by something like Dialog Filter) and the action is `RestartShell`, you get an **infinite exit/restart loop** unless the action is `DoNothing`. **[PRIMARY]**
- **Critical limitation for a game shell:** "Shell Launcher doesn't support a custom shell with an application that launches a different process and exits" — Shell Launcher only monitors the process it started, and acts on *its* exit code. (`write.exe` → `wordpad.exe` is the doc's example.) Our shell must therefore be a long-lived process that owns its own lifetime, and must **not** hand off to a child and exit. **[PRIMARY]**

### 1.4 XML / Assigned Access CSP path

- Node: `./Vendor/MSFT/AssignedAccess/ShellLauncher`, format `chr`, Add/Delete/Get/Replace, device scope, Windows 10 1803+, Enterprise/Education/IoT Enterprise (**not Pro**). You **cannot** set both `ShellLauncher` and `KioskModeApp`. **[PRIMARY]**
- Delivery: Intune custom policy, raw CSP, provisioning package (`SMISettings/ShellLauncher/Enable = ENABLE`), or **MDM Bridge WMI provider** (`root\cimv2\mdm\dmmap : MDM_AssignedAccess`, property `ShellLauncher`, HTML-encoded XML) — the bridge **must run as SYSTEM** (`psexec.exe -i -s powershell.exe`). **[PRIMARY]**
- Namespaces: base `http://schemas.microsoft.com/ShellLauncher/2018/Configuration`, add-on `V2 = .../2019/Configuration`. The versioning table on Learn still labels both rows "Windows 10" — the schema was never re-versioned for Windows 11. **[PRIMARY, but Win10-era labelling]**
- `Shell` element attributes: `Shell` (full exe path + args, or AUMID for UWP), `V2:AppType` (`Desktop`|`UWP`), `V2:AllAppsFullScreen` (true → *every* app runs full screen/maximized, not just the shell).
- `Configs` map accounts (`devicename\user`, `.\user`, `user`, `domain\sam`, `azuread\upn`) or a Shell-Launcher-managed `<AutoLogonAccount/>` (creates/maintains a local standard user named **`Kiosk`**; XSD pins `HiddenId="{50021E57-1CE4-49DF-99A9-8DB659E2C2DD}"`) to a `Profile Id`. A profile with no `Config` does nothing. The account must already exist for local accounts. **[PRIMARY]**
- Undocumented-in-prose but present in the XSD: `<Account>` accepts **`Sid=""`** as well as `Name=""` (`account_t` has both as optional attributes). Useful for the OOBE/group-SID pitfall in 1.5(1) — you can pin a config to an exact SID from XML, not just from WMI. **[PRIMARY — XSD]**
- The XSD page calls its main schema "the latest Shell Launcher XSD, **introduced in Windows 11**", while the configuration-file page's versioning table labels both namespaces "Windows 10". Same schema, inconsistent labelling. **[PRIMARY, internally inconsistent]**

Working template for a Win32 game shell:

```xml
<?xml version="1.0" encoding="utf-8"?>
<ShellLauncherConfiguration xmlns="http://schemas.microsoft.com/ShellLauncher/2018/Configuration"
                            xmlns:V2="http://schemas.microsoft.com/ShellLauncher/2019/Configuration">
  <Profiles>
    <DefaultProfile>
      <Shell Shell="%SystemRoot%\explorer.exe" />   <!-- keep admins on the real desktop -->
    </DefaultProfile>
    <Profile Id="{GUID}" Name="PC1">
      <Shell Shell="C:\PC1\PC1Shell.exe" V2:AppType="Desktop" V2:AllAppsFullScreen="false">
        <ReturnCodeActions>
          <ReturnCodeAction ReturnCode="0"   Action="RestartShell"/>
          <ReturnCodeAction ReturnCode="-1"  Action="RestartDevice"/>
          <ReturnCodeAction ReturnCode="255" Action="ShutdownDevice"/>
          <ReturnCodeAction ReturnCode="1"   Action="DoNothing"/>   <!-- "drop me to a repair state" -->
        </ReturnCodeActions>
        <DefaultAction Action="RestartShell"/>
      </Shell>
    </Profile>
  </Profiles>
  <Configs>
    <Config><Account Name=".\gamer"/><Profile Id="{GUID}"/></Config>
  </Configs>
</ShellLauncherConfiguration>
```

### 1.5 Known Windows 11 / LTSC 2024 issues

1. **Custom shell before OOBE is unsupported.** "Windows doesn't support setting a custom shell before the out-of-box experience (OOBE). If you do, you can't deploy the resulting image." **[PRIMARY]**
   - Concrete LTSC 2024 report: applying a Shell Launcher V2 config in audit mode then sysprepping produced a **blank screen after "Just a moment…"** during OOBE on **Windows 11 IoT Enterprise LTSC 2024**. Root cause found by the reporter: the config targeted a **group SID**, and OOBE's `defaultuser0` fell into that group, so OOBE itself got the custom shell. Fix: target a **specific user SID**, keep `explorer.exe` as the default profile, apply the real config post-OOBE. The same flow worked on **Win10 IoT LTSC 2021** — this is a Win11-era regression in practice. **[COMMUNITY, LTSC-2024-specific]**
2. **Shell Launcher is much slower to hand off on Windows 11 than Windows 10.** A Q&A report measures **~55 s** extra before the custom shell appears (even with `cmd.exe` as the shell); attributed by the responder (an "Independent Advisor", *not* a Microsoft engineer) to `eshell.exe` doing Run/RunOnce processing plus AppX/AppReadiness work, and to Win11 startup-app throttling. Reported workaround: set `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` directly and skip Shell Launcher → Win10-like speed. **[COMMUNITY — single report + non-Microsoft responder; treat the 55 s as anecdotal, but budget for a multi-second black gap]**
3. **Black screen between boot and shell** is a long-standing Shell Launcher complaint (15–20 s reported), with the only Microsoft-side mitigations being cosmetic: UEFI/firmware splash and the GPO "Force specific lock screen and logon image". That thread is **Windows 10 1607/1809-era (2021)** — treat durations as not applicable to 26100, but the *mitigation* advice still holds. **[COMMUNITY, Win10-era]**
4. **KB5072911 — XAML-dependent shell components fail to start (24H2 & 25H2).** Microsoft-confirmed: after provisioning with a cumulative update from **July 2025 (KB5062553) or later**, "Explorer, the Start menu, and other XAML-dependent apps might not start or close unexpectedly"; affected components include `explorer.exe`, StartMenuExperienceHost, **`shellhost.exe`**, SystemSettings, Taskbar, Search. Cause: "The applications have a dependency on XAML packages that are not registering in time after installing Windows updates." Hits first-logon-after-update and non-persistent/VDI images. **Fixed starting with updates released 2026-06-23 (KB5095093).** Workaround: register `MicrosoftWindows.Client.CBS`, `Microsoft.UI.Xaml.CBS`, `MicrosoftWindows.Client.Core` via a synchronous logon script *before* Explorer launches. Our box is 26100.**9168** (Aug 2026) so it is past the fix, but any image built from an older LTSC 2024 media + July-2025..June-2026 CU is exposed — and a *WinUI/XAML-based* custom shell would be in the blast radius. **[PRIMARY — Microsoft KB]**
5. **Shell Launcher config changes need SYSTEM.** Writing the config (WMI bridge / `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI\ShellLauncher`) requires SYSTEM, so an "Exit to Windows desktop" button in our shell needs a helper (SYSTEM service + named pipe, or a scheduled task running as SYSTEM) that flips the shell back to `explorer.exe` and forces a logoff/logon. **[COMMUNITY — Microsoft Q&A answer, Oct 2025, Win11 IoT Enterprise]**
6. **UAC**: "If your shell application requires administrative rights and needs to be elevated, and User Account Control (UAC) is enabled, you must disable UAC for Shell Launcher to launch the shell application." Design the shell to run **non-elevated**. **[PRIMARY]**

### 1.5b Alternative to Shell Launcher: the raw `Winlogon\Shell` value

- `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` (machine-wide) and `HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` (per-user, checked first) are the underlying mechanism; `Userinit` is the sibling hook that runs *before* the shell. The only Microsoft documentation for these is **Windows 2000-era** (`cc939851`), and the per-user variant is essentially undocumented for Windows 11. **[UNCERTAIN — works in practice, no current primary doc]**
- Trade-offs vs Shell Launcher: no `DefaultAction`/return-code state machine (nothing restarts the shell if it crashes), no per-SID mapping UI, no Run/RunOnce processing guarantee — but the community report in 1.5(2) claims it avoids Shell Launcher's Win11 startup delay entirely. Community shell-replacement projects for Playnite (Motion-Shell guide) use the **HKCU** value precisely because it's per-user and doesn't need the optional feature. **[COMMUNITY]**
- Practical recommendation: use **Shell Launcher** (supported, gives us the crash-restart policy on an IoT Enterprise LTSC SKU where it's a first-class feature), and only fall back to `Winlogon\Shell` if the startup delay proves real on our hardware.

### 1.6 Are the docs Win11-2024-specific?

- The Shell Launcher article family (`/windows/configuration/shell-launcher/*`) carries **ms.date 2025-03-07** and edition tables that include "IoT Enterprise / IoT Enterprise LTSC" — refreshed in the Win11 era, but the *content* (v1/v2 history, XML schema, WMI class) is unchanged Win10 material; the XML versioning table literally still says "Windows 10" for both namespaces.
- The **IoT Enterprise** lab page is the freshest (ms.date **2026-06-24**).
- Nothing in current docs describes Win11-specific startup timing, a "wait for desktop ready" contract, or a recommended delay. That gap is only covered by community reports (items 2–3 above). **[UNCERTAIN — no primary source on shell-start timing]**

### Sources — area 1

- Shell Launcher overview — https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/
- Configure Shell Launcher — https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure
- Create a Shell Launcher configuration file — https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configuration-file
- WESL_UserSetting — https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersetting
- WESL_UserSetting.SetCustomShell — https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/wesl-usersettingsetcustomshell
- Shell Launcher XSD — https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/xsd
- AssignedAccess CSP (ShellLauncher node) — https://learn.microsoft.com/en-us/windows/client-management/mdm/assignedaccess-csp
- IoT Enterprise: Configure Shell launcher or Assigned Access — https://learn.microsoft.com/en-us/windows/iot/iot-enterprise/commercialization/iot-ent-shell-launcher-app-launcher
- KB5072911 (XAML/shell provisioning regression, fixed by KB5095093) — https://support.microsoft.com/en-us/topic/kb5072911-explorer-the-start-menu-and-other-xaml-dependent-apps-might-not-start-or-close-unexpectedly-on-some-enterprise-devices-d2d30684-4e2b-47f5-9899-a00a8e0acb09
- Q&A: Blank screen during OOBE, Win11 IoT Enterprise LTSC 2024 + Shell Launcher V2 — https://learn.microsoft.com/en-my/answers/questions/2074822/blank-screen-during-oobe-windows-11-iot-enterprise
- Q&A: Shell Launcher slow to boot up shell in Windows 11 — https://learn.microsoft.com/en-ca/answers/questions/5768947/shell-launcher-slow-to-boot-up-shell-in-windows-11
- Q&A: Shell Launcher shows black screen before application starts (Win10-era) — https://learn.microsoft.com/en-us/answers/questions/365126/shell-launcher-shows-black-screen-before-applicati
- Q&A: Enable full desktop access from within a custom shell (Win11 IoT) — https://learn.microsoft.com/en-us/answers/questions/5576492/how-can-we-enable-full-windows-desktop-access-from
- Shell Launcher V2 samples — https://github.com/microsoft/Windows-IoT-Samples/tree/master/samples/ShellLauncher/ShellLauncherV2
- Winlogon `Shell` / `Userinit` (Windows 2000-era reference, the only MS doc) — https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-2000-server/cc939851(v=technet.10)
- Motion-Shell: setting Playnite as the shell via HKCU Winlogon\Shell — https://sites.google.com/view/motion-shell/help/manuals/setting-playnite-as-the-shell

---

## 2. Playnite fullscreen mode internals

Source read: `JosefNemec/Playnite` **master @ `5911f4e9`** (2026-08-13; latest release tag 10.56, 2026-05-26) and `JosefNemec/PlayniteExtensions` master @ `04b46a23` (the Steam/Xbox library plugins live in the *extensions* repo, not the main one, and link Playnite's `Common/*.cs` from a **pinned submodule @ `39e7ff05`, 2023-05-03** — so plugins and core are effectively two generations of the same code). All projects target **.NET Framework 4.6.2**. Permalink base: `https://github.com/JosefNemec/Playnite/blob/5911f4e964e628aa7a69c2030ab35101afa63067/<path>`

### (a) Launching a game

`source/Playnite/Common/ProcessStarter.cs` has three primitives:

- `StartProcess(path, arguments, workDir, asAdmin)` (L95) — the main one. Builds a `ProcessStartInfo`, sets `Verb = "runas"` when elevating, defaults `WorkingDirectory` to the exe's own directory. **It never sets `UseShellExecute`**, which on .NET FX 4.6.2 leaves it `true` — that's what makes `.lnk`/`.url`/`.bat`/file associations and the `runas` verb work. *(The `true` default is inferred from the target framework, not stated in code.)*
- `StartUrl(url)` (L70) — `Process.Start(url)`, with a `cmd /C start <url>` fallback on failure (the returned `Process` is then useless for tracking).
- `ShellExecute(cmdLine)` (L25) — raw `CreateProcess`; **not used for games**.

Dispatch lives in `source/Playnite/GamesEditor.cs` `PlayGame()` (L189): collect play actions → optional picker → run pre-scripts → fire cancellable `OnGameStarting` → toggle HDR → hand to either a plugin `PlayController` (e.g. `SteamPlayController`) or the built-in `GenericPlayController` (`source/Playnite/Controllers/GenericGameController.cs`, note the class/file name mismatch). In `GenericPlayController.Start()` (L481): **Script** action → PowerShell on a `Task.Run` (no process at all); **File** → `StartProcess`; **URL** → `StartUrl`. Emulators go through `StartEmulatorProcess` (L221) and are forced to `TrackingMode.Process`.

### (b) Exit detection — polling, not events

`TrackingMode` (`source/PlayniteSDK/Models/GameAction.cs:15`): `Default=0, Process=1, Directory=2, OriginalProcess=3, ProcessName=4` (the last added 2025-06-03). Companion knobs: `TrackingPath`, `InitialTrackingDelay` (default **0 ms**), `TrackingFrequency` (default **2000 ms**).

`source/Playnite/Common/ProcessMonitor.cs` (196 lines on master — **the old event-driven `ProcessMonitor` class was deleted between 2023-12 and 2024-04**) is four polled helpers:

- `MonitorProcess` — `!process.HasExited`.
- `MonitorProcessTree` (L29) — keeps a growing `relatedIds` set seeded with the original PID; each tick enumerates `Process.GetProcesses().Where(a => a.SessionId != 0)` and **adopts any process whose parent is already in the set**, then prunes to still-running. Parent lookup is `NtQueryInformationProcess` → `PROCESS_BASIC_INFORMATION.InheritedFromUniqueProcessId` (`source/Playnite/Common/Extensions/ProcessExtensions.cs:39`) — **not** WMI, not Toolhelp.
  - **The gap that matters for us:** a child is only adopted if it's observed alive *during a poll while its parent is still in the set*. A launcher that spawns the game and exits inside the 2 s window is missed entirely; and a game re-parented by an already-running client (Steam, Epic, EA) is never a descendant in the first place. *(Analysis, not admitted in a code comment — but the existence of Directory/ProcessName modes is circumstantial confirmation.)*
- `MonitorDirectory` (L119) / `MonitorProcessNames` (L67) — enumerate all session≠0 processes, resolve image paths via `OpenProcess(QueryLimitedInformation)` + `QueryFullProcessImageName`, match by path prefix or filename. Directory matching resolves junctions with `GetFinalPathName` and appends a trailing separator (comment: tracking `c:\Fallout` would otherwise match `c:\Fallout 2\`).

The loop is `GenericGameController.StartTracking(...)` (L670): one `Task.Run` per game, optional start delay, optional `startupCheck` phase that spins until a PID appears (this is how directory mode reports `StartedProcessId` late), then `trackingAction()` every `trackingFrequency` ms until false. Nice detail worth copying — sleep/hibernate protection at L764: if an interval took longer than `trackingFrequency + 30 s`, the interval is discarded rather than counted as playtime.

Mode → monitor mapping (L531–666): `Default`+File+UWP → **`MonitorDirectory` on the UWP package's install dir**; `Default`+File → `MonitorProcessTree`; **`Default`+URL → `MonitorDirectory(game.InstallDirectory)`** (this is the steam://-and-friends answer); `Process` → tree; `OriginalProcess` → single process; `Directory`/`ProcessName` → as named. If a directory monitor finds the dir missing or exe-less it fires `InvokeOnStopped` immediately — that's the "game exits instantly" bug class users hit with a wrong `InstallDirectory`.

### (c) Regaining foreground after the game exits — the exact recipe

Trigger, `GamesEditor.cs:1416` `Controllers_Stopped` — **in fullscreen mode restore is unconditional**, and there is a literal magic sleep:

```csharp
// This delay apparently fixes issues with Playnite not restoring properly after game exits.
// The window will restore, but application will not regain active state.
// This was mainly reported to happen with some emulators, like RPCS3, no idea why.
Thread.Sleep(1000);
Application.Restore();
```

Chain: `FullscreenApplication.Restore()` → `MainModel.RestoreWindow()` → `WindowFactory.RestoreWindow()` (marshals to the UI `SynchronizationContext`) → `WindowUtils.RestoreWindow(Window)` in `source/Playnite/Windows/WindowFactory.cs:185-253`, which does:

1. In fullscreen: `window.Show()` **only** if minimized or not visible (comment: always calling it "will bug out restore if alt-tabbing was used"), then `WindowState = Normal` unconditionally (comment: needed "if user alt-tabbed out of Playnite. Yeah apparently switching windows is something Windows can't do reliably in 2023...").
2. `if (!window.Activate()) { window.Topmost = true; window.Topmost = false; }` — the topmost-flicker fallback.
3. `GetWindowThreadProcessId` on our HWND and on `GetForegroundWindow()`, then **`AttachThreadInput(fgThread, ourThread, true)` → `SetWindowPos(hwnd, HWND_TOP, 0,0,0,0, SWP_NOSIZE|SWP_NOMOVE|SWP_SHOWWINDOW)` → `AttachThreadInput(..., false)`**.

**What it deliberately does *not* use** (verified by grep over the tree; the entire P/Invoke surface for this is 4 imports in `source/Playnite/Native/User32.cs` L37–46): no `SetForegroundWindow`, no `SwitchToThisWindow`, no `keybd_event`/`SendInput` fake-Alt, no `AllowSetForegroundWindow`/`LockSetForegroundWindow`, no `ShowWindowAsync`, no `SetWinEventHook`. So the answer to "which trick" is: **AttachThreadInput + SetWindowPos(HWND_TOP), plus Activate() with a Topmost pulse fallback**.

Other restore entry points: the **Guide button** (`FullscreenAppViewModel.cs:417`, gated on `EnableGameControllerSupport && GuideButtonFocus`, fed by SDL2 polling in `source/Playnite/Input/GameController.cs:314` which keeps running while unfocused — only *navigation* is gated on `IsActive`), and the URI `playnite://playnite/restore` over the named pipe in `source/Playnite/PipeServer.cs`.

Why this is fragile: fullscreen mode is **not** exclusive fullscreen — it's a borderless `WindowBase` sized to a monitor (`SetViewSizeAndPosition`, `FullscreenAppViewModel.cs:880`) with `ResizeMode=NoResize` and a zero-height `WindowChrome`; `Topmost` is never held. Anything that grabs foreground after a game exits stays on top. Open issue **#3795** "Prevent full-screen mode losing its focus when exiting a game" (open since 2024-08-11) is exactly this, and the reporter contrasts it with Steam Big Picture. Related: #3797, #4063 (open); #4097, #2038, #1446, #1979, #104, #3176 (closed). Exit-detection failures: #3675, #1322, #4225, #4114, #2188.

### (d) Steam

`SteamPlayController.Play` (`PlayniteExtensions/source/Libraries/SteamLibrary/SteamGameController.cs:160`) launches **neither a bare `steam://` URI nor the game exe** — it runs `steam.exe` with a URI argument:

```csharp
ProcessStarter.StartProcess(steamExe, $"-silent \"steam://rungameid/{Game.GameId}\"");
// or, when the per-mode "show Steam launch menu" setting is on:
ProcessStarter.StartProcess(steamExe, $"-silent \"steam://launch/{Game.GameId}/Dialog\"");
```

`steamExe` comes from `HKCU\Software\Valve\Steam\SteamPath` + `steam.exe` (`Steam.cs:21`). `-silent` stops the Steam client window popping up — important for a fullscreen shell. Mods/shortcuts always take `rungameid`. Install/uninstall use plain `StartUrl("steam://install/<id>")` / `steam://uninstall/` with their own polling watchers.

Exit detection ignores the launched `steam.exe` completely (it IPCs the running client and dies; the real game is a child of the *pre-existing* steam.exe, so tree tracking is useless). Instead: `procMon.WatchDirectoryProcesses(installDirectory, false)` — poll every 2000 ms for any session≠0 process whose image path contains the game's install dir, with a "seen at least once" latch before `TreeDestroyed` can fire; if the install dir doesn't exist it reports stopped immediately. For mods the watched dir is swapped to the base game's.

### Priors for our shell (area 2)

- Copy the **directory-watch** strategy for anything launched through a client (Steam/Epic/GOG/Xbox), and PID/tree tracking only for direct-exe launches. Expect a 1–2 s detection latency and design the UI for it.
- Copy the **AttachThreadInput + SetWindowPos** restore, the ~1 s post-exit delay, and the Guide-button restore path. Do **not** expect `SetForegroundWindow` alone to work.
- Because our shell *is* the shell (no explorer competing for foreground), we're structurally better placed than Playnite on #3795 — but only if nothing else (launcher splash, overlay) grabs foreground after the game exits.
- Shell Launcher's "shell must not spawn-and-exit" rule (area 1.3) rhymes with Playnite's tracking problem: our process must outlive every game it launches.

### Sources — area 2

- Playnite master tree @ 5911f4e — https://github.com/JosefNemec/Playnite/tree/5911f4e964e628aa7a69c2030ab35101afa63067
- `Common/ProcessStarter.cs` — https://github.com/JosefNemec/Playnite/blob/5911f4e964e628aa7a69c2030ab35101afa63067/source/Playnite/Common/ProcessStarter.cs
- `Common/ProcessMonitor.cs` — https://github.com/JosefNemec/Playnite/blob/5911f4e964e628aa7a69c2030ab35101afa63067/source/Playnite/Common/ProcessMonitor.cs
- `Common/Extensions/ProcessExtensions.cs` — https://github.com/JosefNemec/Playnite/blob/5911f4e964e628aa7a69c2030ab35101afa63067/source/Playnite/Common/Extensions/ProcessExtensions.cs
- `Controllers/GenericGameController.cs` — https://github.com/JosefNemec/Playnite/blob/5911f4e964e628aa7a69c2030ab35101afa63067/source/Playnite/Controllers/GenericGameController.cs
- `GamesEditor.cs` — https://github.com/JosefNemec/Playnite/blob/5911f4e964e628aa7a69c2030ab35101afa63067/source/Playnite/GamesEditor.cs
- `Windows/WindowFactory.cs` (the restore code) — https://github.com/JosefNemec/Playnite/blob/5911f4e964e628aa7a69c2030ab35101afa63067/source/Playnite/Windows/WindowFactory.cs
- `Native/User32.cs` — https://github.com/JosefNemec/Playnite/blob/5911f4e964e628aa7a69c2030ab35101afa63067/source/Playnite/Native/User32.cs
- `Playnite.FullscreenApp/ViewModels/FullscreenAppViewModel.cs` — https://github.com/JosefNemec/Playnite/blob/5911f4e964e628aa7a69c2030ab35101afa63067/source/Playnite.FullscreenApp/ViewModels/FullscreenAppViewModel.cs
- `PlayniteSDK/Models/GameAction.cs` (TrackingMode) — https://github.com/JosefNemec/Playnite/blob/5911f4e964e628aa7a69c2030ab35101afa63067/source/PlayniteSDK/Models/GameAction.cs
- `Input/GameController.cs` (SDL2 polling) — https://github.com/JosefNemec/Playnite/blob/5911f4e964e628aa7a69c2030ab35101afa63067/source/Playnite/Input/GameController.cs
- Legacy `ProcessMonitor` @ pinned submodule 39e7ff0 — https://github.com/JosefNemec/Playnite/blob/39e7ff05696d9f3f5561e4e62f4aa21cbb4cc2df/source/Playnite/Common/ProcessMonitor.cs
- Steam plugin — https://github.com/JosefNemec/PlayniteExtensions/blob/04b46a233e20f44db4f04194fe4fd0a73ed51295/source/Libraries/SteamLibrary/SteamGameController.cs and .../SteamLibrary.cs and .../Steam.cs
- Issue #3795 (fullscreen loses focus after game exit) — https://github.com/JosefNemec/Playnite/issues/3795

---

## 3. Running without explorer.exe as the shell

Evidence-quality warning up front: the Win32 shell API docs are authoritative but describe the Windows 7-era taskbar model, and almost all launcher/HTPC community evidence is 2013–2022 Windows-10-era. Windows 11 24H2 **moved shell surfaces between processes** (Quick Settings / Control Center migrated from `ShellExperienceHost.exe` to `ShellHost.exe`), so per-process claims must be re-verified on 26100. Flagged inline as **[26100-RISK]**.

### 3.1 What actually vanishes

- **Notification area / tray is the hardest breakage.** `Shell_NotifyIcon` lives in `shell32.dll` but is only a switchboard: it does `FindWindow` for the window class **`Shell_TrayWnd`** and forwards by `WM_COPYDATA`. No `Shell_TrayWnd` → the call fails with the error from `FindWindow`. **[COMMUNITY — Geoff Chappell, authoritative RE reference, but shell32 v4–v6 era]** Consequence: every app whose only affordance is a tray icon (Steam, Epic, GOG, Battle.net, Discord, audio mixers) runs headless. **[UNCERTAIN — sound inference from the API contract, not per-app tested]**
- Microsoft documents the other half: the Shell broadcasts a registered **`TaskbarCreated`** message when the taskbar is created and apps must re-add their icons then — and notes this "generally applies only to services that are already running when the Shell launches." **[PRIMARY]** So in a "start explorer later" workaround, only apps that handle `TaskbarCreated` get their icons back; this is the same failure class as "tray icons missing after explorer restart."
- **Taskbar COM surface is gated on a message we'll never get:** "When an application displays a window, its taskbar button is created by the system... That message [`TaskbarButtonCreated`] must be received by your application before it calls any `ITaskbarList3` method." **[PRIMARY]** → no taskbar progress, no icon overlays, no thumbnail toolbars. Jump lists attach to the taskbar button / Start entry, so they have nowhere to render (the `ICustomDestinationList`/`SHAddToRecentDocs` calls may still succeed silently **[UNCERTAIN]**). Deskbands (`IDeskBand2`) and appbars (`SHAppBarMessage`, work-area reservation) are gone.
- **Start / Search / modern hosts:** `StartMenuExperienceHost.exe` (Start) and `ShellExperienceHost.exe` (modern UI layer) are deliberately separate processes from explorer **[COMMUNITY]**, and **[26100-RISK]** Control Center moved to `ShellHost.exe` in 24H2 **[COMMUNITY]**. A kiosk-consultancy summary: with Shell Launcher, "Explorer-derived UI (notifications, the taskbar, the UI that appears on an edge swipe) simply does not exist." **[COMMUNITY]** Microsoft does document a separate IoT **edge-swipe policy** for touch devices **[PRIMARY]**.
- **System volume control** is repeatedly reported to require explorer (mute may still work) — Kodi HTPC lore and LaunchBox users losing "sound control or bringing up virtual keyboard" with BigBox as shell. **[COMMUNITY / UNCERTAIN — the Kodi thread 403s on fetch]** Plan on shipping our own volume UI via the Core Audio APIs.
- **Shell-role APIs:** `GetShellWindow` is documented to return **NULL when no Shell process is present** **[PRIMARY]** — so "no shell" is a first-class, detectable OS state. `RegisterShellHookWindow` is documented but flagged "not intended for general use... may be altered or unavailable"; it delivers `HSHELL_*` notifications and, critically, "the event messages received are only those sent to the **Shell window associated with the specified window's desktop**" **[PRIMARY]**. **`SetShellWindow` has no Microsoft reference page at all** — shell-replacement authors describe it as the only way to take the special bottom-of-z-order shell position (not reachable via `SetWindowPos`) **[COMMUNITY]**. No mainstream front-end (Playnite, Big Box, Steam BPM, EmulationStation, Kodi) appears to call it. **[UNCERTAIN — README review, not a source audit]**
- Registry mechanism: `Winlogon\Shell` (HKLM, or HKCU when HKLM points at `USR:...`) is documented only in **Windows 2000 / Windows Embedded Standard-era** pages **[PRIMARY but ancient]**. Note that modifying `Shell`/`Userinit`/`Notify` is a published malware-detection analytic (MITRE CAR-2021-11-002), so EDR may flag or revert it. **[COMMUNITY]**

### 3.2 Can you just start explorer.exe afterwards? — **direct conflict, and it's the highest-leverage unknown**

- Microsoft Q&A answer (non-Microsoft advisor): launching `explorer.exe` from within a custom shell "**no longer restores the full desktop experience** (taskbar, Start menu, desktop icons). Instead, it opens a standalone File Explorer window... the system suppresses the standard shell components unless explicitly configured otherwise." Fix = flip the shell assignment back **as SYSTEM** and force logoff/logon. **[COMMUNITY]**
- Contradicting: multiple HTPC projects run explorer as a *background* shell after their custom shell starts and observe the taskbar actually appear — GamesDows launches explorer on a 20 s delay so Steam's "Exit to Desktop" works, and lists "taskbar displays temporarily for ~1 second when explorer.exe launches" as a known issue. **[COMMUNITY]** The classic shell-newsgroup description also says explorer creates the desktop when `Winlogon\Shell` is absent/`explorer.exe` **and** no prior instance exists.
- Reconciliation hypothesis: **Shell Launcher (eshell/CustomShellHost) actively suppresses the standard shell, whereas a raw `Winlogon\Shell` swap does not.** **[UNCERTAIN — no source states this. Test #1 for us.]**
- Recovery path in every community setup: Ctrl+Alt+Del → Task Manager → Run new task → `explorer.exe` / `regedit`. Build an equivalent escape hatch into our shell from day one.

### 3.3 Launchers with no explorer shell

**Honest gap:** there is **no primary vendor documentation** from Valve, Epic, GOG, Blizzard, EA, Ubisoft or Microsoft about shell requirements. Everything here is community or inference.

- **Steam / Big Picture** — works as a `Winlogon\Shell` replacement; a mature pattern with several purpose-built projects (`steam-shell`, `SteamKiosk`, `Motion-Shell`, `GamesDows`). Reported failure modes: **log off / shut down from Big Picture doesn't act on Windows** (and `shutdown.exe`/`logoff.exe` added as non-Steam games "does not work, no matter the administrator rights"); **boot race** where Steam starts before the network, prompts offline/online, and a crash deadlocks a machine with no shell to fall back to (mitigation: a delay batch file as the shell); **"Exit to Desktop" is a no-op** unless explorer is running. Steam's tray icon is rendered through Chromium/`steamwebhelper` and breaks even *with* explorer; with no `Shell_TrayWnd` it can't be created at all **[UNCERTAIN — inferred]**. **[COMMUNITY]**
- **Epic, GOG Galaxy, Battle.net, EA app, Ubisoft Connect** — **no evidence found either way**. What can be said: all are Win32 apps with tray icons, so tray affordances are unreachable and "minimize to tray" hides them permanently; their overlays are per-game DLL injection rather than shell hooks, so overlays are the *least* likely thing to break. **[UNCERTAIN — inference]**
- **Xbox app / Game Pass** — riskiest. Packaged MSIX with the Xbox app as licensing broker; the widely-used "add Game Pass game to Steam" recipe is literally `explorer.exe shell:appsFolder\<AUMID>!App`, which is exactly the pattern at risk here (see 3.2 and area 5). **[COMMUNITY]**
- **TeknoParrot arcade titles require explorer.exe to already be running** — a concrete, non-launcher example of a game requiring the shell. **[COMMUNITY]**

### 3.4 Toasts and UWP activation

- **Delivery and presentation are separate layers.** In a documented 25H2 case (build 26200.8655 — near our target), `WpnService`/`WpnUserService` kept running, notifications were still received, and **Notification Center still listed them — only the toast popups stopped appearing**. **[COMMUNITY]**
- Reported directly for custom shells: with a custom shell and no explorer, **toast notifications for app install/restart do not display**; the accepted advice was to stop using native toasts and render your own dialogs. **[COMMUNITY — Intune tech community thread, no Microsoft reply]**
- No primary doc names the process that renders toast popups; candidates all load `wpnapps.dll`/`wpncore.dll`/`notificationcontroller.dll`. **[26100-RISK]** given the 24H2 `ShellHost.exe` move. **Expected behavior: `ToastNotificationManager...Show()` succeeds and nothing appears.** **[UNCERTAIN — test it]** → **our shell must own its own notification UI.**
- **"UWP requires explorer.exe" — contested.** A Microsoft Q&A advisor answer claims it flatly (repro: kill explorer with Calculator open → the app renders nothing and accepts no input until explorer returns). **[COMMUNITY]** Against it: Microsoft ships **Shell Launcher v2 whose entire purpose is a UWP app as the shell**, single-app Assigned Access runs a UWP kiosk with no explorer, and the docs say a custom Win32 shell "can then launch UWP apps, such as Settings and Touch Keyboard." **[PRIMARY]** Best reading: UWP needs *a registered shell* (CustomShellHost provides it; an arbitrary exe set via `Winlogon\Shell` does not) plus `ApplicationFrameHost.exe`. **[UNCERTAIN — reconciliation is inference]** → **another argument for Shell Launcher over a raw registry swap.**
- **Packaged-app registration degrades under a custom shell:** the unresolved `shell:AppsFolder` / `Get-StartApps` / `Get-AppxPackage` staleness reported with Shell Launcher V2 (see area 5.3). **[COMMUNITY]**

### 3.5 What real setups do

1. **Run explorer as a background process** (the dominant pattern). LaunchBox's canonical shell batch: kill explorer → `start /min BigBox.exe` → `timeout 30` → `start /min explorer.exe`. GamesDows does the same on a 20 s delay for Steam BPM/Playnite. Motion-Shell (HKCU `Winlogon\shell` → `Playnite.Fullscreen.exe`) needs a **BAT-to-EXE wrapper for `START explorer`** because launching explorer as a *child* of the shell app misbehaves. Kodi HTPCs have done this since forever via `SilentLaunch`/`InstantSheller`/`Launcher4Kodi`. **[COMMUNITY]**
2. **Avoid shell replacement entirely — use `Userinit`.** GameLauncherShell explicitly moved off shell replacement: "Switch from replace explorer.exe shell method to using Userinit to avoid explorer.exe related issues like random Onscreen Keyboard Popup and explorer window artifacts." Flow: normal logon → kill the desktop → fullscreen `mpv` boot video → launcher loads behind it → explorer restarts hidden beneath. Arguably the most robust community pattern. **[COMMUNITY]**
3. **Replace the shell outright and accept degradation** — PlayniteShell (requires disabling UAC), BatRun (RetroBat/EmulationStation, has an explicit "custom shell mode"), SteamKiosk (documents text-rendering glitches and that it does *not* stop users breaking out). **[COMMUNITY]**
4. **The Windows-11 handheld answer is closed to us:** the Xbox **Full Screen Experience** is an OEM-only posture — per a Microsoft Q&A response, only OEMs can register a boot-into-full-screen "home app", there is **no public GDK API or registration flow**, and no certification path; the suggested action is a Feedback Hub request. Community forcing via PsExec-as-SYSTEM + a scheduled `Physpanel` task is unsupported and reportedly unstable. **[COMMUNITY]**
5. **Nobody calls `SetShellWindow`/`RegisterShellHookWindow`.** If we want proper Show-Desktop/minimize-all/z-order semantics we'd be ahead of the field — using an undocumented API. **[UNCERTAIN]**

### Sources — area 3

- Shell Launcher overview — https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/
- Kiosk configuration options — https://learn.microsoft.com/en-us/windows/configuration/kiosk/
- The Taskbar (Win32, `TaskbarCreated`, Shell_NotifyIcon) — https://learn.microsoft.com/en-us/windows/win32/shell/taskbar
- Taskbar Extensions (`TaskbarButtonCreated` precondition, jump lists, deskbands) — https://learn.microsoft.com/en-us/windows/win32/shell/taskbar-extensions
- GetShellWindow (NULL when no shell) — https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getshellwindow
- RegisterShellHookWindow — https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registershellhookwindow
- ToastNotificationManager — https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.toastnotificationmanager
- Edge swipe policy (IoT) — https://learn.microsoft.com/en-us/windows/iot/iot-enterprise/customize/edge-swipe-policy
- Winlogon Shell value (Win2000-era) — https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-2000-server/cc939851(v=technet.10)
- Per-user shells (Windows Embedded Standard) — https://learn.microsoft.com/en-us/previous-versions/windows/embedded/ms838576(v=winembedded.5)
- Geoff Chappell — Shell_NotifyIcon internals — https://www.geoffchappell.com/studies/windows/shell/shell32/api/shlnot/notifyicon.htm
- zhuman/ShellReplacement wiki (SetShellWindow) — https://github.com/zhuman/ShellReplacement/blob/master/wiki/Desktop.md
- Q&A: full desktop access from a custom shell — https://learn.microsoft.com/en-us/answers/questions/5576492/how-can-we-enable-full-windows-desktop-access-from
- Q&A: "UWP requires explorer.exe" claim — https://learn.microsoft.com/en-us/answers/questions/679864/start-uwp(universal-windows-platform)-application
- Q&A: register an app for Xbox Full Screen Experience (OEM-only) — https://learn.microsoft.com/en-us/answers/questions/5582690/expose-the-means-to-register-an-app-as-an-option-f
- Q&A: tray icons lost on explorer restart — https://learn.microsoft.com/en-us/answers/questions/1316178/process-explorer-systray-taskbar-tray-icons-lost-o
- ExplorerPatcher #5068 (toasts vs Notification Center, 25H2 26200) — https://github.com/valinet/ExplorerPatcher/issues/5068
- Intune thread: install/restart notifications with a custom shell — https://techcommunity.microsoft.com/discussions/microsoft-intune/install--restart-notifications-when-using-custom-shell/3300210
- windhawk-mods #909 (Control Center → ShellHost.exe in 24H2) — https://github.com/ramensoftware/windhawk-mods/issues/909
- GamesDows — https://github.com/jazir555/GamesDows
- GameLauncherShell (Userinit approach) — https://github.com/quangmach/GameLauncherShell
- PlayniteShell — https://github.com/Blueforcer/PlayniteShell
- BatRun (RetroBat custom shell mode) — https://github.com/Aynshe/BatRun
- SteamKiosk — https://github.com/Thomasluigi07/SteamKiosk · steam-shell — https://github.com/frodough/steam-shell
- LaunchBox: launch explorer after BigBox — https://forums.launchbox-app.com/topic/56196-is-there-a-simple-way-to-launch-explorerexe-after-bigbox-has-launchedopened/
- LaunchBox: BigBox as Windows shell — https://forums.launchbox-app.com/topic/90592-a-few-questions-about-bigbox-as-windows-shell/
- Steam: Big Picture as Windows shell — https://steamcommunity.com/discussions/forum/1/492378265882455750/ and https://steamcommunity.com/groups/bigpicture/discussions/1/846938351024388076/
- MITRE CAR-2021-11-002 (Winlogon Shell modification detection) — https://car.mitre.org/analytics/CAR-2021-11-002/

---

## 4. Controller stack

### 4.0 What is actually attached to this machine (checked locally, 2026-08-13) — this changes the recommendation

```
HID\VID_054C&PID_0CE6&MI_03\7&155FFC93&0&0000                                  (USB composite HID iface)
HID\VID_054C&PID_0CE6&MI_03\7&28DF4100&0&0000                                  (USB composite HID iface)
HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\8&1FFB30CB&...  (Bluetooth HID)
HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\8&38F59730&...  (Bluetooth HID)
```

`VID_054C / PID_0CE6` = **Sony DualSense** (two pads, each with a USB and a Bluetooth enumeration). A scan for device instance paths containing `IG_` returned **nothing** — and `IG_` in the HID instance path is precisely the documented marker XInput uses to identify XInput-capable devices. **There is no XInput device on this box.** **[PRIMARY — local device enumeration]**

Consequences, which invert the usual advice:

- **XInput (`xinput1_4.dll`) will report zero connected controllers here.** Sony pads are HID/DirectInput devices; Windows' inbox driver does not present them to XInput. Getting a DualSense into XInput requires a translation layer — **DS4Windows/DSX (ViGEm virtual Xbox pad)** or **Steam Input's PlayStation Configuration Support**. **[COMMUNITY — consistent across PCGamingWiki, DS4Windows docs, vendor-neutral guides]**
- **Windows.Gaming.Input will not surface a DualSense as `Gamepad` either.** WGI's specialized classes (`Gamepad`, `RacingWheel`, …) cover Xbox-compatible devices; everything else — including PlayStation pads — is reachable only through **`RawGameController`**, and `Gamepad.FromGameController()` returns null for them. **[PRIMARY — Microsoft Learn RawGameController / raw-game-controller docs]**
- So for *this* hardware, the real choice is **`RawGameController` (WGI) or raw HID**, not "XInput vs WGI". A DualSense-native path also unlocks the touchpad, gyro, adaptive triggers and the PS button, none of which survive an XInput translation cleanly.
- Raw HID shape (for a native reader): USB input report id **`0x01`**; Bluetooth switches to report id **`0x31`, 78 bytes**, with a CRC32 over `[BT report type | report id | payload]` seeded `0xA1`, and the BT stream only starts emitting the full report after a `GET_REPORT 0x20` feature request. The canonical references are the Linux kernel `drivers/hid/hid-playstation.c` and SDL's PS5 hidapi driver. **[PRIMARY — kernel + SDL source]**
- Practical Windows-11 quirks to expect with BT DualSense: Windows also enumerates the pad's **audio interfaces**, and there are recurring reports of it attaching as an audio device and disrupting the HID stream (mitigation: disable it in `mmsys.cpl` playback/recording); pairing failures on Win11 are a common Q&A topic. **[COMMUNITY]**
- **Strong recommendation: use SDL3 (or SDL2) as the input layer** rather than hand-rolling. SDL already carries the PS5 hidapi driver (BT/USB report parsing, CRC, LED/rumble/trigger effects), handles hot-plug, and — relevant to area 2 — is exactly what Playnite Fullscreen uses for its own controller input (`source/Playnite/Input/GameController.cs`). It also gives us XInput and WGI paths for free if an Xbox pad ever shows up.

### 4.1 XInput vs Windows.Gaming.Input vs GameInput — background/focus behavior

- **XInput versions:** `xinput1_4.dll` is inbox since Windows 8 (correct target, no redist); `xinput9_1_0.dll` is the reduced legacy set; `xinput1_3.dll` was the DirectX SDK redist. **[PRIMARY]**
- **Does XInput read in the background?** The main XInput programming guide documents a plain polling loop with **no focus gating at all**. **[PRIMARY]** The one confusing doc is `XInputEnable`, whose remark says: "Windows 10 or later: *Deprecated*, as game controller input is automatically enabled/disabled by the system based on the application window focus." **[PRIMARY]** Microsoft's own DirectXTK says the opposite about state reads — "Unlike mouse or keyboard input on Windows, **XInput has 'global' focus** when reading the game controller" — and provides `Suspend()`/`Resume()` precisely because the OS does *not* zero the state for you; its source only calls `XInputEnable` on pre-Win10 builds. **[PRIMARY — Microsoft repo]** Best reading: **the Win10+ automatic behavior is about vibration, not state reads**; XInput polling still works unfocused, and a whole class of background tray remappers depends on it. **[COMMUNITY corroboration]** ⚠️ Unresolved conflict — worth an empirical check if we ever do have an XInput device.
- **Windows.Gaming.Input is focus-gated.** The reference pages say nothing about focus (documentation gap), but SDL's WGI backend states it in a source comment: **"The axes are all zero when the application loses focus."** **[PRIMARY — SDL source]** There is also a known failure mode where `Gamepad.GetCurrentReading()` returns zeroes in **console/desktop apps without package identity** **[COMMUNITY]**. No Microsoft changelog announcing when WGI became foreground-only could be found. **[UNCERTAIN]**
- **GameInput (GDK) is the only *officially supported* background path.** Supported on Windows 10 19H1+ and Windows 11, callable from Win32; shipped via the **`Microsoft.GameInput` NuGet** (public header + static lib MIT-licensed as of 3.5) with a **`GameInputRedist.msi`** you must bundle; runtime is `GameInput.dll` in System32. **[PRIMARY]** `IGameInput::SetFocusPolicy` with `GameInputEnableBackgroundInput` (0x40) — "**By default, GameInput will not provide background input**"; system buttons need their own flags. **[PRIMARY]** ⚠️ Two live Microsoft pages contradict each other: the XInput→GameInput **porting guide still says "On PC, input goes to all processes by default"**, which is stale — trust the `GameInputFocusPolicy` reference. **[PRIMARY, conflicting]**
  - Directly relevant bug: **GDK issue #104 — with `GameInputEnableBackgroundInput`, `RegisterReadingCallback` does not fire for gamepads while polling `GetCurrentReading` does**, reported on **Windows 11 build 26100**, GameInput 2.1.26100.6068. Closed without public detail. → **poll, don't rely on reading callbacks.** **[PRIMARY — Microsoft GitHub]**
  - **Do not use the `XInputOnGameInput` shim** in a shell: it returns neutral values whenever the app isn't focused, "regardless of any calls to `XInputEnable`", and raises `XUSER_MAX_COUNT` from 4 to 8. **[PRIMARY]**

### 4.2 Guide button

- **`XInputGetStateEx` = ordinal 100**, same `XINPUT_STATE` struct as `XInputGetState`, Guide reported as **`XINPUT_GAMEPAD_GUIDE = 0x0400`** in `wButtons`. Exported **by ordinal only** → `GetProcAddress(h, MAKEINTRESOURCEA(100))`. Present in `xinput1_4.dll` and `xinput1_3.dll`, **absent from `xinput9_1_0.dll`**. **[PRIMARY — SDL source; COMMUNITY — the original RE writeup]**
- **Verified locally on this machine** (PE export-table dump of `C:\Windows\System32\xinput1_4.dll`, ProductVersion **10.0.26100.8457**): ordinal base 1, 109 function slots, only 8 named exports (`DllMain`, `XInputGetState`, `XInputSetState`, `XInputGetCapabilities`, `XInputEnable`, `XInputGetBatteryInformation`, `XInputGetKeystroke`, `XInputGetAudioDeviceIds`) plus **noname ordinals 100, 101, 102, 103, 104, 108, 109**. So the hidden ordinals (100 `XInputGetStateEx`, 101 `XInputWaitForGuideButton`, 102 `XInputCancelGuideButtonWait`, 103 `XInputPowerOffController`, 104 `XInputGetBaseBusInformation`, 108 `XInputGetCapabilitiesEx`) **still exist on Windows 11 26100**. **[PRIMARY — local binary]** Still code the SDL-style fallback to the named export.
- **WGI cannot report Guide** — `GamepadButtons` has 19 members and no Guide/Xbox/Nexus. **[PRIMARY]**
- **GameInput is the only officially supported Guide-button API:** `GameInputSystemButtons` (`Guide = 0x1`, `Share = 0x2`) + `IGameInput::RegisterSystemButtonCallback`, explicitly a Windows capability ("On the Xbox, the shell always consumes the guide button, so these callbacks are never dispatched on Xbox"). Background Guide needs `GameInputEnableBackgroundGuideButton` (0x80); `GameInputExclusiveForegroundGuideButton` (0x8) lets a focused app **claim** it away from overlays. **[PRIMARY]**
- For our DualSense reality: the **PS button** is just another bit in the HID input report, so a raw-HID/SDL reader gets it for free — no undocumented ordinals, no GameInput redist. Contention with Steam/Game Bar for that button is a separate problem.

### 4.3 Steam Input conflicts

- **The key architectural fact, straight from Steamworks:** "On Windows the Steam Overlay will **hook** traditional gamepad input APIs such as XInput, DirectInput, RawInput, and Windows.Gaming.Input and **inject an emulated Xbox controller device**. On macOS and Linux emulated controller input is provided by a driver." **[PRIMARY]**
  - So on Windows Steam's virtual pad is a **per-process hook, not a system device**. Our shell, launched outside Steam, sees the **physical** controller; Steam's injected pad normally never reaches us. **Double-input is a game-side problem, not a shell-side one.**
  - Steam does **not** hide the physical device system-wide on Windows (no kernel filter of its own).
- **The inverse risk is the real one for us:** because XInput/raw HID reads are global-focus, our background polling fires at the same time as the foreground game. **Gate input handling on our own window focus** (the DirectXTK `Suspend()`/`Resume()` pattern) or the shell will act on inputs meant for the game.
- Discriminators if we ever need to filter Steam's virtual pad: **VID `0x28DE` / PID `0x11FF`** (SDL detects it and parses the WGI non-roamable id `{wgi/nrid/:steam-...}`), or RawInput `RIDI_DEVICENAME`, which Steamworks recommends because it encodes the real device's VID/PID plus the Steam Input handle. **[PRIMARY — SDL source + Steamworks]**
- For SDL-based clients Steam suppresses the physical pad **cooperatively via `SDL_GAMECONTROLLER_IGNORE_DEVICES`**, not by hiding the device. **[COMMUNITY, well attested]**
- Steam ships one real kernel driver, `steamxbox.sys` ("Xbox Extended Feature Support"), which is *not* the emulation mechanism. **[COMMUNITY]**
- **HidHide** (kernel-mode HID filter, now the recommended path for DS4Windows instead of its old exclusive mode) *does* hide devices system-wide with a per-exe allowlist. If a user runs it, our shell must be allowlisted or it sees **no controllers at all** — detect and surface that rather than showing "no controller found". **[COMMUNITY]** Highly relevant here, since DualSense users are exactly the DS4Windows/HidHide population.

### 4.4 Wake from sleep with a controller

| Transport | Wake-capable? | Confidence |
|---|---|---|
| Wired USB | generally yes, if "Allow this device to wake the computer" is offered and BIOS keeps port power | [COMMUNITY + PRIMARY mechanism] |
| Xbox Wireless Adapter (dongle) | sometimes; revision-dependent (older rev 1713 reportedly yes, recent no) | [COMMUNITY, conflicting] |
| **Bluetooth** | **effectively no** — Power Management tab typically absent/greyed | [COMMUNITY, strong consensus] |

- Mechanism: USB remote wake requires the device to be armed via `IRP_MN_WAIT_WAKE`; the USB stack sets `DEVICE_REMOTE_WAKEUP` only on transition to **D1/D2** — "The USB stack does not enable the device for remote wake-up when it receives a request to change the device to a sleep state of D3, because according to the WDM power model, devices in D3 cannot wake the system." Microsoft's enumerated remote-wake device classes are mice, keyboards, hubs, modems, NICs — **not gamepads**. **[PRIMARY]** Also: "Any HID device must have wakeup capability to be usable for selective suspend," and Microsoft **recommends against disabling selective suspend** — the usual folk fix is counterproductive. **[PRIMARY]**
- Modern Standby wake-source spec lists integrated/USB/BT keyboards and mice, touchpad, touchscreen, fingerprint reader — **gamepads appear nowhere**, and that doc is an exhaustive OEM requirements spec, so the omission is meaningful. **[PRIMARY]**
- **24H2-specific (our build):** "Starting in Windows 11, version 24H2, a new power-saving measure was introduced to Modern Standby... **If excessive battery drain is detected, most wake sources will be disabled**," leaving power button / lid. 24H2 also added input suppression after a power-button press and removed Voice Input as a wake source. **[PRIMARY]**
- Handhelds are S0ix-only in practice; ROG Ally owners report the integrated pad's buttons don't wake it. **[COMMUNITY]**
- **Design conclusion: do not build any feature that depends on controller wake.** Our two DualSense pads are Bluetooth — assume no wake. Dependable paths are the chassis power button and (on handhelds) the Windows button. Note also that **Bluetooth pads typically must be re-paired/reconnected by a button press after resume**, so plan a "reconnecting controller…" state in the shell UI.

### Sources — area 4

- XInput versions — https://learn.microsoft.com/en-us/windows/win32/xinput/xinput-versions · Getting started with XInput — https://learn.microsoft.com/en-us/windows/win32/xinput/getting-started-with-xinput
- XInputEnable (the "deprecated on Win10+" remark) — https://learn.microsoft.com/en-us/windows/win32/api/xinput/nf-xinput-xinputenable
- DirectXTK GamePad wiki (XInput has global focus) — https://github.com/microsoft/DirectXTK/wiki/GamePad · GamePad.cpp — https://github.com/microsoft/DirectXTK/blob/main/Src/GamePad.cpp
- Windows.Gaming.Input `Gamepad` — https://learn.microsoft.com/en-us/uwp/api/windows.gaming.input.gamepad · `GamepadButtons` (no Guide) — https://learn.microsoft.com/en-us/uwp/api/windows.gaming.input.gamepadbuttons
- `RawGameController` class — https://learn.microsoft.com/en-us/uwp/api/windows.gaming.input.rawgamecontroller · Raw game controller guide — https://learn.microsoft.com/en-us/windows/uwp/gaming/raw-game-controller · `Gamepad.FromGameController` — https://learn.microsoft.com/en-us/uwp/api/windows.gaming.input.gamepad.fromgamecontroller
- GameInput fundamentals — https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/input/overviews/input-fundamentals · FAQ — https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/input/overviews/input-faq · NuGet/redist — https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/input/overviews/input-nuget
- `GameInputFocusPolicy` — https://learn.microsoft.com/en-us/gaming/gdk/docs/reference/input/gameinput/enums/gameinputfocuspolicy · `GameInputSystemButtons` — https://learn.microsoft.com/en-us/gaming/gdk/docs/reference/input/gameinput/enums/gameinputsystembuttons · `RegisterSystemButtonCallback` — https://learn.microsoft.com/en-us/gaming/gdk/docs/reference/input/gameinput/interfaces/igameinput/methods/igameinput_registersystembuttoncallback
- Stale porting guide ("input goes to all processes") — https://learn.microsoft.com/en-us/gaming/gdk/docs/features/common/input/porting/input-porting-xinput
- GDK issue #104 (background callbacks broken on 26100) — https://github.com/microsoft/GDK/issues/104
- SDL XInput loader / ordinal 100 — https://github.com/libsdl-org/SDL/blob/main/src/core/windows/SDL_xinput.c · `XINPUT_GAMEPAD_GUIDE 0x0400` — https://github.com/libsdl-org/SDL/blob/main/src/core/windows/SDL_xinput.h
- SDL WGI backend ("axes are all zero when the application loses focus", Steam virtual pad ids) — https://github.com/libsdl-org/SDL/blob/main/src/joystick/windows/SDL_windows_gaming_input.c
- Wine: XInputGetStateEx uses the same struct — https://www.winehq.org/pipermail/wine-cvs/2018-January/124932.html
- XInput hidden ordinals writeup — https://reverseengineerlog.blogspot.com/2016/06/xinputs-hidden-functions.html
- Steamworks: Steam Input gamepad emulation best practices — https://partner.steamgames.com/doc/features/steam_controller/steam_input_gamepad_emulation_bestpractices · Getting started for devs — https://partner.steamgames.com/doc/features/steam_controller/getting_started_for_devs
- DS4Windows exclusive mode → HidHide — https://github.com/Ryochan7/DS4Windows/wiki/Exclusive-Mode-(Hide-DS4-Controller-config-option)-tips-and-issues
- Linux `hid-playstation.c` (DualSense report formats/CRC) — https://github.com/torvalds/linux/blob/master/drivers/hid/hid-playstation.c
- PCGamingWiki: DualSense (DirectInput-only without a wrapper) — https://www.pcgamingwiki.com/wiki/Controller:DualSense
- Q&A: DualSense connects as an audio device — https://learn.microsoft.com/en-us/answers/questions/3876833/dualsense-controller-is-connecting-to-windows-howe
- Modern Standby wake sources (24H2 battery-drain guard) — https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/modern-standby-wake-sources
- Remote wake-up of USB devices — https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/remote-wakeup-of-usb-devices · USB selective suspend — https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/usb-selective-suspend
- Q&A: Xbox controller over integrated Bluetooth can't wake — https://learn.microsoft.com/en-us/answers/questions/4030464/allow-xbox-controller-integrated-bluetooth-to-wake

---

## 5. Launching UWP / Xbox (Game Pass) titles without explorer

### 5.1 `IApplicationActivationManager` — what's documented

- `CLSID_ApplicationActivationManager` = `{45BA127D-10A8-46EA-8AB7-56EA9078943C}`; interface `IApplicationActivationManager` (`shobjidl_core.h`). Methods: `ActivateApplication(appUserModelId, arguments, ACTIVATEOPTIONS, out processId)`, `ActivateForFile`, `ActivateForProtocol`. `ActivateApplication` "activates the specified Windows Store app for the generic launch contract (`Windows.Launch`) **in the current session**". Min client Windows 8, **desktop apps only**. **[PRIMARY]**
- `ACTIVATEOPTIONS`: `AO_NONE 0x0`, `AO_DESIGNMODE 0x1`, `AO_NOERRORUI 0x2`, `AO_NOSPLASHSCREEN 0x4`, `AO_PRELAUNCH 0x2000000`. `AO_NOERRORUI` is the one worth setting in a shell (no modal error dialog on a TV). `AO_DESIGNMODE`/`AO_NOSPLASHSCREEN` require debug mode enabled on the package. **[PRIMARY]**
- **The reference page says nothing about explorer.exe.** There is no documented dependency on the shell process. **[PRIMARY — absence of a stated requirement, not a positive guarantee]**
- On this machine the CLSID resolves to **`InProcServer32 = twinui.appcore.dll` (ThreadingModel Both)** — the activation manager is loaded *into the caller*, and the actual launch is brokered by the app-model services (AppX / State Repository / RPC), not by `explorer.exe`. This is the strongest structural argument that AAM does not need explorer. **[PRIMARY — this machine's registry; still an inference about the runtime path]**
- Strong indirect primary evidence that UWP activation works with no explorer shell: Shell Launcher **v2 can run a UWP app *as the shell*** (`CustomShellHost.exe`), single-app Assigned Access kiosks run a UWP app with no explorer shell at all, and the Shell Launcher docs say a custom **Win32** shell "can then launch UWP apps, such as Settings and Touch Keyboard". **[PRIMARY]**
- Known caveat (not explorer-related): calling from an **elevated** process is problematic — Store apps activated this way either fail or launch unelevated, because elevated processes don't load per-user COM classes. Another reason to keep the shell non-elevated. **[COMMUNITY + PRIMARY-adjacent (COM elevation moniker doc)]**

### 5.2 The `shell:AppsFolder\<AUMID>` alternative — and why it's a trap here

- Real-world reference implementation: **Playnite** enumerates UWP/Store/Xbox games with `PackageManager.FindPackagesForUser`, parses `AppxManifest.xml` for the `Application/@Id`, and stores the launch action as:

  ```csharp
  // source/Playnite/Common/Programs2.cs — GetUWPApps()
  Path      = "explorer.exe",
  Arguments = $"shell:AppsFolder\\{package.Id.FamilyName}!{appId}",
  ```
  and `XboxPlayController.Play()` (PlayniteExtensions `source/Libraries/XboxLibrary/XboxGameController.cs`) does `ProcessStarter.StartProcess(prg.Path, prg.Arguments)` — i.e. **it shells out to `explorer.exe`**. It then watches the package's install directory for processes (`procMon.WatchDirectoryProcesses(prg.WorkDir, …)`) because the launched `explorer.exe` tells it nothing about the game process. There's a `// TODO switch to WatchUwpApp once we are building as 64bit app` in that file. **[PRIMARY — source code]**
- Why this matters for us: **launching `explorer.exe` when no shell is registered/running makes that instance become the shell** (desktop + taskbar appear). The classic description: explorer checks `HKLM\...\Winlogon\Shell`; if that value is absent or is `explorer.exe` **and no previous explorer instance exists**, it creates the desktop; otherwise it opens a browser window. With Shell Launcher the `Winlogon\Shell` value is *not* `explorer.exe`, so in principle we get a plain window — but the community Q&A "launching explorer.exe from a custom shell only opens a file explorer window, not the desktop" confirms the *other* half of the risk: it also does **not** give us the shell services back. **[COMMUNITY — behavior described in shell newsgroups/forums; no current Microsoft doc states it]**
- Therefore: **prefer `IApplicationActivationManager::ActivateApplication` over `explorer.exe shell:AppsFolder\…`** in our shell. Keep the AppsFolder form only as a fallback, and if used, invoke it via `ShellExecuteEx` on the parsing name `shell:AppsFolder\<PFN>!<AppId>` rather than by spawning `explorer.exe`. **[UNCERTAIN — the ShellExecute-on-shell:-path variant was not verified against a primary source; needs a bench test with the shell replaced]**
- `ActivateApplication` gives us the **process ID** of the activated app, which `explorer.exe shell:AppsFolder\…` does not. That PID is the anchor for exit detection (and for the launcher-spawns-child problem). Note UWP games are subject to PLM and can be suspended/terminated by the system, so PID-only tracking is not sufficient by itself. **[PRIMARY for the out-param; UNCERTAIN for PLM edge cases]**

### 5.3 Known breakage of the AppsFolder / app enumeration under a custom shell

- Reported on **Windows 11 Enterprise with Shell Launcher V2 + a Win32 custom shell**: `shell:AppsFolder` becomes effectively frozen — apps installed after the custom shell was configured never appear, uninstalled apps linger, and even `Get-StartApps` / `Get-AppxPackage` reportedly miss new installs. "It's as if Windows isn't aware of app installations/removal at all when using a custom shell." No Microsoft answer given (thread from Feb 2023). **[COMMUNITY — unresolved; would break library refresh if we depend on AppsFolder enumeration]**
  - Mitigation prior: enumerate with **`PackageManager.FindPackagesForUser` + AppxManifest parsing** (Playnite's approach) rather than the AppsFolder shell namespace, since that reads the package registry directly.
- A custom-shell project (`a-l-r1/proj.run-uwp-apps-with-windows-custom-shell`) reports that with a non-Explorer shell, UWP apps are broadly inaccessible without going through Shell Launcher / `CustomShellHost.exe`, and separately reports **notification failures** and **missing Wi-Fi/volume tray icons**. **[COMMUNITY]**
- Error code to expect when the AUMID/contract is wrong: `0x80270254` = "This app does not support the contract specified or is not installed." **[COMMUNITY, code meaning is well-known]**

### 5.4 Xbox / Game Pass specifics

- Game Pass PC titles are packaged apps installed via **Gaming Services** (`GamingServices` + `GamingServicesNet` services + the `Microsoft.GamingServices` package); they are activated by AUMID like any other packaged app. Playnite's Xbox plugin proves the AUMID path works for Game Pass titles in an ordinary desktop session. **[PRIMARY for Playnite's implementation; COMMUNITY for the Gaming Services description]**
- Playnite explicitly refuses console-only entries (`if (Game.GameId.StartsWith("CONSOLE")) throw`), installs via `ms-windows-store://pdp/?PFN=<pfn>` or by opening the Xbox app, and uninstalls by opening `ms-settings:appsfeatures` — i.e. **install/uninstall are delegated to Store/Settings UWP UI**, which in our shell means those UWP surfaces must be activatable too. **[PRIMARY — source code]**
- **No source found** that specifically tests Game Pass title launch with `explorer.exe` absent as the shell. This is the single biggest untested assumption in area 5 and should be bench-tested early (Shell Launcher on + `cmd.exe` as shell + a small AAM test harness + one Game Pass title). **[UNCERTAIN]**

### Sources — area 5

- IApplicationActivationManager::ActivateApplication — https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication
- ActivateForProtocol — https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateforprotocol
- Automate launching UWP apps (AAM sample) — https://learn.microsoft.com/en-us/windows/uwp/xbox-apps/automate-launching-uwp-apps
- Find the AUMID of an installed app — https://learn.microsoft.com/en-us/windows/configuration/store/find-aumid
- Playnite `Programs2.GetUWPApps()` — https://github.com/JosefNemec/Playnite/blob/master/source/Playnite/Common/Programs2.cs
- Playnite Xbox play controller — https://github.com/JosefNemec/PlayniteExtensions/blob/master/source/Libraries/XboxLibrary/XboxGameController.cs
- UWPHook `AppManager.cs` (AAM in C#, no explorer fallback) — https://github.com/BrianLima/UWPHook/blob/master/UWPHook/AppManager.cs
- Q&A: shell:AppsFolder not updating under Shell Launcher V2 — https://learn.microsoft.com/en-us/answers/questions/1179925/how-to-get-all-apps-to-show-in-shell-appsfolder-wh
- Q&A: Kiosk mode with ShellLauncher — app that launches other apps — https://learn.microsoft.com/en-us/answers/questions/236085/kiosk-mode-with-shelllauncher-using-an-app-that-la
- proj.run-uwp-apps-with-windows-custom-shell — https://github.com/a-l-r1/proj.run-uwp-apps-with-windows-custom-shell
- "shelling to explorer.exe opens desktop" (explorer-becomes-shell behavior) — https://groups.google.com/g/microsoft.public.platformsdk.shell/c/HuNxxO07Rvc

---

## 6. Open questions — bench tests to run before committing to a design

Ordered by how much design they unblock. All should be run on a throwaway account on 26100 with Shell Launcher enabled and a trivial shell (`cmd.exe` or a stub app), with a Task Manager escape hatch ready.

1. **Does `explorer.exe` started from under a Shell Launcher shell give a real desktop, or only a File Explorer window?** Sources directly contradict each other (3.2). This decides whether the whole "keep explorer alive in the background" family of workarounds — which every community HTPC setup relies on — is available to us, and therefore whether tray icons, toasts and `shell:AppsFolder` come back for free.
2. **Does `IApplicationActivationManager::ActivateApplication` launch a Game Pass title with no explorer shell?** Everything structural says yes (in-proc `twinui.appcore.dll`, UWP-as-shell is a supported Shell Launcher v2 mode) but nobody has published a test. Write a 30-line AAM harness and try one Game Pass game and one Store app.
3. **Do toasts render at all?** Call `ToastNotificationManager` from under the custom shell; check whether anything appears and whether Notification Center still accrues entries. Expect "API succeeds, nothing visible" → budget for our own notification UI.
4. **How long is the black gap between logon and our shell's first frame**, Shell Launcher vs. raw `Winlogon\Shell`? The 55 s community claim (1.5.2) is one anecdote; measure it. If Shell Launcher is slow, the fallback is the registry shell with our own crash-restart supervisor.
5. **Does `shell:AppsFolder` / `Get-AppxPackage` staleness (5.3) reproduce on 26100?** If yes, enumerate packages with `PackageManager.FindPackagesForUser` and never trust the AppsFolder namespace.
6. **Controller behavior with the actual DualSense pads:** confirm SDL3 sees both over Bluetooth, that input keeps arriving while a game is foreground (and that we correctly suppress it), and how long reconnection takes after resume from sleep.
7. **Steam:** does Big Picture's "Exit to Desktop" / shutdown do anything sane under our shell, and does launching `steam.exe -silent "steam://rungameid/<id>"` behave when Steam has never had an explorer session (tray icon absent)?
