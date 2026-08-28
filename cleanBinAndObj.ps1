$scriptPath = $MyInvocation.MyCommand.Path
$currentDirectory = Split-Path -Path $scriptPath -Parent

Get-ChildItem -Path $currentDirectory -Include obj, bin -Recurse -Force

# Get the number of obj and bin folders
$foldersToDelete = Get-ChildItem -Path $currentDirectory -Include obj, bin -Recurse -Force | Measure-Object | Select-Object -ExpandProperty Count

# Display the number of folders to be deleted
Write-Host "Deleting $foldersToDelete folders..."

# Remove all obj and bin folders recursively
Get-ChildItem -Path $currentDirectory -Include obj, bin -Recurse -Force | Remove-Item -Recurse -Force

Read-Host "Done Press Enter to close"