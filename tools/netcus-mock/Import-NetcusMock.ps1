# 목업 서버(NetcusMock.cs)를 이 PowerShell 세션에 올린다. 테스트와 start-mock.ps1 이 같이 쓴다.
#   . .\tools\netcus-mock\Import-NetcusMock.ps1
#   $mock = New-Object FileCryptMock.NetcusMock; $mock.Start(0); $mock.Port
if (-not ('FileCryptMock.NetcusMock' -as [type])) {
    $src = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'NetcusMock.cs'), [Text.Encoding]::UTF8)
    Add-Type -TypeDefinition $src -Language CSharp -ReferencedAssemblies @('System.dll', 'System.Core.dll') -ErrorAction Stop
}
