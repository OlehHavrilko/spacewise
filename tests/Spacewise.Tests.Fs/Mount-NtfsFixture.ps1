<#
.SYNOPSIS
  Creates (if missing) and mounts a dynamic VHDX with one NTFS volume for Spacewise.Tests.Fs.
  Run from an elevated PowerShell. Only the disk backed by -Path is initialized and formatted,
  and only while it is still RAW (freshly created).
.EXAMPLE
  .\Mount-NtfsFixture.ps1 -Path D:\fixtures\spacewise.vhdx
  $env:SPACEWISE_FS_ROOT = 'X:\'; dotnet test tests/Spacewise.Tests.Fs -c Release
  .\Mount-NtfsFixture.ps1 -Path D:\fixtures\spacewise.vhdx -Dismount
#>
param(
    [Parameter(Mandatory)] [string] $Path,
    [int] $SizeMB = 512,
    [switch] $Dismount
)
$ErrorActionPreference = 'Stop'
$Path = [IO.Path]::GetFullPath($Path)
if ([IO.Path]::GetExtension($Path) -ne '.vhdx') { throw "Ожидается файл .vhdx: $Path" }

if ($Dismount) {
    Dismount-DiskImage -ImagePath $Path | Out-Null
    Write-Host "Отключён: $Path"
    return
}

if (-not (Test-Path -LiteralPath $Path)) {
    $script = "create vdisk file=`"$Path`" maximum=$SizeMB type=expandable"
    $script | diskpart | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Path)) { throw "diskpart не создал $Path" }
}

$image = Get-DiskImage -ImagePath $Path
if (-not $image.Attached) { $image = Mount-DiskImage -ImagePath $Path -PassThru }
$disk = $image | Get-Disk
if ($disk.PartitionStyle -eq 'RAW') {
    Initialize-Disk -Number $disk.Number -PartitionStyle GPT
    New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter |
        Format-Volume -FileSystem NTFS -NewFileSystemLabel 'SWFIXTURE' -Confirm:$false | Out-Null
}
$volume = Get-Partition -DiskNumber $disk.Number | Get-Volume | Where-Object FileSystem -eq 'NTFS' | Select-Object -First 1
if (-not $volume.DriveLetter) { throw "На $Path нет тома NTFS с буквой диска" }
$root = "$($volume.DriveLetter):\"
Write-Host "SPACEWISE_FS_ROOT=$root"
$root
