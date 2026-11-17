# Loads the images of this bundle into Docker. Needs no internet.
Set-Location $PSScriptRoot
docker load -i images.tar.gz
docker images | Select-String "gameserver|postgres"
