<#
.SYNOPSIS
    Controls an LCUS-style USB relay board (CH340, VID_1A86/PID_7523).

.DESCRIPTION
    Sends 4-byte frames over serial: A0 <channel> <state> <checksum>,
    where checksum is the sum of the first three bytes.

.EXAMPLE
    .\relay.ps1 -State on
    .\relay.ps1 -Channel 2 -State off
    .\relay.ps1 -State pulse -DurationMs 500
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 8)]
    [int]$Channel = 1,

    [Parameter(Mandatory)]
    [ValidateSet('on', 'off', 'pulse')]
    [string]$State,

    [string]$Port = 'COM3',

    [int]$BaudRate = 9600,

    # Only used with -State pulse: how long to stay on before switching back off.
    [int]$DurationMs = 500
)

function New-RelayFrame {
    param([int]$Channel, [int]$On)
    $bytes = @(0xA0, $Channel, $On)
    $checksum = ($bytes | Measure-Object -Sum).Sum -band 0xFF
    return [byte[]]($bytes + $checksum)
}

$serial = New-Object System.IO.Ports.SerialPort($Port, $BaudRate, 'None', 8, 'One')
try {
    $serial.Open()

    switch ($State) {
        'on'  { $frames = @((New-RelayFrame $Channel 1)) }
        'off' { $frames = @((New-RelayFrame $Channel 0)) }
        'pulse' {
            $frames = @((New-RelayFrame $Channel 1), (New-RelayFrame $Channel 0))
        }
    }

    for ($i = 0; $i -lt $frames.Count; $i++) {
        if ($i -gt 0) { Start-Sleep -Milliseconds $DurationMs }
        $frame = $frames[$i]
        $serial.Write($frame, 0, $frame.Length)
        Write-Verbose ("Sent: " + (($frame | ForEach-Object { '{0:X2}' -f $_ }) -join ' '))
    }

    Write-Output "Relay $Channel on $Port -> $State"
}
finally {
    if ($serial.IsOpen) { $serial.Close() }
    $serial.Dispose()
}
