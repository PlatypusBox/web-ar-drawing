# Publishes Build/WebGL to GitHub Pages (branch gh-pages). Creates the repo on first run, updates it afterwards.
# Prereqs: git installed, `gh auth login` done.   Usage: powershell Tools\deploy_pages.ps1 [-Repo web-ar-drawing]
param([string]$Repo = "web-ar-drawing")
$ErrorActionPreference = "Stop"
$build = Join-Path (Split-Path $PSScriptRoot -Parent) "Build\WebGL"
if (-not (Test-Path "$build\index.html")) { throw "Build the WebGL player first." }
$user = gh api user --jq .login
$tmp = Join-Path $env:TEMP "pages_$Repo"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
Copy-Item $build $tmp -Recurse
New-Item "$tmp\.nojekyll" -ItemType File | Out-Null

& gh repo view "$user/$Repo" *> $null
$exists = ($LASTEXITCODE -eq 0)

Push-Location $tmp
try {
    git init -b gh-pages *> $null
    git config core.autocrlf false
    git add -A
    git commit -m "Deploy Magic Colouring Aquarium WebGL build ($(Get-Date -Format s))" *> $null
    if ($exists) {
        git remote add origin "https://github.com/$user/$Repo.git"
        git push --force origin gh-pages          # gh-pages only holds build output, so replacing it is safe
        if ($LASTEXITCODE -ne 0) { throw "git push failed" }
    } else {
        gh repo create "$user/$Repo" --public --source . --push --remote origin
        gh api -X POST "repos/$user/$Repo/pages" -f "source[branch]=gh-pages" -f "source[path]=/" *> $null
    }
} finally { Pop-Location }
Write-Host "Live in ~1-2 minutes at: https://$user.github.io/$Repo/"
