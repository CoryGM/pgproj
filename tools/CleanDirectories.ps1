# Define the root path of the .Net solution
$rootPath = ".\"

# Get all directories named 'bin' or 'obj' within the root path
$directories = Get-ChildItem -Path $rootPath -Recurse -Directory -Include bin, obj

# Iterate through each directory and remove it
foreach ($dir in $directories) {
    try {
        Remove-Item -Path $dir.FullName -Recurse -Force
        Write-Host "Deleted: $($dir.FullName)"
    } catch {
        Write-Host "Failed to delete: $($dir.FullName). Error: $_"
    }
}