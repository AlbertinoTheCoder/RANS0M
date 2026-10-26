$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'dist/RANS0M_v18.exe'))
$type = $assembly.GetType('Rans0m', $true)
$flags = [Reflection.BindingFlags]'Instance, NonPublic'
function Field($name) { $type.GetField($name, $flags).GetValue($script:game) }
function Call($name, [object[]]$arguments = @()) {
    for ($i=0; $i -lt $arguments.Length; $i++) {
        if ($null -ne $arguments[$i]) { $arguments[$i] = $arguments[$i].PSObject.BaseObject }
    }
    $method = @($type.GetMethods($flags) | Where-Object { $_.Name -eq $name -and $_.GetParameters().Count -eq $arguments.Count })[0]
    $method.Invoke($script:game, $arguments)
}
function Check($condition, $message) { if (!$condition) { throw $message } }
$constructor = $type.GetConstructor($flags, $null, [type[]]@([bool]), $null)
$game = $constructor.Invoke([object[]]@($false))
try {
    Check ((Field 'phase') -eq 0) 'Game must start at the movement prompt.'
    Check ($assembly.GetManifestResourceNames().Count -eq 15) 'Missing embedded assets.'
    Check ([Math]::Abs((Field 'audioClips')['theme'].Duration-90) -lt 0.001) 'Theme must contain the complete 90-second slice from 34.4 to 124.4.'
    Check ([Math]::Abs((Field 'audioClips')['glitch'].Duration-1) -lt .001) 'Glitch audio must last one second.'
    Check ((Field 'audioClips').Count -eq 8) 'Missing preloaded audio.'
    foreach ($clip in (Field 'audioClips').Values) { Check ($clip.Duration -gt 0) 'Audio duration is invalid.' }
    Check ((Field 'face') -and (Field 'coinImage') -and (Field 'victory') -and (Field 'jumpscare') -and (Field 'popupFace') -and (Field 'stopSign') -and (Field 'rareGold')) 'Images failed to load.'
    $type.GetField('muted', $flags).SetValue($game, $true)
    $type.GetField('reducedMotion', $flags).SetValue($game, $true)
    $game.Show()
    (Field 'timer').Stop()
    $bitmap = New-Object Drawing.Bitmap $game.Width, $game.Height
    try {
        $game.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle 0,0,$game.Width,$game.Height))
        $bitmap.Save((Join-Path $PSScriptRoot 'dist/start-screen.png'))
    } finally { $bitmap.Dispose() }
    $preview = New-Object Drawing.Bitmap 120,120
    $graphics = [Drawing.Graphics]::FromImage($preview)
    try {
        $graphics.Clear([Drawing.Color]::FromArgb(15,17,24))
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        Call 'DrawRareCoin' @($graphics, (New-Object Drawing.Rectangle 22,22,76,76), [double]0)
        $preview.Save((Join-Path $PSScriptRoot 'dist/rare-coin.png'))
    } finally { $graphics.Dispose(); $preview.Dispose() }
    Check ($type.GetField('RareCoinChance', [Reflection.BindingFlags]'Static, NonPublic').GetRawConstantValue() -eq 0.02) 'Rare coin chance must be 2%.'
    $script:paintCount = 0
    $game.add_Paint({ $script:paintCount++ })
    $measurement = [Diagnostics.Stopwatch]::StartNew()
    $type.GetField('startingMouse', $flags).SetValue($game, $game.PointToScreen((New-Object Drawing.Point 200,200)))
    $movement = New-Object Windows.Forms.MouseEventArgs ([Windows.Forms.MouseButtons]::None),0,212,200,0
    Call 'OnMouseMove' @($movement)
    Check ((Field 'phase') -eq 1) 'Mouse movement must immediately enter the jumpscare.'
    Call 'SetPhase' @([int]3)
    Check (@((Field 'coins') | Where-Object { !$_.IsFake }).Count -eq 7) 'Expected seven real coins.'
    Check (@((Field 'coins') | Where-Object { $_.IsFake }).Count -eq 2) 'Expected two independently capped glitch coins.'
    $script:paintCount = 0
    $type.GetField('reducedMotion', $flags).SetValue($game, $false)
    (Field 'timer').Start()
    $measurement.Restart()
    while ($measurement.Elapsed.TotalSeconds -lt 2) {
        [Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 1
    }
    (Field 'timer').Stop()
    Check ((Field 'popups').Count -ge 16) 'Expected increased initial alert count.'
    Check (@((Field 'popups') | Where-Object { $_.FinalPosition.X -gt 300 -and $_.FinalPosition.X -lt 850 }).Count -gt 0) 'Alert windows must also spawn in the middle.'
    Write-Host ('Main repaints per second: {0:N1} (only changed regions redraw).' -f ($script:paintCount/$measurement.Elapsed.TotalSeconds))
    $popup = (Field 'popups')[0]
    $popupType = $popup.GetType()
    function PopupCall($name,$event) {
        $arguments = New-Object object[] 1
        $arguments[0] = $event.PSObject.BaseObject
        $popupType.GetMethod($name, $flags).Invoke($popup, $arguments)
    }
    $startPosition = $popup.FinalPosition
    PopupCall 'OnMouseDown' (New-Object Windows.Forms.MouseEventArgs ([Windows.Forms.MouseButtons]::Left),1,10,10,0)
    Check ($popup.Dragging) 'Title bar did not start dragging.'
    PopupCall 'OnMouseMove' (New-Object Windows.Forms.MouseEventArgs ([Windows.Forms.MouseButtons]::Left),1,40,30,0)
    PopupCall 'OnMouseUp' (New-Object Windows.Forms.MouseEventArgs ([Windows.Forms.MouseButtons]::Left),1,40,30,0)
    $newPosition = $popup.FinalPosition
    Check ($newPosition -ne $startPosition -and !$popup.Dragging) 'Title-bar drag did not move and release the window.'
    Call 'AnimatePopups' @([double]((Call 'Now')+3))
    Check ($popup.Location -eq $game.PointToScreen($newPosition)) 'Animation snapped a dragged window back.'
    $popup.MoveTo($startPosition)
    Call 'AnimatePopups' @([double]((Call 'Now')+5))
    Check ($popup.FinalPosition -eq $startPosition) 'Dragging must permanently stop drift.'
    $closePoint = $popup.CloseButton
    PopupCall 'OnMouseDown' (New-Object Windows.Forms.MouseEventArgs ([Windows.Forms.MouseButtons]::Left),1,($closePoint.X+5),($closePoint.Y+5),0)
    PopupCall 'OnMouseUp' (New-Object Windows.Forms.MouseEventArgs ([Windows.Forms.MouseButtons]::Left),1,($closePoint.X+5),($closePoint.Y+5),0)
    Check ($popup.Exiting -or $popup.IsDisposed) 'Popup X must start folding its window.'
    Call 'SpawnPopup' @($false,[double]0)
    $oldest = (Field 'popups')[0]
    $type.GetField('nextReplacementAt', $flags).SetValue($game,[double]0)
    $type.GetField('nextFrame', $flags).SetValue($game,[double]0)
    Call 'Frame' @($null,[EventArgs]::Empty)
    Check ($oldest.Exiting -or $oldest.IsDisposed) 'Periodic close/spawn cycle did not close the oldest window.'
    Check ((Field 'nextReplacementAt') -gt ((Call 'Now')+19)) 'Replacement must repeat after twenty seconds.'

    $panel = Call 'RansomPanel'
    Call 'OnMouseDown' @((New-Object Windows.Forms.MouseEventArgs ([Windows.Forms.MouseButtons]::Left),1,($panel.X+10),($panel.Y+10),0))
    Call 'OnMouseMove' @((New-Object Windows.Forms.MouseEventArgs ([Windows.Forms.MouseButtons]::Left),1,($panel.X-20),($panel.Y-10),0))
    Call 'OnMouseUp' @((New-Object Windows.Forms.MouseEventArgs ([Windows.Forms.MouseButtons]::Left),1,0,0,0))
    Check ((Call 'RansomPanel').Location -ne $panel.Location) 'Main ransom title bar did not drag.'
    $type.GetField('ransomPosition', $flags).SetValue($game, $null)
    $firstGold = (Field 'coins')[0]
    $firstGold.GetType().GetField('Rare').SetValue($firstGold, $true)
    $bitmap = New-Object Drawing.Bitmap $game.Width, $game.Height
    try {
        $game.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle 0,0,$game.Width,$game.Height))
        $bitmap.Save((Join-Path $PSScriptRoot 'dist/gameplay.png'))
    } finally { $bitmap.Dispose() }
    $clicks = 0
    while ((Field 'phase') -eq 3 -and $clicks -lt 60) {
        $gold = (Field 'coins')[0]
        $point = $gold.GetType().GetField('Position').GetValue($gold)
        $click = New-Object Windows.Forms.MouseEventArgs ([Windows.Forms.MouseButtons]::Left),1,($point.X+38),($point.Y+38),0
        $beforeCount = (Field 'coins').Count
        $previousBalance = Field 'balance'
        $expectedValue = if ($gold.Rare) {100} elseif ($gold.IsFake) {0} else {10}
        Check ($gold.Value -eq $expectedValue) 'Normal coins must be 10 and rare coins 100.'
        Call 'ClickCoin' @($null, $click)
        Check ((Field 'balance') -eq [Math]::Min(500,($previousBalance+$expectedValue))) 'Coin value must not receive a streak bonus.'
        $clicks++
        if((Field 'phase') -eq 3){
            $remainingCoins = (Field 'coins').Count
            Check ($remainingCoins -eq ($beforeCount-1)) "Collected coin must not respawn immediately (remaining=$remainingCoins)."
            $now = Call 'Now'
            Call 'RefillCoins' @([double]$now)
            Check ((Field 'coins').Count -eq $remainingCoins) 'Coin respawn delay was skipped.'
            Check ((Field 'coinRespawns')[0] -gt ($now+2)) 'Respawn should wait at least 2.5 seconds from collection.'
            Call 'RefillCoins' @([double]($now+4.1))
            Check ((Field 'coins').Count -le 9 -and ((Field 'coins').Count -gt $remainingCoins -or (Field 'coinRespawns').Count -gt 0)) 'Delayed respawns must respect the cap and retry if space is blocked.'
        }
    }
    Check ((Field 'phase') -eq 5 -and (Field 'balance') -eq 500 -and $clicks -le 60) 'Collecting normal and rare coins must win at 500 gold.'
    Check ((Field 'audioClips')['success'].Duration -gt 4) 'New success audio must be present.'
    Call 'ClickCoin' @($null, $click)
    Call 'OnKeyDown' @((New-Object Windows.Forms.KeyEventArgs ([Windows.Forms.Keys]::Enter)))
    Call 'StartGame'
    Check ((Field 'phase') -eq 5 -and (Field 'balance') -eq 500 -and !$game.IsDisposed) 'Winning must not restart or cut off the win clip.'
    Call 'SetPhase' @([int]4)
    Check ((Field 'phase') -eq 4) 'Timeout must stay in its jumpscare until automatic exit.'
    Call 'SetPhase' @([int]1)
    # Stress placement across compact, common, and large client areas.
    foreach ($size in @(@(640,520), @(800,600), @(1200,800))) {
        $game.ClientSize = New-Object Drawing.Size $size[0],$size[1]
        for ($round=0; $round -lt 4; $round++) {
            Call 'StartGame'
            Call 'SetPhase' @([int]3)
            $goldList = Field 'coins'
            Check ($goldList.Count -gt 0 -and $goldList.Count -le 9) 'Coin count must stay between one and nine.'
            Check (@($goldList | Where-Object { !$_.IsFake }).Count -le 7 -and @($goldList | Where-Object { $_.IsFake }).Count -le 2) 'Real and glitch coin caps must be independent.'
            $panel = Call 'RansomPanel'
            for ($i=0; $i -lt $goldList.Count; $i++) {
                $position = $goldList[$i].Position
                $rect = New-Object Drawing.Rectangle $position.X,$position.Y,76,76
                Check ($game.ClientRectangle.Contains($rect)) 'Coin extends outside the game.'
                Check (!$rect.IntersectsWith($panel)) 'Coin overlaps the HUD.'
                for ($j=$i+1; $j -lt $goldList.Count; $j++) {
                    $other = $goldList[$j].Position
                    Check (!$rect.IntersectsWith((New-Object Drawing.Rectangle $other.X,$other.Y,76,76))) 'Coins overlap.'
                }
                foreach ($popup in (Field 'popups')) {
                    $popupRect = New-Object Drawing.Rectangle $popup.FinalPosition,$popup.Size
                    Check (!$rect.IntersectsWith($popupRect)) 'Popup hides a coin.'
                    Check (!$panel.IntersectsWith($popupRect)) 'Popup hides the HUD.'
                }
            }
        }
    }
    $firstGold = (Field 'coins')[0]
    $firstGold.GetType().GetField('Rare').SetValue($firstGold, $true)
    Call 'SpawnPopup' @($false, [double]0)
    Check ((Field 'coins').Contains($firstGold) -and $firstGold.Rare -and $firstGold.Value -eq 100) 'Popup changed a rare coin.'
    $now = Call 'Now'
    $type.GetField('combo', $flags).SetValue($game, [int]5)
    $type.GetField('lastCoinAt', $flags).SetValue($game, [double]($now-2))
    $type.GetField('nextFrame', $flags).SetValue($game, [double]0)
    Call 'Frame' @($null, [EventArgs]::Empty)
    Check ((Field 'combo') -eq 0) 'Expired combo must disappear.'
    $type.GetField('phaseStarted', $flags).SetValue($game, [double]($now-90.01))
    $position = (Field 'coins')[0].Position
    $lateClick = New-Object Windows.Forms.MouseEventArgs ([Windows.Forms.MouseButtons]::Left),1,($position.X+38),($position.Y+38),0
    Call 'ClickCoin' @($null, $lateClick)
    Check ((Field 'phase') -eq 4 -and (Field 'balance') -eq 0) 'Clicks after the deadline must not score.'
    # A stopped timer must not execute callbacks already waiting in the message queue.
    $frameTimer = Field 'timer'
    $script:staleTicks = 0
    $frameTimer.add_Tick({ $script:staleTicks++ })
    $frameTimer.Start()
    Start-Sleep -Milliseconds 15
    $frameTimer.Stop()
    [Windows.Forms.Application]::DoEvents()
    Check ($script:staleTicks -eq 0) 'Stopped timer delivered a stale callback.'
    $clips = @((Field 'audioClips').Values)
} finally {
    $game.Close()
    $game.Dispose()
}
foreach ($clip in $clips) { Check (!$clip.Ready) 'Audio device was not released.' }
Write-Host "PASS: PCM assets, prompt rendering, movement trigger, $clicks coin clicks to win, seven-coin cap and delayed respawns, no win restart, replacement success audio, retry, audio device cleanup; 12 placement stress rounds, rare preservation, timeout, combo expiry, stopped timer."

# Disposal must work even when a form was never shown (startup failures, tools, tests).
$unshown = [Activator]::CreateInstance($type, $true)
$unshownClips = @($type.GetField('audioClips', $flags).GetValue($unshown).Values)
$unshown.Dispose()
$unshown.Dispose()
foreach ($clip in $unshownClips) { Check (!$clip.Ready) 'Unshown form leaked audio resources.' }
Write-Host 'PASS: unshown-form and repeated disposal.'

# The normal no-argument launch must now be a borderless desktop overlay.
$overlay = [Activator]::CreateInstance($type, $true)
try {
    Check ($overlay.FormBorderStyle -eq [Windows.Forms.FormBorderStyle]::None) 'Default launch is not borderless.'
    Check ($overlay.TopMost) 'Desktop overlay must remain above the wallpaper.'
    Check ($overlay.TransparencyKey -eq $overlay.BackColor) 'Desktop background is not transparent.'
    Check ($overlay.Bounds -eq [Windows.Forms.Screen]::FromPoint([Windows.Forms.Cursor]::Position).Bounds) 'Overlay does not fill the launch monitor.'
    $type.GetField('phase', $flags).SetValue($overlay, [int]3)
    $type.GetMethod('ShowMonitorTints', $flags).Invoke($overlay, @())
    $tints = $type.GetField('monitorTints', $flags).GetValue($overlay)
    Check ($tints.Count -eq [Windows.Forms.Screen]::AllScreens.Length) 'Each monitor must receive a tint.'
    foreach ($tint in $tints) { Check ($tint.Opacity -gt 0 -and $tint.Opacity -lt .3) 'Tint must stay subtle.' }
    $type.GetMethod('SetPhase', $flags).Invoke($overlay, [object[]]@([int]5))
    Check ($tints.Count -eq 0) 'Tint must clear when the round ends.'
} finally { $overlay.Dispose() }
Write-Host 'PASS: default transparent desktop overlay and optional windowed mode.'

$finished = $constructor.Invoke([object[]]@($false))
try {
    $type.GetField('muted', $flags).SetValue($finished, $true)
    $finished.Show()
    $type.GetField('timer', $flags).GetValue($finished).Stop()
    $setPhase = @($type.GetMethods($flags) | Where-Object { $_.Name -eq 'SetPhase' })[0]
    $setPhase.Invoke($finished, [object[]]@([int]5))
    $clock = $type.GetMethod('Now', $flags).Invoke($finished, @())
    $type.GetField('phaseStarted', $flags).SetValue($finished, [double]($clock-10))
    $type.GetMethod('DismissVictory', $flags).Invoke($finished, @())
    Check ($finished.IsDisposed) 'Victory must exit after its audio finishes.'
} finally { $finished.Dispose() }
Write-Host 'PASS: dismissing the completed victory exits.'

# Simulate secondary monitors on either side, including a negative desktop origin.
$mapping = $type.GetMethod('MapPopupMonitors', [Reflection.BindingFlags]'Static, NonPublic')
$monitors = [Drawing.Rectangle[]]@((New-Object Drawing.Rectangle -1920,0,1920,1080),(New-Object Drawing.Rectangle 0,0,1920,1080))
$arguments = New-Object object[] 2
$arguments[0] = (New-Object Drawing.Point 0,0).PSObject.BaseObject
$arguments[1] = $monitors
$mapped = $mapping.Invoke($null,$arguments)
Check ($mapped[0].X -eq -1920 -and $mapped[1].X -eq 0) 'Left secondary monitor mapped incorrectly.'
$arguments[0] = (New-Object Drawing.Point -1920,0).PSObject.BaseObject
$mapped = $mapping.Invoke($null,$arguments)
Check ($mapped[0].X -eq 0 -and $mapped[1].X -eq 1920) 'Game on the left monitor mapped incorrectly.'
Write-Host ('PASS: secondary-monitor coordinate mapping. Connected monitors: '+[Windows.Forms.Screen]::AllScreens.Length)

$idle = $constructor.Invoke([object[]]@($false))
try {
    $type.GetField('muted', $flags).SetValue($idle, $true)
    $idle.Show()
    [Windows.Forms.Application]::DoEvents()
    $type.GetField('timer', $flags).GetValue($idle).Stop()
    $idleClock = $type.GetMethod('Now', $flags).Invoke($idle, @())
    $type.GetField('phaseStarted', $flags).SetValue($idle, [double]($idleClock-1.01))
    $type.GetField('nextFrame', $flags).SetValue($idle, [double]0)
    $type.GetMethod('Frame', $flags).Invoke($idle, [object[]]@($null,[EventArgs]::Empty))
    Check (!$idle.Visible -and !$idle.IsDisposed) 'Intro must disappear but leave the glitch tail playing.'
    $type.GetField('introEnds', $flags).SetValue($idle, [double]($idleClock-1))
    $type.GetField('nextFrame', $flags).SetValue($idle, [double]0)
    $type.GetMethod('Frame', $flags).Invoke($idle, [object[]]@($null,[EventArgs]::Empty))
    Check ($idle.IsDisposed) 'App must close once the intro glitch tail finishes.'
} finally { $idle.Dispose() }
Write-Host 'PASS: one-second visual exit with complete intro glitch tail.'

$staticFlags = [Reflection.BindingFlags]'Static, NonPublic'
$labelMethod = $type.GetMethod('DownloadLabel', $staticFlags)
Check ($labelMethod.Invoke($null, [object[]]@([double]0)) -eq 'DOWNLOADING') 'Download label must start without dots.'
Check ($labelMethod.Invoke($null, [object[]]@([double]1)) -eq 'DOWNLOADING..........') 'Download label must finish with ten dots.'
$timingGame = $constructor.Invoke([object[]]@($false))
try {
    $type.GetField('muted', $flags).SetValue($timingGame, $true)
    $phaseMethod = $type.GetMethod('SetPhase', $flags)
    $phaseMethod.Invoke($timingGame, [object[]]@([int]1))
    $phaseMethod.Invoke($timingGame, [object[]]@([int]2))
    $downloadPopups = $type.GetField('popups', $flags).GetValue($timingGame)
    Check ($downloadPopups.Count -eq 0) 'No alerts may appear during downloading.'
    $spawnMethod = @($type.GetMethods($flags) | Where-Object { $_.Name -eq 'SpawnPopup' -and $_.GetParameters().Count -eq 2 })[0]
    $spawnMethod.Invoke($timingGame, [object[]]@($false,[double]0))
    Check ($downloadPopups.Count -eq 0) 'Popup guard must block alerts until gameplay.'
    $clock = $type.GetMethod('Now', $flags).Invoke($timingGame, @())
    $type.GetField('attackStarted', $flags).SetValue($timingGame, [double]($clock-1.4))
    $preview = New-Object Drawing.Bitmap $timingGame.ClientSize.Width,$timingGame.ClientSize.Height
    $graphics = [Drawing.Graphics]::FromImage($preview)
    try {
        $paint = New-Object Windows.Forms.PaintEventArgs $graphics,$timingGame.ClientRectangle
        $paintArgs = New-Object object[] 1
        $paintArgs[0] = $paint.PSObject.BaseObject
        $type.GetMethod('OnPaint', $flags).Invoke($timingGame,$paintArgs)
        $background = $preview.GetPixel(10,($preview.Height-10))
        Check ($background.R -eq 135 -and $background.G -eq 0 -and $background.B -eq 0) 'Red background must stay throughout downloading.'
        $preview.Save((Join-Path $PSScriptRoot 'dist/download-screen.png'))
    } finally { $graphics.Dispose(); $preview.Dispose() }
    $clock = $type.GetMethod('Now', $flags).Invoke($timingGame, @())
    $type.GetField('attackStarted', $flags).SetValue($timingGame, [double]($clock-2.01))
    $type.GetMethod('Frame', $flags).Invoke($timingGame, [object[]]@($null,[EventArgs]::Empty))
    Check ($type.GetField('phase', $flags).GetValue($timingGame) -eq 3) 'Game must start at the slash, before the attack clip finishes.'
} finally { $timingGame.Dispose() }
Write-Host 'PASS: progressive download dots and game start at the slash cue.'

$escalate = $type.GetMethod('Escalation', [Reflection.BindingFlags]'Static, NonPublic')
Check ($escalate.Invoke($null,[object[]]@([double]74)) -eq 0) 'Escalation starts too soon.'
Check ($escalate.Invoke($null,[object[]]@([double]82.5)) -eq 0.5) 'Final escalation must ramp smoothly.'
Check ($escalate.Invoke($null,[object[]]@([double]90)) -eq 1) 'Escalation must peak at the deadline.'
Write-Host 'PASS: closable popups, drag stops drift, twenty-second recycling and final escalation.'

$shakeMethod=$type.GetMethod('JumpscareOffset',[Reflection.BindingFlags]'Static, NonPublic')
$firstShake=$shakeMethod.Invoke($null,[object[]]@([double]0.1,[double]2.4,$false))
$laterShake=$shakeMethod.Invoke($null,[object[]]@([double]1.2,[double]2.4,$false))
Check ($firstShake -ne [Drawing.Point]::Empty -and $laterShake -ne [Drawing.Point]::Empty -and $firstShake -ne $laterShake) 'Jumpscare must keep shaking during its sound.'
Check ($shakeMethod.Invoke($null,[object[]]@([double]0.5,[double]2.4,$true)) -eq [Drawing.Point]::Empty) 'Reduced motion must disable shake.'
Check ($shakeMethod.Invoke($null,[object[]]@([double]2.5,[double]2.4,$false)) -eq [Drawing.Point]::Empty) 'Shake must end with the sound.'
Write-Host 'PASS: sustained two-axis jumpscare shake and reduced-motion behavior.'

$special = $constructor.Invoke([object[]]@($false))
try {
    $type.GetField('muted', $flags).SetValue($special,$true)
    $type.GetMethod('SetPhase',$flags).Invoke($special,[object[]]@([int]3))
    $specialCoins=$type.GetField('coins',$flags).GetValue($special)
    $decoy=$specialCoins[0]
    $decoy.Rare=$false;$decoy.Decoy=$true
    $clickEvent=New-Object Windows.Forms.MouseEventArgs ([Windows.Forms.MouseButtons]::Left),1,($decoy.Position.X+38),($decoy.Position.Y+38),0
    $argsForClick=New-Object object[] 2
    $argsForClick[1]=$clickEvent.PSObject.BaseObject
    $type.GetMethod('ClickCoin',$flags).Invoke($special,$argsForClick)
    Check (!$specialCoins.Contains($decoy) -and $type.GetField('balance',$flags).GetValue($special) -eq 0) 'Fake-out pickup must disappear without awarding gold.'
    $clock=$type.GetMethod('Now',$flags).Invoke($special,@())
    $type.GetField('phaseStarted',$flags).SetValue($special,[double]($clock-86.5))
    $type.GetField('balance',$flags).SetValue($special,[int]490)
    $lastCoin=$specialCoins[0]
    $lastCoin.Rare=$false;$lastCoin.Decoy=$false
    $clickEvent=New-Object Windows.Forms.MouseEventArgs ([Windows.Forms.MouseButtons]::Left),1,($lastCoin.Position.X+38),($lastCoin.Position.Y+38),0
    $argsForClick[1]=$clickEvent.PSObject.BaseObject
    $type.GetMethod('ClickCoin',$flags).Invoke($special,$argsForClick)
    Check ($type.GetField('closeCallWin',$flags).GetValue($special)) 'Winning with under five seconds left must select the special ending.'
    Check ($type.GetField('phase',$flags).GetValue($special) -eq 5) 'Close-call ending must remain a win.'
    $preview=New-Object Drawing.Bitmap 1200,800
    $graphics=[Drawing.Graphics]::FromImage($preview)
    try {
        $argsForEnding=New-Object object[] 2
        $argsForEnding[0]=$graphics.PSObject.BaseObject;$argsForEnding[1]=[double]1
        $type.GetMethod('DrawCloseCall',$flags).Invoke($special,$argsForEnding)
        $preview.Save((Join-Path $PSScriptRoot 'dist/close-call-ending.png'))
    } finally {$graphics.Dispose();$preview.Dispose()}
} finally {$special.Dispose()}
Write-Host 'PASS: fake-out coins give no gold; under-five-second wins use the special ending.'

$ending = $constructor.Invoke([object[]]@($false))
try {
    $type.GetField('muted',$flags).SetValue($ending,$true)
    $ending.Show();[Windows.Forms.Application]::DoEvents()
    $type.GetField('timer',$flags).GetValue($ending).Stop()
    $type.GetMethod('SetPhase',$flags).Invoke($ending,[object[]]@([int]4))
    $clock=$type.GetMethod('Now',$flags).Invoke($ending,@())
    $failureDuration=$type.GetField('audioClips',$flags).GetValue($ending)['failure'].Duration
    $type.GetField('phaseStarted',$flags).SetValue($ending,[double]($clock-$failureDuration-.5))
    $type.GetField('nextFrame',$flags).SetValue($ending,[double]0)
    $type.GetMethod('Frame',$flags).Invoke($ending,[object[]]@($null,[EventArgs]::Empty))
    Check (!$ending.IsDisposed) 'Timeout must wait one second after its jumpscare.'
    $type.GetField('phaseStarted',$flags).SetValue($ending,[double]($clock-$failureDuration-1.01))
    $type.GetField('nextFrame',$flags).SetValue($ending,[double]0)
    $type.GetMethod('Frame',$flags).Invoke($ending,[object[]]@($null,[EventArgs]::Empty))
    Check ($ending.IsDisposed) 'Timeout must exit automatically after the pause.'
} finally {$ending.Dispose()}
Write-Host 'PASS: timeout jumpscare followed by one-second pause and automatic exit.'

$burstGame=$constructor.Invoke([object[]]@($false))
try {
    $type.GetField('muted',$flags).SetValue($burstGame,$true)
    $type.GetMethod('SetPhase',$flags).Invoke($burstGame,[object[]]@([int]3))
    $clock=$type.GetMethod('Now',$flags).Invoke($burstGame,@())
    $type.GetMethod('SpawnBurst',$flags).Invoke($burstGame,[object[]]@([double]$clock))
    $windows=$type.GetField('popups',$flags).GetValue($burstGame)
    $temporary=@($windows | Where-Object { ![double]::IsPositiveInfinity($_.ExpiresAt) })
    Check ($temporary.Count -gt 0 -and $temporary.Count -le 4) 'Burst must create a small temporary cluster.'
    $trap=@($windows | Where-Object { $_.Dodges })[0]
    Check ($null -ne $trap) 'Some windows must have dodging X buttons.'
    $oldX=$trap.CloseButton
    $move=New-Object Windows.Forms.MouseEventArgs ([Windows.Forms.MouseButtons]::None),0,($oldX.X+8),($oldX.Y+8),0
    $moveArgs=New-Object object[] 1;$moveArgs[0]=$move.PSObject.BaseObject
    $trap.GetType().GetMethod('OnMouseMove',$flags).Invoke($trap,$moveArgs)
    Check ($trap.CloseButton.X -ne $oldX.X) 'Close button must dodge inside its title bar.'
    $type.GetMethod('AnimatePopups',$flags).Invoke($burstGame,[object[]]@([double]($clock+5)))
    foreach($window in $temporary) { Check ($window.IsDisposed) 'Burst windows must fade out automatically.' }
} finally {$burstGame.Dispose()}
Write-Host 'PASS: temporary window bursts, automatic fade-out and dodging X buttons.'

$effects=$constructor.Invoke([object[]]@($false))
try {
    $type.GetField('muted',$flags).SetValue($effects,$true)
    $type.GetMethod('SetPhase',$flags).Invoke($effects,[object[]]@([int]3))
    $type.GetField('popupSequence',$flags).SetValue($effects,[int]9)
    $spawn=@($type.GetMethods($flags) | Where-Object { $_.Name -eq 'SpawnPopup' -and $_.GetParameters().Count -eq 2 })[0]
    $spawn.Invoke($effects,[object[]]@($false,[double]0))
    $thief=@($type.GetField('popups',$flags).GetValue($effects) | Where-Object { $_.Steals })[0]
    Check ($null -ne $thief) 'Marked gold thief must spawn.'
    $type.GetField('balance',$flags).SetValue($effects,[int]20)
    $type.GetField('nextTheftAt',$flags).SetValue($effects,[double]0)
    $clock=$type.GetMethod('Now',$flags).Invoke($effects,@())
    $type.GetMethod('StealGold',$flags).Invoke($effects,[object[]]@([double]$clock))
    Check ($type.GetField('balance',$flags).GetValue($effects) -eq 15) 'Marked window must steal five gold.'
    Check (!$type.GetField('soundStarts',$flags).GetValue($effects).ContainsKey('glitch')) 'Gold theft must not play the glitch-coin sound.'
    $thief.Close()
    $type.GetField('nextTheftAt',$flags).SetValue($effects,[double]0)
    $type.GetMethod('StealGold',$flags).Invoke($effects,[object[]]@([double]($clock+4)))
    Check ($type.GetField('balance',$flags).GetValue($effects) -eq 15) 'Closing thief must stop gold loss.'
    $type.GetField('phaseStarted',$flags).SetValue($effects,[double]($clock-80))
    $type.GetMethod('UpdateCursorTrail',$flags).Invoke($effects,[object[]]@([double]$clock))
    Check ($type.GetField('cursorTrail',$flags).GetValue($effects).Count -gt 0) 'Final countdown must produce cursor trail.'
    $type.GetField('reducedMotion',$flags).SetValue($effects,$true)
    $type.GetMethod('UpdateCursorTrail',$flags).Invoke($effects,[object[]]@([double]($clock+.1)))
    Check ($type.GetField('cursorTrail',$flags).GetValue($effects).Count -eq 0) 'Reduced motion must clear the trail.'
} finally {$effects.Dispose()}
Write-Host 'PASS: marked theft windows, stopping theft on close, final cursor trail and reduced motion.'

$fade=$type.GetMethod('CoinOpacity',[Reflection.BindingFlags]'Static, NonPublic')
Check ($fade.Invoke($null,[object[]]@([double]0,$false)) -eq 0) 'Coin fade must begin transparent.'
$half=$fade.Invoke($null,[object[]]@([double]0.14,$false))
Check ($half -gt 0.45 -and $half -lt 0.55) 'Coin fade must progress smoothly.'
Check ($fade.Invoke($null,[object[]]@([double]0.28,$false)) -eq 1) 'Coin fade must finish fully visible.'
Check ($fade.Invoke($null,[object[]]@([double]0,$true)) -eq 1) 'Reduced motion must show coins immediately.'
Write-Host 'PASS: coin fade endpoints, smooth midpoint and reduced-motion behavior.'

$waveX=$type.GetMethod('WaveX',[Reflection.BindingFlags]'Static, NonPublic')
$desktop=New-Object Drawing.Rectangle -1920,0,3840,1080
Check ($waveX.Invoke($null,[object[]]@($desktop,[double]0)) -lt -1920) 'Wave must start off the left monitor.'
Check ($waveX.Invoke($null,[object[]]@($desktop,[double]0.5)) -eq 0) 'Wave must cross between monitors.'
Check ($waveX.Invoke($null,[object[]]@($desktop,[double]1)) -gt 1920) 'Wave must leave the right monitor.'
Write-Host 'PASS: corruption sweep spans both monitors, including negative desktop coordinates.'

$sweepGame=$constructor.Invoke([object[]]@($false))
try {
    $type.GetField('muted',$flags).SetValue($sweepGame,$true)
    $sweepGame.Show();[Windows.Forms.Application]::DoEvents()
    $type.GetField('timer',$flags).GetValue($sweepGame).Stop()
    $type.GetMethod('SetPhase',$flags).Invoke($sweepGame,[object[]]@([int]3))
    $clock=$type.GetMethod('Now',$flags).Invoke($sweepGame,@())
    $type.GetField('nextWaveAt',$flags).SetValue($sweepGame,[double]0)
    $type.GetMethod('UpdateWave',$flags).Invoke($sweepGame,[object[]]@([double]$clock))
    $beam=$type.GetField('corruptionWave',$flags).GetValue($sweepGame)
    Check ($beam.Visible) 'Sweep surface must become visible.'
    $spawnWindow=@($type.GetMethods($flags) | Where-Object { $_.Name -eq 'SpawnPopup' -and $_.GetParameters().Count -eq 2 })[0]
    $spawnWindow.Invoke($sweepGame,[object[]]@($false,[double]0))
    $victim=$type.GetField('popups',$flags).GetValue($sweepGame)[0]
    Check ($null -ne $victim) 'Test needs a target popup for the sweep.'
    $origin=$sweepGame.PointToScreen([Drawing.Point]::Empty)
    $progress=($victim.Bounds.X+$victim.Width/2-$origin.X+32)/($sweepGame.ClientSize.Width+64)
    $type.GetField('waveStarted',$flags).SetValue($sweepGame,[double]($clock-3.5*$progress))
    $type.GetMethod('UpdateWave',$flags).Invoke($sweepGame,[object[]]@([double]$clock))
    Check ($victim.CorruptUntil -gt $clock) "Sweep must briefly distort crossed windows (progress=$progress, victim=$($victim.Bounds), clock=$clock, corrupt=$($victim.CorruptUntil), beam=$($beam.Bounds))."
    $type.GetField('reducedMotion',$flags).SetValue($sweepGame,$true)
    $type.GetMethod('UpdateWave',$flags).Invoke($sweepGame,[object[]]@([double]($clock+0.1)))
    Check (!$beam.Visible) 'Reduced motion must hide the sweep.'
} finally {$sweepGame.Close();$sweepGame.Dispose()}
Write-Host 'PASS: sweep display, popup intersection, reduced motion and cleanup.'

$animationGame=$constructor.Invoke([object[]]@($false))
try {
    $type.GetField('muted',$flags).SetValue($animationGame,$true)
    $type.GetMethod('SetPhase',$flags).Invoke($animationGame,[object[]]@([int]3))
    $spawn=@($type.GetMethods($flags) | Where-Object { $_.Name -eq 'SpawnPopup' -and $_.GetParameters().Count -eq 2 })[0]
    $spawn.Invoke($animationGame,[object[]]@($false,[double]0))
    $windows=$type.GetField('popups',$flags).GetValue($animationGame)
    $fold=$windows[0]
    $clock=$type.GetMethod('Now',$flags).Invoke($animationGame,@())
    $height=$fold.Height
    $fold.BeginFold($clock)
    $type.GetMethod('AnimatePopups',$flags).Invoke($animationGame,[object[]]@([double]($clock+0.2)))
    Check (!$fold.IsDisposed -and $fold.Height -lt $height) 'Close animation must visibly fold before removal.'
    $type.GetMethod('AnimatePopups',$flags).Invoke($animationGame,[object[]]@([double]($clock+0.5)))
    Check ($fold.IsDisposed) 'Folded window must finish closing.'
    foreach($window in @($windows)) {$window.Dispose()};$windows.Clear()
    $goldList=$type.GetField('coins',$flags).GetValue($animationGame)
    foreach($coin in $goldList) {$coin.Moves=$false}
    $moving=$goldList[0];$moving.Moves=$true;$moving.NextMove=0
    $moveMethod=$type.GetMethod('MoveCoins',$flags)
    for($attempt=0;$attempt -lt 5 -and $null -eq $moving.Target;$attempt++) {
        $moving.NextMove=0;$moveMethod.Invoke($animationGame,[object[]]@([double]$clock))
    }
    Check ($null -ne $moving.Target) 'Moving coin must find a nearby free path.'
    $oldPosition=$moving.Position;$moving.MoveStarted=$clock-0.4
    $moveMethod.Invoke($animationGame,[object[]]@([double]$clock))
    Check ($moving.Position -ne $oldPosition -and $moving.Trail.Count -gt 0) 'Coin must move its hitbox and leave a trail.'
    $theme=$type.GetField('audioClips',$flags).GetValue($animationGame)['theme']
    $loudest=0.0
    for($sample=0;$sample -lt 10;$sample++) {$loudest=[Math]::Max($loudest,$theme.EnvelopeAt($sample*0.2))}
    Check ($loudest -gt 0 -and $loudest -le 1) 'Theme envelope must drive the music-reactive effects.'
} finally {$animationGame.Dispose()}
Write-Host 'PASS: folding close animation, moving coin trails and theme envelope.'

$clockText=$type.GetMethod('CountdownVisual',[Reflection.BindingFlags]'Static,NonPublic')
Check ($clockText.Invoke($null,[object[]]@([int]90,$false,[double]1)) -eq '01:30') 'Uncorrupted countdown must stay accurate.'
Check ($clockText.Invoke($null,[object[]]@([int]90,$true,[double]1)) -ne '01:30') 'Countdown interference must change only its displayed text.'
$chain=$constructor.Invoke([object[]]@($false))
try {
    $type.GetField('muted',$flags).SetValue($chain,$true)
    $type.GetMethod('SetPhase',$flags).Invoke($chain,[object[]]@([int]3))
    $goldList=$type.GetField('coins',$flags).GetValue($chain)
    $decoys=@($goldList | Where-Object { $_.IsFake })
    $decoys[0].Position=New-Object Drawing.Point 180,180
    $decoys[1].Position=New-Object Drawing.Point 300,180
    $clock=$type.GetMethod('Now',$flags).Invoke($chain,@())
    $argsForBurst=New-Object object[] 2
    $argsForBurst[0]=$decoys[0].PSObject.BaseObject;$argsForBurst[1]=[double]$clock
    $type.GetMethod('BurstGlitchCoin',$flags).Invoke($chain,$argsForBurst)
    Check (@($goldList | Where-Object { $_.IsFake }).Count -eq 0) 'Nearby glitch coins must chain-react.'
    Check ($type.GetField('balance',$flags).GetValue($chain) -eq 0) 'Wave-triggered burst must not award gold.'
    Check ($type.GetField('coinBursts',$flags).GetValue($chain).Count -eq 2) 'Chain reaction must render both bursts.'
    Check ($type.GetField('coinRespawns',$flags).GetValue($chain).Count -eq 2) 'Burst coins must keep their respawn slots.'
    Check (!$type.GetField('soundStarts',$flags).GetValue($chain).ContainsKey('glitch')) 'Automatic bursts must not play the pickup-only glitch sound.'
} finally {$chain.Dispose()}
Write-Host 'PASS: visual countdown interference and glitch-coin chain reactions without scoring or pickup audio.'

$downloadShake=$type.GetMethod('DownloadShake',[Reflection.BindingFlags]'Static,NonPublic')
$one=$downloadShake.Invoke($null,[object[]]@([double]0.14,[double]0.5,$false))
$two=$downloadShake.Invoke($null,[object[]]@([double]0.19,[double]0.5,$false))
Check ($one -ne $two -and ($one.X -ne 0 -or $one.Y -ne 0)) 'Download group must shake in both timing samples.'
Check ($downloadShake.Invoke($null,[object[]]@([double]0.14,[double]0.5,$true)) -eq [Drawing.Point]::Empty) 'Reduced motion must disable download shake.'
Write-Host 'PASS: grouped download shake and reduced-motion behavior.'

$popupClass=$type.GetNestedType('FakePopup',[Reflection.BindingFlags]::NonPublic)
$popupTitle=$popupClass.GetMethod('TitleFor',[Reflection.BindingFlags]'Static,NonPublic')
Check ($popupTitle.Invoke($null,[object[]]@([int]0)) -eq 'GOLD') 'Popup titles must include GOLD.'
Check ($popupTitle.Invoke($null,[object[]]@([int]1)) -eq 'YOURGOLDISYUMMY') 'Popup titles must include YOURGOLDISYUMMY.'
Check ($popupTitle.Invoke($null,[object[]]@([int]2)) -eq 'R4NS0M') 'Popup titles must include R4NS0M.'
Write-Host 'PASS: varied ransom/gold popup titles.'

$introSync=$constructor.Invoke([object[]]@($false))
$introBitmap=New-Object Drawing.Bitmap 1200,800
$introGraphics=[Drawing.Graphics]::FromImage($introBitmap)
try {
    $type.GetField('muted',$flags).SetValue($introSync,$true)
    $clock=$type.GetMethod('Now',$flags).Invoke($introSync,@())
    $type.GetField('phaseStarted',$flags).SetValue($introSync,[double]($clock-0.05))
    $paint=New-Object Windows.Forms.PaintEventArgs $introGraphics,$introSync.ClientRectangle
    $paintArgs=New-Object object[] 1;$paintArgs[0]=$paint.PSObject.BaseObject
    $type.GetMethod('OnPaint',$flags).Invoke($introSync,$paintArgs)
    $starts=$type.GetField('soundStarts',$flags).GetValue($introSync)
    Check (!$starts.ContainsKey('anticipation')) 'Intro face must appear before the tuh starts.'
    $clock=$type.GetMethod('Now',$flags).Invoke($introSync,@())
    $type.GetField('phaseStarted',$flags).SetValue($introSync,[double]($clock-0.2))
    $type.GetMethod('OnPaint',$flags).Invoke($introSync,$paintArgs)
    Check ($starts.ContainsKey('anticipation')) 'Intro sound must start on the first stop-sign paint.'
    $started=$starts['anticipation']
    $type.GetMethod('OnPaint',$flags).Invoke($introSync,$paintArgs)
    Check ($starts['anticipation'] -eq $started) 'Repainting the sign must not restart audio.'
} finally {$introGraphics.Dispose();$introBitmap.Dispose();$introSync.Dispose()}
Write-Host 'PASS: silent opening face, sound synchronized with stop sign, no repeated playback.'
