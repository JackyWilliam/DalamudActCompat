param(
    [Parameter(Mandatory = $true)]
    [string]$UpstreamAssembly,
    [Parameter(Mandatory = $true)]
    [string]$OutputFile
)

$ErrorActionPreference = 'Stop'
$upstreamPath = (Resolve-Path -LiteralPath $UpstreamAssembly).Path
$assembly = [Reflection.Assembly]::LoadFile($upstreamPath)
try {
    $types = $assembly.GetTypes()
}
catch [Reflection.ReflectionTypeLoadException] {
    $types = @($_.Exception.Types | Where-Object { $null -ne $_ })
}

$holders = @($types | Where-Object {
    $fields = @($_.GetFields([Reflection.BindingFlags]'Public,Static') |
        Where-Object { $_.FieldType -eq [string] })
    $fields.Count -eq 4
})
if ($holders.Count -ne 1) {
    throw "Expected one upstream four-string runtime holder; found $($holders.Count)."
}

$values = @($holders[0].GetFields([Reflection.BindingFlags]'Public,Static') |
    Where-Object { $_.FieldType -eq [string] } |
    Sort-Object MetadataToken |
    ForEach-Object { [string]$_.GetValue($null) })
$parsedFate = [Guid]::Empty
$parsedNpc = [Guid]::Empty
if ($values.Count -ne 4 -or
    -not [Uri]::IsWellFormedUriString($values[0], [UriKind]::Absolute) -or
    [string]::IsNullOrWhiteSpace($values[1]) -or
    -not [Guid]::TryParse($values[2], [ref]$parsedFate) -or
    -not [Guid]::TryParse($values[3], [ref]$parsedNpc)) {
    throw 'The upstream runtime constants are incomplete.'
}

$plainText = [Text.Encoding]::UTF8.GetBytes(
    (ConvertTo-Json -InputObject $values -Compress))
$sha256 = [Security.Cryptography.SHA256]::Create()
$key = $sha256.ComputeHash([IO.File]::ReadAllBytes($upstreamPath))
$hmac = [Security.Cryptography.HMACSHA256]::new($key)
$tag = $hmac.ComputeHash($plainText)
$cipherText = [byte[]]::new($plainText.Length)
for ($index = 0; $index -lt $plainText.Length; $index++) {
    $cipherText[$index] = $plainText[$index] -bxor $key[$index % $key.Length]
}

$magic = [Text.Encoding]::ASCII.GetBytes('DMR1')
$payload = [byte[]]::new($magic.Length + $tag.Length + $cipherText.Length)
[Array]::Copy($magic, 0, $payload, 0, $magic.Length)
[Array]::Copy($tag, 0, $payload, $magic.Length, $tag.Length)
[Array]::Copy($cipherText, 0, $payload, $magic.Length + $tag.Length, $cipherText.Length)
[IO.File]::WriteAllBytes($OutputFile, $payload)

[Array]::Clear($plainText, 0, $plainText.Length)
[Array]::Clear($key, 0, $key.Length)
