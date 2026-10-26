Add-Type -AssemblyName System.Drawing
$source=[Drawing.Bitmap]::FromFile((Join-Path $PSScriptRoot 'artwork/app-icon.png'))
$frames=@()
try {
 foreach($size in @(16,24,32,48,64,128,256)) {
  $bitmap=New-Object Drawing.Bitmap $size,$size
  $graphics=[Drawing.Graphics]::FromImage($bitmap)
  $stream=New-Object IO.MemoryStream
  try {
   $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
   $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::Half
   $graphics.DrawImage($source,(New-Object Drawing.Rectangle 0,0,$size,$size),0,0,$source.Width,$source.Height,[Drawing.GraphicsUnit]::Pixel)
   $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
   $frames+=,@{Size=$size;Data=$stream.ToArray()}
  } finally {$stream.Dispose();$graphics.Dispose();$bitmap.Dispose()}
 }
 $file=[IO.File]::Create((Join-Path $PSScriptRoot 'assets/app.ico'))
 $writer=New-Object IO.BinaryWriter $file
 try {
  $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count)
  $offset=6+16*$frames.Count
  foreach($frame in $frames) {
   $dimension=if($frame.Size -eq 256){0}else{$frame.Size}
   $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
   $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frame.Data.Length);$writer.Write([uint32]$offset)
   $offset+=$frame.Data.Length
  }
  foreach($frame in $frames){$writer.Write([byte[]]$frame.Data)}
 } finally {$writer.Dispose()}
} finally {$source.Dispose()}
