{ Product-neutral foreground behavior reused from the Nektron Mail installer. }

const
  HWND_TOPMOST = -1;
  HWND_NOTOPMOST = -2;
  SWP_NOSIZE = $0001;
  SWP_NOMOVE = $0002;
  SWP_NOACTIVATE = $0010;
  SWP_SHOWWINDOW = $0040;
  WIZARD_FOREGROUND_PULSE_MS = 175;

function SetWindowPos(
  WindowHandle: HWND;
  InsertAfter: HWND;
  X, Y, Width, Height: Integer;
  Flags: UINT): Boolean;
external 'SetWindowPos@user32.dll stdcall setuponly';

function SetForegroundWindow(WindowHandle: HWND): Boolean;
external 'SetForegroundWindow@user32.dll stdcall setuponly';

function GetForegroundWindow: HWND;
external 'GetForegroundWindow@user32.dll stdcall setuponly';

function SetTimer(
  hWnd, nIDEvent, uElapse, lpTimerFunc: Longword): Longword;
external 'SetTimer@user32.dll stdcall setuponly';

function KillTimer(hWnd, nIDEvent: Longword): Boolean;
external 'KillTimer@user32.dll stdcall setuponly';

var
  WizardForegroundStarted: Boolean;
  WizardForegroundPulseActive: Boolean;
  WizardForegroundRetryTimer: Longword;

procedure StopWizardForegroundRetry;
begin
  if WizardForegroundRetryTimer <> 0 then begin
    KillTimer(0, WizardForegroundRetryTimer);
    WizardForegroundRetryTimer := 0;
  end;
end;

procedure EnsureWizardNormalZOrder;
begin
  if WizardForm = nil then
    Exit;

  SetWindowPos(
    WizardForm.Handle,
    HWND_NOTOPMOST,
    0, 0, 0, 0,
    SWP_NOMOVE or SWP_NOSIZE or SWP_NOACTIVATE or SWP_SHOWWINDOW);
  WizardForegroundPulseActive := False;
end;

procedure RetryWizardForegroundAfterPulse;
begin
  if WizardSilent or (not WizardForm.Showing) then
    Exit;

  if GetForegroundWindow <> WizardForm.Handle then begin
    BringToFrontAndRestore;
    WizardForm.BringToFront;
    SetForegroundWindow(WizardForm.Handle);
  end;
end;

procedure WizardForegroundRetryProc(Arg1, Arg2, Arg3, Arg4: Longword);
begin
  { The timer makes the topmost state a bounded one-render visibility pulse. }
  StopWizardForegroundRetry;
  EnsureWizardNormalZOrder;
  RetryWizardForegroundAfterPulse;
end;

procedure StartWizardForegroundPulse;
var
  TimerArmed: Boolean;
begin
  if WizardSilent then
    Exit;

  StopWizardForegroundRetry;
  TimerArmed := False;
  SetWindowPos(
    WizardForm.Handle,
    HWND_TOPMOST,
    0, 0, 0, 0,
    SWP_NOMOVE or SWP_NOSIZE or SWP_NOACTIVATE or SWP_SHOWWINDOW);
  WizardForegroundPulseActive := True;

  try
    BringToFrontAndRestore;
    WizardForm.BringToFront;
    SetForegroundWindow(WizardForm.Handle);
    WizardForegroundRetryTimer := SetTimer(
      0,
      0,
      WIZARD_FOREGROUND_PULSE_MS,
      CreateCallback(@WizardForegroundRetryProc));
    TimerArmed := WizardForegroundRetryTimer <> 0;
  finally
    if not TimerArmed then
      EnsureWizardNormalZOrder;
  end;

  { A timer-allocation failure must still end in normal z-order and retry once. }
  if not TimerArmed then
    RetryWizardForegroundAfterPulse;
end;

procedure WizardFormShown(Sender: TObject);
begin
  if WizardForegroundStarted or WizardSilent then
    Exit;

  WizardForegroundStarted := True;
  StartWizardForegroundPulse;
end;



procedure DeinitializeSetup;
begin
  StopWizardForegroundRetry;
  if WizardForegroundPulseActive then
    EnsureWizardNormalZOrder;
end;
