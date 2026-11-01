#ifndef PublishDir
  #error PublishDir must point to the self-contained win-x64 publish directory.
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\installer"
#endif
#ifndef AppVersion
  #define AppVersion "17.3.0"
#endif

[Setup]
AppId={{7A8E523B-37BA-40E2-A517-2DAD366A04E7}
AppName=RestaurantOrders
AppVersion={#AppVersion}
AppPublisher=RestaurantOrders
DefaultDirName={autopf}\RestaurantOrders
DefaultGroupName=RestaurantOrders
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir={#OutputDir}
OutputBaseFilename=RestaurantOrders-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\RestaurantOrders.exe
InfoBeforeFile=Customer-Guide-EL.txt

[Languages]
Name: "greek"; MessagesFile: "compiler:Languages\Greek.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Prepare-Service.ps1"; Flags: dontcopy
Source: "ServiceSetup.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "RestaurantOrders.url"; DestDir: "{app}"; Flags: ignoreversion
Source: "Customer-Guide-EL.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{commondesktop}\RestaurantOrders"; Filename: "{app}\RestaurantOrders.url"; IconFilename: "{app}\RestaurantOrders.exe"
Name: "{group}\RestaurantOrders"; Filename: "{app}\RestaurantOrders.url"; IconFilename: "{app}\RestaurantOrders.exe"
Name: "{group}\Πρόσβαση από κινητά και tablets"; Filename: "{app}\Access.html"
Name: "{group}\Οδηγίες"; Filename: "{app}\Customer-Guide-EL.txt"

[Run]
Filename: "{app}\RestaurantOrders.url"; Description: "Άνοιγμα RestaurantOrders"; Flags: shellexec postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\ServiceSetup.ps1"" -Action Uninstall -InstallDirectory ""{app}"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveRestaurantOrdersService"

[Code]
var
  DatabasePage: TInputFileWizardPage;

function RunScript(const ScriptPath, Arguments: String; var ExitCode: Integer): Boolean;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '" ' + Arguments,
    '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

procedure InitializeWizard;
begin
  DatabasePage := CreateInputFilePage(wpSelectDir, 'Υπάρχουσα βάση δεδομένων',
    'Προαιρετική μεταφορά δεδομένων από προηγούμενη εγκατάσταση',
    'Για νέα εγκατάσταση άφησε το πεδίο κενό. Για μεταφορά, επίλεξε την υπάρχουσα restaurant.db. Το αρχικό αρχείο διατηρείται.');
  DatabasePage.Add('Βάση δεδομένων (προαιρετικά):', 'SQLite database|*.db|All files|*.*', '.db');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = DatabasePage.ID) and FileExists(ExpandConstant('{commonappdata}\RestaurantOrders\restaurant.db'));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = DatabasePage.ID) and (DatabasePage.Values[0] <> '') and not FileExists(DatabasePage.Values[0]) then
  begin
    MsgBox('Το επιλεγμένο αρχείο βάσης δεν βρέθηκε.', mbError, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  ExtractTemporaryFile('Prepare-Service.ps1');
  if not RunScript(ExpandConstant('{tmp}\Prepare-Service.ps1'), '', ExitCode) or (ExitCode <> 0) then
    Result := 'Δεν σταμάτησε η προηγούμενη υπηρεσία RestaurantOrders. Κλείσε την εφαρμογή και δοκίμασε ξανά.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
  Arguments: String;
begin
  if CurStep = ssPostInstall then
  begin
    Arguments := '-Action Install -InstallDirectory "' + ExpandConstant('{app}') + '"';
    if not FileExists(ExpandConstant('{commonappdata}\RestaurantOrders\restaurant.db')) and (DatabasePage.Values[0] <> '') then
      Arguments := Arguments + ' -ImportDatabase "' + DatabasePage.Values[0] + '"';
    if not RunScript(ExpandConstant('{app}\installer\ServiceSetup.ps1'), Arguments, ExitCode) or (ExitCode <> 0) then
      RaiseException('Δεν ολοκληρώθηκε η εκκίνηση της υπηρεσίας. Δες C:\ProgramData\RestaurantOrders\installer-error.log και δοκίμασε ξανά το Setup.');
  end;
end;
