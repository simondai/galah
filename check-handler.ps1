Add-Type -AssemblyName System.Drawing
foreach ($f in @('handler-devmgmt.png','handler-services.png')) {
  $bmp = [System.Drawing.Bitmap]::FromFile("D:\Rolan_desktop\galah\$f")
  $colors = @{}
  $non = 0
  for ($x=0; $x -lt $bmp.Width; $x+=1) { for ($y=0; $y -lt $bmp.Height; $y+=1) { $c = $bmp.GetPixel($x,$y); if ($c.A -gt 0) { $non++; $key = "$($c.R),$($c.G),$($c.B)"; $colors[$key] = [int]$colors[$key] + 1 } } }
  $top = ($colors.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 4 | ForEach-Object { "$($_.Key): $($_.Value)" }) -join ' '
  Write-Host "$f non=$non/1024 $top"
  $bmp.Dispose()
}
