; SPDX-License-Identifier: GPL-3.0-or-later
;
; The NectarLink WinUI 3 installer for Windows (built by scripts/package-winui.ps1).
;
; Supports both:
; - Interactive install (Modern UI 2 wizard with NectarLink's icon, license,
;   install location, optional desktop shortcut, and launch checkbox).
; - Silent install (/S, e.g. from the in-app updater): installs into Program
;   Files (or the previous InstallDir), optionally accepts /DESKTOP=0|1,
;   /ADMINONLY=0|1, /PROGRESS=<file>, /RUN, and /D=<dir> (last, unquoted).
;
; Defines passed in:
;   VERSION          Numbers only (e.g. 0.1.5)
;   BUILD            Fourth file-version number (1-998 for nightly, 999 for release, 0 otherwise)
;   DISPLAY_VERSION  Full version string (e.g. 0.1.5-canary.12)
;   ARCH             x64 or arm64
;   STAGE            Folder containing the published WinUI app & native DLLs
;   ICON             Path to nectarlink.ico
;   LICENSE_FILE     Path to LICENSE.txt
;   OUTFILE          Output setup executable path

Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma

!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "WordFunc.nsh"
!include "Sections.nsh"

!define APP "Nectarlink"
!define DISPLAY "NectarLink"
!define EXE "NectarLink.WinUI.exe"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP}"
!define FIREWALL_RULE "Nectarlink"
!define CAPABILITIES_KEY "Software\${APP}\Capabilities"
!define TEL_PROGID "NectarLink.Tel"

Name "${DISPLAY}"
OutFile "${OUTFILE}"
RequestExecutionLevel admin
InstallDir "$PROGRAMFILES64\${APP}"
InstallDirRegKey HKLM "Software\${APP}" "InstallDir"

Icon "${ICON}"
UninstallIcon "${ICON}"
!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING

!define MUI_WELCOMEPAGE_TITLE "Welcome to ${DISPLAY} Setup"
!define MUI_WELCOMEPAGE_TEXT "Setup will guide you through installing ${DISPLAY} ${DISPLAY_VERSION} on your PC.$\r$\n$\r$\nYour Android phone and Windows PC, working as one — notifications, messages, calls, clipboard, files, photos, and screen mirroring."
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${DISPLAY}"
!define MUI_FINISHPAGE_RUN_FUNCTION OpenApp

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${LICENSE_FILE}"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

VIProductVersion "${VERSION}.${BUILD}"
VIAddVersionKey "ProductName" "${DISPLAY}"
VIAddVersionKey "FileDescription" "${DISPLAY} Setup"
VIAddVersionKey "CompanyName" "The NectarLink contributors"
VIAddVersionKey "LegalCopyright" "GPL-3.0-or-later"
VIAddVersionKey "FileVersion" "${VERSION}.${BUILD}"
VIAddVersionKey "ProductVersion" "${DISPLAY_VERSION}"

Var ProgressFile
Var PreviousDir
Var AdminOnly

!macro Progress line
  ${If} $ProgressFile != ""
    FileOpen $9 $ProgressFile a
    FileSeek $9 0 END
    FileWrite $9 "${line}$\r$\n"
    FileClose $9
  ${EndIf}
!macroend

!macro ArchCheck
  ${If} "${ARCH}" == "x64"
    ${IfNot} ${IsNativeAMD64}
      MessageBox MB_ICONSTOP "This copy of ${DISPLAY} is for x64 PCs. Download the ARM64 version for this PC." /SD IDOK
      Abort
    ${EndIf}
  ${ElseIf} "${ARCH}" == "arm64"
    ${IfNot} ${IsNativeARM64}
      MessageBox MB_ICONSTOP "This copy of ${DISPLAY} is for ARM64 PCs. Download the x64 version for this PC." /SD IDOK
      Abort
    ${EndIf}
  ${EndIf}
!macroend

!macro QuitRunningApp
  ${If} $AdminOnly == 1
  ${AndIf} ${FileExists} "$INSTDIR\${EXE}"
    ExecWait '"$INSTDIR\${EXE}" --quit'
  ${EndIf}
  StrCpy $R0 0
  ${Do}
    nsExec::Exec 'cmd /c tasklist /FI "IMAGENAME eq ${EXE}" /NH | find /I "${EXE}"'
    Pop $R1
    ${If} $R1 != 0
      ${Break}
    ${EndIf}
    ${If} $R0 >= 25
      nsExec::Exec 'taskkill /F /IM ${EXE}'
      Sleep 500
      ${Break}
    ${EndIf}
    Sleep 200
    IntOp $R0 $R0 + 1
  ${Loop}
!macroend

!macro MoveAsideInUse FILE
  Delete "$INSTDIR\${FILE}"
  ${If} ${FileExists} "$INSTDIR\${FILE}"
    StrCpy $R2 0
    ${Do}
      IntOp $R2 $R2 + 1
    ${LoopWhile} ${FileExists} "$INSTDIR\${FILE}.$R2.old"
    Rename "$INSTDIR\${FILE}" "$INSTDIR\${FILE}.$R2.old"
    Delete /REBOOTOK "$INSTDIR\${FILE}.$R2.old"
  ${EndIf}
!macroend

!macro MoveAsideCameraFiles
  Delete "$INSTDIR\*.dll.*.old"
  !insertmacro MoveAsideInUse "nectarlink_vcam.dll"
  !insertmacro MoveAsideInUse "nectarlink_credprov.dll"
  !insertmacro MoveAsideInUse "vcruntime140.dll"
  !insertmacro MoveAsideInUse "vcruntime140_1.dll"
  !insertmacro MoveAsideInUse "msvcp140.dll"
!macroend

Function LockFolder
  System::Call 'kernel32::GetFileAttributesW(w "$R1") i .r0'
  IntOp $1 $0 & 0x400
  ${If} $0 == -1
  ${OrIf} $1 != 0
    SetErrors
    Return
  ${EndIf}
  nsExec::Exec 'icacls "$R1" /setowner *S-1-5-32-544'
  Pop $0
  ${If} $0 == 0
    nsExec::Exec 'icacls "$R1" /reset'
    Pop $0
  ${EndIf}
  ${If} $0 == 0
    nsExec::Exec 'icacls "$R1" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX *S-1-15-2-1:(OI)(CI)RX'
    Pop $0
  ${EndIf}
  StrCpy $4 0
  ${If} $0 == 0
    FindFirst $2 $3 "$R1\*.*"
    ${DoWhile} $3 != ""
      ${If} $3 != "."
      ${AndIf} $3 != ".."
        StrCpy $4 1
        ${Break}
      ${EndIf}
      FindNext $2 $3
    ${Loop}
    FindClose $2
  ${EndIf}
  ClearErrors
  ${If} $0 != 0
  ${OrIf} $4 == 1
    SetErrors
  ${EndIf}
FunctionEnd

Function CreateLockedInstallDir
  StrCpy $R0 "$INSTDIR"
  StrCpy $R2 0
  ${Do}
    ${If} ${FileExists} "$R0\*.*"
      ${Break}
    ${EndIf}
    Push $R0
    IntOp $R2 $R2 + 1
    ${GetParent} "$R0" $R0
    ${If} $R0 == ""
      ${Break}
    ${EndIf}
  ${Loop}
  ${If} $R2 == 0
    Push "$INSTDIR"
    StrCpy $R2 1
  ${EndIf}
  StrCpy $R3 0
  ${DoWhile} $R2 > 0
    Pop $R1
    IntOp $R2 $R2 - 1
    ${If} $R3 == 0
      CreateDirectory "$R1"
      Call LockFolder
      ${If} ${Errors}
        StrCpy $R3 1
      ${EndIf}
    ${EndIf}
  ${Loop}
  ClearErrors
  ${If} $R3 == 1
    SetErrors
  ${EndIf}
FunctionEnd

Function OpenApp
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\${EXE}"'
FunctionEnd

Section "NectarLink (required)" SecApp
  SectionIn RO

  ReadRegStr $PreviousDir HKLM "Software\${APP}" "InstallDir"
  ${If} $PreviousDir != ""
  ${AndIfNot} ${FileExists} "$PreviousDir\${EXE}"
    StrCpy $PreviousDir ""
  ${EndIf}

  StrCpy $AdminOnly 0
  StrLen $0 "$PROGRAMFILES64\"
  StrCpy $1 "$INSTDIR" $0
  StrLen $0 "$PROGRAMFILES\"
  StrCpy $2 "$INSTDIR" $0
  ${If} $1 == "$PROGRAMFILES64\"
  ${OrIf} $2 == "$PROGRAMFILES\"
    StrCpy $AdminOnly 1
  ${Else}
    ${GetParameters} $R0
    ClearErrors
    ${GetOptions} $R0 "/ADMINONLY=" $R1
    ${If} ${Errors}
      ${If} $INSTDIR == $PreviousDir
        ReadRegDWORD $AdminOnly HKLM "Software\${APP}" "AdminOnlyFolder"
      ${EndIf}
    ${ElseIf} $R1 == 1
      StrCpy $AdminOnly 1
    ${EndIf}
    ${If} $AdminOnly != 1
      StrCpy $AdminOnly 0
    ${EndIf}
    ${If} $INSTDIR != $PreviousDir
    ${AndIf} $AdminOnly == 1
      Call CreateLockedInstallDir
      ${If} ${Errors}
        !insertmacro Progress "error Setup couldn't make that folder safe for NectarLink. Choose another folder."
        Abort "Setup couldn't make that folder safe for NectarLink. Choose another folder."
      ${EndIf}
    ${EndIf}
  ${EndIf}

  !insertmacro Progress "step closing"
  !insertmacro QuitRunningApp

  !insertmacro Progress "step files"
  !insertmacro MoveAsideCameraFiles
  SetOutPath "$INSTDIR"
  File /r "${STAGE}\*.*"
  WriteUninstaller "$INSTDIR\uninstall.exe"

  !insertmacro Progress "step firewall"
  nsExec::Exec 'netsh advfirewall firewall delete rule name="${FIREWALL_RULE}"'
  nsExec::Exec 'netsh advfirewall firewall add rule name="${FIREWALL_RULE}" dir=in action=allow program="$INSTDIR\${EXE}" enable=yes profile=private,domain description="Lets paired phones connect to NectarLink."'

  !insertmacro Progress "step updates"
  CreateDirectory "$APPDATA\${APP}\updates"
  nsExec::Exec 'icacls "$APPDATA\${APP}" /setowner *S-1-5-32-544'
  nsExec::Exec 'icacls "$APPDATA\${APP}" /grant *S-1-5-32-545:(OI)(CI)M'
  ${If} $AdminOnly != 1
    nsExec::Exec 'schtasks /Delete /TN "Nectarlink Update" /F'
    Goto updates_done
  ${EndIf}
  InitPluginsDir
  ${WordReplace} "$INSTDIR" "&" "&amp;" "+" $1
  FileOpen $0 "$PLUGINSDIR\update-task.xml" w
  FileWriteUTF16LE /BOM $0 '<?xml version="1.0" encoding="UTF-16"?>$\r$\n'
  FileWriteUTF16LE $0 '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">$\r$\n'
  FileWriteUTF16LE $0 '  <RegistrationInfo>$\r$\n'
  FileWriteUTF16LE $0 '    <Description>Installs new versions of NectarLink from its GitHub releases.</Description>$\r$\n'
  FileWriteUTF16LE $0 '    <SecurityDescriptor>D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;AU)</SecurityDescriptor>$\r$\n'
  FileWriteUTF16LE $0 '  </RegistrationInfo>$\r$\n'
  FileWriteUTF16LE $0 '  <Principals>$\r$\n'
  FileWriteUTF16LE $0 '    <Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal>$\r$\n'
  FileWriteUTF16LE $0 '  </Principals>$\r$\n'
  FileWriteUTF16LE $0 '  <Settings>$\r$\n'
  FileWriteUTF16LE $0 '    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>$\r$\n'
  FileWriteUTF16LE $0 '    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>$\r$\n'
  FileWriteUTF16LE $0 '    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>$\r$\n'
  FileWriteUTF16LE $0 '    <AllowStartOnDemand>true</AllowStartOnDemand>$\r$\n'
  FileWriteUTF16LE $0 '    <Enabled>true</Enabled>$\r$\n'
  FileWriteUTF16LE $0 '    <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>$\r$\n'
  FileWriteUTF16LE $0 '  </Settings>$\r$\n'
  FileWriteUTF16LE $0 '  <Actions Context="Author">$\r$\n'
  FileWriteUTF16LE $0 '    <Exec><Command>"$1\${EXE}"</Command><Arguments>--update-service</Arguments></Exec>$\r$\n'
  FileWriteUTF16LE $0 '  </Actions>$\r$\n'
  FileWriteUTF16LE $0 '</Task>$\r$\n'
  FileClose $0
  nsExec::Exec 'schtasks /Create /TN "Nectarlink Update" /XML "$PLUGINSDIR\update-task.xml" /F'
  updates_done:

  !insertmacro Progress "step shortcuts"
  CreateShortCut "$SMPROGRAMS\${DISPLAY}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0 "" "" "Your phone, on your PC"

  Delete "$INSTDIR\sparse-package.log"
  System::Call 'kernel32::SetEnvironmentVariableW(w "NECTARLINK_INSTDIR", w "$INSTDIR")'
  nsExec::Exec 'powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "if (-not [Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) { try { Add-AppxPackage -Register \"$$env:NECTARLINK_INSTDIR\AppxManifest.xml\" -ExternalLocation $$env:NECTARLINK_INSTDIR -ErrorAction Stop } catch { $$_ | Out-File -Encoding utf8 -LiteralPath \"$$env:NECTARLINK_INSTDIR\sparse-package.log\" } }"'

  WriteRegStr HKLM "Software\${APP}" "InstallDir" "$INSTDIR"
  WriteRegDWORD HKLM "Software\${APP}" "AdminOnlyFolder" $AdminOnly
  WriteRegStr HKLM "Software\Classes\${TEL_PROGID}" "" "${DISPLAY}"
  WriteRegStr HKLM "Software\Classes\${TEL_PROGID}" "URL Protocol" ""
  WriteRegStr HKLM "Software\Classes\${TEL_PROGID}\DefaultIcon" "" '"$INSTDIR\${EXE}",0'
  WriteRegStr HKLM "Software\Classes\${TEL_PROGID}\shell\open\command" "" '"$INSTDIR\${EXE}" --tel "%1"'
  WriteRegStr HKLM "${CAPABILITIES_KEY}" "ApplicationName" "${DISPLAY}"
  WriteRegStr HKLM "${CAPABILITIES_KEY}" "ApplicationDescription" "Open telephone links in the NectarLink dialer."
  WriteRegStr HKLM "${CAPABILITIES_KEY}\UrlAssociations" "tel" "${TEL_PROGID}"
  WriteRegStr HKLM "Software\RegisteredApplications" "${DISPLAY}" "${CAPABILITIES_KEY}"
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'

  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayName" "${DISPLAY}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayVersion" "${DISPLAY_VERSION}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "Publisher" "The NectarLink contributors"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "URLInfoAbout" "https://github.com/isaiahscape/nectarlink-winui"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKLM "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "EstimatedSize" $0

  !insertmacro Progress "done"

  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/RUN" $R1
  ${IfNot} ${Errors}
    Call OpenApp
  ${EndIf}
SectionEnd

Section "Desktop shortcut" SecDesktop
  CreateShortCut "$DESKTOP\${DISPLAY}.lnk" "$INSTDIR\${EXE}" "" "$INSTDIR\${EXE}" 0 "" "" "Your phone, on your PC"
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecApp} "Install ${DISPLAY} (WinUI 3) and register Windows integrations."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "Add a ${DISPLAY} shortcut to the Desktop."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function .onInit
  !insertmacro ArchCheck
  SetRegView 64
  SetShellVarContext all

  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/PROGRESS=" $ProgressFile
  ${If} ${Errors}
    StrCpy $ProgressFile ""
  ${EndIf}

  ; If no /D= override was passed, reuse existing 64-bit InstallDir if present.
  ${If} $INSTDIR == "$PROGRAMFILES64\${APP}"
    ReadRegStr $0 HKLM "Software\${APP}" "InstallDir"
    ${If} $0 != ""
    ${AndIf} ${FileExists} "$0\${EXE}"
      StrCpy $INSTDIR $0
    ${EndIf}
  ${EndIf}

  ; Honor /DESKTOP=0|1 when supplied, or preserve desktop shortcut choice on upgrade.
  ClearErrors
  ${GetOptions} $R0 "/DESKTOP=" $R1
  ${IfNot} ${Errors}
    ${If} $R1 == 0
      !insertmacro UnselectSection ${SecDesktop}
    ${Else}
      !insertmacro SelectSection ${SecDesktop}
    ${EndIf}
  ${ElseIf} ${Silent}
    ReadRegStr $0 HKLM "Software\${APP}" "InstallDir"
    ${If} $0 != ""
    ${AndIfNot} ${FileExists} "$DESKTOP\${DISPLAY}.lnk"
      !insertmacro UnselectSection ${SecDesktop}
    ${EndIf}
  ${EndIf}
FunctionEnd

Function un.onInit
  SetRegView 64
  SetShellVarContext all
  StrCpy $AdminOnly 0
  StrLen $0 "$PROGRAMFILES64\"
  StrCpy $1 "$INSTDIR" $0
  StrLen $0 "$PROGRAMFILES\"
  StrCpy $2 "$INSTDIR" $0
  ${If} $1 == "$PROGRAMFILES64\"
  ${OrIf} $2 == "$PROGRAMFILES\"
    StrCpy $AdminOnly 1
  ${Else}
    ReadRegStr $PreviousDir HKLM "Software\${APP}" "InstallDir"
    ${If} $INSTDIR == $PreviousDir
    ${AndIf} ${FileExists} "$INSTDIR\${EXE}"
      ReadRegDWORD $AdminOnly HKLM "Software\${APP}" "AdminOnlyFolder"
      ${If} $AdminOnly != 1
        StrCpy $AdminOnly 0
      ${EndIf}
    ${EndIf}
  ${EndIf}
  ${un.GetParameters} $R0
  ClearErrors
  ${un.GetOptions} $R0 "/PROGRESS=" $ProgressFile
  ${If} ${Errors}
    StrCpy $ProgressFile ""
  ${EndIf}
FunctionEnd

Section "Uninstall"
  !insertmacro Progress "step closing"
  !insertmacro QuitRunningApp
  ${If} $AdminOnly == 1
  ${AndIf} ${FileExists} "$INSTDIR\${EXE}"
    ExecWait '"$INSTDIR\${EXE}" --uninstall'
  ${EndIf}

  !insertmacro Progress "step cleanup"
  nsExec::Exec 'powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "try { Get-AppxPackage -Name \"Nectarlink.Desktop\" -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue } catch {}"'
  nsExec::Exec 'netsh advfirewall firewall delete rule name="${FIREWALL_RULE}"'
  nsExec::Exec 'schtasks /Delete /TN "Nectarlink Update" /F'

  Delete "$APPDATA\${APP}\updates\*.exe"
  Delete "$APPDATA\${APP}\updates\*.part"
  Delete "$APPDATA\${APP}\update-channel"
  Delete "$APPDATA\${APP}\update-feed.json"
  RMDir "$APPDATA\${APP}\updates"
  RMDir "$APPDATA\${APP}"
  Delete "$SMPROGRAMS\${DISPLAY}.lnk"
  Delete "$DESKTOP\${DISPLAY}.lnk"

  ${GetFileName} "$INSTDIR" $0
  ${If} $0 == "${APP}"
    !insertmacro MoveAsideCameraFiles
    RMDir /r /REBOOTOK "$INSTDIR"
  ${Else}
    Delete "$INSTDIR\${EXE}"
    Delete "$INSTDIR\NectarLink.WinUI.dll"
    Delete "$INSTDIR\nectarlink_winui_core.dll"
    Delete "$INSTDIR\nectarlink-agent.exe"
    !insertmacro MoveAsideInUse "nectarlink_vcam.dll"
    !insertmacro MoveAsideInUse "nectarlink_credprov.dll"
    Delete "$INSTDIR\AppxManifest.xml"
    RMDir /r "$INSTDIR\Assets"
    Delete "$INSTDIR\sparse-package.log"
    Delete "$INSTDIR\update.log"
    Delete "$INSTDIR\uninstall.exe"
  ${EndIf}

  DeleteRegValue HKLM "Software\RegisteredApplications" "${DISPLAY}"
  DeleteRegKey HKLM "Software\Classes\${TEL_PROGID}"
  System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
  DeleteRegKey HKLM "${UNINSTALL_KEY}"
  DeleteRegKey HKLM "Software\${APP}"
  DeleteRegKey HKLM "SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{BC7B4BCF-B24C-4C5B-9E55-CB09E19A8799}"
  DeleteRegKey HKLM "SOFTWARE\Classes\CLSID\{BC7B4BCF-B24C-4C5B-9E55-CB09E19A8799}"
  !insertmacro Progress "done"
SectionEnd
