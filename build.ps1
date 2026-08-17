$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourcePath = Join-Path $projectRoot 'src\CodexPetWalker.cs'
$outputDirectory = Join-Path $projectRoot 'dist'
$outputPath = Join-Path $outputDirectory 'CodexPetWalker.exe'
$spriteSourcePath = Join-Path $projectRoot 'assets\spritesheet-keyed.png'
$spriteOutputPath = Join-Path $outputDirectory 'spritesheet.png'
$compilerPath = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $compilerPath)) {
    throw 'The built-in Windows C# compiler was not found.'
}

if (-not (Test-Path -LiteralPath $spriteSourcePath)) {
    throw 'assets\spritesheet-keyed.png 파일이 없습니다. assets\README.md의 준비 방법을 먼저 확인하세요.'
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

& $compilerPath `
    /nologo `
    /target:winexe `
    /optimize+ `
    /platform:x64 `
    /reference:System.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    "/out:$outputPath" `
    $sourcePath

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath $spriteSourcePath -Destination $spriteOutputPath -Force

Get-Item -LiteralPath $outputPath
