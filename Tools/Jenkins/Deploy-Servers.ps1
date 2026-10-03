[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$')][string]$VmHost,
    [string]$IdentityFile = $env:DRAWLIAR_SSH_KEY,
    [string]$KnownHostsFile = $env:DRAWLIAR_KNOWN_HOSTS,
    [ValidatePattern('^[a-z][a-z0-9-]*$')][string]$SshUser = $env:DRAWLIAR_SSH_USER
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
foreach ($file in @($IdentityFile,$KnownHostsFile)) { if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw 'SSH 인증 파일이 없습니다.' } }
$release = 'release-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$archive = Join-Path $repository "Build/$release.tar.gz"
& tar -czf $archive -C (Join-Path $repository 'Build/LinuxServer') .
if ($LASTEXITCODE -ne 0) { throw '배포 압축 실패' }
$sha = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$temporaryKey = Join-Path ([IO.Path]::GetTempPath()) ('drawliar-ssh-' + [Guid]::NewGuid().ToString('N'))
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetOwner($identity)
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'Allow')))
    New-Item -ItemType File -Path $temporaryKey | Out-Null
    Set-Acl -LiteralPath $temporaryKey -AclObject $acl
    [IO.File]::WriteAllBytes($temporaryKey, [IO.File]::ReadAllBytes($IdentityFile))
    $options = @('-i',$temporaryKey,'-o','BatchMode=yes','-o','IdentitiesOnly=yes','-o','StrictHostKeyChecking=yes','-o',"UserKnownHostsFile=$KnownHostsFile",'-o','ConnectTimeout=20','-o','ServerAliveInterval=15')
    & scp @options $archive "${SshUser}@${VmHost}:/srv/drawliar/incoming/$release.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw '배포 업로드 실패' }
    & ssh @options "${SshUser}@${VmHost}" "sudo -n /usr/local/sbin/drawliar-deploy $release $sha"
    if ($LASTEXITCODE -ne 0) { throw 'DrawLiar 배포 또는 상태 검사 실패' }
} finally { if (Test-Path -LiteralPath $temporaryKey) { Remove-Item -LiteralPath $temporaryKey -Force } }
