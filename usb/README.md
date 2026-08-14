# Bootable USB — Windows 11 IoT Enterprise LTSC 2024

Tooling to write the official Microsoft **Windows 11 IoT Enterprise LTSC 2024** evaluation ISO
(x64, en-us) to a USB flash drive as a UEFI-bootable installer.

---

## ⚠️ This script destroys data

`make-boot-usb.ps1` **irreversibly erases physical Disk 1** — the ` USB DISK 3.2`, 29.3 GB
USB stick. Everything on that stick is permanently lost. There is no undo and no recovery.

The system disk (Disk 0, `NVMe WDC PC SN730 SDBQNTY-1T00-1014`, 953.87 GB) is never touched.
The script hard-refuses to operate on Disk 0 and on any disk flagged as boot or system.

### Guards re-checked live on every run

Before a single destructive command executes, the script re-verifies **all** of:

| Guard | Required value |
|---|---|
| Disk exists | Disk number 1 is present and readable |
| `BusType` | `USB` |
| `FriendlyName` (trimmed) | `USB DISK 3.2` |
| Size | between 25 GB and 35 GB |
| Not boot/system | `IsBoot` and `IsSystem` both false |
| Not Disk 0 | disk number ≠ 0 |

If **any** guard fails, the script aborts loudly and writes nothing. It never falls back to a
different disk number. On top of the guards there is an interactive confirmation: you must type
`ERASE` in capitals to proceed (bypass with `-Force` for unattended runs).

---

## What the script does

1. **Elevation** — detects a non-elevated session and relaunches itself via
   `Start-Process -Verb RunAs` (UAC prompt).
2. **Validates the ISO** exists at the path below.
3. **Re-runs every guard** listed above against the live disk.
4. **Confirms** with the operator (type `ERASE`).
5. **Mounts the ISO read-only** and discovers its drive letter dynamically. Sanity-checks that
   the image actually contains `bootmgr`, `efi\boot\bootx64.efi` and `sources\`.
6. **Erases and repartitions** — `Clear-Disk` → `Initialize-Disk -PartitionStyle GPT` →
   `New-Partition -UseMaximumSize`, preferring drive letter `E:` and falling back to an
   auto-assigned letter (it reports which it used).
7. **Formats FAT32**, label `WIN11LTSC`. FAT32 is required for the widest UEFI firmware
   compatibility. 29.3 GB is under the 32 GB ceiling Windows imposes on FAT32 formatting.
   If `Format-Volume` refuses, it falls back to `format.com /FS:FAT32 /Q` and reports which
   method succeeded.
8. **Copies the payload** with `robocopy /E`. Exit codes 0–7 are success; 8+ aborts.
9. **Splits `install.wim` if needed** — see below.
10. **Verifies** that `bootmgr`, `bootmgr.efi`, `efi\boot\bootx64.efi`, `setup.exe` and the
    install image are all present, prints their sizes, the volume free space, and a PASS/FAIL
    summary.
11. **Dismounts the ISO.**

### The FAT32 4 GiB question

FAT32 cannot hold a single file larger than 4,294,967,295 bytes.

For **this** ISO, `sources\install.wim` is **4,247,599,512 bytes** — it fits, with roughly 47 MB
of headroom. **No split is required.** The script still performs the size check every run, so if
you point it at a future or different ISO whose `install.wim` exceeds the limit, it automatically
excludes the file from the bulk copy and splits it with
`dism /Split-Image /FileSize:3800` into `install.swm` + `install2.swm`. DISM reads the ISO and
writes only to the USB — the ISO is never modified.

---

## ISO provenance

| | |
|---|---|
| **Product** | Windows 11 IoT Enterprise LTSC 2024 Evaluation, x64, English (en-us) |
| **Build** | 26100.1742.240906-0331.ge_release_svc_refresh |
| **Local path** | `C:\Users\brain\Downloads\26100.1742.240906-0331.ge_release_svc_refresh_CLIENT_IOT_LTSC_EVAL_x64FRE_en-us.iso` |
| **Size** | 5,060,020,224 bytes |
| **Volume label** | `CESE_X64FREE_EN-US_DV9` |
| **Evaluation term** | 90 days, resettable up to 3 times (270 days total). No product key needed. |

**Download page:**
<https://www.microsoft.com/en-us/evalcenter/download-windows-11-iot-enterprise-ltsc-eval>

**Link followed (x64 / en-us):**
`https://go.microsoft.com/fwlink/?linkid=2270353&clcid=0x409&culture=en-us&country=us`

**Resolved to (verified before download):**
`https://software-static.download.prss.microsoft.com/dbazure/998969d5-f34g-4e03-ac9d-1f9786c66749/26100.1742.240906-0331.ge_release_svc_refresh_CLIENT_IOT_LTSC_EVAL_x64FRE_en-us.iso`

Official Microsoft CDN only. No third-party mirror, archive, or torrent was used at any point.
No registration form was required — the fwlink serves the ISO directly.

### SHA256 — and why it doesn't match the published PDF

```
Computed:               2CEE70BD183DF42B92A2E0DA08CC2BB7A2A9CE3A3841955A012C0F77AEB3CB29
Microsoft's PDF states: 8ABF91C9CD408368DC73AAB3425D5E3C02DAE74900742072EB5C750FC637C195
Result:                 MISMATCH
```

Microsoft's published hash document is stale: the hash-values link on the evaluation page
(`go.microsoft.com/fwlink/?linkid=2269593`) is itself broken — it redirects to the Evaluation
Center root and returns HTTP 403 — and the PDF retrieved from Microsoft's CDN lists a value that
no longer corresponds to the image the CDN actually serves. An independent user on
[Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/2181328/(issue)-broken-windows-11-iot-hashes-pdf-link)
downloaded the same file from the same Evaluation Center and computed the identical
`2cee70bd…` hash, reporting the same documentation mismatch, and the ARM64 image has the same
problem — so this is a Microsoft documentation error, not a corrupted or tampered download.

Hash document (working CDN URL, the one on the eval page is dead):
`https://cdn-dynmedia-1.microsoft.com/is/content/microsoftcorp/microsoft/final/en-us/microsoft-brand/documents/Windows11IoTEnterpriseLTSC2024EvalHashValues.pdf`

### Authenticode verification — this is the real integrity proof

Since the published hash is unreliable, authenticity was established by verifying the digital
signatures of the binaries inside the mounted image:

| File | Status | Signer |
|---|---|---|
| `setup.exe` | **Valid** | `CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US` |
| `bootmgr.efi` | **Valid** | `CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US` |
| `efi\boot\bootx64.efi` | **Valid** | `CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US` |
| `sources\setup.exe` | **Valid** | `CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US` |
| `sources\setuphost.exe` | **Valid** | `CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US` |
| `bootmgr` | `UnknownError` | *(not a failure — see below)* |

`bootmgr` (the legacy BIOS boot manager) carries no embedded Authenticode signature, so
`Get-AuthenticodeSignature` returns `UnknownError` for it on a genuine Microsoft ISO. Every file
expected to be signed verifies as `Valid` and chains to Microsoft.

The ISO9660/UDF structure was also checked: descriptor type 1, standard identifier `CD001`,
volume label `CESE_X64FREE_EN-US_DV9` — the correct Enterprise eval / x64 / en-US / DVD9 image.
The byte count matches the server's advertised `Content-Length` exactly, confirming a complete,
untruncated transfer.

**Conclusion: the image is authentic and complete. The hash mismatch is bad documentation on
Microsoft's side.**

---

## How to run

The account `brain` is a member of the local Administrators group, but interactive sessions carry
a UAC-filtered token, so the script must elevate. It handles that itself.

**Option A — right-click**
> Right-click `make-boot-usb.ps1` → **Run with PowerShell** → approve the UAC prompt.

**Option B — from an already-elevated terminal**
```powershell
cd C:\Users\brain\Documents\repos\PC1\usb
powershell -ExecutionPolicy Bypass -File .\make-boot-usb.ps1
```

**Option C — unattended (skips the typed `ERASE` confirmation, guards still enforced)**
```powershell
.\make-boot-usb.ps1 -Force
```

At the UAC prompt you should see a simple **Yes/No** consent dialog. If Windows instead asks for
a **username and password**, the account is genuinely standard rather than filtered, and you will
need administrator credentials.

### Parameters

| Parameter | Default |
|---|---|
| `-IsoPath` | the ISO path listed above |
| `-DiskNumber` | `1` |
| `-ExpectedFriendlyName` | `USB DISK 3.2` |
| `-MinSizeGB` / `-MaxSizeGB` | `25` / `35` |
| `-PreferredDriveLetter` | `E` |
| `-VolumeLabel` | `WIN11LTSC` |
| `-Force` | off — skips the typed confirmation only |

> Changing `-DiskNumber` to point at a different disk also requires `-ExpectedFriendlyName` and
> the size window to match that disk, by design. Do not loosen these guards casually — they are
> what stands between a USB stick and your system drive.

---

## After it finishes

Boot the target machine in **UEFI mode** (not legacy/CSM) and select the `WIN11LTSC` volume.
Secure Boot can stay enabled — the EFI boot chain is Microsoft-signed, as verified above.

## Notes

- Requires PowerShell 5.1+ (Windows 10/11) and the `Storage` module (built in).
- The script does not eject the USB; the drive stays mounted afterwards.
- Evaluation editions cannot be converted in-place to a licensed edition while keeping
  applications — plan a clean install if you later buy a license.
