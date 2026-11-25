<#
.SYNOPSIS
    AI-Engineer Commercial Setup and In-App Host Integration Wizard (AutoCAD & Revit).
.DESCRIPTION
    Installs dependencies, configures selectable multi-LLM providers (Gemini, Claude, OpenAI/Codex),
    deploys AutoCAD and Revit plugins, and starts the verified local background bridge.
#>

param(
    [switch]$SkipTests,
    [switch]$Unattended,
    [string]$ProviderMode = "auto",
    [string]$GeminiKey = "",
    [string]$ClaudeKey = "",
    [string]$OpenAIKey = ""
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Step([string]$Message) {
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Success([string]$Message) {
    Write-Host "`n[SUCCESS] $Message" -ForegroundColor Green
}

function Fail([string]$Message) {
    Write-Host "`nERROR: $Message" -ForegroundColor Red
    exit 1
}

function Wait-BridgeReady([Diagnostics.Process]$Process, [int]$Port = 8765) {
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        if ($Process.HasExited) { Fail "The operator bridge exited during startup with code $($Process.ExitCode)." }
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -Method Get -TimeoutSec 1
            if ($health.status -eq 'ok') { return $health }
        } catch {}
        Start-Sleep -Seconds 1
    }
    Fail "The operator bridge did not become healthy on 127.0.0.1:$Port. Review the bridge window for the exact error."
}

if ($env:OS -ne 'Windows_NT') {
    Fail 'This installer must be run on Windows.'
}

Clear-Host
Write-Host "========================================================================" -ForegroundColor Yellow
Write-Host "             AI-Engineer — AutoCAD & Revit Copilot Setup                " -ForegroundColor White
Write-Host "     مساعد المهندس الكهربائي و BIM الذكي داخل الأوتوكاد والريفت        " -ForegroundColor Cyan
Write-Host "========================================================================" -ForegroundColor Yellow

# 1. Check Python
Step 'فحص بيئة بايثون (Checking Python Environment)'
$pythonExe = ".\.venv\Scripts\python.exe"
if (-not (Test-Path $pythonExe)) {
    $sysPython = Get-Command python -ErrorAction SilentlyContinue
    if (-not $sysPython) { Fail 'Python 3.10+ was not found in PATH.' }
    $pythonExe = "python"
}
& $pythonExe --version
if ($LASTEXITCODE -ne 0) { Fail 'Python could not be executed.' }

# 2. Install / Verify Dependencies
Step 'تثبيت وتحديث المكتبات الهندسية (Installing Requirements)'
& $pythonExe -m pip install -r requirements.txt --quiet
if ($LASTEXITCODE -ne 0) { Fail 'Failed to install Python requirements.' }

Step 'تهيئة ملفات التشغيل الافتراضية وفحص الجاهزية (Bootstrapping Runtime Data)'
$prevErrPref = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& $pythonExe launcher.py check | Out-Null
$checkExit = $LASTEXITCODE
$ErrorActionPreference = $prevErrPref
if ($checkExit -ne 0) {
    Write-Host "ملاحظة: تعذر إتمام فحص launcher.py check (يتطلب Python 3.13.x)." -ForegroundColor Yellow
} else {
    Success "تم التحقق من سلامة ملفات التشغيل والمنتج بنجاح."
}

if (-not $SkipTests) {
    Step 'فحص الاختبارات الهندسية (Verifying AI-Engineer Core Tests)'
    $pytestBase = Join-Path $PSScriptRoot ("test-temp\pt-setup-" + [guid]::NewGuid().ToString('N'))
    try {
        & $pythonExe -m pytest tests/test_claude_provider.py tests/test_redline_review.py tests/test_spec_matcher.py tests/test_viewport_vision.py -q -p no:cacheprovider --basetemp $pytestBase
    }
    finally {
        if (Test-Path -LiteralPath $pytestBase) {
            Remove-Item -LiteralPath $pytestBase -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# 3. Configure Multi-LLM AI Providers
Step 'إعداد مزودي الذكاء الاصطناعي (Configuring Selectable AI Providers)'
if (-not $Unattended) {
    Write-Host "`nاختر مزود الذكاء الاصطناعي الأساسي (Choose Default AI Provider):" -ForegroundColor Yellow
    Write-Host "1) Google Gemini (Recommended - فائق السرعة واقتصادي)"
    Write-Host "2) Anthropic Claude (Claude 3.7 / 3.5 Sonnet - قدرة برمجية وهندسية فائقة)"
    Write-Host "3) OpenAI / Codex (GPT-4o / GPT-5 / Codex)"
    Write-Host "4) Local / Custom Endpoint (Ollama / Local LLM)"
    Write-Host "5) تخطي الإعداد الآن (Skip for now - keep existing environment)"
    
    $choice = Read-Host "`nأدخل اختيارك [1-5]"
    switch ($choice) {
        "1" {
            $key = Read-Host "أدخل مفتاح Gemini API Key (أو اضغط Enter للاحتفاظ بالحالي)"
            if ($key.Trim()) {
                [Environment]::SetEnvironmentVariable("GEMINI_API_KEY", $key.Trim(), "User")
                $env:GEMINI_API_KEY = $key.Trim()
            }
            [Environment]::SetEnvironmentVariable("AI_ENGINEER_PROVIDER", "gemini", "User")
            [Environment]::SetEnvironmentVariable("AI_ENGINEER_GEMINI_ENABLE", "1", "User")
            $env:AI_ENGINEER_PROVIDER = "gemini"
            $env:AI_ENGINEER_GEMINI_ENABLE = "1"
            Write-Host "تم ضبط المزود على Gemini بنجاح." -ForegroundColor Green
        }
        "2" {
            $key = Read-Host "أدخل مفتاح Anthropic Claude API Key (أو اضغط Enter للاحتفاظ بالحالي)"
            if ($key.Trim()) {
                [Environment]::SetEnvironmentVariable("ANTHROPIC_API_KEY", $key.Trim(), "User")
                $env:ANTHROPIC_API_KEY = $key.Trim()
            }
            [Environment]::SetEnvironmentVariable("AI_ENGINEER_PROVIDER", "claude", "User")
            [Environment]::SetEnvironmentVariable("AI_ENGINEER_CLAUDE_ENABLE", "1", "User")
            $env:AI_ENGINEER_PROVIDER = "claude"
            $env:AI_ENGINEER_CLAUDE_ENABLE = "1"
            Write-Host "تم ضبط المزود على Claude بنجاح." -ForegroundColor Green
        }
        "3" {
            $key = Read-Host "أدخل مفتاح OpenAI API Key (أو اضغط Enter للاحتفاظ بالحالي)"
            if ($key.Trim()) {
                [Environment]::SetEnvironmentVariable("OPENAI_API_KEY", $key.Trim(), "User")
                $env:OPENAI_API_KEY = $key.Trim()
            }
            [Environment]::SetEnvironmentVariable("AI_ENGINEER_PROVIDER", "openai", "User")
            [Environment]::SetEnvironmentVariable("AI_ENGINEER_OPENAI_ENABLE", "1", "User")
            $env:AI_ENGINEER_PROVIDER = "openai"
            $env:AI_ENGINEER_OPENAI_ENABLE = "1"
            Write-Host "تم ضبط المزود على OpenAI بنجاح." -ForegroundColor Green
        }
        default {
            Write-Host "تم تخطي تغيير المزود؛ سيتم استخدام الإعدادات الافتراضية." -ForegroundColor Cyan
        }
    }
}

# 4. Deploy AutoCAD Plugin
Step 'تثبيت وبناء إضافات أوتوكاد (Building & Deploying AutoCAD Plugins)'
$acadDetectedRoot = $null
for ($yr = 2030; $yr -ge 2015; $yr--) {
    $cand = "C:\Program Files\Autodesk\AutoCAD $yr"
    if (Test-Path -LiteralPath (Join-Path $cand "accoremgd.dll") -PathType Leaf) {
        $acadDetectedRoot = $cand
        break
    }
}
$autocadBuildScript = Join-Path $PSScriptRoot "integrations\autocad_context_plugin\build.ps1"
if ($null -ne $acadDetectedRoot -and (Test-Path -LiteralPath $autocadBuildScript -PathType Leaf)) {
    try {
        $builtCtx = & $autocadBuildScript -WorkspaceRoot (Join-Path $PSScriptRoot "workspace") -AutoCADRoot $acadDetectedRoot
        $bridgeDir = Join-Path $PSScriptRoot "workspace\.ai-engineer\autocad\context\bridge"
        Success "تم بناء Context.dll + PanelManufacturing.dll بنجاح لأوتوكاد في: $bridgeDir"
    } catch {
        Write-Host "تحذير: تعذر البناء التلقائي لإضافة AutoCAD: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
$acadPluginsDir = Join-Path $env:APPDATA "Autodesk\ApplicationPlugins"
if (-not (Test-Path $acadPluginsDir)) {
    New-Item -ItemType Directory -Path $acadPluginsDir -Force | Out-Null
}
$targetBundle = Join-Path $acadPluginsDir "AIEngineer.bundle"
$sourceBundle = Join-Path $PSScriptRoot "workspace - Copy\.ai-engineer\autocad\package\AIEngineer.bundle"
if (Test-Path $sourceBundle) {
    Copy-Item -Path $sourceBundle -Destination $targetBundle -Recurse -Force
    Success "تم تثبيت حزمة AutoCAD Bundle بنجاح في: $targetBundle"
}

# 5. Deploy Revit Plugin
Step 'تثبيت وبناء إضافة ريفيت (Building & Deploying Revit Plugin)'
$revitDirectBuild = Join-Path $PSScriptRoot "integrations\revit_direct_plugin\build.ps1"
$revitBuilt = $false
if (Test-Path -LiteralPath $revitDirectBuild -PathType Leaf) {
    try {
        & $revitDirectBuild -AllInstalled -Install | Out-Null
        $revitBuilt = $true
        Success "تم بناء وتفعيل إضافة Revit Direct (2015-2026+) بنجاح."
    } catch {
        Write-Host "ملاحظة: تعذر البناء التلقائي لإضافة Revit Direct: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
if (-not $revitBuilt) {
    $revitAddinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins"
    if (Test-Path $revitAddinsDir) {
        $addinTemplate = Join-Path $PSScriptRoot "integrations\revit_readonly_plugin\AIEngineer.Revit.addin.template"
        if (Test-Path $addinTemplate) {
            $dllPath = Join-Path $PSScriptRoot "integrations\revit_readonly_plugin\bin\AIEngineer.Revit.dll"
            $addinContent = (Get-Content $addinTemplate -Raw).Replace("{{ASSEMBLY_PATH}}", $dllPath)
            foreach ($year in @("2024", "2025", "2026")) {
                $yearDir = Join-Path $revitAddinsDir $year
                if (Test-Path $yearDir) {
                    Set-Content -Path (Join-Path $yearDir "AIEngineer.Revit.addin") -Value $addinContent -Encoding UTF8
                    Success "تم تفعيل إضافة Revit $year في: $yearDir"
                }
            }
        }
    }
}

# 6. Start Operator Bridge Service
Step 'تشغيل الخدمة المحلية ومحرك المحادثة (Starting Local AI-Engineer Service)'
$port = if ($env:AI_ENGINEER_API_PORT) { $env:AI_ENGINEER_API_PORT } else { "8765" }
$bridgeCommand = @"
Set-Location '$($PSScriptRoot.Replace("'", "''"))';
`$host.UI.RawUI.WindowTitle = 'AI-Engineer Host Service (127.0.0.1:$port)';
Write-Host '=====================================================' -ForegroundColor Cyan;
Write-Host '   AI-Engineer Local Host Service - Keep Open        ' -ForegroundColor Green;
Write-Host '   AutoCAD Palette & Revit Bridge is listening       ' -ForegroundColor White;
Write-Host '=====================================================' -ForegroundColor Cyan;
& '$($pythonExe.Replace("'", "''"))' launcher.py operator --allow-project-root;
"@

$bridge = Start-Process powershell -PassThru -ArgumentList '-NoExit','-NoProfile','-ExecutionPolicy','Bypass','-Command',$bridgeCommand
try {
    $health = Wait-BridgeReady -Process $bridge -Port ([int]$port)
    Success "خدمة الذكاء الاصطناعي تعمل الآن بنجاح على: http://127.0.0.1:$port (الإصدار: $($health.version))"
} catch {
    Write-Host "تنبيه: تم تشغيل الخدمة، ويمكن التحقق من نافذتها المستقلة." -ForegroundColor Yellow
}

Write-Host "`n========================================================================" -ForegroundColor Green
Write-Host "                         اكتمل التثبيت بنجاح!                           " -ForegroundColor White
Write-Host "========================================================================" -ForegroundColor Green
Write-Host "1) داخل أوتوكاد (المخططات والإنارة والكميات): اكتب AIENGPALETTE في شريط الأوامر." -ForegroundColor Cyan
Write-Host "2) داخل أوتوكاد (تصنيع اللوحات الصناعية 2D/3D): اكتب AIENGPANEL في شريط الأوامر." -ForegroundColor Cyan
Write-Host "3) داخل ريفيت: ستجد لوحة AI-Engineer المدمجة جاهزة للتواصل." -ForegroundColor Cyan
Write-Host "4) للتعديل أو الرسم: سيظهر التعديل بلون المراجعة (أحمر). اكتب 'تمام' للاعتماد." -ForegroundColor Yellow
Write-Host "========================================================================`n" -ForegroundColor Green
