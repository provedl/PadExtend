param([int]$Port=27555, [int]$MaxFrames=90, [string]$OutH264="recv.h264")
$cli = New-Object System.Net.Sockets.TcpClient
$cli.Connect("127.0.0.1",$Port)
$cli.NoDelay=$true
$s = $cli.GetStream()
function ReadFully([int]$n){ $buf=New-Object byte[] $n; $off=0; while($off -lt $n){ $r=$s.Read($buf,$off,$n-$off); if($r -le 0){throw "eof"}; $off+=$r }; return ,$buf }
function BE32($b,$o){ return ([uint32]$b[$o] -shl 24) -bor ([uint32]$b[$o+1] -shl 16) -bor ([uint32]$b[$o+2] -shl 8) -bor ([uint32]$b[$o+3]) }
$out = New-Object System.IO.MemoryStream
$counts=@{}
$lastSeq=-1; $gaps=0
for($i=0;$i -lt $MaxFrames;$i++){
  $h = ReadFully 16
  $magic=[System.Text.Encoding]::ASCII.GetString($h,0,4)
  if($magic -ne "PEX1"){ throw "bad magic $magic" }
  $seq = BE32 $h 4
  $type= BE32 $h 8
  $len = BE32 $h 12
  $payload = if($len -gt 0){ ReadFully $len } else { ,(New-Object byte[] 0) }
  if($lastSeq -ge 0 -and $seq -ne $lastSeq+1){ $gaps++ }
  $lastSeq=$seq
  if($counts.ContainsKey([int]$type)){ $counts[[int]$type]++ } else { $counts[[int]$type]=1 }
  if($type -eq 3 -or $type -eq 4 -or $type -eq 5){ $out.Write($payload,0,$payload.Length) }
}
$cli.Close()
[System.IO.File]::WriteAllBytes((Join-Path (Get-Location) $OutH264), $out.ToArray())
"frames read: $MaxFrames, seq gaps: $gaps"
"type counts: " + (($counts.GetEnumerator()|Sort-Object Name|%{ "t$($_.Key)=$($_.Value)" }) -join ' ')
"reconstructed H264 bytes: $($out.Length) -> $OutH264"
